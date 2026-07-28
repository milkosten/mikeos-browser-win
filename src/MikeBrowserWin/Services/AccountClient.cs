using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    private const string ListenPrefix = "http://127.0.0.1:8765/";           // catch any path on the port
    private const string Scope = "openid profile email browser.read browser.write";

    private readonly Session _session;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _accessToken;
    private DateTimeOffset _accessExpiry = DateTimeOffset.MinValue;

    public AccountClient(Session session) => _session = session;

    public bool IsSignedIn => !string.IsNullOrEmpty(_session.LoadRefreshToken());

    /// <summary>Interactive sign-in. Returns true on success.</summary>
    public async Task<bool> SignInAsync()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();

        var authUrl = $"{Authority}/oauth/authorize?response_type=code&client_id={ClientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}&state={state}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256";
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { return false; }

        var code = ctx.Request.QueryString["code"];
        var rstate = ctx.Request.QueryString["state"];
        await RespondAsync(ctx, string.IsNullOrEmpty(code)
            ? "Sign-in was cancelled. You can close this tab."
            : "You're signed in to MikeBrowser. You can close this tab.");
        listener.Stop();

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

    private static async Task RespondAsync(HttpListenerContext ctx, string message)
    {
        var html = "<!doctype html><html><body style='font-family:Segoe UI,sans-serif;background:#0f1512;" +
                   "color:#e8efe9;text-align:center;padding-top:90px'><h2 style='color:#37c871'>MikeBrowser</h2>" +
                   $"<p>{message}</p></body></html>";
        var buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        try { await ctx.Response.OutputStream.WriteAsync(buf); } catch { }
        ctx.Response.Close();
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
