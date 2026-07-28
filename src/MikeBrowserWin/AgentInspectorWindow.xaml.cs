using System.Linq;
using System.Windows;
using MikeBrowserWin.Services;

namespace MikeBrowserWin;

public partial class AgentInspectorWindow : Window
{
    private readonly MikeAgent _agent = MikeAgent.Instance;

    public AgentInspectorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        _agent.StateChanged += OnStateChanged;
        Closed += (_, _) => _agent.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(new Action(Refresh));

    private void Refresh()
    {
        StatusText.Text = _agent.Connected
            ? $"Connected to the hive · {_agent.Siblings.Count} sibling(s) heard from"
            : "Not connected — sign in to join the hive";

        var log = _agent.Hive?.Log.Snapshot() ?? System.Array.Empty<HiveLogEntry>();
        MsgList.ItemsSource = log.Select(e =>
            $"{(e.Sent ? "↑ to" : "↓ from")} {Short(e.Peer)}   ·   {e.Type}   ·   {e.Ts:HH:mm:ss}\n    {Trunc(e.Body, 160)}").ToList();
        MsgEmpty.Visibility = log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SkillList.ItemsSource = _agent.Skills.Select(s => $"{s.Name}  —  {s.Description}").ToList();
        PersonaText.Text = _agent.Persona;
        GoalList.ItemsSource = _agent.Goals.Select(g => "• " + g).ToList();
        MemList.ItemsSource = _agent.Memory.ToList();
    }

    private static string Short(string name) =>
        string.IsNullOrEmpty(name) ? "?" : name[(name.LastIndexOf('/') + 1)..];

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
