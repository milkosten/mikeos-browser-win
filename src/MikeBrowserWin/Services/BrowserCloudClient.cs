using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MikeBrowserWin.Services;

public sealed record Bookmark(string Id, string Url, string Title);

/// <summary>
/// Talks to mikeos-browser-cloud with an OAuth Bearer access token (dual-auth cloud).
/// Bookmarks are user-scoped, so they sync with the phone automatically.
/// </summary>
public sealed class BrowserCloudClient
{
    private const string Base = "https://mikeos-browser-cloud-production.up.railway.app";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private HttpRequestMessage Req(HttpMethod m, string path, string token, string? body = null)
    {
        var r = new HttpRequestMessage(m, Base + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    public async Task<List<Bookmark>> ListBookmarksAsync(string token)
    {
        var list = new List<Bookmark>();
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/bookmarks", token));
            if (!resp.IsSuccessStatusCode) return list;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.TryGetProperty("bookmarks", out var b) ? b : doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    list.Add(new Bookmark(
                        Str(e, "id"), Str(e, "url"), Str(e, "title")));
        }
        catch { /* offline → empty */ }
        return list;
    }

    public async Task<string?> AddBookmarkAsync(string token, string url, string title)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { url, title });
            using var resp = await _http.SendAsync(Req(HttpMethod.Post, "/api/bookmarks", token, body));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("bookmark", out var bm) && bm.TryGetProperty("id", out var id))
                return id.GetString();
            return root.TryGetProperty("id", out var id2) ? id2.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<bool> DeleteBookmarkAsync(string token, string id)
    {
        try
        {
            using var resp = await _http.SendAsync(
                Req(HttpMethod.Delete, "/api/bookmarks/" + Uri.EscapeDataString(id), token));
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
