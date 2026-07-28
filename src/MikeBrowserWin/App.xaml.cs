using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace MikeBrowserWin;

public partial class App : Application
{
    /// <summary>A URL passed on the command line (e.g. when MikeBrowser is the default browser
    /// and the OS opens a link), used as the first tab's URL.</summary>
    public static string? StartupUrl { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupUrl = e.Args.FirstOrDefault(a =>
            a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        // First run from Downloads → install to %LocalAppData%\Programs, add a desktop shortcut,
        // register as a browser, then relaunch the installed copy.
        if (Installer.EnsureInstalled(e.Args)) { Shutdown(); return; }
        Installer.MaybeWelcome(e.Args);

        base.OnStartup(e);
    }
}

/// <summary>
/// Self-installer (no external installer framework). When MikeBrowser.exe runs from anywhere
/// other than its install location (e.g. the Downloads folder), it copies itself to
/// %LocalAppData%\Programs\MikeBrowser, creates a Desktop + Start-menu shortcut, registers as a
/// browser (so Windows can set it default), and relaunches the installed copy. Per-user → no UAC.
/// </summary>
public static class Installer
{
    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MikeBrowser");
    private static string InstalledExe => Path.Combine(InstallDir, "MikeBrowser.exe");

    public static bool EnsureInstalled(string[] args)
    {
        try
        {
            var current = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(current)) return false;
            if (string.Equals(current, InstalledExe, StringComparison.OrdinalIgnoreCase)) return false;
            if (args.Contains("--no-install")) return false;

            Directory.CreateDirectory(InstallDir);
            File.Copy(current, InstalledExe, overwrite: true);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MikeBrowser.lnk"));
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MikeBrowser.lnk"));
            RegisterBrowser();

            var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir };
            psi.ArgumentList.Add("--welcome");
            var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
            if (url != null) psi.ArgumentList.Add(url);
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public static void MaybeWelcome(string[] args)
    {
        if (!args.Contains("--welcome")) return;
        var r = MessageBox.Show(
            "MikeBrowser is installed and added to your desktop.\n\nMake it your default browser?",
            "Welcome to MikeBrowser", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes)
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
    }

    private static void CreateShortcut(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = InstalledExe;
            sc.IconLocation = InstalledExe + ",0";
            sc.Description = "MikeBrowser — your bookmarks & passwords, synced";
            sc.WorkingDirectory = InstallDir;
            sc.Save();
        }
        catch { }
    }

    private static void RegisterBrowser()
    {
        try
        {
            var cmd = $"\"{InstalledExe}\" \"%1\"";
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\MikeBrowser"))
            {
                k.SetValue(null, "MikeBrowser");
                using (var di = k.CreateSubKey("DefaultIcon")) di.SetValue(null, InstalledExe + ",0");
                using (var oc = k.CreateSubKey(@"shell\open\command")) oc.SetValue(null, cmd);
                using (var cap = k.CreateSubKey("Capabilities"))
                {
                    cap.SetValue("ApplicationName", "MikeBrowser");
                    cap.SetValue("ApplicationDescription", "Your bookmarks & passwords, synced. Light on CPU and memory.");
                    cap.SetValue("ApplicationIcon", InstalledExe + ",0");
                    using var ua = cap.CreateSubKey("URLAssociations");
                    ua.SetValue("http", "MikeBrowserHTML");
                    ua.SetValue("https", "MikeBrowserHTML");
                }
            }
            using (var ra = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                ra.SetValue("MikeBrowser", @"Software\Clients\StartMenuInternet\MikeBrowser\Capabilities");
            using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\MikeBrowserHTML"))
            {
                prog.SetValue(null, "MikeBrowser Document");
                using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue(null, InstalledExe + ",0");
                using (var oc = prog.CreateSubKey(@"shell\open\command")) oc.SetValue(null, cmd);
            }
        }
        catch { }
    }
}
