using System.Runtime.InteropServices;
using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// The modes that draw on what they drew before: feedback and the swirl, which redraw the last frame
// under this one, and the kaleidoscope and bass spin, which leave a trail of their own.
internal sealed partial class VisualizerPainter
{
    private const int SpinTrail = 10;

    private readonly Canvas2 _feed = new();
    private readonly Canvas2 _swirl = new();
    private readonly SKPaint _fade = new() { Color = SKColors.White.WithAlpha(0xE4) };
    private readonly float[] _kaleidoLevels = new float[16];
    private readonly float[] _spinAngles = new float[2 * SpinTrail];
    private readonly float[] _spinReach = new float[2 * SpinTrail];
    private int[] _swirlFrom = [];
    private byte[] _swirlX = [];
    private byte[] _swirlY = [];
    private float _feedHue;
    private float _kaleidoTurn;
    private int _spinHead;

    private void DisposeFeedback()
    {
        _feed.Dispose();
        _swirl.Dispose();
        _fade.Dispose();
    }

    /// <summary>Two pictures to draw into by turns, each with its canvas and an image over its memory.</summary>
    private sealed class Canvas2 : IDisposable
    {
        private readonly SKBitmap?[] _bitmaps = new SKBitmap?[2];
        private readonly SKCanvas?[] _canvases = new SKCanvas?[2];
        private readonly SKImage?[] _images = new SKImage?[2];
        private int _front;

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Makes both pictures this size, filled with <paramref name="ground"/>; true when made anew.</summary>
        public bool Ensure(int width, int height, SKColor ground)
        {
            if (_bitmaps[0] is not null && width == Width && height == Height) return false;
            Dispose();
            Width = width;
            Height = height;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (var i = 0; i < 2; i++)
            {
                _bitmaps[i] = new SKBitmap(info);
                _canvases[i] = new SKCanvas(_bitmaps[i]);
                _canvases[i]!.Clear(ground);
                _images[i] = SKImage.FromPixels(info, _bitmaps[i]!.GetPixels(), info.RowBytes);
            }

            return true;
        }

        /// <summary>What was drawn last.</summary>
        public SKImage Last => _images[_front]!;

        public Span<uint> LastPixels => MemoryMarshal.Cast<byte, uint>(_bitmaps[_front]!.GetPixelSpan());

        /// <summary>The picture to draw next into.</summary>
        public SKCanvas Next => _canvases[1 - _front]!;

        public Span<uint> NextPixels => MemoryMarshal.Cast<byte, uint>(_bitmaps[1 - _front]!.GetPixelSpan());

        public SKImage NextImage => _images[1 - _front]!;

        /// <summary>The next becomes the last.</summary>
        public void Turn() => _front = 1 - _front;

        public void Dispose()
        {
            for (var i = 0; i < 2; i++)
            {
                _images[i]?.Dispose();
                _canvases[i]?.Dispose();
                _bitmaps[i]?.Dispose();
                _images[i] = null;
                _canvases[i] = null;
                _bitmaps[i] = null;
            }
        }
    }

    // The mechanism behind the famous preset players: the last frame drawn again, a little larger
    // and a little turned, over a ground that drifts along the palette, so what was drawn streams
    // outward and fades into the ground; then this frame's waveform, as a ring, in the colour
    // across the ramp from the ground's, so it stays clear through every generation. The bass
    // pushes the zoom, the beat moves the colours on.
    private void Feedback(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        _feedHue += (dt * 0.03f) + (frame.BeatIntensity * 0.012f);
        var ground = Mix(palette.Background, palette.Sample(Wave(_feedHue)), 0.5f);
        _feed.Ensure(width, height, ground);
        var next = _feed.Next;
        var zoom = 1.012f + (0.035f * bass);
        next.Clear(ground);
        next.Save();
        next.Translate(width / 2f, height / 2f);
        next.Scale(zoom, zoom);
        next.RotateDegrees(MathF.Sin(_time * 0.21f) * 0.9f);
        next.Translate(-width / 2f, -height / 2f);
        next.DrawImage(_feed.Last, 0, 0, Linear, _fade);
        next.Restore();

        const int Points = 160;
        var cx = width / 2f;
        var cy = height / 2f;
        var ring = MathF.Min(width, height) * (0.16f + (0.05f * bass));
        _path.Reset();
        for (var i = 0; i <= Points; i++)
        {
            var u = i % Points / (float)Points;
            var s = Math.Clamp(Sample(frame.Waveform, (int)(u * (Points - 1)), Points) * 1.8f, -1f, 1f);
            var (sin, cos) = MathF.SinCos(u * MathF.Tau);
            var r = ring * (1 + (0.45f * s));
            if (i == 0) _path.MoveTo(cx + (cos * r), cy + (sin * r));
            else _path.LineTo(cx + (cos * r), cy + (sin * r));
        }

        _stroke.StrokeWidth = MathF.Max(2f, height * 0.005f);
        _stroke.Color = Mix(palette.Sample(Wave(_feedHue + 0.5f)), palette.Highlight, 0.3f);
        next.DrawPath(_path, _stroke);
        _feed.Turn();
        canvas.DrawImage(_feed.Last, 0, 0);
    }

