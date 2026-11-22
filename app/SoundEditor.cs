namespace Soundboard;

/// <summary>
/// "Modifier le son": every setting of one sound in one place, with a Listen button that plays
/// the current volume and trim. Nothing changes until OK (one Ctrl+Z step).
/// The window sizes itself from its content, and help texts sit under their field
/// (nothing can run past the right edge).
/// </summary>
sealed class SoundEditor : Form
{
    const int FieldWidth = 460; // width of the right column: help texts wrap inside it

    readonly SoundEntry _sound;
    readonly double _duration;
    readonly TextBox _name;
    readonly ComboBox _category, _mode;
    readonly TrackBar _volume;
    readonly Label _volumePct, _keyWarning, _length;
    readonly Button _key, _clearKey;
    readonly NumericUpDown _start, _end;
    readonly WaveformView _wave;
    readonly List<ColorSwatch> _swatches = new();
    readonly TableLayoutPanel _grid;
    readonly FlowLayoutPanel _buttonRow;
    readonly ToolTip _tips = new();
    int _color;
    Hotkey _hotkey;
    bool _syncing;

    /// <param name="preview">plays the sound with (volume, start, end)</param>
    /// <param name="pickKey">opens the key capture, returns null when cancelled</param>
    /// <param name="keyOwner">name of another sound / action already using a key, or null</param>
    public SoundEditor(SoundEntry sound, float[] data, IReadOnlyList<string> categories, string defaultModeName,
                       (string Name, int Argb)[] swatches,
                       Action<float, double, double> preview, Func<Hotkey, Hotkey?> pickKey, Func<Hotkey, string?> keyOwner)
    {
        _sound = sound;
        _duration = data.Length / 2.0 / AudioEngine.Rate;
        _color = sound.Color;
        _hotkey = sound.Hotkey;

        Text = T("Edit sound");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);

        _grid = new TableLayoutPanel
        {
            // Fill = the space above the OK / Cancel row: the content can never slide under the buttons
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16, 14, 16, 4),
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;

        Label Hint(string text) => new()
        {
            Text = text, AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Tag = "keep-color",
            ForeColor = Theme.Current.SubText, Margin = new Padding(0, 2, 0, 0),
        };

