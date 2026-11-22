using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Soundboard;

sealed partial class MainForm : Form
{
    const int WM_HOTKEY = 0x0312;
    const int MOD_NOREPEAT = 0x4000;
    const int StopHotkeyId = 1;
    const int PauseHotkeyId = 2;
    const int SoundHotkeyBase = 100;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);


    static readonly (string Name, ClickMode Mode, string Hint)[] ClickModes =
    {
        ("Restart", ClickMode.Restart, "Starts the sound again from the beginning"),
        ("Pause", ClickMode.PauseResume, "Pauses, then resumes"),
        ("Overlay", ClickMode.Overlay, "Plays again on top (spam)"),
        ("Stop", ClickMode.Stop, "Stops the sound"),
    };

    static readonly (string Name, int Argb)[] Swatches =
    {
        ("Red", unchecked((int)0xFFE5484D)), ("Orange", unchecked((int)0xFFF76B15)), ("Yellow", unchecked((int)0xFFFFC53D)),
        ("Green", unchecked((int)0xFF30A46C)), ("Teal", unchecked((int)0xFF12A594)), ("Blue", unchecked((int)0xFF0090FF)),
        ("Purple", unchecked((int)0xFF8E4EC6)), ("Pink", unchecked((int)0xFFD6409F)), ("Grey", unchecked((int)0xFF8B8D98)),
    };

    const string FavoritesTab = "★";

    static readonly Size[] ButtonSizes = { new(124, 50), new(168, 74), new(214, 100) };

    readonly AppConfig _config = AppConfig.Load();
    readonly AudioEngine _engine = new();
    readonly List<int> _registeredIds = new();
    readonly List<SoundButton> _buttons = new();
    readonly ToolTip _tips = new();
    CheckBox _hearSoundsBox = null!, _hearVoiceBox = null!;

    readonly FlowLayoutPanel _grid;
    readonly FlowLayoutPanel _categoryBar;
    readonly Label _status;
    readonly LevelMeter _meter;
    readonly TextBox _search;
    int _iconSize = 29; // height of a text button: size of every square icon button
    readonly Button _denoiseButton;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _refresh;
    readonly Icon _appIcon;
    int _tick;
    bool _reallyQuit;

    bool _startHidden;

    /// <summary>startHidden: launched with Windows (--tray), only the icon near the clock shows.</summary>
    public MainForm(bool startHidden = false, bool firstRun = false)
    {
        _startHidden = startHidden;
        Shown += (_, _) => BeginInvoke(() => Welcome(firstRun));
        _engine.Normalize = _config.Normalize;
        try { Startup.Refresh(); } catch { }
        Text = "Soundboard";
        ClientSize = new Size(900, 580);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Font = Theme.UiFont(9.5f);
        _appIcon = LoadAppIcon(SystemInformation.SmallIconSize); // crisp in the notification area
        Icon = LoadAppIcon(null);                                // all sizes: title bar / taskbar / Alt+Tab pick theirs
        if (_config.UiVersion < 2)
        {
            // new look (dark "gaming" style): switched on once; the light theme stays in the settings
            _config.DarkTheme = true;
            _config.UiVersion = 2;
        }
        Theme.Set(_config.DarkTheme);

        // ---- top bar: actions, search, noise suppression, options
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 10, 10, 2), WrapContents = true,
        };
        // primary action in the accent colour
        var add = new Button { Text = T("＋ Add"), AutoSize = true, Tag = "accent" };
        var addMenu = new ContextMenuStrip();
        addMenu.Items.Add(T("Files…"), null, (_, _) => AddSoundsDialog());
        addMenu.Items.Add(T("Folder…"), null, (_, _) => AddFolderDialog());
        addMenu.Items.Add(T("Pack…"), null, (_, _) => ImportPackDialog());
        add.Click += (_, _) => addMenu.Show(add, new Point(0, add.Height));
        _tips.SetToolTip(add, T("Files, folder or pack\nAlso: drag and drop, or Ctrl+V"));
        // every square icon button (⏯, ⏹, ✕) and the search field use the height of a text button
        int std = IconButton.StandardHeight(Font);
        _iconSize = std;

        // search field the height of the buttons, ✕ inside; Ctrl+F to get there, Esc to clear
        var searchField = new SearchField(T("Search… (Ctrl+F)"), 260, std) { Margin = new Padding(3, 3, 10, 3) };
        _search = searchField.Box;
        _tips.SetToolTip(searchField.ClearButton, T("Clear the search"));
        _search.TextChanged += (_, _) => RebuildGrid();

        // noise suppression: one compact button that opens its choices
        _denoiseButton = new Button { AutoSize = true, Margin = new Padding(14, 3, 3, 3) };
        _denoiseButton.Click += (_, _) => DenoiseMenu().Show(_denoiseButton, new Point(0, _denoiseButton.Height));
        _tips.SetToolTip(_denoiseButton,
            T("Filters keyboard and background noise out of your voice.\nThe sounds are never filtered.\nIn Discord: Noise Suppression → None."));
        SetDenoise(DenoiseIndex, save: false);

        top.Controls.AddRange(new Control[] { searchField, add, CreateAssignToggle(), _denoiseButton });

        // ---- side bar: name, categories, settings and help at the bottom
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 210, Padding = new Padding(8, 6, 8, 8), Tag = "sidebar" };
        var brand = new Label
        {
            Text = "Soundboard", Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont(13f, FontStyle.Bold), Padding = new Padding(6, 0, 0, 0),
        };
        _categoryBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        _categoryBar.ClientSizeChanged += (_, _) => FitSidebar(_categoryBar);
        var sideBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 6, 0, 0),
        };
        var settingsItem = new SidebarItem(T("Settings")) { Icon = Icons.Settings, Font = Font, Width = sidebar.Width - sidebar.Padding.Horizontal };
        settingsItem.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        var helpItem = new SidebarItem(T("Help"), "F1") { Icon = Icons.Help, Font = Font, Width = settingsItem.Width };
        helpItem.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowHelp(); };
        _tips.SetToolTip(helpItem, T("Every gesture and shortcut"));
        sideBottom.Controls.Add(settingsItem);
        sideBottom.Controls.Add(helpItem);
        sidebar.Controls.Add(_categoryBar);
        sidebar.Controls.Add(sideBottom);
        sidebar.Controls.Add(brand);

        // ---- sound grid
        _grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 8, 10, 0), AllowDrop = true,
        };
        _grid.DragEnter += OnFilesDragEnter;
        _grid.DragDrop += OnFilesDragDrop;
        _grid.ClientSizeChanged += (_, _) => FitButtons();

        // ---- bottom bar: mic status, pause / stop, one volume for everyone, listening, live meter
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 78, ColumnCount = 7, RowCount = 2, Padding = new Padding(12, 6, 12, 6), Tag = "sidebar" };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold), Tag = "keep-color", Margin = new Padding(0, 0, 12, 0) };
        _status.Click += (_, _) => { if ("keep-color fixable".Equals(_status.Tag)) OpenMicDialog(); };
        var pause = new IconButton(Icons.Pause, std) { Font = Theme.IconFont, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0) };
        pause.Click += (_, _) => _engine.TogglePauseAll();
        _tips.SetToolTip(pause, T("Pause: pauses every sound, or resumes them"));
        var stop = new IconButton(Icons.Stop, std) { Font = Theme.IconFont, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 14, 0) };
        stop.Click += (_, _) => _engine.StopAll();
        _tips.SetToolTip(stop, T("Stop: stops every sound"));
        var volLabel = new Label { Text = T("Volume"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0), Tag = "keep-color", ForeColor = Theme.Current.SubText };
        var volBar = new FlatSlider { Minimum = 0, Maximum = 150, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        volBar.Value = Pct(_config.MicVolume);
        var volPct = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        _tips.SetToolTip(volBar, T("The same level for you and for everyone else"));
        _meter = new LevelMeter { Dock = DockStyle.Fill, Margin = new Padding(10, 6, 0, 6) };
        _tips.SetToolTip(_meter, T("Voice: your filtered mic\nSent: your voice + the sounds"));
        var hearSounds = _hearSoundsBox = new CheckBox { Text = T("Hear the sounds"), Checked = _config.HearSounds, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 24, 0) };
        _tips.SetToolTip(hearSounds, T("The sounds in your headset. No effect on what others hear."));
        var hearVoice = _hearVoiceBox = new CheckBox { Text = T("Hear my voice"), Checked = _config.HearMyVoice, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
        _tips.SetToolTip(hearVoice, T("Your filtered voice, as others hear it"));
        var checks = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        checks.Controls.Add(hearSounds);
        checks.Controls.Add(hearVoice);
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(pause, 1, 0);
        bottom.Controls.Add(stop, 2, 0);
        bottom.Controls.Add(volLabel, 3, 0);
        bottom.Controls.Add(volBar, 4, 0);
        bottom.Controls.Add(volPct, 5, 0);
        bottom.Controls.Add(_meter, 6, 0);
        bottom.SetRowSpan(_meter, 2);
        bottom.Controls.Add(checks, 0, 1);
        bottom.SetColumnSpan(checks, 6);
        void ApplyVolumes()
        {
            _config.MicVolume = volBar.Value / 100f;
            _config.HearSounds = hearSounds.Checked;
            _config.HearMyVoice = hearVoice.Checked;
            _engine.MicVolume = _engine.MonitorVolume = _config.MicVolume;
            _engine.MonitorEnabled = _config.HearSounds;
            _engine.VoiceMonitorEnabled = _config.HearMyVoice;
            volPct.Text = $"{volBar.Value} %";
        }
        volBar.ValueChanged += (_, _) => ApplyVolumes();
        volBar.MouseUp += (_, _) => SaveConfig();
        volBar.KeyUp += (_, _) => SaveConfig();
        hearSounds.CheckedChanged += (_, _) => { ApplyVolumes(); SaveConfig(); };
        hearVoice.CheckedChanged += (_, _) => { ApplyVolumes(); SaveConfig(); };
        ApplyVolumes();

        Controls.Add(_grid);
        Controls.Add(CreateToastBar()); // messages ("3 sounds deleted · Undo"), above "Playing"
        Controls.Add(CreateNowBar()); // between the sounds and the volume bar
        Controls.Add(bottom);
        Controls.Add(top);
        Controls.Add(sidebar); // added last: full height on the left

        DragEnter += OnFilesDragEnter;
        DragDrop += OnFilesDragDrop;

        // ---- notification-area icon
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(T("Open"), null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(T("Settings…"), null, (_, _) => { ShowFromTray(); OpenSettings(); });
        trayMenu.Items.Add(T("Pause"), null, (_, _) => _engine.TogglePauseAll());
        trayMenu.Items.Add(T("Stop"), null, (_, _) => _engine.StopAll());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(T("Quit"), null, (_, _) => QuitApp());
        _tray = new NotifyIcon { Icon = _appIcon, Text = "Soundboard", ContextMenuStrip = trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        // ---- live refresh (gauges, meter, status)
        _refresh = new System.Windows.Forms.Timer { Interval = 50 };
        _refresh.Tick += (_, _) => RefreshLive();
        _refresh.Start();

        RebuildCategories();
        RebuildGrid();
        _engine.ProbeDurations(_config.Sounds.Select(s => s.Path));
        Theme.Apply(this);
        InitUndo();
        RestoreWindow();
        UpdateStatus();
    }

    static int Pct(float v) => Math.Clamp((int)Math.Round(v * 100), 0, 150);

    static readonly string[] DenoiseNames = { "Off", "Light", "Normal", "Strong" }; // translated when shown

    /// <summary>0 = off, 1 light, 2 normal, 3 strong.</summary>
    int DenoiseIndex => _config.DenoiseEnabled ? Math.Clamp(_config.DenoiseLevel, 0, 2) + 1 : 0;

    void SetDenoise(int index, bool save = true)
    {
        _config.DenoiseEnabled = index > 0;
        if (index > 0) _config.DenoiseLevel = index - 1;
        _engine.DenoiseEnabled = _config.DenoiseEnabled;
        _engine.DenoiseLevel = _config.DenoiseLevel;
        _denoiseButton.Text = T("Noise filter: {0} ▾", T(DenoiseNames[DenoiseIndex]));
        if (save) SaveConfig();
    }

    ContextMenuStrip DenoiseMenu()
    {
        var m = new ContextMenuStrip();
        string[] hints =
        {
            T("Your voice is not filtered"),
            T("Gentle filter, the voice stays very natural"),
            T("Removes keyboard and background noise (recommended)"),
            T("Removes more; the voice can sound drier"),
        };
        for (int i = 0; i < DenoiseNames.Length; i++)
        {
            int idx = i;
            m.Items.Add(new ToolStripMenuItem(T(DenoiseNames[i]), null, (_, _) => SetDenoise(idx)) { Checked = DenoiseIndex == i, ToolTipText = hints[i] });
        }
        return m;
    }

    // ------------------------------------------------------------ adding sounds

    void OnFilesDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    void OnFilesDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddSounds(files);
    }

    void AddSoundsDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = T("Add sounds"),
            Multiselect = true,
            Filter = T("Audio files") + "|" + string.Join(";", Library.AudioExtensions.Select(e => "*" + e)) + "|" + T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddSounds(dlg.FileNames);
    }

    /// <summary>Adds dropped / pasted / chosen items: audio files, folders and .soundboard packs.</summary>
    void AddSounds(IEnumerable<string> paths)
    {
        var entries = new List<SoundEntry>();
        string tabCategory = _config.SelectedCategory == FavoritesTab ? "" : _config.SelectedCategory;
        bool tabFavorite = _config.SelectedCategory == FavoritesTab;

        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var (file, sub) in Library.ScanFolder(p))
                    entries.Add(NewEntry(file, sub.Length > 0 ? sub : tabCategory, tabFavorite));
            }
            else if (File.Exists(p) && Library.IsPack(p))
            {
                ImportPack(p);
            }
            else if (File.Exists(p) && Library.IsAudio(p))
            {
                entries.Add(NewEntry(p, tabCategory, tabFavorite));
            }
        }
        AddEntries(entries);
    }

    static SoundEntry NewEntry(string file, string category, bool favorite) => new()
    {
        Name = CleanName(file), Path = file, Category = category, Favorite = favorite,
    };

    /// <summary>Adds sounds (skipping files already in the list) and creates their categories.</summary>
    int AddEntries(List<SoundEntry> entries)
    {
        entries = entries.Where(e => !_config.Sounds.Any(s => string.Equals(s.Path, e.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        UseWaitCursor = true;
        int duplicates = DropDuplicates(entries); // same audio already in the list (other name or folder)
        UseWaitCursor = false;
        int added = 0;
        foreach (var e in entries)
        {
            if (e.Category.Length > 0 && !_config.Categories.Contains(e.Category)) _config.Categories.Add(e.Category);
            _config.Sounds.Add(e);
            added++;
        }

        int autoNames = entries.Count(e => IsAutoName(e.Name));
        var notes = new List<string>();
        if (added > 0) notes.Add(T("{0} sound(s) added", added));
        if (duplicates > 0) notes.Add(T("{0} duplicate(s) skipped", duplicates));
        if (autoNames > 0) notes.Add(T("{0} automatic name(s): hover and press F2 to rename", autoNames));
        if (notes.Count > 0) Toast(string.Join(" · ", notes), 7000);

        if (added == 0) return 0;
        _engine.ProbeDurations(_config.Sounds.Select(s => s.Path));
        SaveConfig();
        RebuildCategories(); // category counters include the new sounds
        RebuildGrid();
        return added;
    }

    void AddFolderDialog()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = T("Sound folder (its subfolders become categories)"),
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int before = _config.Sounds.Count;
        AddSounds(new[] { dlg.SelectedPath });
        int added = _config.Sounds.Count - before;
        if (added == 0)
            MessageBox.Show(this, T("No new sound found in this folder."), "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------ packs (share / backup)

    /// <param name="tab">the tab to export ("" = all, "★" = favourites); null = the tab shown</param>
    void ExportPackDialog() => ExportPackDialog(null);

    void ExportPackDialog(string? tab)
    {
        string sel = tab ?? _config.SelectedCategory;
        var sounds = _config.Sounds
            .Where(s => sel == "" || (sel == FavoritesTab ? s.Favorite : s.Category == sel))
            .ToList();
        if (sounds.Count == 0) { MessageBox.Show(this, T("No sound to export in this category."), "Soundboard"); return; }

        string name = sel == "" ? T("My sounds") : sel == FavoritesTab ? T("Favourites") : sel;
        using var dlg = new SaveFileDialog
        {
            Title = T("Export “{0}” ({1} sounds)", name, sounds.Count),
            Filter = T("Soundboard pack") + "|*.soundboard",
            FileName = name + ".soundboard",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            UseWaitCursor = true;
            int n = Library.Export(sounds, dlg.FileName, name);
            MessageBox.Show(this, T("{0} sounds exported.\nHotkeys are not included.", n), "Soundboard",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Export failed:") + "\n" + ex.Message, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor = false; }
    }

    void ImportPackDialog()
    {
        using var dlg = new OpenFileDialog { Title = T("Import a pack"), Filter = T("Soundboard pack") + "|*.soundboard;*.soundmix" };
        if (dlg.ShowDialog(this) == DialogResult.OK) ImportPack(dlg.FileName);
    }

    void ImportPack(string path)
    {
        try
        {
            var (sounds, name) = Library.Import(path);
            int added = AddEntries(sounds);
            MessageBox.Show(this, T("Pack “{0}”: {1} sounds added.", name, added), "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Import failed:") + "\n" + ex.Message, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Ctrl+V: files copied in the Explorer (or a pasted file / folder path).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleAssignKey(keyData)) return true; // "⌨ Keys" mode takes every key
        if (keyData == (Keys.Control | Keys.V) && !_search.Focused)
        {
            if (Clipboard.ContainsFileDropList())
            {
                AddSounds(Clipboard.GetFileDropList().Cast<string>());
                return true;
            }
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText().Trim().Trim('"');
                if (File.Exists(t) || Directory.Exists(t)) { AddSounds(new[] { t }); return true; }
            }
        }
        if (HandleToolKeys(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------ playing

    ClickMode ModeFor(SoundEntry s) => s.ClickMode == ClickMode.Default ? _config.DefaultClickMode : s.ClickMode;

    async void Play(SoundEntry s)
    {
        try
        {
            await _engine.TriggerAsync(s.Path, s.Volume, ModeFor(s), s.TrimStart, s.TrimEnd);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Can't play: {0}", s.Name) + "\n" + ex.Message, "Soundboard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------ categories

    void RebuildCategories()
    {
        _categoryBar.SuspendLayout();
        foreach (Control c in _categoryBar.Controls) c.Dispose();
        _categoryBar.Controls.Clear();

        bool hasFavorites = _config.Sounds.Any(s => s.Favorite);
        if (_config.SelectedCategory == FavoritesTab ? !hasFavorites
            : !string.IsNullOrEmpty(_config.SelectedCategory) && !_config.Categories.Contains(_config.SelectedCategory))
            _config.SelectedCategory = "";

        void AddTab(string label, int count, string category, string icon)
        {
            var tab = new SidebarItem(label, count.ToString())
            {
                Icon = icon, Font = Font,
                Selected = _config.SelectedCategory == category,
                AllowDrop = category != "",
            };
            tab.MouseClick += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || _config.SelectedCategory == category) return;
                _config.SelectedCategory = category;
                SaveConfig();
                foreach (var (item, cat) in _sideItems) item.Selected = cat == category;
                RebuildGrid();
            };
            _sideItems.Add((tab, category));
            if (category == FavoritesTab)
            {
                // drop a sound on the ★ tab to make it a favourite
                tab.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(typeof(SoundButton)) == true) { e.Effect = DragDropEffects.Move; tab.DropTarget = true; } };
                tab.DragDrop += (_, e) =>
                {
                    if (e.Data?.GetData(typeof(SoundButton)) is SoundButton b)
                    {
                        foreach (var s in Targets(b.Entry)) s.Favorite = true;
                        SaveConfig();
                        BeginInvoke(() => { RebuildCategories(); RebuildGrid(); });
                    }
                };
                _tips.SetToolTip(tab, T("Your favourite sounds\nDrop a sound here to add it"));
                var favMenu = new ContextMenuStrip();
                favMenu.Items.Add(T("Export this category…"), null, (_, _) => ExportPackDialog(FavoritesTab));
                favMenu.Items.Add(new ToolStripSeparator());
                favMenu.Items.Add(T("Remove all favourites"), null, (_, _) =>
                {
                    foreach (var s in _config.Sounds) s.Favorite = false;
                    SaveConfig();
                    BeginInvoke(() => { RebuildCategories(); RebuildGrid(); Toast(T("Favourites removed"), 6000, undo: true); });
                });
                tab.ContextMenuStrip = favMenu;
            }
            else if (category == "")
            {
                // "Tous": same kind of menu as the other tabs
                var allMenu = new ContextMenuStrip();
                allMenu.Items.Add(T("Export this category…"), null, (_, _) => ExportPackDialog(""));
                allMenu.Items.Add(T("New category…"), null, (_, _) => NewCategory(null));
                tab.ContextMenuStrip = allMenu;
                _tips.SetToolTip(tab, T("All your sounds\nRight click: export, new category"));
            }
            else
            {
                int index = _config.Categories.IndexOf(category);
                var menu = new ContextMenuStrip();
                menu.Items.Add(T("Rename…"), null, (_, _) => RenameCategory(category));
                menu.Items.Add(new ToolStripMenuItem(T("▲ Move up"), null, (_, _) => MoveCategory(category, -1)) { Enabled = index > 0 });
                menu.Items.Add(new ToolStripMenuItem(T("▼ Move down"), null, (_, _) => MoveCategory(category, +1)) { Enabled = index < _config.Categories.Count - 1 });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(T("Export this category…"), null, (_, _) => ExportPackDialog(category));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(T("Delete"), null, (_, _) => DeleteCategory(category));
                tab.ContextMenuStrip = menu;

                // drag a tab onto another one to reorder the categories
                Point downAt = Point.Empty;
                bool pressed = false;
                tab.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { pressed = true; downAt = e.Location; } };
                tab.MouseUp += (_, _) => pressed = false;
                tab.MouseMove += (_, e) =>
                {
                    if (!pressed || e.Button != MouseButtons.Left) return;
                    var d = SystemInformation.DragSize;
                    if (Math.Abs(e.X - downAt.X) <= d.Width && Math.Abs(e.Y - downAt.Y) <= d.Height) return;
                    pressed = false;
                    tab.DoDragDrop(new CategoryDrag(category), DragDropEffects.Move);
                };

                // accepts a sound (moved into this category) or another tab (reordered)
                tab.DragEnter += (_, e) =>
                {
                    if (e.Data?.GetDataPresent(typeof(SoundButton)) == true) e.Effect = DragDropEffects.Move;
                    else if (e.Data?.GetData(typeof(CategoryDrag)) is CategoryDrag cd && cd.Name != category) e.Effect = DragDropEffects.Move;
                    tab.DropTarget = e.Effect != DragDropEffects.None;
                };
                tab.DragDrop += (_, e) =>
                {
                    if (e.Data?.GetData(typeof(SoundButton)) is SoundButton b)
                    {
                        foreach (var s in Targets(b.Entry)) s.Category = category;
                        SaveConfig();
                        // this tab is rebuilt too: do it after the drop event has finished
                        BeginInvoke(() => { RebuildCategories(); RebuildGrid(); });
                    }
                    else if (e.Data?.GetData(typeof(CategoryDrag)) is CategoryDrag cd && cd.Name != category)
                    {
                        BeginInvoke(() => DropCategoryOn(cd.Name, category));
                    }
                };
                _tips.SetToolTip(tab, T("Drag the category to change the order\nDrop a sound here to file it\nRight click: rename, move, delete"));
            }
            _categoryBar.Controls.Add(tab);
        }

        _sideItems.Clear();
        AddTab(T("All"), _config.Sounds.Count, "", Icons.All);
        if (hasFavorites) AddTab(T("Favourites"), _config.Sounds.Count(s => s.Favorite), FavoritesTab, Icons.Favourite);
        foreach (var c in _config.Categories)
            AddTab(c, _config.Sounds.Count(s => s.Category == c), c, Icons.Category);

        var addCat = new SidebarItem(T("New category")) { Icon = Icons.Add, Font = Font, Accent = true };
        addCat.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) NewCategory(null); };
        _categoryBar.Controls.Add(addCat);

        FitSidebar(_categoryBar);
        _categoryBar.ResumeLayout();
        Theme.Apply(_categoryBar);
    }

    readonly List<(SidebarItem Item, string Category)> _sideItems = new();

    /// <summary>Side bar rows take the whole width of their list (minus the scrollbar when shown).</summary>
    static void FitSidebar(FlowLayoutPanel list)
    {
        int w = list.ClientSize.Width;
        if (w <= 0) return;
        foreach (Control c in list.Controls) if (c.Width != w) c.Width = w;
    }

    /// <summary>Drag payload for a category tab.</summary>
    sealed record CategoryDrag(string Name);

    /// <summary>Moves a category one place to the left (-1) or right (+1).</summary>
    void MoveCategory(string name, int step)
    {
        var list = _config.Categories;
        int i = list.IndexOf(name), j = i + step;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        SaveConfig();
        RebuildCategories();
    }

    /// <summary>A tab dropped on another one takes its place (same rule as the sounds).</summary>
    void DropCategoryOn(string moved, string target)
    {
        var list = _config.Categories;
        int from = list.IndexOf(moved), to = list.IndexOf(target);
        if (from < 0 || to < 0) return;
        list.RemoveAt(from);
        int t = list.IndexOf(target);
        list.Insert(from < to ? t + 1 : t, moved);
        SaveConfig();
        RebuildCategories();
    }

    string? NewCategory(SoundEntry? moveHere)
    {
        using var dlg = new TextDialog(T("New category"), "");
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value.Length == 0) return null;
        string name = dlg.Value;
        if (!_config.Categories.Contains(name)) _config.Categories.Add(name);
        if (moveHere != null) moveHere.Category = name;
        SaveConfig();
        RebuildCategories();
        RebuildGrid();
        return name;
    }

    void RenameCategory(string old)
    {
        using var dlg = new TextDialog(T("Rename"), old);
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value.Length == 0 || dlg.Value == old) return;
        string name = dlg.Value;
        int i = _config.Categories.IndexOf(old);
        if (_config.Categories.Contains(name)) _config.Categories.RemoveAt(i); // merge into existing
        else _config.Categories[i] = name;
        foreach (var s in _config.Sounds.Where(s => s.Category == old)) s.Category = name;
        if (_config.SelectedCategory == old) _config.SelectedCategory = name;
        SaveConfig();
        RebuildCategories();
        RebuildGrid();
    }

    void DeleteCategory(string name)
    {
        int count = _config.Sounds.Count(s => s.Category == name);
        if (count > 0 && MessageBox.Show(this,
                T("Delete “{0}”?\nIts sounds stay in “All”.", name),
                "Soundboard", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _config.Categories.Remove(name);
        foreach (var s in _config.Sounds.Where(s => s.Category == name)) s.Category = "";
        if (_config.SelectedCategory == name) _config.SelectedCategory = "";
        SaveConfig();
        RebuildCategories();
        RebuildGrid();
    }

    // ------------------------------------------------------------ grid

    static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    void RebuildGrid()
    {
        _grid.SuspendLayout();
        foreach (var b in _buttons) b.Dispose();
        _buttons.Clear();
        foreach (Control c in _grid.Controls) c.Dispose();
        _grid.Controls.Clear();

        string q = Fold(_search.Text.Trim());
        string sel = _config.SelectedCategory;
        var visible = _config.Sounds
            .Where(s => sel == "" || (sel == FavoritesTab ? s.Favorite : s.Category == sel))
            .Where(s => q.Length == 0 || Fold(s.Name).Contains(q))
            .OrderByDescending(s => s.Favorite) // favourites first, the rest keeps your order
            .ToList();

        if (visible.Count == 0)
        {
            _grid.Controls.Add(new Label
            {
                AutoSize = true,
                Padding = new Padding(8),
                Tag = "keep-color",
                ForeColor = Theme.Current.SubText,
                Text = _config.Sounds.Count == 0
                    ? T("No sounds yet. Click “Add” or drop files here.")
                    : q.Length > 0 ? T("No results.") : T("Empty category. Drop a sound on it in the side bar, or add some here."),
            });
        }

        var size = ButtonSizes[Math.Clamp(_config.ButtonSize, 0, 2)];
        foreach (var entry in visible)
        {
            var b = new SoundButton(entry)
            {
                Size = size,
                Font = _config.ButtonSize == 0 ? new Font("Segoe UI", 8.5f) : Font,
                EffectiveMode = ModeFor(entry),
            };
            b.PlayRequested += (_, _) => OnSoundClick(entry);
            b.Selected = _selection.Contains(entry);
            b.StopRequested += (_, _) => _engine.Stop(entry.Path);
            b.Reordered += Reorder;
            b.FilesDropped += AddSounds;
            var menu = new ContextMenuStrip();
            // WinForms cancels opening an empty menu: it is filled here, so open it anyway
            menu.Opening += (_, e) =>
            {
                var targets = Targets(entry);
                if (targets.Count > 1) BuildBulkMenu(menu, targets); else BuildSoundMenu(menu, entry);
                e.Cancel = false;
            };
            b.ContextMenuStrip = menu;
            _tips.SetToolTip(b, entry.Path + "\n" + T("Click: play · Middle: stop · Right: options"));
            _buttons.Add(b);
            _grid.Controls.Add(b);
        }
        if (_buttons.Count > 0)
        {
            // real spacer on its own line: the last row is never cut at the bottom when scrolling
            _grid.SetFlowBreak(_buttons[^1], true);
            _grid.Controls.Add(new Panel { Width = 1, Height = 10, Margin = Padding.Empty, Enabled = false });
        }
        _grid.ResumeLayout();
        _grid.AutoScrollPosition = Point.Empty; // start at the top of the list
        FitButtons();
        if (_assignMode && (_assignTarget == null || !_buttons.Any(b => b.Entry == _assignTarget)))
            _assignTarget = _buttons.FirstOrDefault()?.Entry; // tab changed: start on its first sound
        SyncAssignView();
        RegisterAllHotkeys();
        RefreshLive();
    }

    int _fitWidth = -1;

    /// <summary>
    /// Stretches the buttons so each row fills the width (no empty strip on the right).
    /// The chosen size (Petite / Moyenne / Grande) is the minimum width.
    /// </summary>
    void FitButtons()
    {
        if (_buttons.Count == 0) return;
        var baseSize = ButtonSizes[Math.Clamp(_config.ButtonSize, 0, 2)];
        int margin = _buttons[0].Margin.Horizontal;
        // keep room for the vertical scrollbar so the layout does not jump when it appears
        int available = _grid.ClientSize.Width - _grid.Padding.Horizontal - (_grid.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        int columns = Math.Max(1, available / (baseSize.Width + margin));
        int width = Math.Max(baseSize.Width, available / columns - margin - 1);
        if (width == _fitWidth && _buttons.All(b => b.Width == width)) return;
        _fitWidth = width;
        _grid.SuspendLayout();
        foreach (var b in _buttons) b.Size = new Size(width, baseSize.Height);
        _grid.ResumeLayout();
    }

    void Reorder(SoundButton moved, SoundButton target)
    {
        var list = _config.Sounds;
        int from = list.IndexOf(moved.Entry), to = list.IndexOf(target.Entry);
        if (from < 0 || to < 0) return;
        list.RemoveAt(from);
        int t = list.IndexOf(target.Entry);
        list.Insert(from < to ? t + 1 : t, moved.Entry);
        SaveConfig();
        RebuildGrid();
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    /// <summary>Push-to-talk for the sounds: they reach the mic only while the key is held (works in games too).</summary>
    void PollPushToTalk()
    {
        bool open = true;
        if (_config.PushToTalk && _config.PushToTalkKey.IsSet)
            open = (GetAsyncKeyState(_config.PushToTalkKey.Key) & 0x8000) != 0;
        _engine.SoundGateOpen = open;
        _pttHeld = open;
    }

    bool _pttHeld = true;

    /// <summary>Length shown on a button: the trimmed part when start/end are set.</summary>
    double? DisplayDuration(SoundEntry s)
    {
        var d = _engine.CachedDuration(s.Path);
        if (d is not double full) return null;
        double end = s.TrimEnd > 0 ? Math.Min(s.TrimEnd, full) : full;
        return Math.Max(0, end - s.TrimStart);
    }

    void RefreshLive()
    {
        PollPushToTalk(); // also when the window is hidden
        if (!Visible) return;
        var snap = _engine.Snapshot();
        foreach (var b in _buttons)
        {
            snap.TryGetValue(b.Entry.Path, out var info);
            b.SetState(info, DisplayDuration(b.Entry));
        }
        _meter.Push(_engine.VoiceLevel, _engine.OutputLevel);
        UpdateNowBar(snap);
        if (++_tick % 8 == 0) UpdateStatus();
    }

    // ------------------------------------------------------------ per-sound menu

    /// <summary>Right-click on one sound: quick actions only, everything else is in "Modifier…".</summary>
    void BuildSoundMenu(ContextMenuStrip menu, SoundEntry s)
    {
        menu.Items.Clear();
        var play = new ToolStripMenuItem(T("▶ Play"), null, (_, _) => Play(s)) { ShortcutKeyDisplayString = T("click") };
        play.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(play);
        _engine.Snapshot().TryGetValue(s.Path, out var info);
        if (info.Count > 0)
        {
            menu.Items.Add(info.Paused ? T("▶ Resume") : T("⏸ Pause"), null, (_, _) => _engine.TogglePause(s.Path));
            menu.Items.Add(new ToolStripMenuItem(T("⏹ Stop"), null, (_, _) => _engine.Stop(s.Path)) { ShortcutKeyDisplayString = T("middle click") });
        }
        menu.Items.Add(new ToolStripSeparator());

        var fav = new ToolStripMenuItem(T("★ Favourite")) { Checked = s.Favorite, ToolTipText = T("At the top of the list and in ★ Favourites") };
        fav.Click += (_, _) =>
        {
            s.Favorite = !s.Favorite;
            SaveConfig();
            BeginInvoke(() => { RebuildCategories(); RebuildGrid(); });
        };
        menu.Items.Add(fav);

        // category, with the current one shown
        var cat = new ToolStripMenuItem(T("Category: {0}", s.Category.Length > 0 ? s.Category : T("none")));
        void MoveTo(string c) { s.Category = c; SaveConfig(); BeginInvoke(() => { RebuildCategories(); RebuildGrid(); }); }
        cat.DropDownItems.Add(new ToolStripMenuItem(T("None"), null, (_, _) => MoveTo("")) { Checked = s.Category == "" });
        foreach (var c in _config.Categories)
            cat.DropDownItems.Add(new ToolStripMenuItem(c, null, (_, _) => MoveTo(c)) { Checked = s.Category == c });
        cat.DropDownItems.Add(new ToolStripSeparator());
        cat.DropDownItems.Add(T("New…"), null, (_, _) => NewCategory(s));
        menu.Items.Add(cat);

        // colour, with the current one as the item's dot
        var color = new ToolStripMenuItem(T("Colour"), s.Color != 0 ? Swatch(s.Color) : null);
        void Paint(int argb) { s.Color = argb; SaveConfig(); InvalidateButton(s); }
        foreach (var (name, argb) in Swatches)
            color.DropDownItems.Add(new ToolStripMenuItem(T(name), Swatch(argb), (_, _) => Paint(argb)) { Checked = s.Color == argb });
        color.DropDownItems.Add(new ToolStripSeparator());
        color.DropDownItems.Add(new ToolStripMenuItem(T("Default"), null, (_, _) => Paint(0)) { Checked = s.Color == 0 });
        menu.Items.Add(color);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(T("✎ Edit…"), null, (_, _) => EditSound(s))
        {
            ToolTipText = T("Name, category, colour, volume, hotkey, trim…"),
        });
        menu.Items.Add(new ToolStripMenuItem(T("⌨ Hotkey: {0}…", s.Hotkey.IsSet ? s.Hotkey.ToString() : T("none")), null, (_, _) => QuickHotkey(s))
        {
            ToolTipText = T("Press the key you want straight away"),
        });
        menu.Items.Add(new ToolStripMenuItem(T("Rename…"), null, (_, _) => Rename(s)) { ShortcutKeyDisplayString = "F2" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(T("Delete"), null, (_, _) => Remove(s)) { ShortcutKeyDisplayString = T("Delete") });
    }

    static Bitmap Swatch(int argb) => ColorDot.Icon(argb);

    void InvalidateButton(SoundEntry s)
    {
        foreach (var b in _buttons.Where(b => b.Entry == s)) b.Invalidate();
    }

    /// <summary>"Modifier le son": all the settings of one sound in one window (one Ctrl+Z step on OK).</summary>
    async void EditSound(SoundEntry s)
    {
        float[] data;
        try { data = await Task.Run(() => _engine.Load(s.Path)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Can't play: {0}", s.Name) + "\n" + ex.Message, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string defaultName = ClickModes.First(m => m.Mode == _config.DefaultClickMode).Name;
        string? KeyOwner(Hotkey k)
        {
            if (!k.IsSet) return null;
            bool Same(Hotkey h) => h.IsSet && h.Key == k.Key && h.Modifiers == k.Modifiers;
            if (Same(_config.PauseHotkey)) return T("Pause");
            if (Same(_config.StopHotkey)) return T("Stop");
            if (_config.PushToTalkKey.IsSet && _config.PushToTalkKey.Key == k.Key && k.Modifiers == 0) return T("Push-to-talk");
            return _config.Sounds.FirstOrDefault(o => o != s && Same(o.Hotkey))?.Name;
        }

        UnregisterAllHotkeys(); // so the current key can be captured again
        try
        {
            using var dlg = new SoundEditor(s, data, _config.Categories, defaultName, Swatches,
                // headset only: adjusting a sound never plays it to the others
                preview: (vol, a, b) => _ = _engine.PreviewAsync(s.Path, vol, a, b),
                pickKey: current =>
                {
                    using var hk = new HotkeyDialog(T("Hotkey: {0}", s.Name), current);
                    Theme.Apply(hk);
                    return hk.ShowDialog(this) == DialogResult.OK ? hk.Result : null;
                },
                keyOwner: KeyOwner);
            Theme.Apply(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (s.Category.Length > 0 && !_config.Categories.Contains(s.Category)) _config.Categories.Add(s.Category);
            _engine.SetGain(s.Path, s.Volume);
            SaveConfig();
            RebuildCategories();
        }
        finally
        {
            _engine.StopPreviews(); // closing the editor ends the preview
            RebuildGrid(); // also registers the hotkeys again
        }
    }

    void SetSoundMode(SoundEntry s, ClickMode m)
    {
        s.ClickMode = m;
        SaveConfig();
        foreach (var b in _buttons.Where(b => b.Entry == s)) { b.EffectiveMode = ModeFor(s); b.Invalidate(); }
    }

    void EditSoundHotkey(SoundEntry s) => QuickHotkey(s); // same conflict rule everywhere

    void Rename(SoundEntry s)
    {
        using var dlg = new TextDialog(T("Rename"), s.Name);
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Value.Length > 0)
        {
            s.Name = dlg.Value;
            SaveConfig();
            RebuildGrid();
        }
    }

    void Remove(SoundEntry s) => DeleteSounds(new List<SoundEntry> { s }); // Ctrl+Z brings it back

    // ------------------------------------------------------------ options menu


    static string HotkeyText(Hotkey h) => h.IsSet ? h.ToString() : "—";

    void EditGlobalHotkey(bool pause)
    {
        UnregisterAllHotkeys();
        using var dlg = new HotkeyDialog(T("Hotkey: {0}", pause ? T("Pause") : T("Stop")),
            pause ? _config.PauseHotkey : _config.StopHotkey);
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (pause) _config.PauseHotkey = dlg.Result; else _config.StopHotkey = dlg.Result;
        }
        SaveConfig();
        RegisterAllHotkeys();
    }

    void EditPushToTalkKey()
    {
        UnregisterAllHotkeys();
        using var dlg = new HotkeyDialog(T("Key: {0}", T("Push-to-talk")), _config.PushToTalkKey);
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            // only the key counts (held state), modifiers are ignored
            _config.PushToTalkKey = new Hotkey { Key = dlg.Result.Key };
            _config.PushToTalk = dlg.Result.IsSet;
        }
        SaveConfig();
        RegisterAllHotkeys();
    }

    void SetTheme(bool dark)
    {
        _config.DarkTheme = dark;
        SaveConfig();
        Theme.Set(dark);
        Theme.Apply(this);
        Theme.ApplyTitleBar(this);
        RebuildGrid(); // empty-state label colour
        Invalidate(true);
    }

    bool _saveWarned;

    /// <summary>Saves the configuration; a failure is shown (once) instead of being silently lost.</summary>
    void SaveConfig()
    {
        try
        {
            RecordUndo();
            _config.Save();
            _saveWarned = false;
        }
        catch (Exception ex)
        {
            if (_saveWarned) return;
            _saveWarned = true;
            BeginInvoke(() => MessageBox.Show(this,
                T("Soundboard can't save your sounds and settings:") + "\n" + ex.Message + "\n\n" +
                T("Your latest changes will be lost when you close it if this goes on."),
                "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    void RestoreBackupDialog()
    {
        var backups = AppConfig.ListBackups();
        if (backups.Count == 0)
        {
            MessageBox.Show(this, T("No backup yet.\nSoundboard makes one automatically every 10 minutes of use."),
                "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new Form
        {
            Text = T("Restore a backup"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(400, 300), ShowInTaskbar = false, Font = Font,
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        foreach (var b in backups)
            list.Items.Add(T("{0:g}   —   {1} sounds, {2} categories", b.Time, b.Sounds, b.Categories));
        list.SelectedIndex = 0;
        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        holder.Controls.Add(list);
        dlg.Controls.Add(holder);
        DialogButtons.Add(dlg, T("Restore"));
        holder.BringToFront(); // fill the space left above the button row
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return;

        try
        {
            AppConfig.RestoreBackup(backups[list.SelectedIndex].Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Restore failed:") + "\n" + ex.Message, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // reload everything from the restored file: restart the app without saving the current state over it
        _skipSaveOnExit = true;
        Program.RestartRequested = true;
        QuitApp();
    }

    bool _skipSaveOnExit;

    // ------------------------------------------------------------ global hotkeys

    void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        if (!IsHandleCreated || _assignMode) return; // paused while keys are being assigned
        var failed = new List<string>();

        if (_config.StopHotkey.IsSet && !TryRegister(StopHotkeyId, _config.StopHotkey))
            failed.Add($"Stop ({_config.StopHotkey})");
        if (_config.PauseHotkey.IsSet && !TryRegister(PauseHotkeyId, _config.PauseHotkey))
            failed.Add($"Pause ({_config.PauseHotkey})");

        for (int i = 0; i < _config.Sounds.Count; i++)
        {
            var s = _config.Sounds[i];
            if (s.Hotkey.IsSet && !TryRegister(SoundHotkeyBase + i, s.Hotkey))
                failed.Add($"{s.Name} ({s.Hotkey})");
        }

        if (failed.Count > 0 && !SuppressDialogs)
            MessageBox.Show(this,
                T("Hotkeys already in use:") + "\n\n" + string.Join("\n", failed),
                "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    bool TryRegister(int id, Hotkey h)
    {
        if (!RegisterHotKey(Handle, id, h.Modifiers | MOD_NOREPEAT, h.Key)) return false;
        _registeredIds.Add(id);
        return true;
    }

    void UnregisterAllHotkeys()
    {
        foreach (var id in _registeredIds) UnregisterHotKey(Handle, id);
        _registeredIds.Clear();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
        RegisterAllHotkeys();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == StopHotkeyId) _engine.StopAll();
            else if (id == PauseHotkeyId) _engine.TogglePauseAll();
            else if (id >= SoundHotkeyBase && id - SoundHotkeyBase < _config.Sounds.Count)
                Play(_config.Sounds[id - SoundHotkeyBase]);
        }
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------ status

    bool _moduleInstalled = true;
    long _nextInstallCheck;

    void UpdateStatus()
    {
        var p = Theme.Current;
        if (_engine.State != LinkState.Connected && Environment.TickCount64 >= _nextInstallCheck)
        {
            // registry check: is the module still on a microphone? (a driver update can remove it)
            try { _moduleInstalled = MicSetup.ListMicrophones().Any(m => m.Installed); } catch { }
            _nextInstallCheck = Environment.TickCount64 + 5000;
        }

        bool fixable = false;
        if (_engine.State == LinkState.Connected)
        {
            _status.ForeColor = p.Good;
            _status.Text = T("● Connected to the mic");
        }
        else if (_moduleInstalled)
        {
            _status.ForeColor = p.Warn;
            _status.Text = T("● Waiting: mic not in use");
        }
        else
        {
            _status.ForeColor = p.Bad;
            _status.Text = T("● Module missing · Set up");
            fixable = true;
        }
        if (_config.PushToTalk && _config.PushToTalkKey.IsSet)
            _status.Text += _pttHeld ? T("  ·  sounds sent") : T("  ·  sounds wait for {0}", _config.PushToTalkKey);
        _status.Cursor = fixable ? Cursors.Hand : Cursors.Default;
        _status.Tag = fixable ? "keep-color fixable" : "keep-color";
        ApplyToast();
    }

    void OpenMicDialog()
    {
        using var dlg = new MicDialog();
        Theme.Apply(dlg);
        dlg.ShowDialog(this);
        _nextInstallCheck = 0;
        UpdateStatus();
    }

    // ------------------------------------------------------------ tray / lifetime

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle(); // hotkeys are registered on this handle
            value = false;
        }
        base.SetVisibleCore(value);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>
    /// First start on a PC (after installing): explain the one setup step and open the microphone choice.
    /// Skipped when the module is already on a microphone.
    /// </summary>
    void Welcome(bool firstRun)
    {
        if (_config.LoadProblem is string problem)
            MessageBox.Show(this, problem, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (_config.WelcomeDone && !firstRun) return;
        bool installed = false;
        try { installed = MicSetup.ListMicrophones().Any(m => m.Installed); } catch { }
        _config.WelcomeDone = true;
        SaveConfig();
        if (installed) return;

        MessageBox.Show(this,
            T("Welcome to Soundboard!\n\nOne step only: choose the microphone you use (Discord, games…).\nWindows will ask for administrator rights, and audio will cut out for 2 seconds.\n\nThen add sounds and click them to play."),
            "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        OpenMicDialog();
    }

    public void QuitApp()
    {
        _reallyQuit = true;
        Close();
    }

    /// <summary>The music-note icon embedded in the exe (app/Soundboard.ico), at the requested size.</summary>
    static Icon LoadAppIcon(Size? size)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("Soundboard.ico");
        if (s == null) return MakeAppIcon();
        return size is Size sz ? new Icon(s, sz) : new Icon(s);
    }

    static Icon MakeAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(0, 120, 212));
            g.FillEllipse(bg, 2, 2, 60, 60);
            using var f = new Font("Segoe UI Symbol", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "♪", f, new Rectangle(0, 0, 64, 62), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberWindow();
        if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide(); // hotkeys keep working: the window handle stays alive
            if (!_config.TrayHintShown)
            {
                _tray.ShowBalloonTip(4000, T("Soundboard keeps running"),
                    T("Your hotkeys still work. Double-click the icon to open it again."),
                    ToolTipIcon.Info);
                _config.TrayHintShown = true;
                SaveConfig();
            }
            return;
        }

        UnregisterAllHotkeys();
        _refresh.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (!_skipSaveOnExit) SaveConfig();
        _engine.Dispose();
        base.OnFormClosing(e);
    }
}