    // A whirlpool: every pixel takes the colour a little way round and in from it, more turned near
    // the middle than at the edge, and a little faded, so everything drawn is carried round and out
    // as it dies away. Where each pixel reads from is worked out once per size (four neighbours and
    // how much of each), so a step costs a weighted mean per pixel. Into it each frame goes the
    // waveform as a ring, and the bands as dots round it.
    private void Swirl(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass)
    {
        var w = Math.Max(8, width / 2);
        var h = Math.Max(8, height / 2);
        if (_swirl.Ensure(w, h, palette.Background) || _swirlFrom.Length != w * h) BuildSwirl(w, h);
        var from = _swirl.LastPixels;
        var to = _swirl.NextPixels;
        for (var i = 0; i < to.Length; i++)
        {
            var at = _swirlFrom[i];
            int fx = _swirlX[i];
            int fy = _swirlY[i];
            var (a, b, c, d) = (from[at], from[at + 1], from[at + w], from[at + w + 1]);
            var wa = (256 - fx) * (256 - fy);
            var wb = fx * (256 - fy);
            var wc = (256 - fx) * fy;
            var wd = fx * fy;
            to[i] = Blend(a, b, c, d, wa, wb, wc, wd, 0) | (Blend(a, b, c, d, wa, wb, wc, wd, 8) << 8) | (Blend(a, b, c, d, wa, wb, wc, wd, 16) << 16) | 0xFF000000u;
        }

        var next = _swirl.Next;
        var cx = w / 2f;
        var cy = h / 2f;
        var ring = MathF.Min(w, h) * (0.2f + (0.06f * bass));
        const int Points = 120;
        _path.Reset();
        for (var i = 0; i <= Points; i++)
        {
            var u = i % Points / (float)Points;
            var s = Math.Clamp(Sample(frame.Waveform, (int)(u * (Points - 1)), Points) * 1.8f, -1f, 1f);
            var (sin, cos) = MathF.SinCos(u * MathF.Tau);
            if (i == 0) _path.MoveTo(cx + (cos * ring * (1 + (0.4f * s))), cy + (sin * ring * (1 + (0.4f * s))));
            else _path.LineTo(cx + (cos * ring * (1 + (0.4f * s))), cy + (sin * ring * (1 + (0.4f * s))));
        }

        _stroke.StrokeWidth = 1.5f;
        _stroke.Color = palette.Sample(Wave(_time * 0.05f));
        next.DrawPath(_path, _stroke);
        var bands = frame.Bands;
        for (var b = 0; b < bands.Length; b += 2)
        {
            if (bands[b] < 0.3f) continue;
            var (sin, cos) = MathF.SinCos((b / (float)bands.Length * MathF.Tau) + (_time * 0.4f));
            _fill.Color = palette.Sample(b / (float)Math.Max(1, bands.Length - 1));
            next.DrawCircle(cx + (cos * ring * 1.5f), cy + (sin * ring * 1.5f), 1f + (2.5f * bands[b]), _fill);
        }

        _swirl.Turn();
        canvas.DrawImage(_swirl.Last, new SKRect(0, 0, w, h), Whole(width, height), Linear);
    }

    // One channel of four neighbouring pixels, weighted (the weights add to 65536), a little faded.
    private static uint Blend(uint a, uint b, uint c, uint d, int wa, int wb, int wc, int wd, int shift)
    {
        var mixed = ((int)((a >> shift) & 255) * wa) + ((int)((b >> shift) & 255) * wb) + ((int)((c >> shift) & 255) * wc) + ((int)((d >> shift) & 255) * wd);
        return (uint)(((mixed >> 16) * 250) >> 8);
    }

