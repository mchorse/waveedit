using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WaveEdit.Audio;

namespace WaveEdit.UI;

/// <summary>A half-open selected frame range [Start, End).</summary>
public readonly record struct SelRegion(long Start, long End)
{
    public long Length => Math.Max(0, End - Start);
    public bool Overlaps(SelRegion o) => Start <= o.End && o.Start <= End; // touching counts
}

/// <summary>
/// Scrollable, zoomable waveform display with sample-accurate selection. Renders an
/// amplitude envelope when zoomed out (via a min/max peak cache) and individual
/// samples — points and stems — when zoomed in past one sample per pixel.
/// </summary>
public sealed class WaveformView : Control
{
    // ---- model ----
    private AudioDocument? _doc;

    // ---- view transform ----
    // SamplesPerPixel >= 1  -> envelope mode (zoomed out)
    // SamplesPerPixel  < 1  -> sample mode  (zoomed in; >1 pixel per sample)
    private double _samplesPerPixel = 256;
    private double _firstVisible;       // leftmost visible frame (may be fractional)
    private float _ampScale = 1f;       // vertical amplitude zoom

    // ---- selection / cursor ----
    // Disjoint, sorted, non-overlapping selected regions. Shift+drag adds a region;
    // a plain drag replaces the set with one region.
    private readonly List<SelRegion> _regions = new();
    private long _cursor;
    private long _playhead = -1;

    // in-progress left-button drag
    private bool _selecting;        // Shift+drag building a selection region
    private bool _movingCursor;     // plain drag scrubbing the cursor
    private long _dragAnchor;
    private long _dragStart, _dragEnd;

    // middle-button panning
    private bool _panning;
    private int _panStartX;
    private double _panStartFirstVisible;

    // ---- peak cache ----
    private const int PeakBlock = 256;
    private float[][]? _peakMin;
    private float[][]? _peakMax;

