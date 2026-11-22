// Engine tests without a sound card: run with  dotnet run --project tests
using System.IO.Compression;
using NAudio.Wave;
using Soundboard;

Loc.Init("en"); // the checks below look for the English texts

int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (!ok) failed++;
}

// ---- language: French texts come from the table, English stays as written, {0} is filled in
Loc.Init("fr");
Check("Language: French translation", Loc.T("Add sounds") == "Ajouter des sons" && Loc.T("{0} sound(s) deleted", 3) == "3 son(s) supprimé(s)");
Check("Language: unknown text stays in English", Loc.T("no such text") == "no such text");
Loc.Init("en");
Check("Language: English", Loc.T("Add sounds") == "Add sounds");

string dir = Path.Combine(Path.GetTempPath(), "soundboard-tests");
Directory.CreateDirectory(dir);
string Sine(string name, double seconds, float amp)
{
    string p = Path.Combine(dir, name);
    using var w = new WaveFileWriter(p, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    for (int i = 0; i < (int)(seconds * 48000); i++)
    {
        float s = (float)(amp * Math.Sin(2 * Math.PI * 440 * i / 48000));
        w.WriteSample(s); w.WriteSample(s);
    }
    return p;
}

string oneSec = Sine("one.wav", 1.0, 0.5f);
string other = Sine("other.wav", 1.0, 0.5f);
var buf = new float[4800 * 2];

float Peak(int frames) { float m = 0; for (int i = 0; i < frames * 2; i++) m = Math.Max(m, Math.Abs(buf[i])); return m; }
float MixPeak(AudioEngine e, int frames = 4800) { e.MixInto(buf, frames); return Peak(frames); }

// ---- normalisation
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    float raw = MixPeak(e);
    var n = new AudioEngine(realtime: false) { Normalize = true };
    await n.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    float norm = MixPeak(n);
    Check("Even volume: a 0.5 sine is brought to about -16 dBFS RMS", Math.Abs(norm - 0.16f * MathF.Sqrt(2)) < 0.01f && Math.Abs(raw - 0.5f) < 0.01f,
        $"raw {raw:F3}, evened {norm:F3}");
}

// ---- stop fades out (no click) and is reported as stopped immediately
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    MixPeak(e, 480);
    e.Stop(oneSec);
    Check("Stop: the sound leaves the display at once", !e.Snapshot().ContainsKey(oneSec));
    e.MixInto(buf, 4800);
    float first = 0, last = 0;
    for (int i = 0; i < 480; i++) first = Math.Max(first, Math.Abs(buf[i * 2]));
    for (int i = 4800 - 480; i < 4800; i++) last = Math.Max(last, Math.Abs(buf[i * 2]));
    Check("Stop: fade (the volume goes down gradually)", first > 0.4f && last < first * 0.3f, $"start {first:F2}, end {last:F2}");
    MixPeak(e, 4800);
    Check("Stop: full silence after the fade", MixPeak(e) == 0f);
    Check("Stop: sound removed", e.PlayingCount == 0);
}

// ---- pause / resume
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    MixPeak(e, 4800);
    double before = e.Snapshot()[oneSec].Position;
    e.TogglePause(oneSec);
    Check("Pause: shown as paused at once", e.Snapshot()[oneSec].Paused);
    MixPeak(e, 4800); MixPeak(e, 4800);
    double paused = e.Snapshot()[oneSec].Position;
    Check("Pause: silence and frozen position", MixPeak(e) == 0f && Math.Abs(e.Snapshot()[oneSec].Position - paused) < 1e-9);
    e.TogglePause(oneSec);
    float resumed = MixPeak(e);
    Check("Resume: the sound comes back from where it was", resumed > 0.4f && e.Snapshot()[oneSec].Position > paused && paused - before < 0.2,
        $"paused at {paused:F2}s");
    e.TogglePause(oneSec);
    MixPeak(e); MixPeak(e);
    e.Stop(oneSec);
    MixPeak(e);
    Check("Stop while paused: sound removed", e.PlayingCount == 0);
}