    private void BuildSwirl(int w, int h)
    {
        _swirlFrom = new int[w * h];
        _swirlX = new byte[w * h];
        _swirlY = new byte[w * h];
        var cx = w / 2f;
        var cy = h / 2f;
        var furthest = MathF.Sqrt((cx * cx) + (cy * cy));
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var r = MathF.Sqrt((dx * dx) + (dy * dy));
                var turn = 0.035f + (0.11f * MathF.Pow(1 - (r / furthest), 2));
                var (sin, cos) = MathF.SinCos(-turn);
                var sx = cx + (((dx * cos) - (dy * sin)) * 0.982f);
                var sy = cy + (((dx * sin) + (dy * cos)) * 0.982f);
                sx = Math.Clamp(sx, 0, w - 1.001f);
                sy = Math.Clamp(sy, 0, h - 1.001f);
                var ix = Math.Min((int)sx, w - 2);
                var iy = Math.Min((int)sy, h - 2);
                var i = (y * w) + x;
                _swirlFrom[i] = (iy * w) + ix;
                _swirlX[i] = (byte)Math.Clamp((sx - ix) * 256, 0, 255);
                _swirlY[i] = (byte)Math.Clamp((sy - iy) * 256, 0, 255);
            }
        }
    }

    // The bands folded into ten mirrored wedges round the middle: in each, a stem of sixteen beads
    // from the middle out, the louder a bead's band the further it swings from the wedge's edge
    // and the larger it is, so the whole figure opens and closes with the music as it turns.
    private void Kaleidoscope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        const int Sectors = 10;
        Background(canvas, width, height, palette, frame.BeatIntensity * 0.6f, bass * 0.6f);
        var petals = _kaleidoLevels.Length;
        Regroup(frame.Bands, _kaleidoLevels);
        _kaleidoTurn += dt * (0.1f + (0.9f * frame.BeatIntensity));
        var reach = MathF.Min(width, height) * 0.47f;
        var half = MathF.PI / Sectors;
        _stroke.StrokeWidth = MathF.Max(1f, height * 0.0022f);
        for (var sector = 0; sector < Sectors; sector++)
        {
            canvas.Save();
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateRadians(_kaleidoTurn + (sector * MathF.Tau / Sectors));
            if (sector % 2 == 1) canvas.Scale(1, -1);
            _path.Reset();
            _path.MoveTo(0, 0);
            for (var j = 0; j < petals; j++)
            {
                var level = _kaleidoLevels[j];
                var along = (0.1f + (0.88f * j / (petals - 1))) * reach;
                var angle = half * Math.Clamp(0.12f + (0.8f * level) + (0.08f * MathF.Sin((_time * 0.8f) + j)), 0.02f, 0.98f);
                var (sin, cos) = MathF.SinCos(angle);
                var x = cos * along;
                var y = sin * along;
                _path.LineTo(x, y);
                _fill.Color = palette.Sample(j / (float)(petals - 1)).WithAlpha((byte)(120 + (135 * level)));
                canvas.DrawCircle(x, y, reach * (0.01f + (0.045f * level)), _fill);
            }

            _stroke.Color = palette.Sample(0.5f).WithAlpha(90);
            canvas.DrawPath(_path, _stroke);
            canvas.Restore();
        }

        _fill.Color = palette.Highlight.WithAlpha((byte)(140 + (100 * frame.BeatIntensity)));
        canvas.DrawCircle(width / 2f, height / 2f, reach * (0.03f + (0.05f * bass)), _fill);
    }

    // Two blades turning in opposite directions, the left channel's one way and the right's the
    // other, each spun and lengthened by its channel's level, the last few positions fading
    // behind them: the old visualization studio's bass spin.
    private void BassSpin(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        Flat(canvas, palette);
        var cx = width / 2f;
        var cy = height / 2f;
        var longest = MathF.Min(width, height) * 0.46f;
        var levels = (Left: frame.RmsLeft * frame.RmsLeft, Right: frame.RmsRight * frame.RmsRight);
        var head = _spinHead;
        _spinHead = (_spinHead + 1) % SpinTrail;
        for (var blade = 0; blade < 2; blade++)
        {
            var level = blade == 0 ? levels.Left : levels.Right;
            var last = _spinAngles[(blade * SpinTrail) + head];
            var angle = (last + ((blade == 0 ? 1 : -1) * dt * (0.8f + (11f * level)))) % MathF.Tau;
            _spinAngles[(blade * SpinTrail) + _spinHead] = angle;
            _spinReach[(blade * SpinTrail) + _spinHead] = longest * (0.35f + (0.65f * level));
        }

        for (var age = SpinTrail - 1; age >= 0; age--)
        {
            var slot = ((_spinHead - age) % SpinTrail + SpinTrail) % SpinTrail;
            for (var blade = 0; blade < 2; blade++)
            {
                var angle = _spinAngles[(blade * SpinTrail) + slot];
                var reach = _spinReach[(blade * SpinTrail) + slot];
                if (reach <= 0) continue;
                var colour = palette.Sample(blade == 0 ? 0.3f : 0.9f);
                _path.Reset();
                _path.MoveTo(cx, cy);
                for (var edge = -1; edge <= 1; edge += 2)
                {
                    var (sin, cos) = MathF.SinCos(angle + (edge * 0.26f));
                    _path.LineTo(cx + (cos * reach), cy + (sin * reach));
                }

                _path.Close();
                _fill.Color = age == 0 ? colour : colour.WithAlpha((byte)(110 * (1 - (age / (float)SpinTrail)) / 2));
                canvas.DrawPath(_path, _fill);
                if (age != 0) continue;
                _stroke.StrokeWidth = MathF.Max(1.5f, height * 0.003f);
                _stroke.Color = palette.Highlight.WithAlpha(200);
                canvas.DrawPath(_path, _stroke);
            }
        }

        _fill.Color = palette.Highlight;
        canvas.DrawCircle(cx, cy, MathF.Max(3f, longest * 0.03f), _fill);
    }
}
