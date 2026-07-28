using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MikeBrowserWin.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MikeBrowserWin;

public partial class MainWindow : Window
{
    private const string HomePage = "https://www.google.com";

    // ---- resource governor knobs ----
    private const long BudgetBytes = 500L * 1024 * 1024;      // total tab-content memory budget
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(10);

    private enum TabState { Active, Suspended, Purged }

    private sealed class BrowserTab
    {
        public WebView2? View;              // null once purged from memory
        public string Url = "";
        public string Title = "New tab";
        public TabState State = TabState.Purged;
        public DateTime LastActive = DateTime.Now;
        public Border? Header;
        public TextBlock? TitleText;
    }

    private readonly Session _session = new();
    private readonly AccountClient _account;
    private readonly BrowserCloudClient _cloud = new();
    private readonly VaultClient _vault;
    private List<Bookmark> _bookmarks = new();

    private CoreWebView2Environment? _env;
    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _active;
    private readonly DispatcherTimer _governor = new() { Interval = TimeSpan.FromSeconds(5) };

    // Password-manager content script: recognises login fields, offers save, autofills.
    private const string CredScript = @"
(function(){ if(window.__mbcred)return; window.__mbcred=1;
  function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
  function findUser(pw){ var f=pw.form||document;
    var u=f.querySelector('input[type=email],input[autocomplete=username],input[name*=user i],input[name*=email i],input[id*=user i],input[id*=email i]');
    if(!u){ var ins=[].slice.call(f.querySelectorAll('input[type=text],input:not([type])')); u=ins[0]; }
    return u; }
  function grab(){ var pw=document.querySelector('input[type=password]'); if(!pw||!pw.value)return;
    var u=findUser(pw); post({t:'save',host:location.host,u:u?u.value:'',p:pw.value}); }
  document.addEventListener('submit', grab, true);
  document.addEventListener('keydown', function(e){ if(e.key==='Enter') setTimeout(grab,0); }, true);
  document.addEventListener('click', function(e){ var t=e.target;
    if(t&&(t.type==='submit'||/sign in|log ?in|login|sign on|continue/i.test((t.textContent||'')))) setTimeout(grab,0); }, true);
  if(document.querySelector('input[type=password]')) post({t:'loginform',host:location.host});
  window.__mbfill=function(u,p){ var pw=document.querySelector('input[type=password]'); if(!pw)return;
    var us=findUser(pw); if(us){us.value=u; us.dispatchEvent(new Event('input',{bubbles:true})); us.dispatchEvent(new Event('change',{bubbles:true}));}
    pw.value=p; pw.dispatchEvent(new Event('input',{bubbles:true})); pw.dispatchEvent(new Event('change',{bubbles:true})); };
})();";

    public MainWindow()
    {
        InitializeComponent();
        _account = new AccountClient(_session);
        _vault = new VaultClient(_account, _session);
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MikeBrowser", "WebView2");
            Directory.CreateDirectory(udf);
            _env = await CoreWebView2Environment.CreateAsync(null, udf);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "MikeBrowser needs the Microsoft Edge WebView2 Runtime (built into Windows 11).\n\n" + ex.Message,
                "MikeBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpdateAuthUI();
        if (_account.IsSignedIn) await RefreshBookmarks();

        // A URL from the command line (default-browser launch) wins over session restore.
        var start = App.StartupUrl ?? _session.LastUrl;
        NewTab(string.IsNullOrWhiteSpace(start) ? HomePage : start);

        _governor.Tick += async (_, _) => await Govern();
        _governor.Start();
    }

    // ======================= TABS =======================