// ---- preview ("Listen"): headset only, never the microphone
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    var shared = new float[4800 * 2];
    var local = new float[4800 * 2];
    await e.PreviewAsync(oneSec, 1f);
    e.MixInto(shared, local, 4800);
    float sharedPeak = shared.Max(Math.Abs), localPeak = local.Max(Math.Abs);
    Check("Preview: nothing is sent to the microphone", sharedPeak == 0f, $"micro {sharedPeak:F3}");
    Check("Preview: the sound plays in the headset", localPeak > 0.4f, $"casque {localPeak:F3}");
    Check("Preview: invisible on the tiles (no gauge, not in Playing)", !e.Snapshot().ContainsKey(oneSec));

    // a real playback of the same sound is not cut by a preview, and stays shared
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    await e.PreviewAsync(oneSec, 1f);
    e.MixInto(shared, local, 4800);
    Check("Preview: does not cut a real playback of the same sound", e.Snapshot().ContainsKey(oneSec) && shared.Max(Math.Abs) > 0.4f);

    e.StopPreviews();
    for (int i = 0; i < 3; i++) e.MixInto(shared, local, 4800);
    Check("Preview: stopped when the editor closes, the real playback goes on",
        local.Max(Math.Abs) == 0f && e.Snapshot().ContainsKey(oneSec));
}

// ---- trim
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart, startSec: 0.5, endSec: 0.6);
    var info = e.Snapshot()[oneSec];
    Check("Trim: displayed length 0.1 s", Math.Abs(info.Length - 0.1) < 0.001, $"{info.Length:F3}s");
    e.MixInto(buf, 4800);
    float played = Peak(4800);
    e.MixInto(buf, 480);
    Check("Trim: plays 0.1 s, then stops", played > 0.4f && Peak(480) == 0f && e.PlayingCount == 0);
}

// ---- click modes
{
    var e = new AudioEngine(realtime: false) { Normalize = false };
    await e.TriggerAsync(oneSec, 1f, ClickMode.Overlay);
    await e.TriggerAsync(oneSec, 1f, ClickMode.Overlay);
    Check("Overlay: two copies (×2 badge)", e.Snapshot()[oneSec].Count == 2);
    await e.TriggerAsync(oneSec, 1f, ClickMode.Restart);
    Check("Restart: a single copy shown", e.Snapshot()[oneSec].Count == 1);
    await e.TriggerAsync(oneSec, 1f, ClickMode.Stop);
    Check("Stop mode: a click stops the playing sound", !e.Snapshot().ContainsKey(oneSec));
    await e.TriggerAsync(other, 1f, ClickMode.PauseResume);
    await e.TriggerAsync(other, 1f, ClickMode.PauseResume);
    Check("Pause mode: a second click pauses", e.Snapshot()[other].Paused);
    e.TogglePauseAll();
    Check("Pause all: everything resumes when everything was paused", !e.Snapshot()[other].Paused);
    e.StopAll();
    for (int i = 0; i < 4; i++) MixPeak(e);
    Check("Stop all: nothing plays any more", e.PlayingCount == 0);
}

// ---- folder import: subfolders become categories
{
    string root = Path.Combine(dir, "folder-import");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    Directory.CreateDirectory(Path.Combine(root, "Memes", "deep"));
    Directory.CreateDirectory(Path.Combine(root, "Musique"));
    File.Copy(oneSec, Path.Combine(root, "racine.wav"));
    File.Copy(oneSec, Path.Combine(root, "Memes", "a.wav"));
    File.Copy(oneSec, Path.Combine(root, "Memes", "deep", "b.wav"));
    File.Copy(oneSec, Path.Combine(root, "Musique", "c.wav"));
    File.WriteAllText(Path.Combine(root, "Musique", "notes.txt"), "pas un son");
    var found = Library.ScanFolder(root);
    string Cat(string n) => found.First(f => Path.GetFileName(f.File) == n).Category;
    Check("Folder import: 4 sounds found, non-audio files skipped", found.Count == 4);
    Check("Folder import: categories = first subfolder",
        Cat("racine.wav") == "" && Cat("a.wav") == "Memes" && Cat("b.wav") == "Memes" && Cat("c.wav") == "Musique");
}

