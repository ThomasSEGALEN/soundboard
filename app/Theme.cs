using System.Runtime.InteropServices;

namespace Soundboard;

sealed record Palette(
    Color Back, Color Surface, Color SurfaceHover, Color Text, Color SubText,
    Color Accent, Color Border, Color Paused, Color Good, Color Warn, Color Bad,
    Color Sidebar, Color Selected);

/// <summary>Light / dark colours, applied to standard controls; custom controls read <see cref="Current"/>.</summary>
static class Theme
{
    // light twin of the dark theme: same blurple accent, cool greys
    public static readonly Palette Light = new(
        Back: Color.FromArgb(248, 249, 251), Surface: Color.White, SurfaceHover: Color.FromArgb(235, 237, 254),
        Text: Color.FromArgb(30, 31, 34), SubText: Color.FromArgb(92, 94, 102),
        Accent: Color.FromArgb(88, 101, 242), Border: Color.FromArgb(218, 220, 226),
        Paused: Color.FromArgb(200, 120, 0), Good: Color.FromArgb(36, 128, 70),
        Warn: Color.FromArgb(178, 110, 0), Bad: Color.FromArgb(218, 55, 60),
        Sidebar: Color.FromArgb(235, 237, 241), Selected: Color.FromArgb(214, 217, 225));

    // "gaming" dark: deep greys, blurple accent, side bar a step darker than the sounds
    public static readonly Palette Dark = new(
        Back: Color.FromArgb(49, 51, 56), Surface: Color.FromArgb(30, 31, 34), SurfaceHover: Color.FromArgb(78, 80, 88),
        Text: Color.FromArgb(242, 243, 245), SubText: Color.FromArgb(181, 186, 193),
        Accent: Color.FromArgb(88, 101, 242), Border: Color.FromArgb(30, 31, 34),
        Paused: Color.FromArgb(240, 178, 50), Good: Color.FromArgb(35, 165, 90),
        Warn: Color.FromArgb(240, 178, 50), Bad: Color.FromArgb(242, 63, 67),
        Sidebar: Color.FromArgb(43, 45, 49), Selected: Color.FromArgb(64, 66, 73));

