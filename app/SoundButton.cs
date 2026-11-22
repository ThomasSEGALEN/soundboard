using System.Drawing.Drawing2D;

namespace Soundboard;

/// <summary>
/// One sound tile: name, hotkey, a progress gauge that fills while it plays, remaining time,
/// a "×N" badge when several copies play, custom colour, and drag-and-drop reordering.
/// Left click = play (according to the click mode), middle click = stop this sound.
/// </summary>
sealed class SoundButton : Control
{
    public SoundEntry Entry { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ClickMode EffectiveMode { get; set; }

    public event EventHandler? PlayRequested;
    public event EventHandler? StopRequested;
    public event Action<SoundButton, SoundButton>? Reordered;   // (moved, dropped onto)
    public event Action<string[]>? FilesDropped;

    PlayInfo _info;
    double? _duration;
    bool _hover, _pressed, _dropTarget;
    Point _downAt;

    static readonly Font SmallFont = new("Segoe UI", 7.75f);
    static readonly Font BadgeFont = new("Segoe UI", 7.75f, FontStyle.Bold);

    public SoundButton(SoundEntry entry)
    {
        Entry = entry;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        // not focusable: a scrolling list jumps to the focused control (on start and on every click)
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        Margin = new Padding(5);
    }

    /// <summary>Updates the playback state; repaints only when something visible changed.</summary>
    public void SetState(PlayInfo info, double? duration)
    {
        bool changed = info.Count != _info.Count || info.Paused != _info.Paused || duration != _duration ||
                       (int)(info.Position * 20) != (int)(_info.Position * 20);
        _info = info;
        _duration = duration;
        if (changed) Invalidate();
    }

    public bool IsPlaying => _info.Count > 0;

    /// <summary>Mouse is over the button (F2 renames the hovered sound).</summary>
    public bool IsHovered => _hover;

    bool _assignMode, _assignTarget;

    /// <summary>"⌨ Touches" mode: the key is shown big instead of the progress details.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AssignMode { get => _assignMode; set { if (_assignMode != value) { _assignMode = value; Invalidate(); } } }

    /// <summary>The sound waiting for its key in "⌨ Touches" mode.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsAssignTarget { get => _assignTarget; set { if (_assignTarget != value) { _assignTarget = value; Invalidate(); } } }

    static readonly Font KeyFont = new("Segoe UI Semibold", 13f);

    bool _selected;

    /// <summary>Part of the multiple selection (Ctrl+click / Shift+click).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    // ------------------------------------------------------------ mouse / drag

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _pressed = true; _downAt = e.Location; }
        else if (e.Button == MouseButtons.Middle) StopRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        var d = SystemInformation.DragSize;
        if (Math.Abs(e.X - _downAt.X) > d.Width || Math.Abs(e.Y - _downAt.Y) > d.Height)
        {
            _pressed = false; // a drag is not a click
            DoDragDrop(this, DragDropEffects.Move);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _pressed && ClientRectangle.Contains(e.Location))
            PlayRequested?.Invoke(this, EventArgs.Empty);
        _pressed = false;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetData(typeof(SoundButton)) is SoundButton other && other != this)
        {
            e.Effect = DragDropEffects.Move;
            _dropTarget = true;
            Invalidate();
        }
        else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropTarget = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _dropTarget = false;
        Invalidate();
        if (e.Data?.GetData(typeof(SoundButton)) is SoundButton other && other != this) Reordered?.Invoke(other, this);
        else if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files);
    }

    // ------------------------------------------------------------ painting

    static string Time(double s)
    {
        if (s < 0) s = 0;
        int t = (int)Math.Round(s);
        return $"{t / 60}:{t % 60:00}";
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Tile without its own colour: white in the light theme, a raised grey in the dark one.</summary>
    static Color DefaultTile(Palette pal) => Theme.IsDark ? pal.Selected : pal.Surface;

    /// <summary>"⌨ Touches" mode: name small at the top, the key big in the middle.</summary>
    void PaintAssign(Graphics g, Palette pal)
    {
        bool custom = Entry.Color != 0;
        Color baseColor = custom ? Theme.Blend(Color.FromArgb(255, Color.FromArgb(Entry.Color)), DefaultTile(pal), 0.55) : DefaultTile(pal);
        if (_assignTarget) baseColor = Theme.Blend(baseColor, pal.Accent, 0.12);
        else if (_hover) baseColor = Theme.Blend(baseColor, pal.Accent, 0.06);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var shape = Rounded(rect, 8);
        using (var bg = new SolidBrush(baseColor)) g.FillPath(bg, shape);
        using (var pen = _assignTarget ? new Pen(pal.Accent, 3f) { DashStyle = DashStyle.Dash } : new Pen(pal.Border, 1f))
            g.DrawPath(pen, shape);

        Color text = Theme.TextOn(baseColor);
        if (!custom) text = pal.Text;
        bool compact = Height < 60;
        var nameRect = new Rectangle(8, 3, Width - 16, compact ? 14 : 18);
        TextRenderer.DrawText(g, Entry.Name, SmallFont, nameRect, pal.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        string key = Entry.Hotkey.IsSet ? Entry.Hotkey.ToString() : "—";
        var keyRect = new Rectangle(4, nameRect.Bottom, Width - 8, Height - nameRect.Bottom - (compact ? 2 : 16));
        TextRenderer.DrawText(g, key, compact ? Font : KeyFont, keyRect, Entry.Hotkey.IsSet ? text : pal.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (_assignTarget && !compact)
            TextRenderer.DrawText(g, T("press a key…"), SmallFont, new Rectangle(0, Height - 18, Width, 16), pal.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = Theme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? pal.Back);
        if (_assignMode) { PaintAssign(g, pal); return; }

        bool custom = Entry.Color != 0;
        Color baseColor = custom ? Color.FromArgb(255, Color.FromArgb(Entry.Color)) : DefaultTile(pal);
        if (_hover) baseColor = Theme.Blend(baseColor, custom ? Color.White : pal.Accent, custom ? 0.15 : 0.10);
        Color text = custom ? Theme.TextOn(baseColor) : pal.Text;
        Color sub = custom ? Theme.Blend(text, baseColor, 0.25) : pal.SubText;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var shape = Rounded(rect, 10);
        using (var bg = new SolidBrush(baseColor)) g.FillPath(bg, shape);

        // progress gauge: tinted fill behind the text + solid bar at the bottom
        bool playing = _info.Count > 0 && _info.Length > 0;
        if (playing)
        {
            float frac = (float)Math.Clamp(_info.Position / _info.Length, 0, 1);
            Color accent = _info.Paused ? pal.Paused : (custom ? Theme.TextOn(baseColor) : pal.Accent);
            var old = g.Clip;
            g.SetClip(shape);
            using (var fill = new SolidBrush(Color.FromArgb(custom ? 55 : 45, accent)))
                g.FillRectangle(fill, 0, 0, Width * frac, Height);
            using (var bar = new SolidBrush(accent))
                g.FillRectangle(bar, 0, Height - 5, Width * frac, 5);
            g.Clip = old;
        }

        // border (accent while playing, selected or as drop target)
        Color playingBorder = custom ? Color.White : pal.Accent;
        Color border = _dropTarget || _selected ? (custom ? Color.White : pal.Accent) : playing ? (_info.Paused ? pal.Paused : playingBorder) : (custom ? Theme.Blend(baseColor, Color.Black, 0.2) : pal.Border);
        float borderW = _selected ? 3f : _dropTarget || playing ? 2f : 1f;
        using (var pen = new Pen(border, borderW)) g.DrawPath(pen, shape);
        if (_selected)
        {
            // check mark in the bottom-left corner
            var cr = new RectangleF(4, Height - 22, 16, 16);
            using (var cb = new SolidBrush(pal.Accent)) g.FillEllipse(cb, cr);
            TextRenderer.DrawText(g, "✓", BadgeFont, Rectangle.Round(cr), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        bool compact = Height < 60;
        int pad = compact ? 5 : 8;
        int footer = compact ? 13 : 17;

        // top-left: click-mode icon if this sound overrides the default
        string modeGlyph = Entry.ClickMode switch
        {
            ClickMode.PauseResume => "⏯",
            ClickMode.Restart => "↻",
            ClickMode.Overlay => "⧉",
            ClickMode.Stop => "⏹",
            _ => "",
        };
        int glyphX = pad - 2;
        if (Entry.Favorite)
        {
            TextRenderer.DrawText(g, "★", BadgeFont, new Point(glyphX, 3), custom ? text : Color.FromArgb(240, 180, 0), TextFormatFlags.NoPadding);
            glyphX += 13;
        }
        if (modeGlyph.Length > 0)
            TextRenderer.DrawText(g, modeGlyph, SmallFont, new Point(glyphX, 3), sub, TextFormatFlags.NoPadding);
        if (Entry.TrimStart > 0 || Entry.TrimEnd > 0)
            TextRenderer.DrawText(g, "✂", SmallFont, new Point(glyphX + (modeGlyph.Length > 0 ? 13 : 0), 3), sub, TextFormatFlags.NoPadding);

        // top-right: ×N badge when several copies play
        if (_info.Count > 1)
        {
            string badge = $"×{_info.Count}";
            var sz = TextRenderer.MeasureText(badge, BadgeFont, Size.Empty, TextFormatFlags.NoPadding);
            var br = new RectangleF(Width - sz.Width - 12, 4, sz.Width + 8, sz.Height + 2);
            using var bp = Rounded(br, (br.Height) / 2);
            using (var bb = new SolidBrush(pal.Accent)) g.FillPath(bb, bp);
            TextRenderer.DrawText(g, badge, BadgeFont, Rectangle.Round(br), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // name
        var nameRect = new Rectangle(pad, compact ? 2 : 6, Width - pad * 2, Height - footer - (compact ? 2 : 6));
        TextRenderer.DrawText(g, Entry.Name, Font, nameRect, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // footer: hotkey (left), time (right)
        int checkRoom = _selected ? 18 : 0; // keep the hotkey clear of the check mark
        var footRect = new Rectangle(pad + checkRoom, Height - footer - 3, Width - pad * 2 - checkRoom, footer);
        if (Entry.Hotkey.IsSet)
            TextRenderer.DrawText(g, Entry.Hotkey.ToString(), SmallFont, footRect, sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        string time = playing
            ? (_info.Paused ? "⏸ " : "") + $"{Time(_info.Position)} / {Time(_info.Length)}"
            : _duration is double dur ? Time(dur) : "";
        if (time.Length > 0)
            TextRenderer.DrawText(g, time, SmallFont, footRect, playing ? text : sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // volume hint when the sound is not at 100 %
        if (Math.Abs(Entry.Volume - 1f) > 0.005f && !compact)
            TextRenderer.DrawText(g, $"{Entry.Volume * 100:0} %", SmallFont,
                new Rectangle(0, Height - footer - 3, Width, footer), sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