// ---- pack export / import round trip
{
    var sounds = new List<SoundEntry>
    {
        new() { Name = "Un", Path = oneSec, Category = "Memes", Volume = 1.5f, ClickMode = ClickMode.Overlay,
                Color = unchecked((int)0xFFE5484D), Favorite = true, TrimStart = 0.2, TrimEnd = 0.8,
                Hotkey = new Hotkey { Key = (int)Keys.F5 } },
        new() { Name = "Deux", Path = other, Category = "" },
        new() { Name = "Disparu", Path = Path.Combine(dir, "nexiste-pas.wav") },
    };
    string pack = Path.Combine(dir, "test.soundboard");
    int n = Library.Export(sounds, pack, "Mon pack");
    Check("Pack: exports existing sounds (missing file skipped)", n == 2);

    string lib = Path.Combine(dir, "library");
    if (Directory.Exists(lib)) Directory.Delete(lib, true);
    var (imported, name) = Library.Import(pack, lib);
    var un = imported.First(s => s.Name == "Un");
    Check("Pack: import, name and 2 sounds", name == "Mon pack" && imported.Count == 2);
    Check("Pack: settings kept (category, volume, mode, colour, favourite, trim)",
        un.Category == "Memes" && un.Volume == 1.5f && un.ClickMode == ClickMode.Overlay && un.Color == unchecked((int)0xFFE5484D)
        && un.Favorite && un.TrimStart == 0.2 && un.TrimEnd == 0.8);
    Check("Pack: hotkeys not exported", !un.Hotkey.IsSet);
    Check("Pack: audio files extracted and identical",
        imported.All(s => File.Exists(s.Path) && s.Path.StartsWith(lib)) &&
        File.ReadAllBytes(un.Path).SequenceEqual(File.ReadAllBytes(oneSec)));
    var (again, _) = Library.Import(pack, lib);
    Check("Pack: re-import goes to a new folder (nothing overwritten)", Path.GetDirectoryName(again[0].Path) != Path.GetDirectoryName(un.Path));

    // a malicious pack cannot write outside the library folder
    string evil = Path.Combine(dir, "evil.soundboard");
    if (File.Exists(evil)) File.Delete(evil);
    using (var z = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
    {
        z.CreateEntryFromFile(oneSec, "../../escape.wav");
        using var w = new StreamWriter(z.CreateEntry("manifest.json").Open());
        w.Write("{\"Name\":\"evil\",\"Sounds\":[{\"File\":\"../../escape.wav\",\"Name\":\"x\"}]}");
    }
    var (ev, _) = Library.Import(evil, lib);
    Check("Pack: malicious paths neutralised", ev.Count == 1 && ev[0].Path.StartsWith(lib) && !File.Exists(Path.Combine(dir, "escape.wav")));
}

// ---- config: backups, never silently empty, restore
{
    string cdir = Path.Combine(dir, "config");
    if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
    AppConfig.DirOverride = cdir;
    try
    {
        var c = new AppConfig();
        c.Categories.Add("discord");
        for (int i = 0; i < 5; i++) c.Sounds.Add(new SoundEntry { Name = $"s{i}", Path = $"C:\\s{i}.mp3", Category = "discord" });
        c.Save();
        Check("Config: first save", AppConfig.Load().Sounds.Count == 5);

        c.Sounds.Add(new SoundEntry { Name = "s5", Path = "C:\\s5.mp3" });
        c.Save(); // < 10 min and more sounds: no new backup needed
        int backupsAfterAdd = AppConfig.ListBackups().Count;

        var stale = new AppConfig(); // e.g. an older copy that never saw these sounds
        stale.Categories.Add("test");
        stale.Save();
        var backups = AppConfig.ListBackups();
        Check("Config: a version with fewer sounds backs up the old one first",
            backups.Count == backupsAfterAdd + 1 && backups[0].Sounds == 6 && backups[0].Categories == 1,
            $"{backups.Count} backup(s), latest: {backups.FirstOrDefault().Sounds} sounds");

        AppConfig.RestoreBackup(backups[0].Path);
        var restored = AppConfig.Load();
        Check("Config: restore (6 sounds, discord category)", restored.Sounds.Count == 6 && restored.Categories.SequenceEqual(new[] { "discord" }));
        Check("Config: the replaced state is kept too", AppConfig.ListBackups().Any(b => b.Sounds == 0 && b.Categories == 1));

        File.WriteAllText(Path.Combine(cdir, "config.json"), "{ ceci n'est pas du json");
        var repaired = AppConfig.Load();
        Check("Unreadable config: the latest backup is loaded, not an empty list",
            repaired.Sounds.Count > 0 && repaired.LoadProblem != null, $"{repaired.Sounds.Count} sounds");
        Check("Unreadable config: the damaged file is set aside",
            Directory.GetFiles(cdir, "config.illisible-*.json").Length == 1);
    }
    finally { AppConfig.DirOverride = null; }
}

// ---- clean names
{
    (string file, string expected)[] cases =
    {
        (@"C:\d\minecraft-door-open.mp3", "Minecraft door open"),
        (@"C:\d\naruto_shadow_clones-mp3cut.mp3", "Naruto shadow clones"),
        (@"C:\d\bamboo-hit-anime-bonk-sound.mp3", "Bamboo hit anime bonk sound"),
        (@"C:\d\WOW.mp3", "WOW"),
        (@"C:\d\Naruto Shippuden - Girei (Pains Theme Song).mp3", "Naruto Shippuden Girei (Pains Theme Song)"),
        (@"C:\d\yt1s_nYWSz5R.mp3", "yt1s_nYWSz5R"),          // made-up name: kept, to be renamed by hand
        (@"C:\d\yt1s_nYWSz5R (1).mp3", "yt1s_nYWSz5R (1)"),
        (@"C:\d\ytmp3free_BaQB7oM.mp3", "ytmp3free_BaQB7oM"),
    };
    var wrong = cases.Where(c => MainForm.CleanName(c.file) != c.expected)
                     .Select(c => $"{Path.GetFileName(c.file)} -> {MainForm.CleanName(c.file)}").ToList();
    Check("Clean names when adding", wrong.Count == 0, string.Join(" | ", wrong));
}

// ---- sound editor: a conflict warning that appears after opening never covers OK / Cancel
RunSta(() =>
{
    var data = new float[48000 * 2];
    var s = new SoundEntry { Name = "Test" };
    string longOwner = "A really, really long sound name to check that the warning wraps onto a second line without hiding anything";
    var ed = new SoundEditor(s, data, Array.Empty<string>(), "Restart", Array.Empty<(string, int)>(),
        (_, _, _) => { }, _ => new Hotkey { Key = (int)Keys.NumPad1 }, k => k.IsSet ? longOwner : null)
    { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
    ed.Show();
    Application.DoEvents();
    int heightBefore = ed.ClientSize.Height;
    var grid = ed.Controls.OfType<TableLayoutPanel>().First();
    var row = ed.Controls.OfType<FlowLayoutPanel>().First(p => p.Dock == DockStyle.Bottom);

    ed.ClickKeyButton(); // picks NumPad1, which "belongs" to another sound
    for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
    // what is drawn: the last row of content must end above the buttons
    int contentBottom = grid.Controls.Cast<Control>().Max(c => grid.Top + c.Bottom);
    bool warned = AllText(ed).Contains("already used");
    Check("Editor: the “key already used” warning shows", warned);
    Check("Editor: the warning does not overlap OK / Cancel", contentBottom <= row.Top,
        $"content down to {contentBottom}, buttons from {row.Top}");
    var cut = Overflows(ed);
    Check("Editor: nothing overflows after the warning", cut.Count == 0, string.Join(" | ", cut));
    Check("Editor: the window grew just enough (two-line warning)", ed.ClientSize.Height >= heightBefore,
        $"{heightBefore} → {ed.ClientSize.Height}");
    using var bmp = new Bitmap(ed.Width, ed.Height);
    ed.DrawToBitmap(bmp, new Rectangle(Point.Empty, ed.Size));
    bmp.Save(Path.Combine(dir, "dialog-editor-conflict.png"));
    ed.Close();
});

static string AllText(Control c) => c.Text + " " + string.Join(" ", c.Controls.Cast<Control>().Select(AllText));

// ---- "⌨ Keys" mode: click / key / next sound, conflicts resolved
if (args.Contains("--ui")) RunSta(() =>
{
    string cdir = Path.Combine(dir, "keys-config");
    if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
    AppConfig.DirOverride = cdir;
    MainForm.SuppressDialogs = true; // a conflict then means "take the key away from the other one"
    try
    {
        var seed = new AppConfig { MinimizeToTray = false, WelcomeDone = true, PauseHotkey = new Hotkey { Key = (int)Keys.F9 } };
        seed.Sounds.Add(new SoundEntry { Name = "A", Path = oneSec, Hotkey = new Hotkey { Key = (int)Keys.NumPad1 } });
        seed.Sounds.Add(new SoundEntry { Name = "B", Path = other });
        seed.Sounds.Add(new SoundEntry { Name = "C", Path = oneSec + "x", Hotkey = new Hotkey { Key = (int)Keys.F5 } });
        seed.Save();

        var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        main.Show();
        Application.DoEvents();
        main.TestSetAssignMode(true);
        Check("Keys mode: starts on the first sound", main.TestAssignTarget == "A", main.TestAssignTarget ?? "none");

        main.TestPressKey(Keys.NumPad2);
        var cfg = main.TestConfig;
        Check("Keys mode: a key is assigned and the next sound is picked",
            cfg.Sounds[0].Hotkey.Key == (int)Keys.NumPad2 && main.TestAssignTarget == "B", main.TestAssignTarget ?? "none");

        main.TestPressKey(Keys.Control | Keys.F5); // a combination
        Check("Keys mode: combinations (Ctrl+…) work",
            cfg.Sounds[1].Hotkey.Key == (int)Keys.F5 && cfg.Sounds[1].Hotkey.Modifiers == 2 && main.TestAssignTarget == "C");

        main.TestPressKey(Keys.F9); // already used by Pause
        Check("Keys mode: conflict with Pause → the sound gets the key, Pause loses it",
            cfg.Sounds[2].Hotkey.Key == (int)Keys.F9 && !cfg.PauseHotkey.IsSet);

        main.TestPressKey(Keys.Delete);
        Check("Keys mode: Delete clears the key", !cfg.Sounds[2].Hotkey.IsSet);

        // capture of the buttons in the mode
        main.Location = new Point(-4000, -4000);
        using (var bmp = new Bitmap(main.Width, main.Height))
        {
            main.DrawToBitmap(bmp, new Rectangle(Point.Empty, main.Size));
            bmp.Save(Path.Combine(dir, "keys-mode.png"));
        }

        main.TestPressKey(Keys.Escape);
        var saved = AppConfig.Load();
        Check("Keys mode: Esc leaves the mode and everything is saved",
            saved.Sounds[0].Hotkey.Key == (int)Keys.NumPad2 && saved.Sounds[1].Hotkey.Key == (int)Keys.F5 && !saved.Sounds[2].Hotkey.IsSet);
        main.QuitApp();
        Application.DoEvents();
    }
    finally { AppConfig.DirOverride = null; MainForm.SuppressDialogs = false; }
});

// ---- part 3: message bar with "Undo", help window, top bar
if (args.Contains("--ui")) RunSta(() =>
{
    string cdir = Path.Combine(dir, "part3-config");
    if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
    AppConfig.DirOverride = cdir;
    MainForm.SuppressDialogs = true;
    try
    {
        var seed = new AppConfig { MinimizeToTray = false, WelcomeDone = true };
        seed.Categories.Add("discord");
        foreach (var n in new[] { "Dexter meme", "Minecraft door open", "Lego die" })
            seed.Sounds.Add(new SoundEntry { Name = n, Path = Path.Combine(dir, n + ".wav"), Category = "discord" });
        seed.Save();
        var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(920, 620) };
        main.Show();
        Application.DoEvents();

        Check("Top bar: compact noise filter", main.TestDenoiseLabel.StartsWith("Noise filter: "), main.TestDenoiseLabel);

        // every element of the top bar has the same height (text buttons, ⌨, search field, ?)
        IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(k => new[] { k }.Concat(All(k)));
        var addBtn = All(main).OfType<Button>().First(b => b.Text.Contains("Add"));
        var bar = addBtn.Parent!;
        var heights = bar.Controls.Cast<Control>().Where(c => c.Visible).Select(c => (Name: c.Text.Length > 0 ? c.Text : c.GetType().Name, c.Height)).ToList();
        int h0 = addBtn.Height;
        Check("Top bar: every button has the same height (search included)",
            heights.All(x => Math.Abs(x.Height - h0) <= 1), string.Join(", ", heights.Select(x => $"{x.Name}={x.Height}")));
        var pauseBtn = All(main).OfType<IconButton>().First(b => b.Text == Icons.Pause);
        Check("Bottom bar: Pause is square, as tall as the buttons", pauseBtn.Width == pauseBtn.Height && Math.Abs(pauseBtn.Height - h0) <= 1,
            $"{pauseBtn.Width}x{pauseBtn.Height}, boutons {h0}");

        // side bar: All + the category + "New category", full width, the selected one highlighted
        var rows = All(main).OfType<SidebarItem>().Where(i => i.Parent is FlowLayoutPanel { FlowDirection: FlowDirection.TopDown, AutoScroll: true }).ToList();
        Check("Side bar: All, discord and New category",
            rows.Select(r => r.Text).SequenceEqual(new[] { "All", "discord", "New category" }), string.Join(" | ", rows.Select(r => r.Text)));
        Check("Side bar: sound counters", rows[0].Count == "3" && rows[1].Count == "3", $"{rows[0].Count}/{rows[1].Count}");
        Check("Side bar: rows take the full width", rows.All(r => r.Width == r.Parent!.ClientSize.Width && r.Width > 150),
            string.Join(", ", rows.Select(r => r.Width)));
        Check("Side bar: All selected at start", rows[0].Selected && !rows[1].Selected);
        Check("Side bar: Settings and Help at the bottom", All(main).OfType<SidebarItem>().Any(i => i.Text.Contains("Settings")) &&
            All(main).OfType<SidebarItem>().Any(i => i.Text.Contains("Help")));
        Check("Side bar: every row has an icon (labels line up)", All(main).OfType<SidebarItem>().All(i => i.Icon.Length > 0));
        Check("New look: dark theme switched on once", main.TestConfig.DarkTheme && main.TestConfig.UiVersion == 2);

        main.TestToolKey(Keys.Control | Keys.A);
        main.TestToolKey(Keys.Delete);
        Application.DoEvents();
        var t = main.TestToast;
        Check("Message: “3 sound(s) deleted” with an Undo link", t.Visible && t.Text.Contains("3 sound") && t.UndoShown, t.Text);
        var closeX = All(main).OfType<IconButton>().First(b => b.Text == "✕" && b.Parent?.Parent is Panel { Tag: "toast" });
        var toastBar = (Panel)closeX.Parent!.Parent!;
        int gapTop = closeX.Top, gapBottom = toastBar.Height - closeX.Bottom;
        Check("Message: the ✕ is square and vertically centred", closeX.Width == closeX.Height && Math.Abs(gapTop - gapBottom) <= 1,
            $"{closeX.Width}x{closeX.Height}, marges {gapTop}/{gapBottom}");
        using (var bmp = new Bitmap(main.Width, main.Height))
        {
            main.DrawToBitmap(bmp, new Rectangle(Point.Empty, main.Size));
            bmp.Save(Path.Combine(dir, "main-toast.png"));
        }
        main.TestUndo();
        Application.DoEvents();
        Check("Message: Undo brings the 3 sounds back", main.TestConfig.Sounds.Count == 3);
        using (var bmp = new Bitmap(main.Width, main.Height))
        {
            main.DrawToBitmap(bmp, new Rectangle(Point.Empty, main.Size));
            bmp.Save(Path.Combine(dir, "main-topbar.png"));
        }

        var help = main.BuildHelpForm();
        help.StartPosition = FormStartPosition.Manual;
        help.Location = new Point(-3000, -3000);
        help.Show();
        Application.DoEvents();
        var cut = Overflows(help);
        Check("Help: everything readable, nothing overflows", cut.Count == 0, string.Join(" | ", cut));
        using (var bmp = new Bitmap(help.Width, help.Height))
        {
            help.DrawToBitmap(bmp, new Rectangle(Point.Empty, help.Size));
            bmp.Save(Path.Combine(dir, "help.png"));
        }
        help.Close();
        main.QuitApp();
        Application.DoEvents();

        // light theme: same window rendered for a visual check (picture kept in the temp folder)
        var light = AppConfig.Load();
        light.DarkTheme = false;
        light.UiVersion = 2;
        light.Sounds[0].Color = unchecked((int)0xFF8E4EC6); // one coloured sound, the others default
        light.Save();
        var mainLight = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(920, 620) };
        mainLight.Show();
        Application.DoEvents();
        Check("Light theme: stays light (no forced switch)", !Theme.IsDark);
        using (var bmp = new Bitmap(mainLight.Width, mainLight.Height))
        {
            mainLight.DrawToBitmap(bmp, new Rectangle(Point.Empty, mainLight.Size));
            bmp.Save(Path.Combine(Path.GetTempPath(), "soundboard-light.png"));
        }
        mainLight.QuitApp();
        Application.DoEvents();
        Theme.Set(true);
    }
    finally { AppConfig.DirOverride = null; MainForm.SuppressDialogs = false; }
});

