namespace Soundboard;

// The "Settings" window: navigation on the left, one page per topic, changes apply immediately.
sealed partial class MainForm
{
    // page keys (English); shown translated
    static readonly string[] SettingsPages = { "General", "Playback", "Microphone", "Hotkeys", "Backup", "About" };

    Form? _settings;

    /// <summary>For the tests (renders each page off screen).</summary>
    internal Form? SettingsWindow => _settings;

    /// <summary>For the tests: no blocking message boxes.</summary>
    internal static bool SuppressDialogs;

    /// <summary>Opens the settings window (or brings it to the front) on the given page.</summary>
    public void OpenSettings(string page = "General")
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            ShowSettingsPage(page);
            return;
        }

        var f = new Form
        {
            Text = T("Settings"), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.Sizable,
            ClientSize = new Size(760, 520), MinimumSize = new Size(640, 440), Font = Font, Icon = Icon,
            KeyPreview = true,
        };
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 170, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(10, 12, 6, 10),
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 10), AutoScroll = true };
        var close = new Button { Text = T("Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 10) };
        bottom.Controls.Add(close);
        close.Click += (_, _) => f.Close();
        f.CancelButton = close;

        foreach (var name in SettingsPages)
        {
            var item = new RadioButton
            {
                Text = T(name), Appearance = Appearance.Button, Width = 150, Height = 34, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 4), Padding = new Padding(8, 0, 0, 0), Tag = name,
            };
            item.CheckedChanged += (_, _) => { if (item.Checked) FillSettingsPage(host, name); };
            nav.Controls.Add(item);
        }

        f.Controls.Add(host);
        f.Controls.Add(nav);
        f.Controls.Add(bottom);
        f.Tag = (nav, host);
        f.FormClosed += (_, _) => { _settings = null; SaveConfig(); };
        _settings = f;
        Theme.Apply(f);
        Theme.ApplyTitleBar(f);
        ShowSettingsPage(page);
        PlaceOverMainWindow(f);
        f.Show(this);
    }

    /// <summary>
    /// Centres a non-blocking window on Soundboard (Windows only does it for modal ones and would
    /// otherwise put it in the top-left corner of the screen), kept inside the screen.
    /// </summary>
    void PlaceOverMainWindow(Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        var anchor = Visible && WindowState != FormWindowState.Minimized ? Bounds : Screen.FromControl(this).WorkingArea;
        var area = Screen.FromRectangle(anchor).WorkingArea;
        int x = anchor.X + (anchor.Width - f.Width) / 2;
        int y = anchor.Y + (anchor.Height - f.Height) / 2;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - f.Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - f.Height));
        f.Location = new Point(x, y);
    }

    void ShowSettingsPage(string page)
    {
        if (_settings?.Tag is not (FlowLayoutPanel nav, Panel)) return;
        foreach (RadioButton r in nav.Controls)
            if ((string)r.Tag! == page) { if (r.Checked) r.Checked = false; r.Checked = true; }
    }

    /// <summary>Rebuilds the open page (after a change that affects what it shows).</summary>
    void RefreshSettingsPage()
    {
        if (_settings?.Tag is not (FlowLayoutPanel nav, Panel host)) return;
        var current = nav.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked);
        if (current != null) FillSettingsPage(host, (string)current.Tag!);
    }

    // ------------------------------------------------------------ page building blocks

    sealed class PageBuilder
    {
        readonly FlowLayoutPanel _col;
        public PageBuilder(Panel host, string title)
        {
            _col = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            host.Controls.Add(_col);
            _col.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, 10) });
        }

        public int Width { get; set; } = 520;

        public void Section(string title) =>
            _col.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f), Tag = "keep-color", ForeColor = Theme.Current.SubText, Margin = new Padding(0, 12, 0, 4) });

        /// <summary>A setting: the control (checkbox, combo, button…), then an explanation under it.</summary>
        public void Row(Control control, string? description = null)
        {
            control.Margin = new Padding(0, 2, 0, description == null ? 8 : 0);
            _col.Controls.Add(control);
            if (description != null)
                _col.Controls.Add(new Label
                {
                    Text = description, AutoSize = true, MaximumSize = new Size(Width, 0), Tag = "keep-color", ForeColor = Theme.Current.SubText,
                    Margin = new Padding(control is CheckBox ? 18 : 0, 0, 0, 10),
                });
        }

        /// <summary>A label with a control to its right (e.g. "Theme" + combo).</summary>
        public T Labeled<T>(string label, T control, string? description = null) where T : Control
        {
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            line.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
            control.Margin = new Padding(0, 2, 0, 0);
            line.Controls.Add(control);
            Row(line, description);
            return control;
        }

        public Button Button(string text, Action click, string? description = null)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => click();
            Row(b, description);
            return b;
        }

        public void Add(Control c) { c.Margin = new Padding(0, 2, 0, 8); _col.Controls.Add(c); }
    }

    static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1);
        c.SelectedIndexChanged += (_, _) => changed(c.SelectedIndex);
        return c;
    }

    static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true };
        c.CheckedChanged += (_, _) => changed(c.Checked);
        return c;
    }

    // ------------------------------------------------------------ pages

    void FillSettingsPage(Panel host, string page)
    {
        host.SuspendLayout();
        foreach (Control c in host.Controls) c.Dispose();
        host.Controls.Clear();
        var p = new PageBuilder(host, T(page)) { Width = Math.Max(360, host.ClientSize.Width - 40) };

        switch (page)
        {
            case "General":
                p.Section(T("Startup"));
                p.Row(Check(T("Launch with Windows"), Startup.IsEnabled, v => Startup.Set(v)),
                    T("Soundboard starts next to the clock when you sign in: your hotkeys work right away."));
                p.Row(Check(T("Keep running in the background when closed"), _config.MinimizeToTray, v => { _config.MinimizeToTray = v; SaveConfig(); }),
                    T("Closing the window keeps Soundboard running (icon next to the clock). Otherwise, closing quits the app."));
                p.Section(T("Window"));
                p.Row(Check(T("Always on top"), _config.AlwaysOnTop, v => { _config.AlwaysOnTop = TopMost = v; SaveConfig(); }),
                    T("Keeps Soundboard above other windows, handy with a game in windowed mode."));
                p.Labeled(T("Theme"), Combo(new[] { T("Light"), T("Dark") }, _config.DarkTheme ? 1 : 0, i =>
                {
                    SetTheme(i == 1);
                    if (_settings != null) { Theme.Apply(_settings); Theme.ApplyTitleBar(_settings); }
                }));
                p.Labeled(T("Button size"), Combo(new[] { T("Small"), T("Medium"), T("Large") }, _config.ButtonSize, i =>
                {
                    _config.ButtonSize = i; SaveConfig(); RebuildGrid();
                }));
                p.Labeled(T("Language"), Combo(Loc.Languages.Select(l => l.Code == "" ? T(l.Name) : l.Name),
                    Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == _config.Language)), i =>
                {
                    if (_config.Language == Loc.Languages[i].Code) return;
                    _config.Language = Loc.Languages[i].Code;
                    SaveConfig();
                    if (!SuppressDialogs && MessageBox.Show(_settings, T("Restart Soundboard now to change the language?"), "Soundboard",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Program.RestartRequested = true;
                        QuitApp();
                    }
                }), T("Takes effect the next time Soundboard starts."));
                break;

            case "Playback":
                p.Section(T("Clicking a sound that is already playing"));
                p.Labeled(T("Default"), Combo(ClickModes.Select(m => T(m.Name)), Array.FindIndex(ClickModes, m => m.Mode == _config.DefaultClickMode), i =>
                {
                    _config.DefaultClickMode = ClickModes[i].Mode; SaveConfig(); RebuildGrid();
                }), T("Restart: starts over · Pause: pauses, then resumes · Overlay: plays again on top (spam) · Stop: stops the sound. ") +
                    T("Each sound can have its own setting (right click)."));
                p.Section(T("Volume"));
                p.Row(Check(T("Even out volume"), _config.Normalize, v => { _config.Normalize = _engine.Normalize = v; SaveConfig(); }),
                    T("Brings every sound to a similar level, without adjusting each one. Applies the next time a sound starts."));
                p.Section(T("Push-to-talk"));
                var ptt = Check(T("On"), _config.PushToTalk && _config.PushToTalkKey.IsSet, v => { _config.PushToTalk = v; SaveConfig(); UpdateStatus(); });
                ptt.Enabled = _config.PushToTalkKey.IsSet;
                p.Row(ptt, T("Sounds only reach the microphone while the key is held. You always hear them in your headset."));
                p.Button(_config.PushToTalkKey.IsSet ? T("Key: {0}", _config.PushToTalkKey) + "…" : T("Choose a key…"), () => { EditPushToTalkKey(); RefreshSettingsPage(); },
                    T("Tip: choose the same key as your Discord push-to-talk."));
                break;

            case "Microphone":
                p.Section(T("Connection"));
                var state = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Tag = "keep-color", ForeColor = _status.ForeColor, Text = _status.Text };
                p.Add(state);
                List<MicInfo> mics;
                try { mics = MicSetup.ListMicrophones(); } catch { mics = new List<MicInfo>(); }
                var on = mics.Where(m => m.Installed).Select(m => m.Name).ToList();
                p.Row(new Label { AutoSize = true, MaximumSize = new Size(p.Width, 0), Text = on.Count > 0 ? T("Equipped microphone: {0}", string.Join(", ", on)) : T("No microphone equipped yet.") });
                p.Button(T("Choose the microphone…"), () => { OpenMicDialog(); RefreshSettingsPage(); },
                    T("Tick the microphone you use in Discord and your games (administrator rights required)."));
                p.Button(T("Repair"), () =>
                {
                    UseWaitCursor = true;
                    var (ok, msg) = MicDialog.Repair();
                    UseWaitCursor = false;
                    MessageBox.Show(_settings, ok ? T("Done. Apps can take a few seconds to reconnect to the microphone.") : msg,
                        "Soundboard", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    _nextInstallCheck = 0; UpdateStatus(); RefreshSettingsPage();
                }, T("Use this if the light stays red after an audio driver or Windows update."));
                p.Section(T("Your voice"));
                p.Labeled(T("Noise filter"), Combo(DenoiseNames.Select(n => T(n)), DenoiseIndex, i => SetDenoise(i)),
                    T("Removes keyboard and background noise from your voice, never from the sounds. In Discord, set Noise Suppression to “None”."));
                p.Row(Check(T("Hear my voice"), _config.HearMyVoice, v => _hearVoiceBox.Checked = v),
                    T("You hear your voice the way others receive it, handy to tune the noise filter."));
                p.Row(Check(T("Hear the sounds in my headset"), _config.HearSounds, v => _hearSoundsBox.Checked = v),
                    T("At the same volume as everyone else. Unticking changes nothing for them."));
                break;

            case "Hotkeys":
                FillShortcutsPage(p, host);
                break;

            case "Backup":
                p.Section(T("Share"));
                p.Button(T("Export the category shown…"), ExportPackDialog,
                    T("Creates a .soundboard pack with the sounds and their settings (without hotkeys), to keep or to send to a friend."));
                p.Button(T("Import a pack…"), ImportPackDialog);
                p.Section(T("History"));
                var backups = AppConfig.ListBackups();
                p.Button(T("Restore a backup…"), RestoreBackupDialog,
                    backups.Count > 0
                        ? T("{0} automatic backup(s), the latest from {1:g} ({2} sounds).", backups.Count, backups[0].Time, backups[0].Sounds)
                        : T("Soundboard backs up your list automatically while you use it."));
                p.Section(T("Clean up"));
                p.Button(T("Find duplicates"), () => { _settings?.Close(); FindDuplicates(); },
                    T("Selects duplicate sounds (same audio file), keeping the first of each."));
                break;

            case "About":
                p.Row(new Label { AutoSize = true, Text = $"Soundboard {Installer.Version}", Font = new Font(Font, FontStyle.Bold) },
                    T("Sounds straight into your microphone: a Windows audio effect on the mic, no virtual cable, nothing touches your games."));
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundboard");
                p.Button(T("Open the data folder"), () =>
                {
                    Directory.CreateDirectory(data);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{data}\"") { UseShellExecute = true });
                }, T("Your sound list, your settings, the backups and the imported packs."));
                p.Section(T("Quit"));
                p.Button(T("Quit Soundboard"), QuitApp, T("Closes Soundboard completely (hotkeys stop working until it starts again)."));
                if (Installer.IsInstalled)
                    p.Button(T("Uninstall Soundboard…"), () =>
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe, "--uninstall-app") { UseShellExecute = true }),
                        T("Restores your original microphone and removes Soundboard from this PC."));
                break;
        }

        host.ResumeLayout();
        Theme.Apply(host);
    }

    // ------------------------------------------------------------ shortcuts table

    sealed record ShortcutRow(string Action, string Where, Hotkey Key, Action Edit, Action Clear, bool Global = false);

    List<ShortcutRow> ShortcutRows()
    {
        var rows = new List<ShortcutRow>
        {
            new(T("Pause (all sounds)"), T("General"), _config.PauseHotkey, () => EditGlobalHotkey(true),
                () => { _config.PauseHotkey = new Hotkey(); SaveConfig(); RegisterAllHotkeys(); }, Global: true),
            new(T("Stop (all sounds)"), T("General"), _config.StopHotkey, () => EditGlobalHotkey(false),
                () => { _config.StopHotkey = new Hotkey(); SaveConfig(); RegisterAllHotkeys(); }, Global: true),
            new(T("Push-to-talk"), T("General"), _config.PushToTalkKey, EditPushToTalkKey,
                () => { _config.PushToTalkKey = new Hotkey(); _config.PushToTalk = false; SaveConfig(); }, Global: true),
        };
        foreach (var s in _config.Sounds)
        {
            var entry = s;
            rows.Add(new(entry.Name, entry.Category.Length > 0 ? entry.Category : "—", entry.Hotkey,
                () => EditSoundHotkey(entry),
                () => { entry.Hotkey = new Hotkey(); SaveConfig(); RebuildGrid(); }));
        }
        return rows;
    }

    void FillShortcutsPage(PageBuilder p, Panel host)
    {
        p.Row(new Label { AutoSize = true, MaximumSize = new Size(p.Width, 0), Text = T("Double-click a row to change its key. Duplicate keys are shown in red.") });
        var onlySet = Check(T("Only show sounds that have a hotkey"), false, _ => { });
        p.Add(onlySet);

        var list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Width = p.Width, Height = Math.Max(200, host.ClientSize.Height - 210), BorderStyle = BorderStyle.FixedSingle,
        };
        list.Columns.Add(T("Action"), (int)(p.Width * 0.44));
        list.Columns.Add(T("Category"), (int)(p.Width * 0.20));
        list.Columns.Add(T("Key"), (int)(p.Width * 0.32));
        p.Add(list);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var edit = new Button { Text = T("Edit…"), AutoSize = true, Enabled = false };
        var clear = new Button { Text = T("Clear"), AutoSize = true, Enabled = false, Margin = new Padding(6, 3, 3, 3) };
        buttons.Controls.Add(edit);
        buttons.Controls.Add(clear);
        p.Add(buttons);

        List<ShortcutRow> rows = new();
        void Fill()
        {
            rows = ShortcutRows();
            var counts = rows.Where(r => r.Key.IsSet).GroupBy(r => (r.Key.Modifiers, r.Key.Key)).ToDictionary(g => g.Key, g => g.Count());
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows)
            {
                if (onlySet.Checked && !r.Key.IsSet && !r.Global) continue;
                bool conflict = r.Key.IsSet && counts[(r.Key.Modifiers, r.Key.Key)] > 1;
                var item = new ListViewItem(new[] { r.Action, r.Where, r.Key.IsSet ? r.Key.ToString() + (conflict ? T("  ⚠ duplicate") : "") : "—" }) { Tag = r };
                if (conflict) item.ForeColor = Theme.Current.Bad;
                else if (!r.Key.IsSet) item.ForeColor = Theme.Current.SubText;
                list.Items.Add(item);
            }
            list.EndUpdate();
            edit.Enabled = clear.Enabled = false;
        }

        ShortcutRow? Selected() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ShortcutRow : null;
        void DoEdit() { if (Selected() is { } r) { r.Edit(); Fill(); } }
        list.SelectedIndexChanged += (_, _) => { var r = Selected(); edit.Enabled = r != null; clear.Enabled = r?.Key.IsSet == true; };
        list.DoubleClick += (_, _) => DoEdit();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) DoEdit(); if (e.KeyCode == Keys.Delete && Selected() is { } r) { r.Clear(); Fill(); } };
        edit.Click += (_, _) => DoEdit();
        clear.Click += (_, _) => { if (Selected() is { } r) { r.Clear(); Fill(); } };
        onlySet.CheckedChanged += (_, _) => Fill();
        Fill();
    }
}
