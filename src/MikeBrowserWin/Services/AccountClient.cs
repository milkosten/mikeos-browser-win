using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MikeBrowserWin.Services;

/// <summary>
/// "Sign in with MikeOS" — OAuth 2.0 Authorization Code + PKCE against account.osmike.com,
/// the RFC 8252 native-app way: open the system browser to /oauth/authorize, catch the
/// redirect on a loopback HttpListener, exchange the code for tokens. The refresh token is
/// persisted (DPAPI) so the user stays signed in; access tokens are minted/refreshed silently.
/// </summary>
public sealed class AccountClient
{
    private const string Authority = "https://account.osmike.com";
    private const string ClientId = "mikeos-browser";
    private const string RedirectUri = "http://127.0.0.1:8765/callback";     // exact-match registered
    private const string Scope = "openid profile email browser.read browser.write vault.read vault.write";

    private readonly Session _session;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _accessToken;
    private DateTimeOffset _accessExpiry = DateTimeOffset.MinValue;
    private string? _capturedPassword;
    private string? _capturedEmail;

    public AccountClient(Session session) => _session = session;

    /// <summary>Return (and clear) the account password captured during the last interactive
    /// sign-in — used once to unlock the vault + mint the hive identity, then forgotten.</summary>
    public string? PopCapturedPassword()
    {
        var p = _capturedPassword;
        _capturedPassword = null;
        return p;
    }

    /// <summary>The account email captured during the last interactive sign-in (for hive minting).</summary>
    public string? PopCapturedEmail()
    {
        var e = _capturedEmail;
        _capturedEmail = null;
        return e;
    }

    public bool IsSignedIn => !string.IsNullOrEmpty(_session.LoadRefreshToken());

    /// <summary>
    /// Interactive sign-in — hosted INSIDE MikeBrowser (embedded WebView2), not the system
    /// browser. Opens the account.osmike.com authorize page in a child window and captures the
    /// loopback redirect + PKCE code exchange. Returns true on success.
    /// </summary>
    public async Task<bool> SignInAsync(Window owner)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authUrl = $"{Authority}/oauth/authorize?response_type=code&client_id={ClientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}&state={state}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256";

        var win = new LoginWindow(authUrl, RedirectUri) { Owner = owner };
        var ok = win.ShowDialog();
        if (ok != true || win.Callback == null) return false;

        _capturedPassword = win.CapturedPassword;   // used once to unlock the vault
        _capturedEmail = win.CapturedEmail;          // used once to mint the hive identity

        var code = QueryValue(win.Callback, "code");
        var rstate = QueryValue(win.Callback, "state");
        if (string.IsNullOrEmpty(code) || rstate != state) return false;

        return await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
        });
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (Uri.UnescapeDataString(kv[0]) == key)
                return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return null;
    }

    /// <summary>A valid access token, refreshing silently if needed. Null if signed out.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-60))
            return _accessToken;
        var rt = _session.LoadRefreshToken();
        if (string.IsNullOrEmpty(rt)) return null;
        var ok = await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rt,
            ["client_id"] = ClientId,
        });
        return ok ? _accessToken : null;
    }

    public void SignOut()
    {
        _session.SaveRefreshToken(null);
        _accessToken = null;
        _accessExpiry = DateTimeOffset.MinValue;
    }

    private async Task<bool> ExchangeAsync(Dictionary<string, string> form)
    {
        try
        {
            using var resp = await _http.PostAsync($"{Authority}/oauth/token", new FormUrlEncodedContent(form));
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _accessToken = root.GetProperty("access_token").GetString();
            var expIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(expIn);
            if (root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                _session.SaveRefreshToken(rt.GetString());   // rotating refresh — persist the new one
            return !string.IsNullOrEmpty(_accessToken);
        }
        catch { return false; }
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