        // label | field (+ optional help text under the field)
        void AddRow(string label, Control field, string? hint = null)
        {
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 14, 6) }, 0, row);
            Control cell = field;
            if (hint != null)
            {
                var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
                field.Margin = Padding.Empty;
                stack.Controls.Add(field);
                stack.Controls.Add(Hint(hint));
                cell = stack;
            }
            cell.Margin = new Padding(0, 4, 0, 8);
            _grid.Controls.Add(cell, 1, row);
            row++;
        }

        // a row of controls whose children are all vertically centred on the same line
        FlowLayoutPanel Line(params Control[] items)
        {
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in items) line.Controls.Add(c);
            return line;
        }

        // ---- name
        _name = new TextBox { Text = sound.Name, Width = FieldWidth };
        AddRow(T("Name"), _name);

        // ---- category (type a new name to create one)
        _category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 240 };
        _category.Items.Add(T("None"));
        foreach (var c in categories) _category.Items.Add(c);
        _category.Text = sound.Category.Length > 0 ? sound.Category : T("None");
        AddRow(T("Category"), _category, T("Pick a category, or type a new name to create one."));

        // ---- colour: round swatches
        var swatchLine = Line();
        void AddSwatch(int argb, string tip)
        {
            var s = new ColorSwatch(argb) { Margin = new Padding(0, 0, 4, 0) };
            s.Click += (_, _) => { _color = argb; PaintSwatches(); };
            _tips.SetToolTip(s, tip);
            _swatches.Add(s);
            swatchLine.Controls.Add(s);
        }
        AddSwatch(0, T("Default"));
        foreach (var (name, argb) in swatches) AddSwatch(argb, T(name));
        var other = new Button { Text = T("Other…"), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        other.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = _color != 0 ? Color.FromArgb(_color) : Theme.Current.Accent };
            if (dlg.ShowDialog(this) == DialogResult.OK) { _color = dlg.Color.ToArgb(); PaintSwatches(); }
        };
        swatchLine.Controls.Add(other);
        AddRow(T("Colour"), swatchLine);

        // ---- volume + listen
        _volume = new TrackBar { Minimum = 0, Maximum = 200, TickFrequency = 25, Width = 300, Value = (int)Math.Clamp(sound.Volume * 100, 0, 200), AutoSize = false, Height = 36, Margin = Padding.Empty };
        _volumePct = new Label { AutoSize = true, Margin = new Padding(6, 8, 10, 0) };
        var listen = new Button { Text = T("▶ Listen"), AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        listen.Click += (_, _) => preview(_volume.Value / 100f, Start, End);
        _volume.ValueChanged += (_, _) => _volumePct.Text = $"{_volume.Value} %";
        _volumePct.Text = $"{_volume.Value} %";
        AddRow(T("Volume"), Line(_volume, _volumePct, listen), T("Plays the sound in your headset only (nobody else hears it), with this volume and trim."));

        // ---- hotkey: both buttons on the same baseline, warning under them
        _key = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(75, 0), Margin = new Padding(0, 0, 6, 0) };
        _clearKey = new Button { Text = T("Clear"), AutoSize = true, Margin = Padding.Empty };
        _keyWarning = Hint("");
        _keyWarning.ForeColor = Theme.Current.Bad;
        void ShowKey()
        {
            _key.Text = _hotkey.IsSet ? _hotkey.ToString() : T("Choose a key…");
            _clearKey.Enabled = _hotkey.IsSet;
            string? owner = _hotkey.IsSet ? keyOwner(_hotkey) : null;
            // the line is always there (empty when there is no conflict): the content never jumps
            _keyWarning.Text = owner != null ? T("⚠ Key already used by “{0}”.", owner) : " ";
            // a long warning can wrap on two lines: resize once the layout has settled
            if (IsHandleCreated) BeginInvoke(FitToContent);
        }
        _key.Click += (_, _) => { if (pickKey(_hotkey) is { } k) { _hotkey = k; ShowKey(); } };
        _clearKey.Click += (_, _) => { _hotkey = new Hotkey(); ShowKey(); };
        var keyStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        keyStack.Controls.Add(Line(_key, _clearKey));
        keyStack.Controls.Add(_keyWarning);
        ShowKey();
        AddRow(T("Hotkey"), keyStack);

        // ---- click mode
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        _mode.Items.AddRange(new object[] { T("Default ({0})", defaultModeName), T("Restart"), T("Pause"), T("Overlay"), T("Stop") });
        _mode.SelectedIndex = sound.ClickMode switch
        {
            ClickMode.Restart => 1, ClickMode.PauseResume => 2, ClickMode.Overlay => 3, ClickMode.Stop => 4, _ => 0,
        };
        AddRow(T("If already playing"), _mode, T("What a click on this sound does while it is playing."));

        // ---- trim on the waveform
        _wave = new WaveformView(data) { Width = FieldWidth, Height = 100 };
        decimal max = (decimal)Math.Round(_duration, 2);
        NumericUpDown Num(double v) => new()
        {
            DecimalPlaces = 2, Increment = 0.05m, Minimum = 0, Maximum = max, Width = 80, Margin = new Padding(0, 0, 0, 0),
            Value = Math.Clamp((decimal)Math.Round(v, 2), 0, max),
        };
        _start = Num(sound.TrimStart);
        _end = Num(sound.TrimEnd > 0 ? sound.TrimEnd : _duration);
        var reset = new Button { Text = T("Reset"), AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        reset.Click += (_, _) => { _start.Value = 0; _end.Value = max; };
        _length = Hint("");
        var trimStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        trimStack.Controls.Add(_wave);
        _wave.Margin = new Padding(0, 0, 0, 6);
        trimStack.Controls.Add(Line(
            new Label { Text = T("Start"), AutoSize = true, Margin = new Padding(0, 4, 6, 0) }, _start,
            new Label { Text = T("End"), AutoSize = true, Margin = new Padding(14, 4, 6, 0) }, _end, reset));
        trimStack.Controls.Add(_length);
        AddRow(T("Trim"), trimStack);

        void SyncTrim(bool fromWave)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                if (fromWave)
                {
                    _start.Value = Math.Clamp((decimal)Math.Round(_wave.Start, 2), 0, max);
                    _end.Value = Math.Clamp((decimal)Math.Round(_wave.End, 2), 0, max);
                }
                if (_end.Value <= _start.Value) _end.Value = Math.Min(max, _start.Value + 0.05m);
                _wave.SetRange((double)_start.Value, (double)_end.Value);
                _length.Text = T("Plays {0:0.00} s of {1:0.00} s", (double)(_end.Value - _start.Value), _duration);
            }
            finally { _syncing = false; }
        }
        _start.ValueChanged += (_, _) => SyncTrim(false);
        _end.ValueChanged += (_, _) => SyncTrim(false);
        _wave.RangeChanged += () => SyncTrim(true);
        SyncTrim(false);

        Controls.Add(_grid);
        var (ok, _) = DialogButtons.Add(this);
        _buttonRow = (FlowLayoutPanel)ok.Parent!;
        ok.Click += (_, _) => ApplyChanges();
        PaintSwatches();
        FitToContent();
    }

    /// <summary>Window size = what the content needs (any font size, any text length).</summary>
    bool _fitting;

    void FitToContent()
    {
        if (_fitting) return;
        _fitting = true;
        try
        {
            int width = Math.Max(_grid.GetPreferredSize(new Size(int.MaxValue, int.MaxValue)).Width, 480);
            // height measured at the real width, so wrapped texts are counted
            int content = _grid.GetPreferredSize(new Size(width, int.MaxValue)).Height;
            int buttons = _buttonRow.GetPreferredSize(new Size(width, int.MaxValue)).Height;
            var want = new Size(width, content + buttons);
            if (ClientSize != want) ClientSize = want;
        }
        finally { _fitting = false; }
    }

    /// <summary>For the tests: simulates choosing a key with the "Choose a key…" button.</summary>
    internal void ClickKeyButton() => _key.PerformClick();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToContent(); // again once the final fonts / theme are applied
    }

    double Start => (double)_start.Value;
    double End => (double)_end.Value >= _duration - 0.05 ? 0 : (double)_end.Value;

    void PaintSwatches()
    {
        foreach (var s in _swatches) s.Selected = s.Argb == _color;
    }

    /// <summary>The category typed or chosen ("" for none).</summary>
    public string CategoryChoice
    {
        get
        {
            string t = _category.Text.Trim();
            return t.Length == 0 || t == T("None") ? "" : t;
        }
    }

    void ApplyChanges()
    {
        string name = _name.Text.Trim();
        if (name.Length > 0) _sound.Name = name;
        _sound.Category = CategoryChoice;
        _sound.Color = _color;
        _sound.Volume = _volume.Value / 100f;
        _sound.Hotkey = _hotkey;
        _sound.ClickMode = _mode.SelectedIndex switch
        {
            1 => ClickMode.Restart, 2 => ClickMode.PauseResume, 3 => ClickMode.Overlay, 4 => ClickMode.Stop, _ => ClickMode.Default,
        };
        _sound.TrimStart = Start;
        _sound.TrimEnd = End;
    }
}