    private BrowserTab NewTab(string url)
    {
        var tab = new BrowserTab { Url = NormalizeUrl(url) };
        BuildHeader(tab);
        _tabs.Add(tab);
        ActivateTab(tab);
        return tab;
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab(HomePage);

    private async void ActivateTab(BrowserTab tab)
    {
        if (_env == null) return;
        var prev = _active;
        _active = tab;

        if (prev != null && prev != tab) await SuspendTab(prev);

        if (tab.View == null)
            await CreateViewFor(tab);                       // new or previously purged → (re)load
        else
        {
            tab.View.Visibility = Visibility.Visible;
            try { tab.View.CoreWebView2?.Resume(); } catch { }
        }
        tab.State = TabState.Active;
        tab.LastActive = DateTime.Now;

        if (!AddressBar.IsKeyboardFocusWithin) AddressBar.Text = tab.Url;
        UpdateNavButtons();
        UpdateStar();
        UpdateHeaders();
    }

    private async Task CreateViewFor(BrowserTab tab)
    {
        var view = new WebView2();
        tab.View = view;
        view.Visibility = Visibility.Collapsed;
        TabHost.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(_env);
            var core = view.CoreWebView2;
            view.NavigationCompleted += (_, _) => OnNavigated(tab);
            view.SourceChanged += (_, _) => OnNavigated(tab);
            core.WebMessageReceived += OnWebMessage;
            core.DOMContentLoaded += async (_, _) => { try { await core.ExecuteScriptAsync(CredScript); } catch { } };
            core.DocumentTitleChanged += (_, _) => { tab.Title = core.DocumentTitle; UpdateHeaders(); };
            core.NewWindowRequested += (_, e) => { e.Handled = true; NewTab(e.Uri); };   // links → new tab
            core.Navigate(string.IsNullOrWhiteSpace(tab.Url) ? HomePage : tab.Url);
        }
        catch { }
        view.Visibility = (tab == _active) ? Visibility.Visible : Visibility.Collapsed;
    }

