using System.Diagnostics;
using Microsoft.Win32;

namespace Soundboard;

/// <summary>
/// The exe installs itself: it copies itself to %LOCALAPPDATA%\Programs\Soundboard, creates the
/// Desktop / Start menu shortcuts and an entry in Windows "Installed apps" (per user, no admin).
/// Uninstalling from Windows runs "Soundboard.exe --uninstall-app".
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Soundboard";
    public const string QuitEventName = @"Local\SoundboardApp_Quit";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Soundboard");
    public static string InstalledExe => Path.Combine(InstallDir, "Soundboard.exe");

    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Soundboard.lnk");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Soundboard.lnk");

    public static string Version =>
        typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    // ------------------------------------------------------------ install / update

    /// <summary>Copies this exe into place (closing a running copy first) and registers it.</summary>
    public static void Install()
    {
        // updating from "SoundMix" (the early development name): close it, take over its startup entry, remove its traces
        bool oldStartup = Legacy.RemoveOldApp();
        Directory.CreateDirectory(InstallDir);
        if (!IsInstalledCopy)
        {
            CloseRunningInstances();
            for (int i = 0; ; i++)
            {
                try { File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(250); }
            }
        }

        CreateShortcut(DesktopShortcut);
        CreateShortcut(StartMenuShortcut);

        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Soundboard");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "Soundboard");
        k.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall-app");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);

        // the "launch with Windows" entry (if enabled) must point at the installed copy
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (oldStartup || run?.GetValue("Soundboard") is string) run?.SetValue("Soundboard", $"\"{InstalledExe}\" --tray");
    }

    static void CreateShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = InstalledExe;
        lnk.WorkingDirectory = InstallDir;
        lnk.IconLocation = InstalledExe + ",0";
        lnk.Description = T("Soundboard - play sounds into your microphone");
        lnk.Save();
    }

    /// <summary>Asks a running Soundboard to quit (it may be hidden near the clock), then makes sure it is gone.</summary>
    public static void CloseRunningInstances()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(QuitEventName);
            ev.Set();
        }
        catch { /* not running */ }

        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("Soundboard").Where(p => p.Id != me))
        {
            try
            {
                if (!p.WaitForExit(5000)) p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------ uninstall

    /// <summary>Full uninstall (from Windows "Installed apps"): microphone, startup, shortcuts, files.</summary>
    /// <param name="quiet">no questions, personal data kept (used for testing / scripts)</param>
    public static int UninstallInteractive(bool quiet = false)
    {
        if (!quiet && MessageBox.Show(T("Uninstall Soundboard?\n\nYour microphone gets its original settings back."), "Soundboard",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;

        CloseRunningInstances();

        // 1) put the microphones back as they were (needs admin)
        bool micChanged = MicSetup.PreviouslyInstalled().Count > 0 || MicSetup.ListMicrophones().Any(m => m.Installed)
                          || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Soundboard", "SoundboardApo.dll"))
                          || Legacy.HasOldMicBackups || Directory.Exists(Legacy.OldModuleDir);
        if (micChanged)
        {
            var (ok, msg) = MicSetup.RunElevated("uninstall");
            if (!ok)
            {
                if (quiet) return 2;
                MessageBox.Show(T("The microphone module could not be removed:") + "\n" + msg + "\n\n" +
                                T("Soundboard was not uninstalled. Try again and accept the administrator prompt."),
                    "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        // 2) startup entry, shortcuts, Windows entry
        try { Startup.Set(false); } catch { }
        foreach (var lnk in new[] { DesktopShortcut, StartMenuShortcut })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // 3) personal data, only if asked
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundboard");
        if (!quiet && Directory.Exists(data) && MessageBox.Show(
                T("Also delete your sound list, your settings and the imported packs?\n\n(Your original audio files are never deleted.)"),
                "Soundboard", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            try { Directory.Delete(data, true); } catch { }
        }

        // 4) the exe cannot delete itself while running: a short-lived cmd removes the folder once we exit
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(), // not inside the folder being removed
        });

        if (!quiet) MessageBox.Show(T("Soundboard has been uninstalled."), "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}