// ---- waveform rendering (saved for a visual check)
{
    var data = new float[48000 * 2 * 2];
    for (int i = 0; i < data.Length / 2; i++)
    {
        double env = Math.Exp(-((i % 24000) / 6000.0));       // two "hits" that fade out
        float v = (float)(0.8 * env * Math.Sin(2 * Math.PI * 220 * i / 48000));
        data[i * 2] = data[i * 2 + 1] = v;
    }
    var wave = new WaveformView(data) { Size = new Size(532, 110) };
    wave.SetRange(0.4, 1.6);
    using var bmp = new Bitmap(wave.Width, wave.Height);
    wave.DrawToBitmap(bmp, new Rectangle(Point.Empty, wave.Size));
    string png = Path.Combine(dir, "waveform.png");
    bmp.Save(png);
    Check("Waveform: rendering", File.Exists(png) && bmp.GetPixel(wave.Width / 2, wave.Height / 2).ToArgb() != Theme.Current.Surface.ToArgb(), png);
}

// ---- dialogs: buttons must be large enough for their label
{
    var data = new float[48000 * 2 * 2];
    for (int i = 0; i < data.Length / 2; i++)
    {
        float v = (float)(0.7 * Math.Exp(-((i % 24000) / 5000.0)) * Math.Sin(2 * Math.PI * 330 * i / 48000));
        data[i * 2] = data[i * 2 + 1] = v;
    }
    var editSound = new SoundEntry { Name = "Minecraft door open", Category = "discord", Volume = 0.8f, Color = unchecked((int)0xFF0090FF),
                                     Hotkey = new Hotkey { Key = (int)Keys.NumPad1 }, TrimStart = 0.3, TrimEnd = 1.6 };
    var forms = new (string Name, Form Form)[]
    {
        ("editor", new SoundEditor(editSound, data, new[] { "discord", "Memes" }, "Restart",
            new[] { ("Rouge", unchecked((int)0xFFE5484D)), ("Bleu", unchecked((int)0xFF0090FF)), ("Vert", unchecked((int)0xFF30A46C)) },
            (_, _, _) => { }, _ => null, k => k.Key == (int)Keys.NumPad1 ? "Dexter meme" : null)),
        ("text", new TextDialog("Renommer", "Minecraft door open")),
    };
    foreach (var (name, f) in forms)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-3000, -3000); // off screen
        f.Show();
        Application.DoEvents();
        var buttons = new List<Button>();
        void Walk(Control c) { foreach (Control k in c.Controls) { if (k is Button b && (b.Text is "OK" or "Cancel")) buttons.Add(b); Walk(k); } }
        Walk(f);
        bool fits = buttons.Count == 2 && buttons.All(b =>
            TextRenderer.MeasureText(b.Text, b.Font).Width + 12 <= b.Width &&
            b.Height >= b.Font.Height + 8 &&
            f.ClientRectangle.Contains(f.RectangleToClient(b.RectangleToScreen(b.ClientRectangle))));
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(Path.Combine(dir, $"dialog-{name}.png"));
        Check($"Window “{name}”: OK / Cancel readable and inside the window", fits,
            string.Join(", ", buttons.Select(b => $"{b.Text} {b.Width}x{b.Height}")));
        var cut = Overflows(f);
        Check($"Window “{name}”: nothing overflows or is cut", cut.Count == 0, string.Join(" | ", cut));
        f.Close();
    }
}