    private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom };
    private bool _suppressScroll;

    private const int RulerHeight = 22;

    // ---- colors ----
    private readonly Color _cBg = Color.FromArgb(28, 30, 34);
    private readonly Color _cWave = Color.FromArgb(120, 200, 255);
    private readonly Color _cWaveRms = Color.FromArgb(60, 130, 190);
    private readonly Color _cAxis = Color.FromArgb(60, 64, 70);
    private readonly Color _cSel = Color.FromArgb(70, 90, 150, 230);
    private readonly Color _cSelEdge = Color.FromArgb(150, 180, 220);
    private readonly Color _cCursor = Color.FromArgb(230, 230, 120);
    private readonly Color _cPlay = Color.FromArgb(255, 90, 90);
    private readonly Color _cRuler = Color.FromArgb(40, 43, 48);
    private readonly Color _cText = Color.FromArgb(170, 175, 180);
    private readonly Color _cSample = Color.FromArgb(180, 230, 255);

    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    public event Action? CursorMoved;

    public WaveformView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = _cBg;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, _) =>
        {
            if (_suppressScroll) return;
            _firstVisible = _scroll.Value;
            Invalidate();
            ViewChanged?.Invoke();
        };
    }

    // ---------- public model API ----------

    public AudioDocument? Document => _doc;

    public void SetDocument(AudioDocument? doc, bool resetView)
    {
        _doc = doc;
        _regions.Clear();
        _cursor = 0;
        _playhead = -1;
        ReloadPeaks();
        if (resetView) ZoomFull();
        else { ClampView(); UpdateScrollBar(); Invalidate(); }
        SelectionChanged?.Invoke();
    }

    public long Length => _doc?.Length ?? 0;

    /// <summary>The disjoint selected regions, sorted ascending by start.</summary>
    public IReadOnlyList<SelRegion> Regions => _regions;
    public bool HasSelection => _regions.Count > 0;
    public int RegionCount => _regions.Count;

    /// <summary>Leftmost selected frame (bounding start), or the cursor when nothing is selected.</summary>
    public long SelectionStart => _regions.Count > 0 ? _regions[0].Start : _cursor;
    /// <summary>Rightmost selected frame (bounding end).</summary>
    public long SelectionEnd => _regions.Count > 0 ? _regions[^1].End : _cursor;
    /// <summary>Total selected frames across every region.</summary>
    public long TotalSelectedFrames
    {
        get { long t = 0; foreach (var r in _regions) t += r.Length; return t; }
    }

    public long CursorFrame => _cursor;
    public double SamplesPerPixel => _samplesPerPixel;
    public long PlayheadFrame => _playhead;

    /// <summary>Replace the whole selection with a single region.</summary>
    public void SetSelection(long start, long end)
    {
        _regions.Clear();
        AddRegionInternal(start, end);
        _cursor = SelectionStart;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    /// <summary>Add a region to the existing selection, merging any overlaps.</summary>
    public void AddRegion(long start, long end)
    {
        AddRegionInternal(start, end);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void AddRegionInternal(long start, long end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, 0, Length);
        if (end <= start) return;
        var add = new SelRegion(Math.Min(start, end), Math.Max(start, end));

        // merge with any overlapping/touching existing regions
        var merged = new List<SelRegion>();
        foreach (var r in _regions)
        {
            if (r.Overlaps(add))
                add = new SelRegion(Math.Min(add.Start, r.Start), Math.Max(add.End, r.End));
            else
                merged.Add(r);
        }
        merged.Add(add);
        merged.Sort((a, b) => a.Start.CompareTo(b.Start));
        _regions.Clear();
        _regions.AddRange(merged);
    }

    public void ClearSelection()
    {
        if (_regions.Count == 0) return;
        _regions.Clear();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void SelectAll() => SetSelection(0, Length);

    public void SetCursor(long frame)
    {
        _cursor = Math.Clamp(frame, 0, Length);
        _regions.Clear();
        Invalidate();
        SelectionChanged?.Invoke();
        CursorMoved?.Invoke();
    }

    public void SetPlayhead(long frame)
    {
        _playhead = frame;
        Invalidate();
    }

    /// <summary>Rebuild the min/max peak cache from the current document.</summary>
    public void ReloadPeaks()
    {
        if (_doc == null || _doc.Length == 0) { _peakMin = _peakMax = null; return; }
        int ch = _doc.ChannelCount;
        long blocks = (_doc.Length + PeakBlock - 1) / PeakBlock;
        _peakMin = new float[ch][];
        _peakMax = new float[ch][];
        for (int c = 0; c < ch; c++)
        {
            var mn = new float[blocks];
            var mx = new float[blocks];
            var data = _doc.Channels[c];
            for (long b = 0; b < blocks; b++)
            {
                long s = b * PeakBlock;
                long e = Math.Min(s + PeakBlock, data.LongLength);
                float lo = 0f, hi = 0f;
                for (long i = s; i < e; i++)
                {
                    float v = data[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                mn[b] = lo; mx[b] = hi;
            }
            _peakMin[c] = mn; _peakMax[c] = mx;
        }
    }

    // ---------- zoom / navigation ----------

    private int WaveHeight => Math.Max(1, ClientSize.Height - _scroll.Height - RulerHeight);
    private int WaveWidth => Math.Max(1, ClientSize.Width);
    private double VisibleFrames => WaveWidth * _samplesPerPixel;

    public void ZoomFull()
    {
        if (Length <= 0) { _samplesPerPixel = 256; _firstVisible = 0; }
        else { _samplesPerPixel = Math.Max((double)Length / WaveWidth, 1.0 / 64); _firstVisible = 0; }
        ClampView(); UpdateScrollBar(); Invalidate(); ViewChanged?.Invoke();
    }

    public void ZoomToSamples()
    {
        _samplesPerPixel = 1.0 / 16; // 16 px per sample
        CenterOn(HasSelection ? (SelectionStart + SelectionEnd) / 2 : _cursor);
    }

    public void ZoomToSelection()
    {
        if (!HasSelection) return;
        long span = SelectionEnd - SelectionStart;
        _samplesPerPixel = Math.Max((double)span / WaveWidth, 1.0 / 64);
        _firstVisible = SelectionStart;
        ClampView(); UpdateScrollBar(); Invalidate(); ViewChanged?.Invoke();
    }

    public void ZoomIn() => ZoomAround(WaveWidth / 2, 0.5);
    public void ZoomOut() => ZoomAround(WaveWidth / 2, 2.0);

    private void ZoomAround(int pixelX, double factor)
    {
        double anchorFrame = _firstVisible + pixelX * _samplesPerPixel;
        double minSpp = 1.0 / 64;                 // up to 64 px per sample
        double maxSpp = Length > 0 ? Math.Max(1, (double)Length / WaveWidth) : 1e9;
        _samplesPerPixel = Math.Clamp(_samplesPerPixel * factor, minSpp, Math.Max(minSpp, maxSpp));
        _firstVisible = anchorFrame - pixelX * _samplesPerPixel;
        ClampView(); UpdateScrollBar(); Invalidate(); ViewChanged?.Invoke();
    }

    private void CenterOn(long frame)
    {
        _firstVisible = frame - VisibleFrames / 2;
        ClampView(); UpdateScrollBar(); Invalidate(); ViewChanged?.Invoke();
    }

    /// <summary>Scroll the minimum amount so a frame is on screen.</summary>
    public void EnsureVisible(long frame)
    {
        if (frame < _firstVisible) _firstVisible = frame;
        else if (frame > _firstVisible + VisibleFrames) _firstVisible = frame - VisibleFrames * 0.9;
        ClampView(); UpdateScrollBar(); Invalidate();
    }

    public void AdjustAmplitude(float factor)
    {
        _ampScale = Math.Clamp(_ampScale * factor, 0.1f, 64f);
        Invalidate();
    }

    private void ClampView()
    {
        double maxStart = Math.Max(0, Length - VisibleFrames);
        if (_firstVisible > maxStart) _firstVisible = maxStart;
        if (_firstVisible < 0) _firstVisible = 0;
    }

    private void UpdateScrollBar()
    {
        _suppressScroll = true;
        long len = Length;
        int vis = (int)Math.Min(int.MaxValue, Math.Max(1, VisibleFrames));
        if (len <= 0)
        {
            _scroll.Enabled = false;
            _scroll.Maximum = 0; _scroll.LargeChange = 1; _scroll.Value = 0;
        }
        else
        {
            _scroll.Enabled = VisibleFrames < len;
            int max = (int)Math.Min(int.MaxValue, len);
            _scroll.Maximum = max;
            _scroll.LargeChange = vis;
            _scroll.SmallChange = Math.Max(1, vis / 16);
            int val = (int)Math.Clamp(_firstVisible, 0, Math.Max(0, max - vis));
            _scroll.Value = Math.Clamp(val, _scroll.Minimum, _scroll.Maximum);
        }
        _suppressScroll = false;
    }

    // ---------- coordinate mapping ----------

    private double FrameAtX(int x) => _firstVisible + x * _samplesPerPixel;
    private double XOfFrame(double frame) => (frame - _firstVisible) / _samplesPerPixel;

    // ---------- input ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // middle-button drag pans the timeline ("grab and scroll")
        if (e.Button == MouseButtons.Middle && _doc != null)
        {
            _panning = true;
            _panStartX = e.X;
            _panStartFirstVisible = _firstVisible;
            Cursor = Cursors.SizeWE;
            Capture = true;
            return;
        }

        if (e.Button != MouseButtons.Left || _doc == null) return;
        if (e.Y > WaveHeight + RulerHeight) return; // on scrollbar

        long frame = (long)Math.Round(Math.Clamp(FrameAtX(e.X), 0, Length));
        bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;

        if (shift)
        {
            // Shift+drag is the ONLY way to (add to the) selection.
            _selecting = true;
            _dragAnchor = frame;
            _dragStart = _dragEnd = frame;
            Capture = true;
        }
        else
        {
            // Plain LMB moves the cursor (and keeps moving it while dragged);
            // it never changes the selection. Use Ctrl+D to deselect everything.
            _movingCursor = true;
            _cursor = frame;
            Invalidate();
            CursorMoved?.Invoke();
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_panning && _doc != null)
        {
            // drag right -> content moves right -> view start decreases
            _firstVisible = _panStartFirstVisible - (e.X - _panStartX) * _samplesPerPixel;
            ClampView();
            UpdateScrollBar();
            Invalidate();
            ViewChanged?.Invoke();
            return;
        }

        if (_doc == null || (!_selecting && !_movingCursor)) return;

        // auto-scroll when dragging past an edge
        if (e.X < 0) _firstVisible -= _samplesPerPixel * 16;
        else if (e.X > WaveWidth) _firstVisible += _samplesPerPixel * 16;
        ClampView();

        long frame = (long)Math.Round(Math.Clamp(FrameAtX(e.X), 0, Length));

        if (_movingCursor)
        {
            _cursor = frame;
            UpdateScrollBar();
            Invalidate();
            CursorMoved?.Invoke();
            return;
        }

        _dragStart = Math.Min(_dragAnchor, frame);
        _dragEnd = Math.Max(_dragAnchor, frame);
        UpdateScrollBar();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && e.Button == MouseButtons.Middle)
        {
            _panning = false;
            Capture = false;
            Cursor = Cursors.Default;
            return;
        }
        if (_movingCursor && e.Button == MouseButtons.Left)
        {
            _movingCursor = false;
            Capture = false;
            return;
        }
        if (_selecting && e.Button == MouseButtons.Left)
        {
            _selecting = false;
            Capture = false;
            if (_dragEnd > _dragStart)
                AddRegionInternal(_dragStart, _dragEnd);   // commit (merges overlaps)
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_doc == null) { base.OnMouseWheel(e); return; }
        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            // horizontal scroll
            _firstVisible -= Math.Sign(e.Delta) * VisibleFrames * 0.15;
            ClampView(); UpdateScrollBar(); Invalidate(); ViewChanged?.Invoke();
        }
        else if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            AdjustAmplitude(e.Delta > 0 ? 1.25f : 0.8f);
        }
        else
        {
            ZoomAround(e.X, e.Delta > 0 ? 0.8 : 1.25);
        }
    }

    protected override bool IsInputKey(Keys keyData) => true;

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_cBg);
        int w = WaveWidth, h = WaveHeight;
        DrawRuler(g, w);

        if (_doc == null || _doc.Length == 0)
        {
            TextRenderer.DrawText(g, "No audio loaded — Ctrl+O to open, or record (F5).",
                Font, new Rectangle(0, RulerHeight, w, h), _cText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int ch = _doc.ChannelCount;
        int bandH = h / ch;

        // selection highlights (full height of wave area), one per region
        using (var selBrush = new SolidBrush(_cSel))
            foreach (var r in EnumerateVisibleRegions())
            {
                double x0 = XOfFrame(r.Start), x1 = XOfFrame(r.End);
                var rect = Rectangle.FromLTRB(
                    (int)Math.Max(0, Math.Floor(x0)), RulerHeight,
                    (int)Math.Min(w, Math.Ceiling(x1)), RulerHeight + h);
                if (rect.Width > 0) g.FillRectangle(selBrush, rect);
            }

        for (int c = 0; c < ch; c++)
        {
            int top = RulerHeight + c * bandH;
            DrawChannel(g, c, top, bandH, w);
        }

        DrawMarkers(g, w, h);
    }

    private void DrawChannel(Graphics g, int c, int top, int bandH, int w)
    {
        int mid = top + bandH / 2;
        float halfAmp = (bandH / 2f - 2f) * _ampScale;

        using (var axisPen = new Pen(_cAxis))
        {
            g.DrawLine(axisPen, 0, mid, w, mid);                 // zero axis
            g.DrawLine(axisPen, 0, top, w, top);                 // band separator
        }

        if (_samplesPerPixel >= 1.0) DrawEnvelope(g, c, mid, halfAmp, w);
        else DrawSamples(g, c, mid, halfAmp, w);
    }

    private float SampleToY(float v, int mid, float halfAmp)
    {
        float y = mid - v * halfAmp;
        if (y < mid - halfAmp) y = mid - halfAmp;
        if (y > mid + halfAmp) y = mid + halfAmp;
        return y;
    }

    private void DrawEnvelope(Graphics g, int c, int mid, float halfAmp, int w)
    {
        var data = _doc!.Channels[c];
        bool useCache = _samplesPerPixel >= PeakBlock && _peakMin != null;
        using var pen = new Pen(_cWave);

        for (int x = 0; x < w; x++)
        {
            long s0 = (long)(_firstVisible + x * _samplesPerPixel);
            long s1 = (long)(_firstVisible + (x + 1) * _samplesPerPixel);
            if (s0 >= data.LongLength) break;
            if (s1 > data.LongLength) s1 = data.LongLength;
            if (s1 <= s0) s1 = s0 + 1;

            float lo, hi;
            if (useCache) MinMaxFromCache(c, s0, s1, out lo, out hi);
            else MinMaxFromData(data, s0, s1, out lo, out hi);

            float yLo = SampleToY(hi, mid, halfAmp);
            float yHi = SampleToY(lo, mid, halfAmp);
            if (yHi - yLo < 1) yHi = yLo + 1;
            g.DrawLine(pen, x, yLo, x, yHi);
        }
    }

    private void MinMaxFromData(float[] data, long s0, long s1, out float lo, out float hi)
    {
        lo = 0f; hi = 0f;
        for (long i = s0; i < s1; i++)
        {
            float v = data[i];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
    }

    private void MinMaxFromCache(int c, long s0, long s1, out float lo, out float hi)
    {
        var mn = _peakMin![c]; var mx = _peakMax![c];
        long b0 = s0 / PeakBlock, b1 = (s1 - 1) / PeakBlock;
        lo = 0f; hi = 0f;
        for (long b = b0; b <= b1 && b < mn.LongLength; b++)
        {
            if (mn[b] < lo) lo = mn[b];
            if (mx[b] > hi) hi = mx[b];
        }
    }

    private void DrawSamples(Graphics g, int c, int mid, float halfAmp, int w)
    {
        var data = _doc!.Channels[c];
        double pps = 1.0 / _samplesPerPixel; // pixels per sample
        long first = (long)Math.Floor(_firstVisible);
        if (first < 0) first = 0;

        using var linePen = new Pen(_cWave) { LineJoin = LineJoin.Round };
        using var stemPen = new Pen(_cWaveRms);
        using var dotBrush = new SolidBrush(_cSample);

        float prevX = 0, prevY = 0;
        bool have = false;
        bool drawStems = pps >= 10;
        bool drawDots = pps >= 6;
        float dotR = pps >= 14 ? 3.5f : 2.5f;

        for (long i = first; i < data.LongLength; i++)
        {
            float x = (float)XOfFrame(i);
            if (x > w + 4) break;
            float y = SampleToY(data[i], mid, halfAmp);

            if (have) g.DrawLine(linePen, prevX, prevY, x, y);
            if (drawStems) g.DrawLine(stemPen, x, mid, x, y);
            if (drawDots) g.FillEllipse(dotBrush, x - dotR, y - dotR, dotR * 2, dotR * 2);

            prevX = x; prevY = y; have = true;
        }
    }

    /// <summary>Committed regions plus the in-progress drag region (if any).</summary>
    private IEnumerable<SelRegion> EnumerateVisibleRegions()
    {
        foreach (var r in _regions) yield return r;
        if (_selecting && _dragEnd > _dragStart) yield return new SelRegion(_dragStart, _dragEnd);
    }

    private void DrawMarkers(Graphics g, int w, int h)
    {
        // selection edges for every region
        using (var edge = new Pen(_cSelEdge))
            foreach (var r in EnumerateVisibleRegions())
            {
                float x0 = (float)XOfFrame(r.Start), x1 = (float)XOfFrame(r.End);
                g.DrawLine(edge, x0, RulerHeight, x0, RulerHeight + h);
                g.DrawLine(edge, x1, RulerHeight, x1, RulerHeight + h);
            }

        // cursor is always shown (plain LMB moves it; it no longer affects selection)
        using (var cur = new Pen(_cCursor))
        {
            float x = (float)XOfFrame(_cursor);
            g.DrawLine(cur, x, RulerHeight, x, RulerHeight + h);
        }

        if (_playhead >= 0)
        {
            using var pp = new Pen(_cPlay, 1.5f);
            float x = (float)XOfFrame(_playhead);
            g.DrawLine(pp, x, RulerHeight, x, RulerHeight + h);
        }
    }

    private void DrawRuler(Graphics g, int w)
    {
        using (var b = new SolidBrush(_cRuler)) g.FillRectangle(b, 0, 0, w, RulerHeight);
        if (_doc == null || _doc.SampleRate <= 0) return;

        int sr = _doc.SampleRate;
        bool sampleUnits = _samplesPerPixel < 1.0; // show sample indices when very zoomed in
        using var pen = new Pen(_cAxis);
        using var brush = new SolidBrush(_cText);

        // choose a tick spacing of ~80 px
        double framesPerTick = NiceStep(_samplesPerPixel * 80);
        double startFrame = Math.Floor(_firstVisible / framesPerTick) * framesPerTick;

        for (double f = startFrame; ; f += framesPerTick)
        {
            double x = XOfFrame(f);
            if (x > w) break;
            if (x < 0) continue;
            g.DrawLine(pen, (float)x, RulerHeight - 6, (float)x, RulerHeight);
            string label = sampleUnits
                ? ((long)Math.Round(f)).ToString()
                : FormatTime(f / sr);
            g.DrawString(label, Font, brush, (float)x + 2, 2);
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw < 1) raw = 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return step * mag;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 1) return $"{seconds * 1000:0}ms";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        return $"{ts.Seconds}.{ts.Milliseconds:000}s";
    }
}
