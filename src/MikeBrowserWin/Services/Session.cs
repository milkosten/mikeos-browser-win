using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MikeBrowserWin.Services;

/// <summary>
/// Local, per-user persisted state: the OAuth refresh token (encrypted at rest with
/// Windows DPAPI) and small preferences (last opened URL). Lives in %APPDATA%\MikeBrowser.
/// </summary>
public sealed class Session
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MikeBrowser");
    private static readonly string RefreshFile = Path.Combine(Dir, "refresh.bin");
    private static readonly string VdkFile = Path.Combine(Dir, "vdk.bin");
    private static readonly string PrefsFile = Path.Combine(Dir, "prefs.json");

    private sealed class Prefs { public string? LastUrl { get; set; } }

    public Session() => Directory.CreateDirectory(Dir);

    // ---- refresh token (DPAPI, CurrentUser scope) ----
    public string? LoadRefreshToken()
    {
        try
        {
            if (!File.Exists(RefreshFile)) return null;
            var enc = File.ReadAllBytes(RefreshFile);
            var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }

    public void SaveRefreshToken(string? token)
    {
        try
        {
            if (string.IsNullOrEmpty(token)) { if (File.Exists(RefreshFile)) File.Delete(RefreshFile); return; }
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(RefreshFile, enc);
        }
        catch { /* best effort */ }
    }

    // ---- vault data key (DPAPI, cached so the vault stays unlocked across launches) ----
    public byte[]? LoadVdk()
    {
        try
        {
            if (!File.Exists(VdkFile)) return null;
            return ProtectedData.Unprotect(File.ReadAllBytes(VdkFile), null, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    public void SaveVdk(byte[]? vdk)
    {
        try
        {
            if (vdk == null || vdk.Length == 0) { if (File.Exists(VdkFile)) File.Delete(VdkFile); return; }
            File.WriteAllBytes(VdkFile, ProtectedData.Protect(vdk, null, DataProtectionScope.CurrentUser));
        }
        catch { /* best effort */ }
    }

    // ---- prefs ----
    public string? LastUrl
    {
        get
        {
            try { return File.Exists(PrefsFile) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile))?.LastUrl : null; }
            catch { return null; }
        }
        set
        {
            try { File.WriteAllText(PrefsFile, JsonSerializer.Serialize(new Prefs { LastUrl = value })); }
            catch { /* best effort */ }
        }
    }
}
