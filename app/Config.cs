using System.Text.Json;

namespace Soundboard;

/// <summary>What clicking a sound that is already playing does.</summary>
enum ClickMode { Default = 0, Restart = 1, PauseResume = 2, Overlay = 3, Stop = 4 }

sealed class SoundEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public Hotkey Hotkey { get; set; } = new();
    public float Volume { get; set; } = 1f;                    // per-sound gain, 0..2
    public ClickMode ClickMode { get; set; } = ClickMode.Default;
    public int Color { get; set; }                             // ARGB, 0 = theme default
    public string Category { get; set; } = "";                 // "" = no category
    public bool Favorite { get; set; }
    public double TrimStart { get; set; }                      // seconds, 0 = from the beginning
    public double TrimEnd { get; set; }                        // seconds, 0 = until the end
}

sealed class Hotkey
{
    public int Modifiers { get; set; }   // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public int Key { get; set; }         // virtual key code, 0 = none

    public bool IsSet => Key != 0;

    public override string ToString()
    {
        if (!IsSet) return "";
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add(T("Shift"));
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(((Keys)Key).ToString());
        return string.Join("+", parts);
    }
}

sealed class AppConfig
{
    public List<SoundEntry> Sounds { get; set; } = new();
    public float MicVolume { get; set; } = 0.8f;
    public float MonitorVolume { get; set; } = 0.5f;
    public bool HearSounds { get; set; } = true;      // play the sounds in your own headset
    public bool HearMyVoice { get; set; }             // hear your own voice (after noise suppression)
    public bool DenoiseEnabled { get; set; } = true;
    public int DenoiseLevel { get; set; } = 1;   // 0 = light, 1 = normal, 2 = strong
    public Hotkey StopHotkey { get; set; } = new();
    public Hotkey PauseHotkey { get; set; } = new();
    public ClickMode DefaultClickMode { get; set; } = ClickMode.Restart;
    public int ButtonSize { get; set; } = 1;                   // 0 compact, 1 normal, 2 large
    public bool DarkTheme { get; set; } = true;
    public int UiVersion { get; set; }                         // 2 = sidebar layout (dark theme switched on once)
    public string Language { get; set; } = "";                 // "" = follow Windows, "en", "fr"
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public bool WelcomeDone { get; set; }
    public List<string> Categories { get; set; } = new();
    public string SelectedCategory { get; set; } = "";         // "" = all sounds, "★" = favourites
    public bool Normalize { get; set; } = true;                // equalise sound levels
    public bool PushToTalk { get; set; }                       // sounds only reach the mic while a key is held
    public Hotkey PushToTalkKey { get; set; } = new();
    public bool AlwaysOnTop { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }                       // 0 = default size, centred
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // ------------------------------------------------------------ persistence

    /// <summary>Tests point this at a temporary folder.</summary>
    internal static string? DirOverride;

    static string Dir => DirOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundboard");
    static string FilePath => System.IO.Path.Combine(Dir, "config.json");
    public static string BackupDir => System.IO.Path.Combine(Dir, "backups");

    const int KeepBackups = 30;
    static readonly TimeSpan BackupEvery = TimeSpan.FromMinutes(10);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Set by <see cref="Load"/> when something had to be repaired (shown to the user).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadProblem { get; private set; }

    static string ReadWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < 10) { Thread.Sleep(100); } // briefly locked (antivirus, other copy saving)
        }
    }

    public static AppConfig Load()
    {
        if (DirOverride == null) Legacy.MigrateUserData(Dir); // the app was called SoundMix during its early development
        if (!File.Exists(FilePath)) return new AppConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(ReadWithRetry(FilePath), Options) ?? throw new JsonException("empty");
            if (DirOverride == null && Legacy.FixPaths(cfg, Dir)) cfg.Save();
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Never start empty over a real config: keep the unreadable file aside, use the newest backup.
            string broken = System.IO.Path.Combine(Dir, $"config.illisible-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(FilePath, broken, true); } catch { }
            // newest backup that has sounds first (an empty list is rarely the state to bring back)
            var all = ListBackups();
            foreach (var b in all.Where(b => b.Sounds > 0).Concat(all.Where(b => b.Sounds == 0)))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(b.Path), Options);
                    if (cfg == null) continue;
                    cfg.LoadProblem = T("Your configuration was unreadable: the backup from {0:dd/MM} at {0:HH:mm} was restored.", b.Time);
                    return cfg;
                }
                catch { }
            }
            return new AppConfig { LoadProblem = T("Your configuration was unreadable and no backup was found.") };
        }
    }

    /// <summary>
    /// Saves atomically (temp file + replace, retried if the file is briefly locked).
    /// Before overwriting, the previous file is kept in backups/ every 10 minutes, and always
    /// when the new version has fewer sounds (a deletion, or an older copy writing over a newer one).
    /// Throws if the file really cannot be written.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        BackupBeforeOverwrite();
        var tmp = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(this, Options);
        for (int i = 0; ; i++)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, true);
                return;
            }
            catch (Exception) when (i < 10) { Thread.Sleep(100); }
        }
    }

    void BackupBeforeOverwrite()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var last = ListBackups().FirstOrDefault();
            int onDisk = CountSounds(FilePath);
            bool due = last.Path == null || DateTime.Now - last.Time > BackupEvery;
            if (!due && onDisk <= Sounds.Count) return;
            if (onDisk == 0 && (last.Path != null && last.Sounds == 0)) return; // nothing worth keeping twice

            Directory.CreateDirectory(BackupDir);
            File.Copy(FilePath, System.IO.Path.Combine(BackupDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), true);
            foreach (var old in ListBackups().Skip(KeepBackups))
                try { File.Delete(old.Path); } catch { }
        }
        catch { /* a failed backup must not block saving */ }
    }

    static int CountSounds(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Sounds", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    public readonly record struct BackupInfo(string Path, DateTime Time, int Sounds, int Categories);

    /// <summary>Backups, newest first.</summary>
    public static List<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(BackupDir)) return new List<BackupInfo>();
        return Directory.GetFiles(BackupDir, "config-*.json")
            .Select(p =>
            {
                int sounds = 0, cats = 0;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("Sounds", out var s) && s.ValueKind == JsonValueKind.Array) sounds = s.GetArrayLength();
                    if (doc.RootElement.TryGetProperty("Categories", out var c) && c.ValueKind == JsonValueKind.Array) cats = c.GetArrayLength();
                }
                catch { }
                return new BackupInfo(p, File.GetLastWriteTime(p), sounds, cats);
            })
            .OrderByDescending(b => b.Time)
            .ToList();
    }

    /// <summary>Puts a backup back as the current configuration (the current one is backed up first).</summary>
    public static void RestoreBackup(string backupPath)
    {
        // validate first
        _ = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(backupPath), Options) ?? throw new JsonException("empty backup");
        Directory.CreateDirectory(BackupDir);
        if (File.Exists(FilePath))
            File.Copy(FilePath, System.IO.Path.Combine(BackupDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), true);
        File.Copy(backupPath, FilePath, true);
    }
}
