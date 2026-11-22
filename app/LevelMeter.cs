namespace Soundboard;

/// <summary>Two live bars: your voice after noise suppression, and everything sent (voice + sounds).</summary>
sealed class LevelMeter : Control
{
    float _voice, _output;
    static readonly Font LabelFont = new("Segoe UI", 7.75f);

    public LevelMeter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Feeds new peaks (0..1); bars rise instantly and fall smoothly.</summary>
    public void Push(float voice, float output)
    {
        float nv = Math.Max(voice, _voice * 0.82f);
        float no = Math.Max(output, _output * 0.82f);
        if (nv < 0.001f) nv = 0;   // below -60 dB: empty bar
        if (no < 0.001f) no = 0;
        // Always keep the decayed values (skipping small steps used to freeze the fall);
        // only the repaint is skipped when the bars would not visibly move.
        bool redraw = Bar(nv) != Bar(_voice) || Bar(no) != Bar(_output);
        _voice = nv;
        _output = no;
        if (redraw) Invalidate();
    }

    int Bar(float peak) => (int)(ToFraction(peak) * Math.Max(1, Width - 53));

    static float ToFraction(float peak)
    {
        if (peak <= 0.001f) return 0;
        float db = 20f * MathF.Log10(peak);   // -60 dB .. 0 dB
        return Math.Clamp((db + 60f) / 60f, 0f, 1f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.Clear(Parent?.BackColor ?? p.Back);
        int labelW = 46;
        int barH = Math.Max(4, (Height - 6) / 2);
        DrawBar(g, T("Voice"), _voice, 1, labelW, barH, p);
        DrawBar(g, T("Sent"), _output, barH + 5, labelW, barH, p);
    }

    void DrawBar(Graphics g, string label, float peak, int y, int labelW, int h, Palette p)
    {
        TextRenderer.DrawText(g, label, LabelFont, new Rectangle(0, y - 2, labelW, h + 4), p.SubText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var track = new RectangleF(labelW + 6, y, Width - labelW - 7, h);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape = Shapes.Rounded(track, h / 2f); // pill-shaped track
        using (var b = new SolidBrush(Theme.IsDark ? p.Surface : p.Border)) g.FillPath(b, shape);

        float f = ToFraction(peak);
        float w = track.Width * f;
        if (w <= 0) return;
        // green up to -12 dB, yellow to -3 dB, red above (clipped to the rounded track)
        float x12 = track.Width * (48f / 60f), x3 = track.Width * (57f / 60f);
        var old = g.Clip;
        g.SetClip(shape);
        using var green = new SolidBrush(p.Good);
        using var yellow = new SolidBrush(p.Warn);
        using var red = new SolidBrush(p.Bad);
        g.FillRectangle(green, track.X, track.Y, Math.Min(w, x12), track.Height);
        if (w > x12) g.FillRectangle(yellow, track.X + x12, track.Y, Math.Min(w, x3) - x12, track.Height);
        if (w > x3) g.FillRectangle(red, track.X + x3, track.Y, w - x3, track.Height);
        g.Clip = old;
    }
}