// ---- settings window: every page renders (captures saved for a visual check)
if (args.Contains("--ui")) RunSta(() =>
{
    string cdir = Path.Combine(dir, "ui-config");
    if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
    AppConfig.DirOverride = cdir; // never touch the real configuration
    try
    {
        var seed = new AppConfig { MinimizeToTray = false, WelcomeDone = true };
        seed.Categories.Add("discord");
        seed.Sounds.Add(new SoundEntry { Name = "Minecraft door open", Path = oneSec, Category = "discord", Hotkey = new Hotkey { Key = (int)Keys.NumPad1 } });
        seed.Sounds.Add(new SoundEntry { Name = "Dexter meme", Path = other, Category = "discord", Hotkey = new Hotkey { Key = (int)Keys.NumPad1 } });
        seed.PauseHotkey = new Hotkey { Key = (int)Keys.F9 };
        seed.Save();

        MainForm.SuppressDialogs = true;
        var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        main.Show();
        Application.DoEvents();
        // placement: the settings window opens centred on Soundboard (invisible windows, no flash on screen)
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            main.Opacity = 0;
            main.WindowState = FormWindowState.Normal;
            main.Bounds = new Rectangle(area.Left + 200, area.Top + 120, 1000, 700);
            Application.DoEvents();
            main.OpenSettings();
            var s = main.SettingsWindow!;
            s.Opacity = 0;
            Application.DoEvents();
            var mc = new Point(main.Left + main.Width / 2, main.Top + main.Height / 2);
            var sc = new Point(s.Left + s.Width / 2, s.Top + s.Height / 2);
            Check("Settings: opened centred on the Soundboard window", Math.Abs(mc.X - sc.X) <= 2 && Math.Abs(mc.Y - sc.Y) <= 2,
                $"Soundboard centre {mc}, Settings centre {sc}");
            s.Close();
            Application.DoEvents();
            main.Location = new Point(-4000, -4000);
        }

        foreach (var page in new[] { "General", "Playback", "Microphone", "Hotkeys", "Backup", "About" })
        {
            main.OpenSettings(page);
            var sw = main.SettingsWindow!;
            sw.Location = new Point(-3000, -3000);
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
            using var bmp = new Bitmap(sw.Width, sw.Height);
            sw.DrawToBitmap(bmp, new Rectangle(Point.Empty, sw.Size));
            string png = Path.Combine(dir, $"settings-{page}.png");
            bmp.Save(png);
            Check($"Settings “{page}”: rendering", File.Exists(png));
            var cutS = Overflows(sw);
            Check($"Settings “{page}”: nothing overflows or is cut", cutS.Count == 0, string.Join(" | ", cutS));
            Console.Out.Flush();
        }
        main.SettingsWindow?.Close();
        main.QuitApp();
        Application.DoEvents();
    }
    finally { AppConfig.DirOverride = null; }
});