    /// <summary>Windows 11 font when present, else Segoe UI.</summary>
    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        var f = new Font("Segoe UI Variable Text", size, style);
        if (f.Name == "Segoe UI Variable Text") return f;
        f.Dispose();
        return new Font("Segoe UI", size, style);
    }

    /// <summary>The Windows icon font at the size used everywhere (side bar, icon buttons).</summary>
    public static Font IconFont { get; } = MakeIconFont(10.5f);

    static Font MakeIconFont(float size)
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var f = new Font(name, size);
            if (f.Name == name) return f;
            f.Dispose();
        }
        return new Font("Segoe UI Symbol", size);
    }

    public static Palette Current { get; private set; } = Light;
    public static bool IsDark => Current == Dark;

    public static void Set(bool dark)
    {
        Current = dark ? Dark : Light;
        ToolStripManager.Renderer = new MenuRenderer();
    }

    /// <summary>Colours a control tree. Controls tagged "keep-color" keep their foreground.</summary>
    public static void Apply(Control root)
    {
        var p = Current;
        ApplyOne(root, p);
        foreach (Control c in root.Controls) Apply(c);
    }

    static void ApplyOne(Control c, Palette p)
    {
        switch (c)
        {
            case SoundButton or LevelMeter:
                c.Invalidate();
                return;
            case Button { Tag: int }: // colour swatch: keeps its own colour
                return;
            case Button { Tag: "accent" } a: // the main action (e.g. "Ajouter")
                a.FlatStyle = FlatStyle.Flat;
                a.BackColor = p.Accent;
                a.ForeColor = Color.White;
                a.FlatAppearance.BorderColor = Blend(p.Accent, Color.Black, 0.15);
                a.FlatAppearance.MouseOverBackColor = Blend(p.Accent, Color.White, 0.15);
                a.Font = new Font(a.Font, FontStyle.Bold);
                Rounded(a);
                return;
            case SidebarItem or FlatSlider:
                c.Invalidate();
                return;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = IsDark ? p.Selected : p.Surface;
                b.ForeColor = p.Text;
                b.FlatAppearance.BorderColor = IsDark ? b.BackColor : p.Border; // dark: filled, no outline
                b.FlatAppearance.MouseOverBackColor = p.SurfaceHover;
                Rounded(b);
                return;
            case CheckBox { Appearance: Appearance.Button } t:
                t.FlatStyle = FlatStyle.Flat;
                t.BackColor = IsDark ? p.Selected : p.Surface;
                t.ForeColor = p.Text;
                t.FlatAppearance.BorderColor = p.Border;
                t.FlatAppearance.CheckedBackColor = p.Accent;
                t.FlatAppearance.MouseOverBackColor = p.SurfaceHover;
                Rounded(t);
                return;
            case RadioButton { Appearance: Appearance.Button } r:
                r.FlatStyle = FlatStyle.Flat;
                r.BackColor = IsDark ? p.Selected : p.Surface;
                r.ForeColor = p.Text;
                r.FlatAppearance.BorderColor = p.Border;
                r.FlatAppearance.CheckedBackColor = p.Accent;
                r.FlatAppearance.MouseOverBackColor = p.SurfaceHover;
                Rounded(r);
                return;
            case SearchField sf:
                sf.BackColor = p.Surface;
                sf.Invalidate();
                return;
            case TextBox { Parent: SearchField } inner: // borderless inside its frame
                inner.BackColor = p.Surface;
                inner.ForeColor = p.Text;
                return;
            case TextBox or ComboBox:
                c.BackColor = p.Surface;
                c.ForeColor = p.Text;
                if (c is ComboBox cb)
                {
                    cb.FlatStyle = FlatStyle.Standard; // Flat ignores owner drawing of the edit area
                    if (cb.DrawMode != DrawMode.OwnerDrawFixed)
                    {
                        cb.DrawMode = DrawMode.OwnerDrawFixed;
                        cb.DrawItem += DrawComboItem;
                    }
                }
                if (c is TextBox tb) tb.BorderStyle = IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                return;
            default:
                c.BackColor = OnSidebar(c) ? p.Sidebar : p.Back;
                if (!(c.Tag is string tag && tag.StartsWith("keep-color"))) c.ForeColor = p.Text;
                return;
        }
    }

    // ------------------------------------------------------------ rounded buttons

    public const int ButtonRadius = 6;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ButtonBase, object> RoundedOnes = new();

    /// <summary>
    /// Flat buttons and toggles are redrawn with rounded corners (anti-aliased), once per control.
    /// The colours still come from BackColor / FlatAppearance, so hover, checked and accent keep working.
    /// </summary>
    static void Rounded(ButtonBase b)
    {
        if (RoundedOnes.TryGetValue(b, out _)) return;
        RoundedOnes.Add(b, new object());
        b.Paint += PaintRounded;
        b.EnabledChanged += (_, _) => b.Invalidate();
    }

    static void PaintRounded(object? sender, PaintEventArgs e)
    {
        if (sender is not ButtonBase b) return;
        var p = Current;
        var g = e.Graphics;
        g.Clear(b.Parent?.BackColor ?? p.Back);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        bool on = b is CheckBox { Checked: true } || b is RadioButton { Checked: true };
        bool hover = b.Enabled && b.ClientRectangle.Contains(b.PointToClient(Control.MousePosition));
        bool down = hover && (Control.MouseButtons & MouseButtons.Left) != 0;
        var fa = b.FlatAppearance;
        Color fill = on && !fa.CheckedBackColor.IsEmpty ? fa.CheckedBackColor
            : down && !fa.MouseDownBackColor.IsEmpty ? fa.MouseDownBackColor
            : down ? Blend(b.BackColor, IsDark ? Color.White : Color.Black, 0.10)
            : hover && !fa.MouseOverBackColor.IsEmpty ? fa.MouseOverBackColor
            : b.BackColor;
        if (on && hover) fill = Blend(fill, Color.White, 0.10);

        var r = new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f);
        using (var shape = Shapes.Rounded(r, ButtonRadius))
        {
            using (var bg = new SolidBrush(fill)) g.FillPath(bg, shape);
            Color edge = on ? fill : fa.BorderColor;
            if (!edge.IsEmpty && edge.ToArgb() != fill.ToArgb())
                using (var pen = new Pen(edge)) g.DrawPath(pen, shape);
        }

        Color text = !b.Enabled ? p.SubText : on ? Color.White : b.ForeColor;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine; // AutoSize already fits the text
        if (!b.UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        var area = b.ClientRectangle;
        if (b is IconButton)
            flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding; // symbol in the exact centre
        else
        {
            area = new Rectangle(area.X + b.Padding.Left, area.Y + b.Padding.Top, area.Width - b.Padding.Horizontal, area.Height - b.Padding.Vertical);
            flags |= b.TextAlign switch
            {
                ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
                ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
                _ => TextFormatFlags.HorizontalCenter,
            };
        }
        if (b.Image != null)
        {
            var img = b.Image;
            g.DrawImage(img, area.X, area.Y + (area.Height - img.Height) / 2, img.Width, img.Height);
            area = new Rectangle(area.X + img.Width + 4, area.Y, area.Width - img.Width - 4, area.Height);
        }
        TextRenderer.DrawText(g, b.Text, b.Font, area, text, flags);

        // keyboard focus: thin accent ring (not after a mouse click)
        if (b.Focused && LastInputWasKeyboard)
        {
            using var ring = Shapes.Rounded(new RectangleF(1.5f, 1.5f, b.Width - 3.5f, b.Height - 3.5f), ButtonRadius - 1);
            using var pen = new Pen(p.Accent, 2f);
            g.DrawPath(pen, ring);
        }
    }

    /// <summary>Set by the main window's key handling: the focus ring shows only when navigating with the keyboard.</summary>
    public static bool LastInputWasKeyboard { get; set; }

    /// <summary>Inside a container tagged "sidebar" (side bar, bottom bar): painted in the sidebar colour.</summary>
    public static bool OnSidebar(Control c)
    {
        for (var k = c; k != null; k = k.Parent)
            if (k.Tag is "sidebar") return true;
        return false;
    }

    // Combo boxes ignore BackColor for the selected item, so they are drawn here.
    static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb || e.Index < 0) return;
        var p = Current;
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(selected ? p.Accent : p.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString(), e.Font ?? cb.Font, e.Bounds,
            selected ? Color.White : p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light window title bar (Windows 10 2004+ / 11).</summary>
    public static void ApplyTitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
        // Windows 11: title bar in the side bar colour (ignored on Windows 10)
        var c = Current.Sidebar;
        int bgr = c.R | (c.G << 8) | (c.B << 16);
        DwmSetWindowAttribute(f.Handle, 35, ref bgr, sizeof(int));
    }

    /// <summary>Readable text colour on top of an arbitrary background.</summary>
    public static Color TextOn(Color bg)
    {
        double l = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255;
        return l > 0.6 ? Color.FromArgb(25, 25, 25) : Color.White;
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Current.Text : Current.SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Current.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
            using var bg = new SolidBrush(Blend(Current.Surface, Current.Accent, 0.35));
            e.Graphics.FillRectangle(bg, r);
            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, Current.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        static Palette P => Current;
        public override Color ToolStripDropDownBackground => P.Surface;
        public override Color ImageMarginGradientBegin => P.Surface;
        public override Color ImageMarginGradientMiddle => P.Surface;
        public override Color ImageMarginGradientEnd => P.Surface;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => P.Accent;
        public override Color MenuItemSelected => P.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => P.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => P.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => P.SurfaceHover;
        public override Color MenuItemPressedGradientEnd => P.SurfaceHover;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Border;
        public override Color CheckBackground => P.SurfaceHover;
        public override Color CheckSelectedBackground => P.SurfaceHover;
    }
}

/// <summary>Tracks whether the last input was the keyboard (Tab / arrows) or the mouse, for focus rings.</summary>
sealed class InputModeFilter : IMessageFilter
{
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;
        if (m.Msg == WM_KEYDOWN)
        {
            var k = (Keys)(int)m.WParam;
            if (k is Keys.Tab or Keys.Left or Keys.Right or Keys.Up or Keys.Down) Theme.LastInputWasKeyboard = true;
        }
        else if (m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN) Theme.LastInputWasKeyboard = false;
        return false;
    }
}
