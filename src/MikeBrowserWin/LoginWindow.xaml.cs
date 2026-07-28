using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MikeBrowserWin;

/// <summary>
/// Embedded "Sign in with MikeOS" window — hosts the account.osmike.com OAuth authorize
/// page in a WebView2 (so sign-in stays INSIDE MikeBrowser, not the system browser) and
/// captures the redirect to the loopback callback. Returns the callback URI via <see cref="Callback"/>.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly string _authorizeUrl;
    private readonly string _redirectPrefix;

    public Uri? Callback { get; private set; }

    /// <summary>
    /// The account password the user typed on the MikeOS login page — captured transiently so
    /// MikeBrowser can derive the vault key (VAULT.md v1 "reuse account password"). Never stored.
    /// </summary>
    public string? CapturedPassword { get; private set; }

    // Injected into the login page: posts the password value on submit/Enter so the app can
    // derive the vault key. Only the account.osmike.com login page is ever loaded here.
    private const string CaptureScript = @"
(function(){ if(window.__mbpw)return; window.__mbpw=1;
  function grab(){ var p=document.querySelector('input[type=password]');
    if(p&&p.value) window.chrome.webview.postMessage('pw:'+p.value); }
  document.addEventListener('submit', grab, true);
  document.addEventListener('keydown', function(e){ if(e.key==='Enter') grab(); }, true);
  document.addEventListener('click', function(e){ var t=e.target;
    if(t&&(t.type==='submit'||/sign|log|continue|approve/i.test(t.textContent||''))) grab(); }, true);
})();";

    public LoginWindow(string authorizeUrl, string redirectUri)
    {
        InitializeComponent();
        _authorizeUrl = authorizeUrl;
        _redirectPrefix = redirectUri;
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MikeBrowser", "Login");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(_redirectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // The AS redirected back with ?code=… — grab it and close BEFORE the
                    // (dead) loopback navigation actually happens.
                    e.Cancel = true;
                    Callback = new Uri(e.Uri);
                    Dispatcher.BeginInvoke(new Action(() => { DialogResult = true; }));
                }
            };
            Web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var msg = e.TryGetWebMessageAsString();
                    if (msg != null && msg.StartsWith("pw:")) CapturedPassword = msg[3..];
                }
                catch { }
            };
            Web.CoreWebView2.DOMContentLoaded += async (_, _) =>
            {
                try { await Web.CoreWebView2.ExecuteScriptAsync(CaptureScript); } catch { }
            };
            Web.CoreWebView2.Navigate(_authorizeUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't open the sign-in page.\n\n" + ex.Message,
                "MikeBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
        }
    }
}
