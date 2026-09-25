using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

/// <summary>
/// Draws one frame of the full-window visualizer from one analysis frame, in any mode and any
/// palette. It runs on the visualizer's own thread and keeps its paints, paths and sparks there.
/// </summary>
/// <remarks>
/// Every size is a fraction of the canvas, so a frame reads the same small or full screen, and
/// every mode takes its colours from the palette's ramp, so a palette fits all of them. Nothing
/// here re-analyses the audio: bars, caps, the waveform and the beat all come from the frame.
/// The modes live by family in the other parts of this class; each draws its own ground.
/// </remarks>
internal sealed partial class VisualizerPainter : IDisposable
{
    private const int MaxSparks = 900;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPath _path = new();
    private readonly Spark[] _sparks = new Spark[MaxSparks];
    private readonly Random _random = new(7);
    private readonly SKPaint _copy = new() { BlendMode = SKBlendMode.Src };
    private readonly SKPaint _tint = new();
    private SKBitmap? _backdrop;
    private SKImage? _glow;
    private VisualizerPalette? _backdropFor;
    private SKBitmap? _ramp;
    private VisualizerPalette? _rampFor;
    private int _sparkCount;
    private float _time;
    private float _emitDebt;

    private struct Spark
    {
        public float X, Y, Vx, Vy, Life, Age, Size;
        public SKColor Colour;
    }

    /// <summary>How many sparks are alive: the particle mode's own measure of what it draws.</summary>
    public int Sparks => _sparkCount;

    /// <summary>The classic skins' frame for the same moment, quicker and in 19 bands, when there is one: the pixel modes draw it.</summary>
    public AudioFrame? Classic { get; set; }

    /// <summary>The cover playing, when there is one; the renderer owns it.</summary>
    public SKImage? Cover { get; set; }

