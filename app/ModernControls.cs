using System.Drawing.Drawing2D;

namespace Soundboard;

/// <summary>
/// Glyphs of the Windows icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10):
/// one consistent family and size for every icon, unlike emoji or text symbols.
/// </summary>
static class Icons
{
    public const string All = "";        // bulleted list
    public const string Favourite = "";  // star
    public const string Category = "";   // folder
    public const string Add = "";        // plus
    public const string Settings = "";   // gear
    public const string Help = "";       // question mark in a circle
    public const string Pause = "";
    public const string Stop = "";
}

static class Shapes
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// One row of the side bar (a category, "New category", "Settings"...):
/// rounded highlight when selected or hovered, label on the left, count on the right.
/// </summary>
sealed class SidebarItem : Control
{
    bool _selected, _hover, _dropTarget;
    string _count = "";

    public SidebarItem(string text, string count = "")
    {
        Text = text;
        _count = count;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
        Height = 34;
        Margin = new Padding(0, 1, 0, 1);
    }

    /// <summary>Number shown on the right (sounds in the category).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Count { get => _count; set { _count = value; Invalidate(); } }

    /// <summary>Accent text instead of the normal colour ("＋ New category").</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    /// <summary>Highlighted while a sound or a category is dragged over it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DropTarget { get => _dropTarget; set { if (_dropTarget != value) { _dropTarget = value; Invalidate(); } } }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnDragLeave(EventArgs e) { DropTarget = false; base.OnDragLeave(e); }
    protected override void OnDragDrop(DragEventArgs e) { DropTarget = false; base.OnDragDrop(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Parent?.BackColor ?? p.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color? bg = _selected ? p.Selected : _dropTarget ? Theme.Blend(p.Sidebar, p.Accent, 0.35) : _hover ? Theme.Blend(p.Sidebar, p.Selected, 0.55) : null;
        if (bg is Color c)
        {
            using var shape = Shapes.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 6);
            using var b = new SolidBrush(c);
            g.FillPath(b, shape);
        }
        if (_dropTarget)
        {
            using var shape = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 6);
            using var pen = new Pen(p.Accent, 1.5f);
            g.DrawPath(pen, shape);
        }

        Color text = Accent ? p.Accent : _selected || _hover ? p.Text : p.SubText;
        int countW = 0;
        if (_count.Length > 0)
        {
            countW = TextRenderer.MeasureText(_count, Font).Width + 4;
            TextRenderer.DrawText(g, _count, Font, new Rectangle(Width - countW - 8, 0, countW, Height), p.SubText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        // every row has its icon in the same fixed column, so all the labels start at the same x
        if (Icon.Length > 0)
            TextRenderer.DrawText(g, Icon, Theme.IconFont, new Rectangle(IconLeft, 0, IconBox, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int textLeft = IconLeft + IconBox + 10;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft - 8 - countW, Height), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    const int IconLeft = 10, IconBox = 18;

    /// <summary>A Windows icon-font glyph (see <see cref="Icons"/>), drawn at the same size on every row.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Icon { get; set; } = "";
}

/// <summary>
/// Thin slider (rounded track, filled part in the accent colour, round thumb).
/// The standard TrackBar cannot be coloured in the dark theme.
/// Mouse, wheel and arrow keys change the value.
/// </summary>
sealed class FlatSlider : Control
{
    int _value, _min, _max = 100;
    bool _drag, _hover;

    public event EventHandler? ValueChanged;

    public FlatSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Height = 24;
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Minimum { get => _min; set { _min = value; Value = _value; } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => _max; set { _max = value; Value = _value; } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    const int Thumb = 14;
    int TrackLeft => Thumb / 2 + 1;
    int TrackWidth => Math.Max(1, Width - Thumb - 2);
    float Fraction => _max == _min ? 0 : (_value - _min) / (float)(_max - _min);

    void SetFromX(int x) => Value = _min + (int)Math.Round((x - TrackLeft) / (double)TrackWidth * (_max - _min));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drag = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Value += e.Delta > 0 ? 5 : -5; }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= 1; e.Handled = true; break;
            case Keys.Right or Keys.Up: Value += 1; e.Handled = true; break;
            case Keys.PageDown: Value -= 10; e.Handled = true; break;
            case Keys.PageUp: Value += 10; e.Handled = true; break;
            case Keys.Home: Value = _min; e.Handled = true; break;
            case Keys.End: Value = _max; e.Handled = true; break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        var track = new RectangleF(TrackLeft, cy - 2, TrackWidth, 4);
        using (var shape = Shapes.Rounded(track, 2))
        using (var b = new SolidBrush(Theme.IsDark ? p.Surface : p.Border))
            g.FillPath(b, shape);
        float x = TrackLeft + TrackWidth * Fraction;
        if (x > TrackLeft + 1)
        {
            using var done = Shapes.Rounded(new RectangleF(TrackLeft, cy - 2, x - TrackLeft, 4), 2);
            using var b = new SolidBrush(p.Accent);
            g.FillPath(b, done);
        }
        float r = _hover || _drag || Focused ? Thumb / 2f + 1 : Thumb / 2f;
        using (var thumb = new SolidBrush(Color.White)) g.FillEllipse(thumb, x - r, cy - r, r * 2, r * 2);
        if (!Theme.IsDark && !Focused)
        {
            using var edge = new Pen(Theme.Blend(p.Border, Color.Black, 0.15), 1f); // white thumb on a light page
            g.DrawEllipse(edge, x - r, cy - r, r * 2, r * 2);
        }
        if (Focused)
        {
            using var ring = new Pen(p.Accent, 2f);
            g.DrawEllipse(ring, x - r, cy - r, r * 2, r * 2);
        }
    }
}
