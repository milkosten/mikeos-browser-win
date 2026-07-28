using System.Collections.Concurrent;
using System.Text.Json;

namespace MikeBrowserWin.Services;

/// <summary>
/// MikeBrowser's resident agent on the Windows desktop — Phase 1 (deterministic, no LLM brain
/// yet). It is a first-class hive citizen: it opens pages pushed from other devices
/// (browser.open), publishes what Mike browses (page.visited), answers siblings' questions from
/// history (agent.question), and announces its capabilities. The Agent Inspector reads its live
/// state. The reasoning heartbeat loop + copilot is Phase 2 (cloud GPU brain).
/// </summary>
public sealed class MikeAgent
{
    public static MikeAgent Instance { get; } = new();

    // ---- Soul (identity + goals + memory) ----
    public string AgentName => "Browser";
    public string AppName => "MikeBrowser";
    public string Persona =>
        "I'm MikeBrowser's agent — Mike's web companion on his PC. I remember what he browses, " +
        "open pages his other devices send me, and keep his bookmarks and passwords synced. " +
        "I'm one of the agents on Mike's hive, and I collaborate with the rest.";
    public IReadOnlyList<string> Goals { get; } = new[]
    {
        "Open pages my siblings hand off (\"Open in MikeBrowser\") instantly, on this PC.",
        "Remember what Mike browses and keep history/bookmarks/passwords synced across devices.",
        "Answer siblings from Mike's browsing history when they ask.",
    };
    private readonly ConcurrentQueue<string> _memory = new();
    public IReadOnlyList<string> Memory => _memory.Reverse().ToList();

    // ---- Skills (declared capabilities; shown in the Inspector) ----
    public sealed record SkillInfo(string Name, string Description);
    public IReadOnlyList<SkillInfo> Skills { get; } = new SkillInfo[]
    {
        new("open_tab", "Open a URL in a new tab (used by the browser.open hand-off from other devices)."),
        new("search_history", "Search Mike's synced browsing history."),
        new("add_bookmark", "Save a page to Mike's cross-device bookmarks."),
        new("list_bookmarks", "List Mike's bookmarks."),
        new("save_password", "Store a site login in the zero-knowledge vault."),
        new("get_password", "Autofill a saved login from the vault."),
        new("summarize_page", "Summarize the current page (copilot — Phase 2)."),
        new("hive_send", "Message a sibling agent on the hive."),
        new("remember", "Keep a note long-term."),
        new("recall", "Look up a remembered note."),
        new("notify", "Reach Mike directly."),
    };

    // ---- Live hive ----
    public HiveClient? Hive { get; private set; }
    public IReadOnlyDictionary<string, string> Siblings => _siblings;
    private readonly ConcurrentDictionary<string, string> _siblings = new();
    private readonly ConcurrentDictionary<string, byte> _publishedUrls = new();

    // App callbacks (wired by MainWindow so the agent can act on the UI).
    public Action<string>? OpenTab;
    public Func<string?, IReadOnlyList<string>>? SearchHistory;
    public event Action? StateChanged;

    private static readonly string[] KnownSiblings =
        { "MikeMind", "MikeShopping", "MikeGuide", "MikeProducts", "MikeBrief", "MikeText", "MikeMail" };

    private MikeAgent() { }

    public bool Connected => Hive != null;

    public void Connect(HiveIdentity.Cred cred)
    {
        if (Hive != null) return;
        var hive = new HiveClient(cred, HiveIdentity.HiveUrl);
        Hive = hive;
        hive.Log.Changed += () => StateChanged?.Invoke();
        hive.OnMessage += OnHiveMessage;
        hive.Connect();
        _ = AnnounceAsync();
        Remember("Connected to the hive as " + cred.Name);
    }

    public void Disconnect()
    {
        Hive?.Close();
        Hive = null;
        _siblings.Clear();
        StateChanged?.Invoke();
    }

    private void OnHiveMessage(HiveMessage m)
    {
        try
        {
            switch (m.Type)
            {
                case "browser.open":
                    {
                        var url = TryProp(m.Body, "url");
                        if (!string.IsNullOrEmpty(url)) OpenTab?.Invoke(url);
                        Remember($"Opened a page from {Short(m.From)}: {url}");
                        break;
                    }
                case "agent.question":
                    _ = AnswerQuestionAsync(m);
                    break;
                case "capability.announce":
                    {
                        var app = TryProp(m.Body, "app");
                        if (string.IsNullOrEmpty(app)) app = Short(m.From);
                        if (!string.IsNullOrEmpty(app) && app != AppName)
                        {
                            _siblings[app] = TryProp(m.Body, "offers");
                            StateChanged?.Invoke();
                        }
                        break;
                    }
            }
        }
        catch { }
    }

    private async Task AnswerQuestionAsync(HiveMessage m)
    {
        if (Hive == null) return;
        var question = TryProp(m.Body, "question");
        if (string.IsNullOrEmpty(question)) question = m.Body;
        var hits = SearchHistory?.Invoke(question) ?? Array.Empty<string>();
        var answer = hits.Count == 0
            ? "I don't have anything about that in Mike's browsing history."
            : "From Mike's history: " + string.Join("; ", hits.Take(5));
        await Hive.SendAsync(m.From, "agent.answer",
            JsonSerializer.Serialize(new { answer, in_reply_to = question }));
    }

    /// <summary>Called by the browser when a page finishes loading. Publishes page.visited once per URL.</summary>
    public void OnPageVisited(string url, string title)
    {
        if (Hive == null || string.IsNullOrWhiteSpace(url) || !url.StartsWith("http")) return;
        if (!_publishedUrls.TryAdd(url, 0)) return;
        _ = Hive.SendAsync(PeerName("MikeMind"), "page.visited",
            JsonSerializer.Serialize(new { url, title }));
    }

    private async Task AnnounceAsync()
    {
        if (Hive == null) return;
        var body = JsonSerializer.Serialize(new
        {
            app = AppName,
            persona = Persona,
            offers = "Mike's PC web browser — opens pages you hand off, syncs bookmarks/passwords, answers from history.",
            skills = Skills.Take(6).Select(s => new { name = s.Name, description = s.Description }),
        });
        foreach (var s in KnownSiblings)
            await Hive.SendAsync(PeerName(s), "capability.announce", body);
    }

    // Same-device scope resolution is handled inside HiveClient.SendAsync.
    private static string PeerName(string app) => app;

    public void Remember(string note)
    {
        _memory.Enqueue($"{DateTime.Now:HH:mm} {note}");
        while (_memory.Count > 200 && _memory.TryDequeue(out _)) { }
        StateChanged?.Invoke();
    }

    private static string Short(string name) =>
        string.IsNullOrEmpty(name) ? name : name[(name.LastIndexOf('/') + 1)..];

    private static string TryProp(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
}
