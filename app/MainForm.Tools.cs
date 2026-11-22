using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soundboard;

// Selection, undo, quick rename, clean names, duplicates, "now playing" bar, window memory.
sealed partial class MainForm
{
    // ============================================================ multiple selection

    readonly HashSet<SoundEntry> _selection = new();
    SoundEntry? _anchor; // start point of a Shift+click range

    /// <summary>Click on a sound: Ctrl toggles it in the selection, Shift selects a range, plain click plays.</summary>
    void OnSoundClick(SoundEntry entry)
    {
        if (_assignMode) { SetAssignTarget(entry); return; } // "⌨ Keys": choose which sound gets the next key
        var mods = ModifierKeys;
        if ((mods & Keys.Control) != 0)
        {
            if (!_selection.Remove(entry)) _selection.Add(entry);
            _anchor = entry;
            SyncSelection();
            return;
        }
        if ((mods & Keys.Shift) != 0)
        {
            var order = _buttons.Select(b => b.Entry).ToList();
            int a = _anchor != null ? order.IndexOf(_anchor) : -1, b = order.IndexOf(entry);
            if (a < 0) a = b;
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selection.Add(order[i]);
            SyncSelection();
            return;
        }
        if (_selection.Count > 0) ClearSelection();
        _anchor = entry;
        Play(entry);
    }

    void SyncSelection()
    {
        _selection.RemoveWhere(s => !_config.Sounds.Contains(s));
        foreach (var b in _buttons) b.Selected = _selection.Contains(b.Entry);
        if (_selection.Count > 0) Toast(T("{0} sound(s) selected · right click: actions · Delete: delete · Esc: clear the selection", _selection.Count), 0);
        else Toast(null, 0);
    }

    void ClearSelection()
    {
        _selection.Clear();
        SyncSelection();
    }

    /// <summary>The sounds an action applies to: the selection if this sound is part of it, otherwise just this one.</summary>
    List<SoundEntry> Targets(SoundEntry s) =>
        _selection.Contains(s) && _selection.Count > 1 ? _config.Sounds.Where(_selection.Contains).ToList() : new List<SoundEntry> { s };

    /// <summary>Right-click menu when several sounds are selected.</summary>
    void BuildBulkMenu(ContextMenuStrip menu, List<SoundEntry> sounds)
    {
        menu.Items.Clear();
        var title = new ToolStripMenuItem(T("{0} sounds selected", sounds.Count)) { Enabled = false };
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());

        var cat = new ToolStripMenuItem(T("Category"));
        cat.DropDownItems.Add(T("None"), null, (_, _) => Apply(sounds, s => s.Category = ""));
        foreach (var c in _config.Categories)
            cat.DropDownItems.Add(c, null, (_, _) => Apply(sounds, s => s.Category = c));
        cat.DropDownItems.Add(new ToolStripSeparator());
        cat.DropDownItems.Add(T("New…"), null, (_, _) =>
        {
            string? name = NewCategory(null);
            if (name != null) Apply(sounds, s => s.Category = name);
        });
        menu.Items.Add(cat);

        var color = new ToolStripMenuItem(T("Colour"));
        foreach (var (name, argb) in Swatches)
            color.DropDownItems.Add(T(name), Swatch(argb), (_, _) => Apply(sounds, s => s.Color = argb));
        color.DropDownItems.Add(new ToolStripSeparator());
        color.DropDownItems.Add(T("Default"), null, (_, _) => Apply(sounds, s => s.Color = 0));
        menu.Items.Add(color);

