using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MikeBrowserWin.Services;
using Microsoft.Web.WebView2.Core;

namespace MikeBrowserWin;

public partial class MainWindow : Window
{
    private const string HomePage = "https://www.google.com";

    private readonly Session _session = new();
    private readonly AccountClient _account;
    private readonly BrowserCloudClient _cloud = new();
    private List<Bookmark> _bookmarks = new();
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _account = new AccountClient(_session);
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
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await Web.EnsureCoreWebView2Async(env);

            Web.NavigationCompleted += (_, _) => OnNavigated();
            Web.SourceChanged += (_, _) => OnNavigated();

            var start = _session.LastUrl;
            Web.CoreWebView2.Navigate(string.IsNullOrWhiteSpace(start) ? HomePage : start);
            _ready = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "MikeBrowser needs the Microsoft Edge WebView2 Runtime (built into Windows 11).\n\n" + ex.Message,
                "MikeBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateAuthUI();
        if (_account.IsSignedIn) { await RefreshBookmarks(); UpdateStar(); }
    }

    // ---- navigation ----
    private void NavigateTo(string raw)
    {
        if (!_ready) return;
        var url = NormalizeUrl(raw);
        Web.CoreWebView2.Navigate(url);
    }

    private void OnNavigated()
    {
        var url = Web.Source?.ToString() ?? "";
        if (!AddressBar.IsKeyboardFocusWithin) AddressBar.Text = url;
        if (!string.IsNullOrWhiteSpace(url) && url != "about:blank") _session.LastUrl = url;
        BackBtn.IsEnabled = Web.CanGoBack;
        FwdBtn.IsEnabled = Web.CanGoForward;
        UpdateStar();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void Fwd_Click(object sender, RoutedEventArgs e) { if (Web.CanGoForward) Web.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Web.Reload();

    // ---- omnibox (Chrome-style select-all + URL/search) ----
    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateTo(AddressBar.Text);
            Keyboard.ClearFocus();
            Web.Focus();
            e.Handled = true;
        }
    }

    private void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => AddressBar.SelectAll()));

    private void Address_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBar.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            AddressBar.Focus();      // GotFocus selects all
        }
    }

    // ---- bookmarks + sync ----
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
            var title = Web.CoreWebView2?.DocumentTitle;
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
        {
            menu.Items.Add(new MenuItem { Header = "No bookmarks yet — tap ☆ to add one", IsEnabled = false });
        }
        else
        {
            foreach (var b in _bookmarks)
            {
                var url = b.Url;
                var mi = new MenuItem { Header = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title };
                mi.Click += (_, _) => NavigateTo(url);
                menu.Items.Add(mi);
            }
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
            _bookmarks = new();
            UpdateAuthUI();
            UpdateStar();
            return;
        }
        SignInBtn.Content = "Signing in…";
        SignInBtn.IsEnabled = false;
        bool ok = await _account.SignInAsync();
        SignInBtn.IsEnabled = true;
        if (ok) await RefreshBookmarks();
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

    // ---- ui state ----
    private void UpdateAuthUI() => SignInBtn.Content = _account.IsSignedIn ? "Signed in" : "Sign in";

    private void UpdateStar()
    {
        var url = CurrentUrl();
        bool marked = !string.IsNullOrWhiteSpace(url) && _bookmarks.Any(b => SameUrl(b.Url, url));
        StarBtn.Content = marked ? "★" : "☆";               // ★ / ☆
        StarBtn.Foreground = marked
            ? (System.Windows.Media.Brush)FindResource("MikeGreen")
            : System.Windows.Media.Brushes.White;
    }

    private string CurrentUrl() => Web.Source?.ToString() ?? "";

    private static bool SameUrl(string a, string b) =>
        (a ?? "").TrimEnd('/') == (b ?? "").TrimEnd('/');

    // Omnibox rule (like Chrome): scheme→trust, host/domain→https, else Google search.
    private static string NormalizeUrl(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "about:blank";
        if (Regex.IsMatch(s, "^[a-zA-Z][a-zA-Z0-9+.-]*://") || s.StartsWith("about:") || s.StartsWith("data:"))
            return s;
        bool looksLikeUrl = !s.Contains(' ') && (s.Contains('.') || s.StartsWith("localhost"));
        return looksLikeUrl ? "https://" + s
                            : "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
    }
}
