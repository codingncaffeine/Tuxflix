using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// The spectrum over time: a scrolling heat map, ridgelines receding, and a plane of dots.
internal sealed partial class VisualizerPainter
{
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly PixelField _gram = new();
    private readonly SpectrumHistory _ridges = new(40, 48, 0.085f);
    private readonly SpectrumHistory _plane = new(24, 32, 0.06f);
    private readonly int[] _lut = new int[256];
    private VisualizerPalette? _lutFor;
    private int _gramColumn;
    private long _gramSequence = -1;

    private void DisposeTime() => _gram.Dispose();

    // 256 colours from the palette's ground up its ramp to the highlight, for pictures made pixel
    // by pixel: 0 is the ground, the first fifth rises to the quiet end of the ramp.
    private int[] Lut(VisualizerPalette palette)
    {
        if (ReferenceEquals(_lutFor, palette)) return _lut;
        _lutFor = palette;
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255f;
            var colour = t < 0.2f ? Mix(palette.Background, palette.Stops[0], t / 0.2f)
                : t < 0.9f ? palette.Sample((t - 0.2f) / 0.7f)
                : Mix(palette.Stops[^1], palette.Highlight, (t - 0.9f) / 0.1f);
            _lut[i] = Pack(colour.WithAlpha(255));
        }

        return _lut;
    }

    // Time runs right to left, pitch up, loudness as light: a column for every analysis, written
    // into a ring so nothing already drawn is moved, and the ring drawn in two parts with the
    // newest column at the right edge.
    private void Spectrogram(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var lut = Lut(palette);
        if (_gram.Ensure(width, height))
        {
            _gramColumn = 0;
            Array.Fill(_gram.Pixels, lut[0]);
        }

        var w = _gram.Width;
        var h = _gram.Height;
        if (frame.Sequence != _gramSequence)
        {
            _gramSequence = frame.Sequence;
            var bands = frame.Bands;
            var pixels = _gram.Pixels;
            for (var y = 0; y < h; y++)
            {
                var level = LevelAt(bands, (h - 1 - y) * (bands.Length - 1) / (float)Math.Max(1, h - 1));
                pixels[(y * w) + _gramColumn] = lut[(int)(MathF.Pow(Math.Clamp(level, 0f, 1f), 1.4f) * 255)];
            }

            _gramColumn = (_gramColumn + 1) % w;
        }

        _gram.Upload();
        var tail = w - _gramColumn;
        _gram.Draw(canvas, new SKRect(_gramColumn, 0, w, h), new SKRect(0, 0, tail * width / (float)w, height), Nearest);
        if (_gramColumn > 0) _gram.Draw(canvas, new SKRect(0, 0, _gramColumn, h), new SKRect(tail * width / (float)w, 0, width, height), Nearest);
    }

    // The last few seconds of spectra as ridgelines, the newest in front and live, the older ones
    // receding up the screen, narrower, lower and dimmer. Each is filled with the ground below its
    // line, so a nearer ridge hides the lines behind it where it rises over them. The rise is
    // strongest in the middle and nothing at the edges, as the famous plot of a pulsar's was.
    private void Terrain(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        Flat(canvas, palette);
        _ridges.Add(frame.Bands, dt, Regroup);
        var rows = _ridges.Rows;
        var points = _ridges.Columns;
        var front = height * 0.9f;
        var horizon = height * 0.2f;
        _fill.Color = palette.Background;
        for (var age = rows - 1; age >= 0; age--)
        {
            var depth = Math.Clamp((age + _ridges.Fraction) / rows, 0f, 1f);
            var nearness = 1 - depth;
            var baseline = horizon + ((front - horizon) * MathF.Pow(nearness, 1.35f));
            var half = width * 0.42f * (1 - (0.45f * depth));
            var rise = height * 0.24f * (1 - (0.55f * depth));
            var row = _ridges.Row(age);

            _path.Reset();
            _curveLine.Reset();
            _path.MoveTo((width / 2f) - half, baseline + 2);
            for (var i = 0; i < points; i++)
            {
                var u = i / (float)(points - 1);
                var bell = 0.12f + (0.88f * MathF.Pow(MathF.Sin(MathF.PI * u), 2));
                var x = (width / 2f) - half + (2 * half * u);
                var y = baseline - (row[i] * rise * bell);
                _path.LineTo(x, y);
                if (i == 0) _curveLine.MoveTo(x, y);
                else _curveLine.LineTo(x, y);
            }

            _path.LineTo((width / 2f) + half, baseline + 2);
            _path.Close();
            canvas.DrawPath(_path, _fill);
            _stroke.StrokeWidth = MathF.Max(1.2f, height * 0.0028f * (1 - (0.4f * depth)));
            _stroke.Color = Mix(palette.Sample(0.2f + (0.8f * nearness)), palette.Highlight, MathF.Pow(nearness, 4) * 0.6f).WithAlpha((byte)(255 * (0.3f + (0.7f * nearness))));
            canvas.DrawPath(_curveLine, _stroke);
        }
    }

    // A plane of dots in perspective, turning slowly: across it the bands, along it the last second
    // or so of them, newest in front, each dot raised by its level and coloured by it.
    private void DotPlane(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        const float Pitch = 0.52f;
        const float Distance = 3.1f;
        Flat(canvas, palette);
        _plane.Add(frame.Bands, dt, Regroup);
        var rows = _plane.Rows;
        var columns = _plane.Columns;
        var turn = _time * 0.16f;
        var (sinTurn, cosTurn) = MathF.SinCos(turn);
        var (sinPitch, cosPitch) = MathF.SinCos(Pitch);
        var focal = MathF.Min(width, height) * 1.3f;
        var cx = width / 2f;
        var cy = height * 0.6f;
        var dot = MathF.Min(width, height) * 0.0065f;
        for (var age = rows - 1; age >= 0; age--)
        {
            var row = _plane.Row(age);
            var z = -1f + (2f * Math.Clamp((age + _plane.Fraction) / (rows - 1), 0f, 1f));
            for (var i = 0; i < columns; i++)
            {
                var x = -1f + (2f * i / (columns - 1));
                var level = row[i];
                var y = level * 0.62f;

                // Turned about the upright, then tipped towards the viewer.
                var x1 = (x * cosTurn) + (z * sinTurn);
                var z1 = (z * cosTurn) - (x * sinTurn);
                var y2 = (y * cosPitch) + (z1 * sinPitch);
                var z2 = (z1 * cosPitch) - (y * sinPitch);
                var scale = focal / (z2 + Distance);
                var near = Math.Clamp(1 - ((z2 + 1.2f) / 2.4f), 0f, 1f);
                _fill.Color = palette.Sample(0.15f + (0.85f * level)).WithAlpha((byte)(255 * (0.3f + (0.7f * near))));
                canvas.DrawCircle(cx + (x1 * scale), cy - (y2 * scale), MathF.Max(1f, dot * (Distance / (z2 + Distance)) * (0.7f + level)), _fill);
            }
        }
    }
}
