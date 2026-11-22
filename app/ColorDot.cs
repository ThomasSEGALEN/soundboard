using System.Drawing.Drawing2D;

namespace Soundboard;

/// <summary>Round colour swatches, drawn smooth (antialiased) instead of a pixelated ellipse.</summary>
static class ColorDot
{
    /// <summary>Menu icon (16 px): a filled circle with a thin darker outline.</summary>
    public static Bitmap Icon(int argb, int size = 16)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var c = Color.FromArgb(255, Color.FromArgb(argb));
        var r = new RectangleF(1.5f, 1.5f, size - 3f, size - 3f);
        using (var fill = new SolidBrush(c)) g.FillEllipse(fill, r);
        using (var pen = new Pen(Theme.Blend(c, Color.Black, 0.35), 1f)) g.DrawEllipse(pen, r);
        return bmp;
    }
}

/// <summary>Clickable colour circle for the sound editor (ring around the selected one).</summary>
sealed class ColorSwatch : Control
{
    public int Argb { get; }
    bool _selected, _hover;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    public ColorSwatch(int argb)
    {
        Argb = argb;
        Size = new Size(28, 28);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float pad = 4f;
        var dot = new RectangleF(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
        if (Argb == 0)
        {
            // "default": theme surface with a diagonal stroke
            using (var fill = new SolidBrush(p.Surface)) g.FillEllipse(fill, dot);
            using (var pen = new Pen(p.SubText, 1.5f)) g.DrawLine(pen, dot.Left + 4, dot.Bottom - 4, dot.Right - 4, dot.Top + 4);
            using (var pen = new Pen(p.Border, 1f)) g.DrawEllipse(pen, dot);
        }
        else
        {
            var c = Color.FromArgb(255, Color.FromArgb(Argb));
            using (var fill = new SolidBrush(c)) g.FillEllipse(fill, dot);
            using (var pen = new Pen(Theme.Blend(c, Color.Black, 0.3), 1f)) g.DrawEllipse(pen, dot);
        }

        if (_selected || _hover)
        {
            var ring = new RectangleF(1f, 1f, Width - 3f, Height - 3f);
            using var pen = new Pen(_selected ? p.Text : p.Border, _selected ? 2f : 1.5f);
            g.DrawEllipse(pen, ring);
        }
    }
}
