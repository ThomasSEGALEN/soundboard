using System.Diagnostics;
using Microsoft.Win32;

namespace Soundboard;

/// <summary>
/// Before its first release the app was called "SoundMix". This moves what that version left behind
/// (sounds and settings, shortcuts, startup entry, Windows entry, microphone backups) to the new names,
/// so updating keeps everything. Every step is best effort and safe to run again.
/// </summary>
static class Legacy
{
    const string OldName = "SoundMix";

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string OldUserDir => Path.Combine(AppData, OldName);
    public static string OldProgramData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), OldName);
    public static string OldModuleDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), OldName);

    // ------------------------------------------------------------ user data (no admin)

    /// <summary>
    /// %APPDATA%\SoundMix → %APPDATA%\Soundboard (sounds list, backups, imported packs),
    /// only when the new folder has no configuration yet.
    /// </summary>
    public static void MigrateUserData(string newDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(OldUserDir, "config.json")) || File.Exists(Path.Combine(newDir, "config.json"))) return;
            // the old app would write its config back into the old folder when it closes: wait until it is gone
            if (Process.GetProcessesByName(OldName).Length > 0) return;
            if (!Directory.Exists(newDir)) Directory.Move(OldUserDir, newDir); // same volume: instant
            else CopyTree(OldUserDir, newDir);
        }
        catch { /* the old app may still hold a file: tried again at the next start */ }
    }

    /// <summary>Sounds imported from packs pointed into the old folder: point them at the new one.</summary>
    public static bool FixPaths(AppConfig config, string newDir)
    {
        string oldPrefix = OldUserDir + Path.DirectorySeparatorChar;
        bool changed = false;
        foreach (var s in config.Sounds)
        {
            if (!s.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) || File.Exists(s.Path)) continue;
            string moved = Path.Combine(newDir, s.Path[oldPrefix.Length..]);
            if (File.Exists(moved)) { s.Path = moved; changed = true; }
        }
        return changed;
    }

    static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }

    // ------------------------------------------------------------ the old installed app (no admin)

    /// <summary>
    /// Closes the old app and removes its shortcuts, Windows entry and folder.
    /// Returns true when "launch with Windows" was on, so the new entry can take over.
    /// </summary>
    public static bool RemoveOldApp()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(@"Local\SoundMixApp_Quit");
            ev.Set();
        }
        catch { /* not running */ }
        foreach (var p in Process.GetProcessesByName(OldName))
        {
            try { if (!p.WaitForExit(5000)) p.Kill(); p.WaitForExit(3000); } catch { }
        }

        bool startup = false;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(OldName) is string) { startup = true; run.DeleteValue(OldName, false); }
        }
        catch { }

        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), OldName + ".lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldName + ".lnk"),
                 })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }

        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName, false); } catch { }

        string oldInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", OldName);
        try { if (Directory.Exists(oldInstall)) Directory.Delete(oldInstall, true); } catch { }
        return startup;
    }

    // ------------------------------------------------------------ microphone module (admin, from MicSetup)

    /// <summary>Microphone backups (needed to restore the original settings) move to the new data folder.</summary>
    public static void MigrateMicBackups(string newBackupDir)
    {
        try
        {
            string old = Path.Combine(OldProgramData, "backup");
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(newBackupDir);
            foreach (var f in Directory.GetFiles(old, "*.json"))
            {
                string target = Path.Combine(newBackupDir, Path.GetFileName(f));
                if (!File.Exists(target)) File.Move(f, target);
            }
        }
        catch { }
    }

    /// <summary>The old module DLL and folders, once the new DLL is registered (or the module is removed).</summary>
    public static void RemoveOldModuleFiles()
    {
        foreach (var dir in new[] { OldModuleDir, OldProgramData })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* still loaded: next time */ }
    }

    /// <summary>The old module is still on a microphone (its backups exist): uninstall must handle it.</summary>
    public static bool HasOldMicBackups =>
        Directory.Exists(Path.Combine(OldProgramData, "backup")) &&
        Directory.EnumerateFiles(Path.Combine(OldProgramData, "backup"), "*.json").Any();
}