    // Suspend a background tab: hide it and freeze its renderer (no CPU).
    private async Task SuspendTab(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 == null) return;
        tab.View.Visibility = Visibility.Collapsed;
        try { await tab.View.CoreWebView2.TrySuspendAsync(); } catch { }
        tab.State = TabState.Suspended;
    }

    // Purge a background tab from memory entirely: dispose the WebView2, keep the URL.
    // It reloads on next activation.
    private void PurgeTab(BrowserTab tab)
    {
        if (tab == _active || tab.View == null) return;
        try { TabHost.Children.Remove(tab.View); tab.View.Dispose(); } catch { }
        tab.View = null;
        tab.State = TabState.Purged;
        UpdateHeaders();
    }

    private void CloseTab(BrowserTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        if (tab.View != null) { try { TabHost.Children.Remove(tab.View); tab.View.Dispose(); } catch { } }
        if (tab.Header != null) TabStrip.Children.Remove(tab.Header);
        _tabs.Remove(tab);
        if (_tabs.Count == 0) { NewTab(HomePage); return; }
        if (_active == tab) ActivateTab(_tabs[Math.Clamp(idx - 1, 0, _tabs.Count - 1)]);
        else UpdateHeaders();
    }

    private WebView2? ActiveView => _active?.View;

    // ---- tab-strip UI (built in code) ----
    private void BuildHeader(BrowserTab tab)
    {
        var title = new TextBlock
        {
            Text = tab.Title, Foreground = Hex("#E8EFE9"), FontSize = 13,
            MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        tab.TitleText = title;

        var close = new Button
        {
            Content = "×", Width = 18, Height = 18, Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent, Foreground = Hex("#8ea79a"), BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, FontSize = 13,
        };
        close.Click += (_, _) => CloseTab(tab);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(title);
        sp.Children.Add(close);

        var border = new Border
        {
            Padding = new Thickness(12, 6, 8, 6), Margin = new Thickness(0, 0, 4, 0),
            CornerRadius = new CornerRadius(8, 8, 0, 0), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, Child = sp,
        };
        border.MouseLeftButtonUp += (_, _) => ActivateTab(tab);   // Button consumes its own click
        tab.Header = border;
        TabStrip.Children.Add(border);
    }

    private void UpdateHeaders()
    {
        foreach (var t in _tabs)
        {
            if (t.TitleText != null)
                t.TitleText.Text = (t.State == TabState.Purged ? "\U0001F4A4 " : "") +
                                   (string.IsNullOrWhiteSpace(t.Title) ? "New tab" : t.Title);
            if (t.Header != null)
                t.Header.Background = (t == _active) ? Hex("#161D19") : Brushes.Transparent;
        }
    }

    private static SolidColorBrush Hex(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    // ======================= RESOURCE GOVERNOR =======================
    // Inactive tabs are suspended (no CPU). If total renderer memory exceeds the budget, or a
    // tab has been idle too long, the least-recently-used inactive tabs are purged from memory.
    private async Task Govern()
    {
        foreach (var t in _tabs.ToList())
            if (t != _active && t.View?.CoreWebView2 != null && t.State == TabState.Active)
                await SuspendTab(t);

        foreach (var t in _tabs.Where(t => t != _active && t.View != null &&
                                           DateTime.Now - t.LastActive > MaxIdle).ToList())
            PurgeTab(t);

        if (RendererMemoryBytes() > BudgetBytes)
        {
            foreach (var t in _tabs.Where(t => t != _active && t.View != null)
                                   .OrderBy(t => t.LastActive).ToList())
            {
                PurgeTab(t);
                if (RendererMemoryBytes() <= BudgetBytes) break;
            }
        }

        long mb = RendererMemoryBytes() / (1024 * 1024);
        MemLabel.Text = $"{mb} MB · {_tabs.Count(t => t.View != null)}/{_tabs.Count} live";
    }

    private long RendererMemoryBytes()
    {
        long total = 0;
        try
        {
            if (_env == null) return 0;
            foreach (var pi in _env.GetProcessInfos())
                if (pi.Kind == CoreWebView2ProcessKind.Renderer)
                    try { total += Process.GetProcessById(pi.ProcessId).WorkingSet64; } catch { }
        }
        catch { }
        return total;
    }

    // ======================= NAVIGATION / OMNIBOX =======================

    private void NavigateTo(string raw)
    {
        var core = ActiveView?.CoreWebView2;
        if (core == null) return;
        core.Navigate(NormalizeUrl(raw));
    }

    private void OnNavigated(BrowserTab tab)
    {
        var url = tab.View?.Source?.ToString() ?? "";
        tab.Url = url;
        if (!string.IsNullOrWhiteSpace(url) && url != "about:blank") _session.LastUrl = url;
        if (tab == _active)
        {
            if (!AddressBar.IsKeyboardFocusWithin) AddressBar.Text = url;
            UpdateNavButtons();
            UpdateStar();
        }
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = ActiveView?.CanGoBack ?? false;
        FwdBtn.IsEnabled = ActiveView?.CanGoForward ?? false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (ActiveView?.CanGoBack == true) ActiveView.GoBack(); }
    private void Fwd_Click(object sender, RoutedEventArgs e) { if (ActiveView?.CanGoForward == true) ActiveView.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => ActiveView?.Reload();

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateTo(AddressBar.Text);
            Keyboard.ClearFocus();
            ActiveView?.Focus();
            e.Handled = true;
        }
    }

    private void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => AddressBar.SelectAll()));

    private void Address_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBar.IsKeyboardFocusWithin) { e.Handled = true; AddressBar.Focus(); }
    }

    // Alt+D / Ctrl+L → address bar; Ctrl+T new tab; Ctrl+W close tab.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if ((alt && (e.SystemKey == Key.D || e.Key == Key.D)) || (ctrl && e.Key == Key.L))
        {
            e.Handled = true; AddressBar.Focus(); AddressBar.SelectAll();
        }
        else if (ctrl && e.Key == Key.T) { e.Handled = true; NewTab(HomePage); }
        else if (ctrl && e.Key == Key.W && _active != null) { e.Handled = true; CloseTab(_active); }
    }

    // ======================= PASSWORD MANAGER (WebMessage) =======================
    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var core = sender as CoreWebView2;
        string msg;
        try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        if (core == null || msg.Length == 0 || msg[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var r = doc.RootElement;
            var t = r.TryGetProperty("t", out var tt) ? tt.GetString() : null;
            var host = r.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(host) || !_vault.IsUnlocked) return;

            if (t == "loginform")
            {
                var cred = await _vault.GetPasswordAsync(host);
                if (cred != null)
                    await core.ExecuteScriptAsync(
                        $"window.__mbfill && window.__mbfill({JsonEnc(cred.Value.User)},{JsonEnc(cred.Value.Pass)})");
            }
            else if (t == "save")
            {
                var u = r.TryGetProperty("u", out var uu) ? uu.GetString() ?? "" : "";
                var p = r.TryGetProperty("p", out var pp) ? pp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(p)) return;
                var existing = await _vault.GetPasswordAsync(host);
                if (existing != null && existing.Value.Pass == p) return;
                if (MessageBox.Show(
                        $"Save this password for {host} in MikeVault?\n\nIt syncs (encrypted) to your other devices.",
                        "MikeBrowser", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await _vault.SavePasswordAsync(host, u, p);
            }
        }
        catch { }
    }

    private static string JsonEnc(string s) => JsonSerializer.Serialize(s);

    // ======================= BOOKMARKS =======================
    private async void Star_Click(object sender, RoutedEventArgs e)
    {
        var token = await _account.GetAccessTokenAsync();
        if (token == null) { PromptSignIn(); return; }
        var url = CurrentUrl();
        if (string.IsNullOrWhiteSpace(url)) return;

        var existing = _bookmarks.FirstOrDefault(b => SameUrl(b.Url, url));
        if (existing != null) await _cloud.DeleteBookmarkAsync(token, existing.Id);
        else
        {
            var title = ActiveView?.CoreWebView2?.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) title = url;
            await _cloud.AddBookmarkAsync(token, url, title);
        }
        await RefreshBookmarks();
        UpdateStar();
    }

    private void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (!_account.IsSignedIn)
        {
            var mi = new MenuItem { Header = "Sign in to sync your bookmarks" };
            mi.Click += (_, _) => SignIn_Click(sender, e);
            menu.Items.Add(mi);
        }
        else if (_bookmarks.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No bookmarks yet — tap ☆ to add one", IsEnabled = false });
        else
            foreach (var b in _bookmarks)
            {
                var url = b.Url;
                var mi = new MenuItem { Header = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title };
                mi.Click += (_, _) => NavigateTo(url);
                menu.Items.Add(mi);
            }
        menu.PlacementTarget = BookmarksBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_account.IsSignedIn)
        {
            _account.SignOut();
            _vault.Lock();
            _bookmarks = new();
            UpdateAuthUI();
            UpdateStar();
            return;
        }
        SignInBtn.Content = "Signing in…";
        SignInBtn.IsEnabled = false;
        bool ok = await _account.SignInAsync(this);
        SignInBtn.IsEnabled = true;
        if (ok)
        {
            var pw = _account.PopCapturedPassword();
            if (!string.IsNullOrEmpty(pw)) { try { await _vault.UnlockAsync(pw); } catch { } }
            await RefreshBookmarks();
        }
        UpdateAuthUI();
        UpdateStar();
    }

    private void PromptSignIn()
    {
        if (MessageBox.Show("Sign in with your osmike.com account to sync bookmarks?",
                "MikeBrowser", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            SignIn_Click(this, new RoutedEventArgs());
    }

    private async Task RefreshBookmarks()
    {
        var token = await _account.GetAccessTokenAsync();
        _bookmarks = token == null ? new() : await _cloud.ListBookmarksAsync(token);
    }

    private void UpdateAuthUI() => SignInBtn.Content = _account.IsSignedIn ? "Signed in" : "Sign in";

    private void UpdateStar()
    {
        var url = CurrentUrl();
        bool marked = !string.IsNullOrWhiteSpace(url) && _bookmarks.Any(b => SameUrl(b.Url, url));
        StarBtn.Content = marked ? "★" : "☆";
        StarBtn.Foreground = marked ? (Brush)FindResource("MikeGreen") : Brushes.White;
    }

    private string CurrentUrl() => ActiveView?.Source?.ToString() ?? "";

    private static bool SameUrl(string a, string b) => (a ?? "").TrimEnd('/') == (b ?? "").TrimEnd('/');

    // Omnibox rule (like Chrome): scheme→trust, host/domain→https, else Google search.
    private static string NormalizeUrl(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return HomePage;
        if (Regex.IsMatch(s, "^[a-zA-Z][a-zA-Z0-9+.-]*://") || s.StartsWith("about:") || s.StartsWith("data:"))
            return s;
        bool looksLikeUrl = !s.Contains(' ') && (s.Contains('.') || s.StartsWith("localhost"));
        return looksLikeUrl ? "https://" + s
                            : "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
    }
}