    public void Paint(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerMode mode, VisualizerPalette palette, float dt)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(palette);
        _time += dt;
        var bass = Bass(frame);
        switch (mode)
        {
            case VisualizerMode.Spectrum:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Spectrum(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Mirror:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Mirror(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Radial:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Radial(canvas, width, height, frame, palette, bass);
                break;
            case VisualizerMode.Scope:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Scope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Particles:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Particles(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.PixelBars:
                PixelBars(canvas, width, height, Classic ?? frame, palette);
                break;
            case VisualizerMode.FlatBars:
                FlatBars(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.GradientBars:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                GradientBars(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.GlowPills:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                GlowPills(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Led:
                Led(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.DotMatrix:
                DotMatrix(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Curve:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Curve(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Spectrogram:
                Spectrogram(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Terrain:
                Terrain(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.DotPlane:
                DotPlane(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.PixelScope:
                PixelScope(canvas, width, height, Classic ?? frame, palette);
                break;
            case VisualizerMode.FilledScope:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                FilledScope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Envelope:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Envelope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.DotScope:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                DotScope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Vectorscope:
                Vectorscope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Superscope:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Superscope(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.ScopeStar:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                ScopeStar(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.VuMeters:
                VuMeters(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.Fountain:
                Background(canvas, width, height, palette, frame.BeatIntensity, bass);
                Fountain(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.Fireworks:
                Fireworks(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Starfield:
                Starfield(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Tunnel:
                Tunnel(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Plasma:
                Plasma(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.Fire:
                Fire(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Water:
                Water(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Blobs:
                Blobs(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Ambience:
                Ambience(canvas, width, height, frame, palette);
                break;
            case VisualizerMode.Feedback:
                Feedback(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Swirl:
                Swirl(canvas, width, height, frame, palette, bass);
                break;
            case VisualizerMode.Kaleidoscope:
                Kaleidoscope(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.BassSpin:
                BassSpin(canvas, width, height, frame, palette, dt);
                break;
            case VisualizerMode.Pulse:
                Background(canvas, width, height, palette, frame.BeatIntensity * 0.5f, bass * 0.5f);
                Pulse(canvas, width, height, frame, palette, bass, dt);
                break;
            case VisualizerMode.Cover:
                CoverArt(canvas, width, height, frame, palette, bass, dt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "No such visualizer mode.");
        }
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _path.Dispose();
        _copy.Dispose();
        _tint.Dispose();
        _backdrop?.Dispose();
        _glow?.Dispose();
        _ramp?.Dispose();
        DisposeBars();
        DisposeTime();
        DisposeScopes();
        DisposeFields();
        DisposeFeedback();
        DisposeScenes();
    }

    // ===== Shared by the modes =====

    // A plain ground in the palette's own dark: the classic players' black, tinted.
    private static void Flat(SKCanvas canvas, VisualizerPalette palette) => canvas.Clear(palette.Background);

    // The level at a fractional band position, between the two bands either side of it.
    private static float LevelAt(float[] bands, float position)
    {
        if (bands.Length == 0) return 0;
        position = Math.Clamp(position, 0, bands.Length - 1);
        var i = Math.Min((int)position, bands.Length - 2);
        if (i < 0) return bands[0];
        var f = position - i;
        return (bands[i] * (1 - f)) + (bands[i + 1] * f);
    }

    // The bands regrouped into into.Length: the loudest of each group when fewer, between
    // neighbours when more. A regrouped bar never reads quieter than the bands it stands for.
    private static void Regroup(float[] bands, float[] into)
    {
        var n = bands.Length;
        var m = into.Length;
        if (n == 0)
        {
            Array.Clear(into);
            return;
        }

        if (m >= n)
        {
            for (var i = 0; i < m; i++) into[i] = LevelAt(bands, m == 1 ? 0 : i * (n - 1) / (float)(m - 1));
            return;
        }

        for (var i = 0; i < m; i++)
        {
            var from = i * n / m;
            var to = Math.Max(from + 1, (i + 1) * n / m);
            var loudest = 0f;
            for (var b = from; b < to; b++) loudest = MathF.Max(loudest, bands[b]);
            into[i] = loudest;
        }
    }

    private static SKColor Mix(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + ((b.Red - a.Red) * t)),
            (byte)(a.Green + ((b.Green - a.Green) * t)),
            (byte)(a.Blue + ((b.Blue - a.Blue) * t)),
            (byte)(a.Alpha + ((b.Alpha - a.Alpha) * t)));
    }

    // Blue, green, red, alpha in memory order: one pixel of a Bgra8888 picture.
    private static int Pack(SKColor c) => c.Blue | (c.Green << 8) | (c.Red << 16) | (c.Alpha << 24);

    // A fill that runs down through colours: the gradient drawn once into a picture one pixel wide,
    // and that picture as the fill. Skia shades a large area with a gradient many times more slowly
    // (4.4 ms against 0.3 for the area under a full curve), for the same colours.
    private static (SKImage Ramp, SKShader Shader) VerticalRamp(float top, float bottom, SKColor[] colours, float[] positions)
    {
        var tall = Math.Max(1, (int)MathF.Ceiling(bottom - top));
        using var surface = SKSurface.Create(new SKImageInfo(1, tall, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, tall), colours, positions, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = gradient })
        {
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawRect(0, 0, 1, tall, paint);
        }

        var ramp = surface.Snapshot();
        return (ramp, ramp.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateTranslation(0, top)));
    }

    // The lowest quarter of the bands, the kick and the bass line: what the pulses follow.
    private static float Bass(AudioFrame frame)
    {
        var n = Math.Max(1, frame.Bands.Length / 4);
        var sum = 0f;
        for (var i = 0; i < n && i < frame.Bands.Length; i++) sum += frame.Bands[i];
        return sum / n;
    }

    // The background is drawn once per size and palette, and copied in each frame: a gradient over
    // the whole canvas costs Skia more than a frame has (12 ms at 1600 by 1000), a copy a fifth of
    // a millisecond. The pulse is the same glow, small, stretched over the lower middle only.
    private void Background(SKCanvas canvas, int width, int height, VisualizerPalette palette, float beat, float bass)
    {
        if (_backdrop is null || _backdrop.Width != width || _backdrop.Height != height || !ReferenceEquals(_backdropFor, palette))
        {
            BuildBackdrop(width, height, palette);
        }

        canvas.DrawBitmap(_backdrop, 0, 0, _copy);
        var pulse = Math.Clamp((0.55f * beat) + (0.45f * bass), 0f, 1f);
        if (pulse < 0.02f) return;
        _tint.Color = SKColors.White.WithAlpha((byte)(170 * pulse));
        canvas.DrawImage(_glow, new SKRect(width * 0.18f, height * 0.5f, width * 0.82f, height), new SKSamplingOptions(SKFilterMode.Linear), _tint);
    }

    private void BuildBackdrop(int width, int height, VisualizerPalette palette)
    {
        _backdrop?.Dispose();
        _glow?.Dispose();
        _backdropFor = palette;

        // The glow, small: a soft light from below in the middle of the ramp.
        var colour = palette.Sample(0.5f);
        using (var small = new SKBitmap(new SKImageInfo(128, 64, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            using (var glowCanvas = new SKCanvas(small))
            using (var shader = SKShader.CreateRadialGradient(new SKPoint(64, 64), 72, [colour.WithAlpha(150), colour.WithAlpha(40), colour.WithAlpha(0)], [0f, 0.5f, 1f], SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = shader })
            {
                glowCanvas.Clear(SKColors.Transparent);
                glowCanvas.DrawRect(0, 0, 128, 64, paint);
            }

            _glow = SKImage.FromBitmap(small);
        }

        _backdrop = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(_backdrop);
        canvas.Clear(palette.Background);
        using var faint = new SKPaint { Color = SKColors.White.WithAlpha(90) };
        canvas.DrawImage(_glow, new SKRect(-width * 0.25f, height * 0.25f, width * 1.25f, height * 1.1f), new SKSamplingOptions(SKFilterMode.Linear), faint);
    }

    // The bars' ramp, quiet at the foot to loud at the top, one pixel wide: each bar is a copy of
    // its lower part, stretched across, far cheaper than shading every bar with a gradient.
    private SKBitmap Ramp(int tallest, VisualizerPalette palette)
    {
        if (_ramp is { } ramp && ramp.Height == tallest && ReferenceEquals(_rampFor, palette)) return ramp;
        _ramp?.Dispose();
        _rampFor = palette;
        _ramp = new SKBitmap(new SKImageInfo(1, Math.Max(1, tallest), SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < _ramp.Height; y++) _ramp.SetPixel(0, y, palette.Sample(1f - (y / (float)Math.Max(1, _ramp.Height - 1))));
        return _ramp;
    }

    private void Spectrum(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var bands = frame.Bands;
        var n = bands.Length;
        if (n == 0) return;
        var left = width * 0.07f;
        var usable = width * 0.86f;
        var slot = usable / n;
        var bar = slot * 0.7f;
        var floor = height * 0.74f;
        var tallest = height * 0.58f;
        var radius = Math.Min(bar * 0.25f, 6f);

        var ramp = Ramp((int)tallest, palette);
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            var h = Math.Clamp(bands[b] * tallest, 2f, ramp.Height);
            canvas.DrawBitmap(ramp, new SKRect(0, ramp.Height - h, 1, ramp.Height), new SKRect(x, floor - h, x + bar, floor));
        }

        // The reflection: the same bars, upside down under the floor, faint and short.
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            var h = bands[b] * tallest * 0.3f;
            _fill.Color = palette.Sample(bands[b] * 0.8f).WithAlpha(46);
            canvas.DrawRoundRect(x, floor + (height * 0.012f), bar, h, radius, radius, _fill);
        }

        // Caps that hang, then fall.
        _fill.Color = palette.Highlight;
        var cap = Math.Max(2f, height * 0.006f);
        for (var b = 0; b < n; b++)
        {
            var x = left + (b * slot) + ((slot - bar) / 2);
            var y = floor - (frame.Peaks[b] * tallest) - (cap * 2);
            canvas.DrawRect(x, y, bar, cap, _fill);
        }
    }

    private void Mirror(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var bands = frame.Bands;
        var n = bands.Length;
        if (n == 0) return;
        var centreY = height / 2f;
        var slot = width * 0.9f / (2 * n);
        var bar = slot * 0.66f;
        var reach = height * 0.4f;
        var radius = Math.Min(bar * 0.5f, 8f);
        for (var i = 0; i < 2 * n; i++)
        {
            // The bass in the middle, the treble out at both edges.
            var b = i < n ? n - 1 - i : i - n;
            var level = bands[b];
            var x = (width * 0.05f) + (i * slot) + ((slot - bar) / 2);
            var h = Math.Max(2f, level * reach);
            _fill.Color = palette.Sample((0.35f * (b / (float)(n - 1))) + (0.65f * level));
            canvas.DrawRoundRect(x, centreY - h, bar, 2 * h, radius, radius, _fill);
        }

        _stroke.Color = palette.Highlight.WithAlpha(90);
        _stroke.StrokeWidth = Math.Max(1f, height * 0.002f);
        canvas.DrawLine(width * 0.04f, centreY, width * 0.96f, centreY, _stroke);
    }

    private void Radial(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass)
    {
        var bands = frame.Bands;
        var n = bands.Length;
        if (n == 0) return;
        var cx = width / 2f;
        var cy = height / 2f;
        var size = Math.Min(width, height);
        var ring = size * 0.2f * (1 + (0.1f * frame.BeatIntensity) + (0.05f * bass));
        var reach = size * 0.26f;
        var spokes = 2 * n;
        var turn = _time * 0.08f;
        _stroke.StrokeWidth = Math.Max(2f, (MathF.Tau * ring / spokes) * 0.55f);
        for (var i = 0; i < spokes; i++)
        {
            var b = i < n ? i : spokes - 1 - i;
            var level = bands[b];
            var angle = turn + (MathF.Tau * i / spokes) - (MathF.PI / 2);
            var (sin, cos) = MathF.SinCos(angle);
            var inner = ring + (size * 0.012f);
            var outer = inner + Math.Max(size * 0.006f, level * reach);
            _stroke.Color = palette.Sample((0.3f * (b / (float)(n - 1))) + (0.7f * level));
            canvas.DrawLine(cx + (cos * inner), cy + (sin * inner), cx + (cos * outer), cy + (sin * outer), _stroke);
        }

        _stroke.Color = palette.Highlight.WithAlpha((byte)(90 + (140 * frame.BeatIntensity)));
        _stroke.StrokeWidth = Math.Max(1.5f, size * 0.006f);
        canvas.DrawCircle(cx, cy, ring, _stroke);
        _fill.Color = palette.Sample(bass).WithAlpha((byte)(40 + (90 * bass)));
        canvas.DrawCircle(cx, cy, ring * 0.82f, _fill);
    }

    private void Scope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var wave = frame.Waveform;
        var cy = height / 2f;
        var amplitude = height * 0.36f;
        _path.Reset();
        if (wave.Length < 2 || frame.Silent)
        {
            _path.MoveTo(0, cy);
            _path.LineTo(width, cy);
        }
        else
        {
            // At most one point per two pixels: the line is as smooth, and the path half as long.
            var points = Math.Min(wave.Length, Math.Max(2, width / 2));
            for (var p = 0; p < points; p++)
            {
                var i = (int)((long)p * (wave.Length - 1) / (points - 1));
                var x = width * p / (float)(points - 1);
                var y = cy - (Math.Clamp(wave[i] * 1.6f, -1f, 1f) * amplitude);
                if (p == 0) _path.MoveTo(x, y);
                else _path.LineTo(x, y);
            }
        }

        // A wide, faint stroke for the glow in one colour, then the trace in the ramp: shading the wide
        // stroke too would cost more than the rest of the frame.
        _stroke.StrokeWidth = Math.Max(6f, height * 0.018f);
        _stroke.Color = palette.Sample(0.5f).WithAlpha(70);
        canvas.DrawPath(_path, _stroke);
        using var ramp = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, 0), [palette.Stops[0], palette.Stops[^1], palette.Stops[0]], SKShaderTileMode.Clamp);
        _stroke.Shader = ramp;
        _stroke.StrokeWidth = Math.Max(2f, height * 0.004f);
        _stroke.Color = SKColors.White;
        canvas.DrawPath(_path, _stroke);
        _stroke.Shader = null;
    }

    private void Particles(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        var cx = width / 2f;
        var cy = height / 2f;
        var size = Math.Min(width, height);

        // A steady stream that thickens with the bass, and a burst on each beat.
        _emitDebt += dt * (frame.Silent ? 6f : 30f + (260f * bass));
        var burst = frame.Beat ? 60 + (int)(90 * bass) : 0;
        var emit = (int)_emitDebt + burst;
        _emitDebt -= (int)_emitDebt;
        for (var e = 0; e < emit && _sparkCount < MaxSparks; e++)
        {
            var angle = (float)(_random.NextDouble() * MathF.Tau);
            var speed = size * (0.08f + (float)_random.NextDouble() * (0.28f + (0.5f * bass)));
            var (sin, cos) = MathF.SinCos(angle);
            var band = _random.Next(Math.Max(1, frame.Bands.Length));
            _sparks[_sparkCount++] = new Spark
            {
                X = cx + (cos * size * 0.05f),
                Y = cy + (sin * size * 0.05f),
                Vx = cos * speed,
                Vy = sin * speed,
                Life = 1.2f + (float)_random.NextDouble() * 1.4f,
                Size = size * (0.002f + (float)_random.NextDouble() * 0.005f),
                Colour = palette.Sample(frame.Bands.Length == 0 ? 0.5f : (0.4f * band / frame.Bands.Length) + (0.6f * frame.Bands[band])),
            };
        }

        var drag = MathF.Max(0f, 1f - (0.7f * dt));
        for (var i = 0; i < _sparkCount;)
        {
            ref var s = ref _sparks[i];
            s.Age += dt;
            if (s.Age >= s.Life || s.X < -50 || s.Y < -50 || s.X > width + 50 || s.Y > height + 50)
            {
                _sparks[i] = _sparks[--_sparkCount];
                continue;
            }

            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.Vx *= drag;
            s.Vy *= drag;
            var fade = 1f - (s.Age / s.Life);
            _fill.Color = s.Colour.WithAlpha((byte)(255 * fade));
            canvas.DrawCircle(s.X, s.Y, s.Size * (0.6f + fade), _fill);
            i++;
        }

        // The core: an orb that swells with the bass and flashes on the beat.
        var orb = size * 0.05f * (1 + (1.4f * bass) + (0.6f * frame.BeatIntensity));
        using var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), orb, [palette.Highlight, palette.Sample(bass).WithAlpha(120), palette.Background.WithAlpha(0)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
        _fill.Color = SKColors.White;
        _fill.Shader = shader;
        canvas.DrawCircle(cx, cy, orb, _fill);
        _fill.Shader = null;
    }
}
