using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MikeBrowserWin.Services;

/// <summary>
/// MikeVault client — the zero-knowledge password store (mikeos-vault-cloud). All crypto is
/// client-side; the server only ever sees ciphertext. Scheme (ecosystem/VAULT.md v1):
///   account password ─PBKDF2(SHA256,600k)→ KEK ─AES-256-GCM unwrap→ VDK ─AES-256-GCM→ each item.
/// The VDK is cached (DPAPI) so the vault stays unlocked across launches without the password.
/// Shares the same vault as the MikeOS phone (same keybag, same account-password key).
/// </summary>
public sealed class VaultClient
{
    private const string Base = "https://mikeos-vault-cloud-production.up.railway.app";
    private const int Iterations = 600_000;
    private static readonly byte[] VdkAad = Encoding.UTF8.GetBytes("mikeos-vault-vdk-v1");
    private static byte[] ItemAad(string type) => Encoding.UTF8.GetBytes($"mikeos-vault-item-v1:{type}");

    private readonly AccountClient _account;
    private readonly Session _session;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private byte[]? _vdk;

    public VaultClient(AccountClient account, Session session)
    {
        _account = account;
        _session = session;
        _vdk = _session.LoadVdk();   // may already be cached from a previous session
    }

    public bool IsUnlocked => _vdk != null;

    /// <summary>
    /// Unlock the vault with the account password captured at sign-in: fetch the keybag and
    /// unwrap the VDK, or create a fresh keybag if the user has no vault yet. Caches the VDK.
    /// </summary>
    public async Task<bool> UnlockAsync(string accountPassword)
    {
        try
        {
            var token = await _account.GetAccessTokenAsync();
            if (token == null) return false;

            var (status, kb) = await SendAsync(HttpMethod.Get, "/api/vault/keybag", token, null);
            if (status == 200 && kb != null)
            {
                var root = kb.Value;
                var salt = Convert.FromBase64String(root.GetProperty("kdf_salt").GetString()!);
                var iters = root.TryGetProperty("kdf_params", out var kp) && kp.TryGetProperty("iterations", out var it)
                    ? it.GetInt32() : Iterations;
                var kek = Pbkdf2(accountPassword, salt, iters);
                _vdk = GcmOpen(kek, B64(root, "nonce"), B64(root, "wrapped_vdk"), VdkAad);
            }
            else // 404 → create a new vault
            {
                _vdk = RandomNumberGenerator.GetBytes(32);
                var salt = RandomNumberGenerator.GetBytes(16);
                var kek = Pbkdf2(accountPassword, salt, Iterations);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var wrapped = GcmSeal(kek, nonce, _vdk, VdkAad);
                var body = JsonSerializer.Serialize(new
                {
                    v = 1,
                    alg = "pbkdf2-sha256+aes256gcm",
                    kdf_salt = Convert.ToBase64String(salt),
                    kdf_params = new { iterations = Iterations, dkLen = 32 },
                    wrapped_vdk = Convert.ToBase64String(wrapped),
                    nonce = Convert.ToBase64String(nonce),
                });
                var (st, _) = await SendAsync(HttpMethod.Put, "/api/vault/keybag", token, body);
                if (st is < 200 or >= 300) { _vdk = null; return false; }
            }
            _session.SaveVdk(_vdk);
            return true;
        }
        catch { _vdk = null; return false; }
    }

    public void Lock() { _vdk = null; _session.SaveVdk(null); }

    /// <summary>Store (upsert) a login for a host. Returns true once the server confirms.</summary>
    public async Task<bool> SavePasswordAsync(string host, string username, string password)
    {
        if (_vdk == null) return false;
        var token = await _account.GetAccessTokenAsync();
        if (token == null) return false;

        var label = host.Trim().ToLowerInvariant();
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { u = username, p = password, host = label }));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = GcmSeal(_vdk, nonce, plain, ItemAad("password"));
        var body = JsonSerializer.Serialize(new
        {
            item_type = "password",
            label_hash = LabelHash(_vdk, "password", label),
            ciphertext = Convert.ToBase64String(ct),
            nonce = Convert.ToBase64String(nonce),
            v = 1,
            alg = "aes256gcm",
        });
        var (st, resp) = await SendAsync(HttpMethod.Post, "/api/vault/items", token, body);
        return st == 200 && resp?.TryGetProperty("id", out _) == true;   // never-trust-200
    }

    /// <summary>Look up a saved login for a host. Returns (username, password) or null.</summary>
    public async Task<(string User, string Pass)?> GetPasswordAsync(string host)
    {
        if (_vdk == null) return null;
        var token = await _account.GetAccessTokenAsync();
        if (token == null) return null;

        var label = host.Trim().ToLowerInvariant();
        var wanted = LabelHash(_vdk, "password", label);
        var (st, list) = await SendAsync(HttpMethod.Get, "/api/vault/items?type=password", token, null);
        if (st != 200 || list == null || !list.Value.TryGetProperty("items", out var items)) return null;

        foreach (var it in items.EnumerateArray())
        {
            if (it.TryGetProperty("label_hash", out var lh) && lh.GetString() == wanted)
            {
                try
                {
                    var plain = GcmOpen(_vdk, B64(it, "nonce"), B64(it, "ciphertext"), ItemAad("password"));
                    using var doc = JsonDocument.Parse(plain);
                    var r = doc.RootElement;
                    return (r.GetProperty("u").GetString() ?? "", r.GetProperty("p").GetString() ?? "");
                }
                catch { return null; }
            }
        }
        return null;
    }

    // ---- crypto primitives (all built into .NET) ----
    private static byte[] Pbkdf2(string password, byte[] salt, int iters) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iters, HashAlgorithmName.SHA256, 32);

    private static byte[] GcmSeal(byte[] key, byte[] nonce, byte[] plain, byte[] aad)
    {
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, ct, tag, aad);
        var combined = new byte[ct.Length + 16];         // ciphertext‖tag (the wire format)
        Buffer.BlockCopy(ct, 0, combined, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, combined, ct.Length, 16);
        return combined;
    }

    private static byte[] GcmOpen(byte[] key, byte[] nonce, byte[] combined, byte[] aad)
    {
        var ct = new byte[combined.Length - 16];
        var tag = new byte[16];
        Buffer.BlockCopy(combined, 0, ct, 0, ct.Length);
        Buffer.BlockCopy(combined, ct.Length, tag, 0, 16);
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, ct, tag, plain, aad);
        return plain;
    }

    private static string LabelHash(byte[] vdk, string type, string normalizedLabel)
    {
        using var h = new HMACSHA256(vdk);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes($"{type}:{normalizedLabel}"));
        return Convert.ToHexString(mac).ToLowerInvariant()[..32];
    }

    private static byte[] B64(JsonElement e, string name) => Convert.FromBase64String(e.GetProperty(name).GetString()!);

    private async Task<(int Status, JsonElement? Json)> SendAsync(HttpMethod m, string path, string token, string? body)
    {
        using var req = new HttpRequestMessage(m, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonElement? json = null;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch { }
        return ((int)resp.StatusCode, json);
    }
}
