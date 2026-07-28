using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MikeBrowserWin.Services;

public sealed record HiveMessage(string Id, string From, string Type, string Body);
public sealed record HiveLogEntry(bool Sent, string Peer, string Type, string Body, DateTime Ts);

/// <summary>
/// Mints (once) this Windows device's MikeBrowser hive identity via the IdP — the desktop
/// equivalent of the phone daemon's §0 self-registration. Reuses the validated flow:
/// login → pair device → mint agent_key. Cached (DPAPI) so it's minted only on first sign-in.
/// </summary>
public static class HiveIdentity
{
    private const string Idp = "https://account.osmike.com";
    public const string HiveUrl = "https://mikeos-hive-production.up.railway.app";

    public sealed record Cred(string AgentKey, string Name);

    public static async Task<Cred?> EnsureAsync(Session session, string email, string password)
    {
        var cached = session.LoadHiveCred();
        if (cached != null) return cached;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            // 1) account login → JWT
            var jwt = (await PostJsonAsync(http, $"{Idp}/api/auth/login",
                new { email, password }, null))?.GetProperty("token").GetString();
            if (string.IsNullOrEmpty(jwt)) return null;

            // 2) pair this PC as a device (request → activate) → canonical device_id
            var deviceName = Environment.MachineName;
            var deviceId = Guid.NewGuid().ToString();
            var reqResp = await PostJsonAsync(http, $"{Idp}/api/devices/pair/request",
                new { deviceId, deviceName }, null);
            var code = reqResp?.GetProperty("code").GetString();
            if (string.IsNullOrEmpty(code)) return null;

            var act = await PostJsonAsync(http, $"{Idp}/api/devices/pair/activate",
                new { code, mode = "new_slot", slot_name = deviceName }, jwt);
            var canonical = act.HasValue && act.Value.TryGetProperty("device_id", out var d)
                ? d.GetString() : deviceId;

            // 3) mint the MikeBrowser agent key (a linked device_id is the credential)
            var mint = await PostJsonAsync(http, $"{Idp}/api/mikeos/agents",
                new { deviceId = canonical, app = "MikeBrowser" }, null);
            var agentKey = mint?.GetProperty("agent_key").GetString();
            var name = mint.HasValue && mint.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(agentKey)) return null;

            var cred = new Cred(agentKey, name ?? $"{email}/{deviceName}/MikeBrowser");
            session.SaveHiveCred(cred);
            return cred;
        }
        catch { return null; }
    }

    private static async Task<JsonElement?> PostJsonAsync(HttpClient http, string url, object body, string? bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return null;
        try { return JsonDocument.Parse(text).RootElement.Clone(); } catch { return null; }
    }
}

/// <summary>
/// The live hive: a persistent WebSocket signal channel + REST fetch/ack/send (mirrors the
/// Android HiveSocket). Emits inbound messages to a handler and keeps a message log for the
/// Agent Inspector. Auto-reconnects.
/// </summary>
public sealed class HiveClient
{
    private readonly HiveIdentity.Cred _cred;
    private readonly string _hiveUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public event Action<HiveMessage>? OnMessage;
    public ObservableLog Log { get; } = new();

    public HiveClient(HiveIdentity.Cred cred, string hiveUrl)
    {
        _cred = cred;
        _hiveUrl = hiveUrl.TrimEnd('/');
    }

    public void Connect()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Close()
    {
        _running = false;
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var backoff = 1000;
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                var wsUrl = _hiveUrl.Replace("https://", "wss://").Replace("http://", "ws://")
                            + $"/api/agent/ws?api_key={Uri.EscapeDataString(_cred.AgentKey)}";
                await _ws.ConnectAsync(new Uri(wsUrl), ct);
                backoff = 1000;
                await FetchUnreadAsync();                     // drain anything queued while offline
                var buf = new byte[8192];
                while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buf, 0, res.Count);
                    bool hasUnread = false;
                    try { hasUnread = JsonDocument.Parse(text).RootElement.TryGetProperty("has_unread", out var h) && h.GetBoolean(); }
                    catch { }
                    if (hasUnread) await FetchUnreadAsync();
                }
            }
            catch { }
            if (!_running) break;
            await Task.Delay(backoff, CancellationToken.None);
            backoff = Math.Min(backoff * 2, 60000);
        }
    }

    private async Task FetchUnreadAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_hiveUrl}/api/messages?unread_only=true&limit=100");
            req.Headers.Add("X-API-KEY", _cred.AgentKey);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var ids = new List<string>();
            foreach (var m in arr.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var from = m.TryGetProperty("from_agent", out var f) ? f.GetString() ?? "" : "";
                var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var (type, bodyText) = SplitType(content);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
                Log.Add(new HiveLogEntry(false, from, type, bodyText, DateTime.Now));
                OnMessage?.Invoke(new HiveMessage(id, from, type, bodyText));
            }
            if (ids.Count > 0) await AckAsync(ids);
        }
        catch { }
    }

    private async Task AckAsync(List<string> ids)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_hiveUrl}/api/messages/mark-read")
            { Content = new StringContent(JsonSerializer.Serialize(new { ids }), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-API-KEY", _cred.AgentKey);
            using var _ = await _http.SendAsync(req);
        }
        catch { }
    }

    /// <summary>Send a message to a sibling (short name → resolved to same-device scope).</summary>
    public async Task<bool> SendAsync(string to, string type, string body)
    {
        try
        {
            var message = string.IsNullOrEmpty(type) ? body : $"{type}: {body}";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_hiveUrl}/api/hive/send")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    agent_name = _cred.Name,
                    description = "hive_send",
                    to_agent = ResolveName(to),
                    message,
                }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-API-KEY", _cred.AgentKey);
            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) Log.Add(new HiveLogEntry(true, to, string.IsNullOrEmpty(type) ? "message" : type, body, DateTime.Now));
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string ResolveName(string to)
    {
        if (string.IsNullOrEmpty(to) || to.Contains('/')) return to;
        var scope = _cred.Name.Contains('/') ? _cred.Name[.._cred.Name.LastIndexOf('/')] : "";
        return string.IsNullOrEmpty(scope) ? to : $"{scope}/{to}";
    }

    private static (string, string) SplitType(string content)
    {
        var idx = content.IndexOf(": ", StringComparison.Ordinal);
        if (idx is > 0 and <= 40)
        {
            var maybe = content[..idx];
            if (maybe.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
                return (maybe, content[(idx + 2)..]);
        }
        return ("message", content);
    }
}

/// <summary>A small observable log the Agent Inspector binds to.</summary>
public sealed class ObservableLog
{
    private readonly ConcurrentQueue<HiveLogEntry> _entries = new();
    public event Action? Changed;
    public void Add(HiveLogEntry e)
    {
        _entries.Enqueue(e);
        while (_entries.Count > 500 && _entries.TryDequeue(out _)) { }
        Changed?.Invoke();
    }
    public IReadOnlyList<HiveLogEntry> Snapshot() => _entries.Reverse().ToList();
}
