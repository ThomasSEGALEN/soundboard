using Microsoft.Win32;

namespace Soundboard;

/// <summary>"Launch with Windows": a per-user Run entry that starts Soundboard hidden (--tray).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Soundboard";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string v && v.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, Command);
        else k.DeleteValue(Name, false);
    }

    /// <summary>Keeps the entry pointing at the current exe (after it moved or was updated).</summary>
    public static void Refresh()
    {
        if (!IsEnabled) return;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k != null && !Command.Equals(k.GetValue(Name) as string, StringComparison.OrdinalIgnoreCase))
            k.SetValue(Name, Command);
    }
}
