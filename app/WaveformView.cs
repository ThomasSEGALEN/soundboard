namespace Soundboard;

/// <summary>Waveform with start / end markers; the trimmed-out parts are dimmed.</summary>
sealed class WaveformView : Control
{
    readonly float[] _data;
    readonly double _duration;
    float[]? _min, _max; // per-pixel peaks, recomputed when the width changes
    double _start, _end;
    int _drag; // 0 none, 1 start, 2 end

    public event Action? RangeChanged;
    public double Start => _start;
    public double End => _end;

    public WaveformView(float[] stereo)
    {
        _data = stereo;
        _duration = stereo.Length / 2.0 / AudioEngine.Rate;
        _end = _duration;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.IBeam;
    }

    public void SetRange(double start, double end)
    {
        _start = Math.Clamp(start, 0, _duration);
        _end = Math.Clamp(end, _start, _duration);
        Invalidate();
    }

    int X(double t) => (int)(t / Math.Max(1e-9, _duration) * (Width - 1));
    double T(int x) => Math.Clamp(x / (double)Math.Max(1, Width - 1) * _duration, 0, _duration);

    void ComputePeaks()
    {
        int w = Math.Max(1, Width);
        _min = new float[w];
        _max = new float[w];
        int frames = _data.Length / 2;
        for (int x = 0; x < w; x++)
        {
            int a = (int)((long)x * frames / w), b = Math.Max(a + 1, (int)((long)(x + 1) * frames / w));
            float lo = 0, hi = 0;
            for (int i = a; i < b && i < frames; i++)
            {
                float v = (_data[i * 2] + _data[i * 2 + 1]) * 0.5f;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            _min[x] = lo;
            _max[x] = hi;
        }
    }

    protected override void OnResize(EventArgs e) { _min = null; base.OnResize(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        if (_min == null || _min.Length != Math.Max(1, Width)) ComputePeaks();
        g.Clear(p.Surface);
        int mid = Height / 2;
        int xs = X(_start), xe = X(_end);
        using var inPen = new Pen(p.Accent);
        using var outPen = new Pen(Theme.Blend(p.Surface, p.SubText, 0.45));
        for (int x = 0; x < Width; x++)
        {
            int y1 = mid - (int)(_max![x] * (mid - 2)), y2 = mid - (int)(_min![x] * (mid - 2));
            g.DrawLine(x >= xs && x <= xe ? inPen : outPen, x, y1, x, Math.Max(y1 + 1, y2));
        }
        // dim the trimmed-out parts
        using (var dim = new SolidBrush(Color.FromArgb(70, p.Back)))
        {
            g.FillRectangle(dim, 0, 0, xs, Height);
            g.FillRectangle(dim, xe, 0, Width - xe, Height);
        }
        using var marker = new Pen(p.Text, 2);
        g.DrawLine(marker, xs, 0, xs, Height);
        g.DrawLine(marker, xe, 0, xe, Height);
        using var border = new Pen(p.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Loc.T("Left click: start · right click: end"), Font, new Point(6, 4), p.SubText);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int xs = X(_start), xe = X(_end);
        if (e.Button == MouseButtons.Left)
        {
            // grab the closest marker if near one, otherwise place the start here
            if (Math.Abs(e.X - xe) < 6 && Math.Abs(e.X - xe) <= Math.Abs(e.X - xs)) _drag = 2;
            else { _drag = 1; _start = Math.Min(T(e.X), _end - 0.05); }
        }
        else if (e.Button == MouseButtons.Right) { _drag = 2; _end = Math.Max(T(e.X), _start + 0.05); }
        Invalidate();
        RangeChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Math.Abs(e.X - X(_start)) < 6 || Math.Abs(e.X - X(_end)) < 6 ? Cursors.SizeWE : Cursors.IBeam;
        if (_drag == 0) return;
        double t = T(e.X);
        if (_drag == 1) _start = Math.Clamp(t, 0, _end - 0.05);
        else _end = Math.Clamp(t, _start + 0.05, _duration);
        Invalidate();
        RangeChanged?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = 0; }
}
