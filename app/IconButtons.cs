namespace Soundboard;

/// <summary>
/// Square buttons with a single symbol (?, ＋, ✕): same height as the text buttons next to them,
/// symbol drawn in the exact centre (a normal Button pads and offsets its text).
/// </summary>
sealed class IconButton : Button
{
    public IconButton(string glyph, int size)
    {
        Text = glyph;
        AutoSize = false;
        Size = new Size(size, size);
        Padding = Padding.Empty;
        TextAlign = ContentAlignment.MiddleCenter;
        UseMnemonic = false;
    }

    /// <summary>Height of an ordinary text button in this font: the reference for every icon button.</summary>
    public static int StandardHeight(Font font)
    {
        // measured with the flat style the theme gives every button (its border adds 2 px)
        using var probe = new Button { Font = font, Text = "Ag", AutoSize = true, FlatStyle = FlatStyle.Flat };
        return probe.GetPreferredSize(Size.Empty).Height;
    }

    // painting: the theme's rounded painter centres the symbol on the whole button (Theme.PaintRounded)
}

/// <summary>
/// Search field the height of the buttons: borderless text box + ✕ inside, one frame around both.
/// </summary>
sealed class SearchField : Panel
{
    public TextBox Box { get; }
    readonly Button _clear;

    public SearchField(string placeholder, int width, int height)
    {
        Size = new Size(width, height);
        Padding = new Padding(6, 0, 2, 0);
        Box = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = placeholder };
        _clear = new IconButton("✕", height - 6) { Visible = false, TabStop = false, Cursor = Cursors.Hand };
        _clear.Click += (_, _) => { Box.Text = ""; Box.Focus(); };
        Box.TextChanged += (_, _) => _clear.Visible = Box.TextLength > 0;
        Box.GotFocus += (_, _) => Invalidate();   // accent frame while typing
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Controls.Add(_clear);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Click += (_, _) => Box.Focus();
    }

    public Button ClearButton => _clear;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_clear == null || Box == null) return; // Size is set before the children exist
        int clearW = _clear.Width;
        _clear.Location = new Point(Width - clearW - 3, (Height - _clear.Height) / 2);
        Box.Width = Width - Padding.Left - clearW - 8;
        Box.Location = new Point(Padding.Left, (Height - Box.Height) / 2); // text vertically centred
    }

    // rounded frame like the buttons: page colour in the corners, field colour inside
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.ButtonRadius);
        using (var bg = new SolidBrush(BackColor)) g.FillPath(bg, shape);
        using var pen = new Pen(Box.Focused ? p.Accent : p.Border, Box.Focused ? 1.5f : 1f);
        g.DrawPath(pen, shape);
    }
}