Console.WriteLine(failed == 0 ? "\nAll tests passed." : $"\n{failed} test(s) failed.");
return failed == 0 ? 0 : 1;


// WinForms windows need an STA thread; the UI part runs there with a time limit.
void RunSta(Action body)
{
    Exception? error = null;
    var th = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
    th.SetApartmentState(ApartmentState.STA);
    th.IsBackground = true;
    th.Start();
    if (!th.Join(TimeSpan.FromSeconds(90))) { Console.WriteLine("FAIL  interface: timed out"); failed++; }
    if (error != null) { Console.WriteLine("FAIL  interface : " + error); failed++; }
}

// Visible controls that run past the window's client area (or past a scrolling parent's width),
// and fixed-size labels whose text does not fit.
static List<string> Overflows(Form form)
{
    var problems = new List<string>();
    var client = form.ClientRectangle;
    void Walk(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;
            var r = form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
            // inside an AutoScroll panel, only the width matters (it scrolls vertically)
            bool scrolls = false;
            for (var a = c.Parent; a != null && a != form; a = a.Parent) if (a is ScrollableControl { AutoScroll: true }) scrolls = true;
            if (r.Right > client.Right || (!scrolls && r.Bottom > client.Bottom))
                problems.Add($"{c.GetType().Name} “{Short(c.Text)}” overflows ({r.Right}>{client.Right} or {r.Bottom}>{client.Bottom})");
            if (c is Label { AutoSize: false } l && l.PreferredWidth > l.Width + 2 && l.MaximumSize.Width == 0)
                problems.Add($"Label “{Short(l.Text)}” cut ({l.PreferredWidth}>{l.Width})");
            Walk(c);
        }
    }
    Walk(form);
    return problems;
}

static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s;