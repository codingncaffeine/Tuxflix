using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// The bars: the classic players' analysers, blown up to the window, and their descendants.
internal sealed partial class VisualizerPainter
{
    private readonly SKPaint _pixel = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Bevel };
    private readonly float[] _pixelBands = new float[19];
    private readonly float[] _pixelPeaks = new float[19];
    private readonly float[] _ledBands = new float[24];
    private readonly float[] _ledPeaks = new float[24];
    private readonly SKPath _curve = new();
    private readonly SKPath _curveLine = new();
    private SKShader? _curveFill;
    private SKImage? _curveRamp;
    private (int Height, VisualizerPalette? Palette) _curveFillFor;

    private void DisposeBars()
    {
        _pixel.Dispose();
        _line.Dispose();
        _curve.Dispose();
        _curveLine.Dispose();
        _curveFill?.Dispose();
        _curveRamp?.Dispose();
    }

    // Square cells on whole pixels, as large as fit, centred.
    private static (float Cell, float Left, float Top) PixelGrid(int width, int height, int columns, int rows)
    {
        var cell = MathF.Max(1f, MathF.Floor(MathF.Min(width * 0.86f / columns, height * 0.72f / rows)));
        return (cell, MathF.Round((width - (cell * columns)) / 2), MathF.Round((height - (cell * rows)) / 2));
    }

    // The classic players' dotted ground: a dot on every other cell, dim, under whatever they drew.
    private void PixelGround(SKCanvas canvas, VisualizerPalette palette, float cell, float left, float top, int columns, int rows)
    {
        _pixel.Color = Mix(palette.Background, palette.Sample(0.2f), 0.3f);
        var dot = MathF.Max(1f, MathF.Round(cell * 0.16f));
        var inset = (cell - dot) / 2;
        for (var r = 1; r < rows; r += 2)
        {
            for (var c = 1; c < columns; c += 2) canvas.DrawRect(left + (c * cell) + inset, top + (r * cell) + inset, dot, dot, _pixel);
        }
    }

    // The classic player's analyser at the window's size: 19 bars three pixels wide with one
    // between, square pixels on a dotted ground, every row of pixels one colour so a tall bar
    // reaches the loud end of the ramp at its tip while a short one stays at the quiet end, and a
    // cap one pixel high that hangs, then falls.
    private void PixelBars(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Bars = 19;
        const int Wide = 3;
        const int Columns = (Bars * (Wide + 1)) - 1;
        const int Rows = 32;
        Flat(canvas, palette);
        var (cell, left, top) = PixelGrid(width, height, Columns, Rows);
        PixelGround(canvas, palette, cell, left, top, Columns, Rows);
        Regroup(frame.Bands, _pixelBands);
        Regroup(frame.Peaks, _pixelPeaks);
        var size = cell - MathF.Max(1f, MathF.Round(cell * 0.1f));
        for (var b = 0; b < Bars; b++)
        {
            var x = left + (b * (Wide + 1) * cell);
            var lit = (int)MathF.Round(_pixelBands[b] * Rows);
            for (var r = 0; r < lit; r++)
            {
                _pixel.Color = palette.Sample(r / (float)(Rows - 1));
                var y = top + ((Rows - 1 - r) * cell);
                for (var c = 0; c < Wide; c++) canvas.DrawRect(x + (c * cell), y, size, size, _pixel);
            }

            var peak = (int)MathF.Round(_pixelPeaks[b] * Rows) - 1;
            if (peak < lit || peak < 1) continue;
            _pixel.Color = palette.Highlight;
            for (var c = 0; c < Wide; c++) canvas.DrawRect(x + (c * cell), top + ((Rows - 1 - peak) * cell), size, size, _pixel);
        }
    }

    // Flat bars in one colour under caps that stand apart from them, on a plain ground: the
    // analyser whose bars never changed colour, only height.
    private void FlatBars(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        Flat(canvas, palette);
        var n = frame.Bands.Length;
        if (n == 0) return;
        var left = width * 0.06f;
        var slot = width * 0.88f / n;
        var bar = MathF.Max(1f, MathF.Round(slot * 0.72f));
        var floor = MathF.Round(height * 0.84f);
        var tallest = height * 0.66f;
        var cap = MathF.Max(2f, MathF.Round(height * 0.007f));
        _pixel.Color = palette.Stops[0];
        for (var b = 0; b < n; b++)
        {
            var h = MathF.Max(1f, MathF.Round(frame.Bands[b] * tallest));
            canvas.DrawRect(MathF.Round(left + (b * slot)), floor - h, bar, h, _pixel);
        }

        // A cap's height of air between each cap and its bar, always.
        _pixel.Color = palette.Highlight;
        for (var b = 0; b < n; b++)
        {
            var y = MathF.Round(floor - (frame.Peaks[b] * tallest) - (cap * 2));
            canvas.DrawRect(MathF.Round(left + (b * slot)), y, bar, cap, _pixel);
        }
    }

    // Each bar in its band's colour, the bass at the quiet end of the ramp and the treble at the
    // loud end, paling to its tip, the louder the paler. Slices, not a gradient per bar: a shader
    // per bar per frame is native memory for nothing.
    private void GradientBars(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Slices = 10;
        var n = frame.Bands.Length;
        if (n == 0) return;
        var left = width * 0.07f;
        var slot = width * 0.86f / n;
        var bar = slot * 0.66f;
        var floor = height * 0.78f;
        var tallest = height * 0.6f;
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            var h = MathF.Max(2f, frame.Bands[b] * tallest);
            var colour = palette.Sample(b / (float)Math.Max(1, n - 1));
            var tip = Mix(colour, SKColors.White, 0.15f + (0.55f * frame.Bands[b]));
            var slice = h / Slices;
            for (var s = 0; s < Slices; s++)
            {
                _pixel.Color = Mix(colour, tip, s / (float)(Slices - 1));
                canvas.DrawRect(x, floor - ((s + 1) * slice), bar, slice + 0.5f, _pixel);
            }
        }

        _pixel.Color = palette.Highlight;
        var cap = MathF.Max(2f, height * 0.005f);
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            canvas.DrawRect(x, floor - (frame.Peaks[b] * tallest) - (cap * 2), bar, cap, _pixel);
        }
    }

    // Rounded bars in a halo of their own light, their colour moving up the ramp as they grow.
    // Never shorter than they are wide, so a quiet band is a round dot rather than a sliver.
    private void GlowPills(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var n = frame.Bands.Length;
        if (n == 0) return;
        var left = width * 0.07f;
        var slot = width * 0.86f / n;
        var bar = slot * 0.52f;
        var floor = height * 0.8f;
        var tallest = height * 0.62f;
        var halo = bar * 0.34f;
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            var h = MathF.Max(bar, frame.Bands[b] * tallest);
            var top = floor - h;
            var colour = palette.Sample((0.3f * b / Math.Max(1, n - 1)) + (0.7f * frame.Bands[b]));
            var r = MathF.Min(bar, h) / 2;
            for (var g = 2; g >= 1; g--)
            {
                var grow = halo * g;
                _fill.Color = colour.WithAlpha((byte)(60 / g));
                canvas.DrawRoundRect(x - grow, top - grow, bar + (2 * grow), h + (2 * grow), r + grow, r + grow, _fill);
            }

            _fill.Color = colour;
            canvas.DrawRoundRect(x, top, bar, h, r, r, _fill);
        }

        _fill.Color = palette.Highlight;
        var cap = MathF.Max(3f, bar * 0.28f);
        for (var b = 0; b < n; b++)
        {
            if (frame.Peaks[b] < 0.03f) continue;
            var x = left + (b * slot) + ((slot - bar) / 2);
            var y = floor - MathF.Max(bar, frame.Peaks[b] * tallest) - (cap * 2.2f);
            canvas.DrawRoundRect(x, y, bar, cap, cap / 2, cap / 2, _fill);
        }
    }

    // Lit segments on dark glass, as a hi-fi's display: 24 bands of 24 segments, each lit one
    // coloured by its height along the ramp, the unlit ones faintly there so the grid reads in a
    // quiet passage, and the peak held as one segment in the highlight.
    private void Led(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Segments = 24;
        var columns = _ledBands.Length;
        Flat(canvas, palette);
        Regroup(frame.Bands, _ledBands);
        Regroup(frame.Peaks, _ledPeaks);
        var left = width * 0.08f;
        var slot = width * 0.84f / columns;
        var bar = MathF.Round(slot * 0.78f);
        var top = height * 0.14f;
        var tall = height * 0.72f;
        var segment = tall / Segments;
        var lit = MathF.Max(1f, MathF.Round(segment * 0.68f));
        for (var c = 0; c < columns; c++)
        {
            var x = MathF.Round(left + (c * slot) + ((slot - bar) / 2));
            var on = (int)MathF.Round(_ledBands[c] * Segments);
            var peak = (int)MathF.Round(_ledPeaks[c] * Segments) - 1;
            for (var s = 0; s < Segments; s++)
            {
                var colour = palette.Sample(s / (float)(Segments - 1));
                _pixel.Color = s < on ? colour : s == peak ? palette.Highlight : colour.WithAlpha(26);
                canvas.DrawRect(x, MathF.Round(top + tall - ((s + 1) * segment)), bar, lit, _pixel);
            }
        }
    }

    // Round dots, a column of sixteen for each band, lit to the band's level in its colour; the
    // unlit ones stay faintly there, and the dot at the peak is lit in the highlight.
    private void DotMatrix(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Rows = 16;
        Flat(canvas, palette);
        var n = frame.Bands.Length;
        if (n == 0) return;
        var left = width * 0.07f;
        var slot = width * 0.86f / n;
        var top = height * 0.17f;
        var tall = height * 0.66f;
        var row = tall / Rows;
        var radius = MathF.Max(1.5f, MathF.Min(slot, row) * 0.36f);
        for (var b = 0; b < n; b++)
        {
            var colour = palette.Sample(b / (float)Math.Max(1, n - 1));
            var cx = left + ((b + 0.5f) * slot);
            var lit = (int)MathF.Round(frame.Bands[b] * Rows);
            var peak = (int)MathF.Round(frame.Peaks[b] * Rows) - 1;
            for (var r = 0; r < Rows; r++)
            {
                _fill.Color = r < lit ? colour : r == peak ? palette.Highlight : colour.WithAlpha(28);
                canvas.DrawCircle(cx, top + tall - ((r + 0.5f) * row), radius, _fill);
            }
        }
    }

    // The spectrum as one smooth line with the ground under it filled, the loud end of the ramp at
    // the top fading to nothing at the floor, and the caps as a fainter line over it.
    private void Curve(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var n = frame.Bands.Length;
        if (n < 2) return;
        var left = width * 0.05f;
        var usable = width * 0.9f;
        var floor = height * 0.8f;
        var tallest = height * 0.62f;
        if (_curveFill is null || _curveFillFor != (height, palette))
        {
            _curveFill?.Dispose();
            _curveRamp?.Dispose();
            (_curveRamp, _curveFill) = VerticalRamp(
                floor - tallest,
                floor,
                [palette.Sample(1f).WithAlpha(230), palette.Sample(0.5f).WithAlpha(120), palette.Sample(0f).WithAlpha(12)],
                [0f, 0.55f, 1f]);
            _curveFillFor = (height, palette);
        }

        Smooth(_curve, frame.Bands, left, usable, floor, tallest, close: true);

        // The body without smoothing, the dearest part of the frame to smooth: the line drawn over
        // its top edge covers it. A paint's alpha dims its shader, so opaque white first.
        _pixel.Color = SKColors.White;
        _pixel.Shader = _curveFill;
        canvas.DrawPath(_curve, _pixel);
        _pixel.Shader = null;

        // The lines are short steps meeting at shallow angles: a bevel there is as good as a round
        // join to the eye and much cheaper.
        Smooth(_curveLine, frame.Bands, left, usable, floor, tallest, close: false);
        _line.StrokeWidth = MathF.Max(2f, height * 0.004f);
        _line.Color = palette.Highlight;
        canvas.DrawPath(_curveLine, _line);

        Smooth(_curveLine, frame.Peaks, left, usable, floor, tallest, close: false);
        _line.StrokeWidth = MathF.Max(1f, height * 0.002f);
        _line.Color = palette.Highlight.WithAlpha(100);
        canvas.DrawPath(_curveLine, _line);
    }

    // A smooth line through the tops (Catmull-Rom: through every band, bending evenly between them),
    // laid down as short straight steps a few pixels long: the same line to the eye, and far less
    // for Skia to fill and stroke than a run of cubics.
    private static void Smooth(SKPath path, float[] levels, float left, float usable, float floor, float tallest, bool close)
    {
        const int Steps = 8;
        var n = levels.Length;
        path.Reset();
        SKPoint At(int i) => new(left + (usable * i / (n - 1)), floor - (Math.Clamp(levels[Math.Clamp(i, 0, n - 1)], 0f, 1f) * tallest));
        if (close)
        {
            path.MoveTo(left, floor);
            path.LineTo(At(0));
        }
        else
        {
            path.MoveTo(At(0));
        }

        for (var i = 0; i < n - 1; i++)
        {
            var (p0, p1, p2, p3) = (At(i - 1), At(i), At(i + 1), At(i + 2));
            for (var s = 1; s <= Steps; s++)
            {
                var t = s / (float)Steps;
                var t2 = t * t;
                var t3 = t2 * t;
                var x = 0.5f * ((2 * p1.X) + ((p2.X - p0.X) * t) + (((2 * p0.X) - (5 * p1.X) + (4 * p2.X) - p3.X) * t2) + (((3 * p1.X) - p0.X - (3 * p2.X) + p3.X) * t3));
                var y = 0.5f * ((2 * p1.Y) + ((p2.Y - p0.Y) * t) + (((2 * p0.Y) - (5 * p1.Y) + (4 * p2.Y) - p3.Y) * t2) + (((3 * p1.Y) - p0.Y - (3 * p2.Y) + p3.Y) * t3));
                path.LineTo(x, MathF.Min(floor, y));
            }
        }

        if (!close) return;
        path.LineTo(left + usable, floor);
        path.Close();
    }
}