        bool allFav = sounds.All(s => s.Favorite);
        menu.Items.Add(allFav ? T("Remove from favourites") : T("★ Add to favourites"), null, (_, _) => Apply(sounds, s => s.Favorite = !allFav));
        menu.Items.Add(T("Clean up names"), null, (_, _) => Apply(sounds, s => s.Name = CleanName(s.Path, s.Name)))
            .ToolTipText = T("“minecraft-door-open” becomes “Minecraft door open”");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Delete ({0})", sounds.Count), null, (_, _) => DeleteSounds(sounds));
        menu.Items.Add(T("Deselect all"), null, (_, _) => ClearSelection());
    }

    void Apply(List<SoundEntry> sounds, Action<SoundEntry> change)
    {
        foreach (var s in sounds) change(s);
        SaveConfig();
        BeginInvoke(() => { RebuildCategories(); RebuildGrid(); });
    }

    void DeleteSounds(List<SoundEntry> sounds)
    {
        foreach (var s in sounds)
        {
            _engine.Stop(s.Path);
            _config.Sounds.Remove(s);
            _engine.Forget(s.Path);
            _selection.Remove(s);
        }
        SaveConfig(); // the undo step is recorded here
        RebuildCategories();
        RebuildGrid();
    }

    // ============================================================ undo (Ctrl+Z)

    // Every save compares the sound list and categories with the previous save:
    // when they changed, the previous state goes on the undo stack. No action has to remember it.
    readonly Stack<(string Json, string Label)> _undo = new();
    string? _lastState;
    bool _restoringUndo;
    const int MaxUndo = 30;

    sealed record UndoState(List<SoundEntry> Sounds, List<string> Categories);

    string CurrentState() => JsonSerializer.Serialize(new UndoState(_config.Sounds, _config.Categories));

    void InitUndo() => _lastState = CurrentState();

    /// <summary>Called by SaveConfig.</summary>
    void RecordUndo()
    {
        if (_restoringUndo) return;
        string now = CurrentState();
        if (_lastState == null || now == _lastState) { _lastState = now; return; }

        var before = JsonSerializer.Deserialize<UndoState>(_lastState)!;
        int removed = before.Sounds.Count - _config.Sounds.Count;
        string label = removed > 0 ? T("{0} sound(s) deleted", removed) : removed < 0 ? T("{0} sound(s) added", -removed) : T("change");
        _undo.Push((_lastState, label));
        while (_undo.Count > MaxUndo)
        {
            var keep = _undo.ToArray().Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        _lastState = now;
        if (removed > 0) Toast(label.Length > 0 ? char.ToUpper(label[0]) + label[1..] : label, 8000, undo: true);
    }

    void Undo()
    {
        if (_undo.Count == 0) { Toast(T("Nothing to undo"), 2500); return; }
        var (json, label) = _undo.Pop();
        var state = JsonSerializer.Deserialize<UndoState>(json)!;
        _config.Sounds = state.Sounds;
        _config.Categories = state.Categories;
        _selection.Clear();
        _restoringUndo = true;
        try { SaveConfig(); } finally { _restoringUndo = false; }
        _lastState = json;
        _engine.ProbeDurations(_config.Sounds.Select(s => s.Path));
        RebuildCategories();
        RebuildGrid();
        Toast(T("Undone: {0}", label), 3000);
    }

    // ============================================================ message bar (bottom of the window)

    string? _toast;
    long _toastUntil;
    Panel? _toastBar;
    Label? _toastText;
    LinkLabel? _toastUndo;

    /// <summary>The message bar, just above the "now playing" bar: never hides the connection status.</summary>
    Panel CreateToastBar()
    {
        int closeSize = _iconSize - 6;                 // small square ✕, centred in the bar
        int barHeight = _iconSize + 6;
        _toastBar = new Panel { Dock = DockStyle.Bottom, Height = barHeight, Visible = false, Padding = new Padding(12, 0, 8, 0), Tag = "toast" };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Tag = "toast" };
        int textTop = (barHeight - TextRenderer.MeasureText("Ag", Font).Height) / 2;
        _toastText = new Label { AutoSize = true, Margin = new Padding(0, textTop, 12, 0), Tag = "keep-color" };
        _toastUndo = new LinkLabel { Text = T("Undo"), AutoSize = true, Margin = new Padding(0, textTop, 12, 0), Tag = "keep-color" };
        _toastUndo.LinkClicked += (_, _) => Undo();
        var close = new IconButton("✕", closeSize) { Margin = new Padding(0, (barHeight - closeSize) / 2, 0, 0) };
        close.Click += (_, _) => Toast(null, 0);
        _tips.SetToolTip(close, T("Hide"));
        row.Controls.AddRange(new Control[] { _toastText, _toastUndo, close });
        _toastBar.Controls.Add(row);
        return _toastBar;
    }

    /// <summary>
    /// Shows a message at the bottom (ms = 0: until replaced, null: hide).
    /// undo = true adds a clickable "Annuler" (same as Ctrl+Z).
    /// </summary>
    void Toast(string? text, int ms, bool undo = false)
    {
        _toast = text;
        _toastUntil = ms > 0 ? Environment.TickCount64 + ms : long.MaxValue;
        if (_toastBar == null || _toastText == null || _toastUndo == null) return;
        if (text == null) { _toastBar.Visible = false; return; }
        var p = Theme.Current;
        _toastBar.BackColor = Theme.Blend(p.Back, p.Accent, 0.10);
        foreach (Control c in _toastBar.Controls) c.BackColor = _toastBar.BackColor;
        _toastText.ForeColor = p.Text;
        _toastText.Text = text;
        _toastUndo.Visible = undo;
        _toastUndo.LinkColor = _toastUndo.ActiveLinkColor = p.Accent;
        _toastBar.Visible = true;
    }

    /// <summary>Called regularly (UpdateStatus): hides a timed message when its time is up.</summary>
    void ApplyToast()
    {
        if (_toast != null && Environment.TickCount64 > _toastUntil) Toast(null, 0);
    }

    // ============================================================ quick rename (F2) and clean names

    void RenameHoveredOrSelected()
    {
        var target = _selection.Count == 1 ? _selection.First() : _buttons.FirstOrDefault(b => b.IsHovered)?.Entry;
        if (target != null) Rename(target);
        else Toast(T("Hover over a sound (or select it), then press F2"), 2500);
    }

    static readonly Regex AutoName = new(@"^(yt1s|ytmp3free|y2mate|ssyoutube|savefrom|mp3juices)[ _\-.]?\w*(\s*\(\d+\))?$", RegexOptions.IgnoreCase);
    static readonly Regex Junk = new(@"([_\- ]?(mp3cut|cut|final|copy|copie|\(\d+\)))+$", RegexOptions.IgnoreCase);

    /// <summary>True for names made up by download sites (yt1s_nYWSz5R…): worth renaming by hand.</summary>
    static bool IsAutoName(string name) => AutoName.IsMatch(name.Trim());

    /// <summary>"minecraft-door-open" → "Minecraft door open", "naruto_shadow_clones-mp3cut" → "Naruto shadow clones".</summary>
    internal static string CleanName(string path, string? current = null)
    {
        string name = current ?? Path.GetFileNameWithoutExtension(path);
        if (IsAutoName(name)) return name; // nothing readable to clean
        string n = Junk.Replace(name, "");
        n = Regex.Replace(n, @"[_\-.]+", " ");
        n = Regex.Replace(n, @"\s{2,}", " ").Trim();
        if (n.Length == 0) return name;
        return char.ToUpper(n[0]) + n[1..];
    }

    // ============================================================ duplicates

    readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Same audio file content (size + SHA-256), whatever its name or folder.</summary>
    string? Fingerprint(string path)
    {
        if (_fingerprints.TryGetValue(path, out var f)) return f;
        try
        {
            var info = new FileInfo(path);
            using var s = File.OpenRead(path);
            byte[] hash;
            if (info.Length <= 64L * 1024 * 1024) hash = SHA256.HashData(s);
            else { var head = new byte[4 * 1024 * 1024]; int n = s.Read(head, 0, head.Length); hash = SHA256.HashData(head.AsSpan(0, n)); }
            f = info.Length + ":" + Convert.ToHexString(hash);
            _fingerprints[path] = f;
            return f;
        }
        catch { return null; }
    }

    /// <summary>Removes from `entries` the files already in the list (or twice in the batch). Returns how many.</summary>
    int DropDuplicates(List<SoundEntry> entries)
    {
        var known = new HashSet<string>(_config.Sounds.Select(s => Fingerprint(s.Path)).OfType<string>());
        int dropped = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            string? fp = Fingerprint(entries[i].Path);
            if (fp != null && !known.Add(fp)) { entries.RemoveAt(i--); dropped++; }
        }
        return dropped;
    }

    void FindDuplicates()
    {
        UseWaitCursor = true;
        var groups = _config.Sounds
            .GroupBy(s => Fingerprint(s.Path) ?? Guid.NewGuid().ToString())
            .Where(g => g.Count() > 1)
            .ToList();
        UseWaitCursor = false;
        if (groups.Count == 0) { Toast(T("No duplicates"), 3000); return; }

        _config.SelectedCategory = "";
        _search.Text = "";
        RebuildCategories();
        RebuildGrid();
        _selection.Clear();
        foreach (var g in groups) foreach (var extra in g.Skip(1)) _selection.Add(extra); // keep the first of each
        SyncSelection();
        Toast(T("{0} duplicate(s) selected (the first of each is kept) · Delete to remove them", _selection.Count), 0);
    }

    // ============================================================ "now playing" bar

    FlowLayoutPanel? _nowBar;
    string _nowKeys = "";

    FlowLayoutPanel CreateNowBar()
    {
        _nowBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2), WrapContents = true, Visible = false,
        };
        return _nowBar;
    }

    /// <summary>Called by RefreshLive: one chip per playing sound (click = pause / resume, ✕ = stop).</summary>
    void UpdateNowBar(Dictionary<string, PlayInfo> snap)
    {
        if (_nowBar == null) return;
        string keys = string.Join("|", snap.Keys);
        if (keys != _nowKeys)
        {
            _nowKeys = keys;
            _nowBar.SuspendLayout();
            foreach (Control c in _nowBar.Controls) c.Dispose();
            _nowBar.Controls.Clear();
            if (snap.Count > 0)
                _nowBar.Controls.Add(new Label { Text = T("Playing:"), AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            foreach (var path in snap.Keys)
            {
                string p = path;
                var chip = new Button { AutoSize = true, Tag = p, Margin = new Padding(2, 2, 0, 2) };
                chip.Click += (_, _) => _engine.TogglePause(p);
                _tips.SetToolTip(chip, T("Click: pause / resume"));
                var close = new IconButton("✕", _iconSize) { Margin = new Padding(0, 2, 8, 2) };
                close.Click += (_, _) => _engine.Stop(p);
                _tips.SetToolTip(close, T("Stop"));
                _nowBar.Controls.Add(chip);
                _nowBar.Controls.Add(close);
            }
            _nowBar.Visible = snap.Count > 0;
            _nowBar.ResumeLayout();
            Theme.Apply(_nowBar);
        }
        foreach (Control c in _nowBar.Controls)
        {
            if (c is not Button { Tag: string p } chip || !snap.TryGetValue(p, out var info)) continue;
            string name = _config.Sounds.FirstOrDefault(s => string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase))?.Name
                          ?? Path.GetFileNameWithoutExtension(p);
            if (name.Length > 28) name = name[..27] + "…";
            string text = $"{(info.Paused ? "⏸" : "▶")} {name}  {FormatTime(info.Position)} / {FormatTime(info.Length)}" +
                          (info.Count > 1 ? $"  ×{info.Count}" : "");
            if (chip.Text != text) chip.Text = text;
        }
    }

    static string FormatTime(double s)
    {
        int t = (int)Math.Max(0, Math.Round(s));
        return $"{t / 60}:{t % 60:00}";
    }

    // ============================================================ window size / position, always on top

    void RestoreWindow()
    {
        TopMost = _config.AlwaysOnTop;
        if (_config.WindowWidth < MinimumSize.Width || _config.WindowHeight < MinimumSize.Height) return;
        var r = new Rectangle(_config.WindowX, _config.WindowY, _config.WindowWidth, _config.WindowHeight);
        // only if the saved place is still on a screen (monitor unplugged, resolution changed…)
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(r, -60, -60)))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (_config.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    void RememberWindow()
    {
        if (WindowState == FormWindowState.Minimized) return;
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.WindowX = r.X; _config.WindowY = r.Y; _config.WindowWidth = r.Width; _config.WindowHeight = r.Height;
        _config.WindowMaximized = WindowState == FormWindowState.Maximized;
    }

    // ============================================================ keyboard

    /// <summary>Called by ProcessCmdKey. Returns true when the key was handled.</summary>
    bool HandleToolKeys(Keys keyData)
    {
        if (_search.Focused && keyData == Keys.Escape) { _search.Text = ""; ActiveControl = null; return true; }
        if (keyData == Keys.F1) { ShowHelp(); return true; }
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
        if (_search.Focused && keyData is not (Keys.Escape or (Keys.Control | Keys.Z))) return false;
        switch (keyData)
        {
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.F2: RenameHoveredOrSelected(); return true;
            case Keys.Escape when _selection.Count > 0: ClearSelection(); return true;
            case Keys.Delete when _selection.Count > 0:
                DeleteSounds(_config.Sounds.Where(_selection.Contains).ToList());
                return true;
            case Keys.Control | Keys.A:
                foreach (var b in _buttons) _selection.Add(b.Entry);
                SyncSelection();
                return true;
        }
        return false;
    }
}
