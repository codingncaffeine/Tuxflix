using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// The scopes and meters: the waveform drawn every way the classic players drew it, left against
// right, and a pair of needles.
internal sealed partial class VisualizerPainter
{
    private const int EnvelopeLength = 240;
    private const int DotTrail = 3;

    // Where the needles' scale puts each mark (volume units), and the marks it prints numbers for.
    private static readonly float[] VuMarks = [-20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3];
    private static readonly float[] VuNumbers = [-20, -10, -7, -5, -3, 0, 3];
    private static readonly string[] VuLabels = ["-20", "-10", "-7", "-5", "-3", "0", "+3"];

    private readonly float[] _envelope = new float[EnvelopeLength];
    private readonly float[][] _dotTrail = [.. Enumerable.Range(0, DotTrail).Select(_ => Array.Empty<float>())];
    private readonly SKPoint[][] _goniometer = [.. Enumerable.Range(0, 4).Select(_ => Array.Empty<SKPoint>())];
    private readonly SKFont _font = new() { Subpixel = true };
    private readonly SKPath _faceClip = new();
    private readonly float[] _needle = new float[2];
    private readonly float[] _needleSpeed = new float[2];
    private readonly float[] _overload = new float[2];
    private SKShader? _scopeFill;
    private SKImage? _scopeRamp;
    private (int Height, VisualizerPalette? Palette) _scopeFillFor;
    private SKImage? _face;
    private (SKSize Size, VisualizerPalette? Palette) _faceFor;
    private int _envelopeHead;
    private long _envelopeSequence = -1;
    private int _dotHead;
    private int _goniometerHead;
    private long _goniometerSequence = -1;
    private int _beats;
    private float _starTurn;

    private void DisposeScopes()
    {
        _font.Dispose();
        _faceClip.Dispose();
        _scopeFill?.Dispose();
        _scopeRamp?.Dispose();
        _face?.Dispose();
    }

    private static float Sample(float[] wave, int point, int points) =>
        wave.Length < 2 || points < 2 ? 0f : wave[(int)((long)point * (wave.Length - 1) / (points - 1))];

    // A fill for shapes about the centre line: the loud end of the ramp at the extremes, the quiet
    // end, faint, along the middle. The paint goes opaque white with it: a paint's alpha dims its
    // shader, whatever the last drawing left in it.
    private SKShader ScopeFill(int height, float amplitude, VisualizerPalette palette)
    {
        _fill.Color = SKColors.White;
        if (_scopeFill is not null && _scopeFillFor == (height, palette)) return _scopeFill;
        _scopeFill?.Dispose();
        _scopeRamp?.Dispose();
        var cy = height / 2f;
        (_scopeRamp, _scopeFill) = VerticalRamp(
            cy - amplitude,
            cy + amplitude,
            [palette.Sample(1f).WithAlpha(235), palette.Sample(0.15f).WithAlpha(70), palette.Sample(1f).WithAlpha(235)],
            [0f, 0.5f, 1f]);
        _scopeFillFor = (height, palette);
        return _scopeFill;
    }

    // The classic player's oscilloscope at the window's size: 75 columns of square pixels on the
    // dotted ground, each column joined to the last so the trace is unbroken, and every pixel
    // coloured by how far from the middle it swings.
    private void PixelScope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Columns = 75;
        const int Rows = 32;
        Flat(canvas, palette);
        var (cell, left, top) = PixelGrid(width, height, Columns, Rows);
        PixelGround(canvas, palette, cell, left, top, Columns, Rows);
        var size = cell - MathF.Max(1f, MathF.Round(cell * 0.1f));
        var middle = (Rows - 1) / 2f;
        var previous = -1;
        for (var c = 0; c < Columns; c++)
        {
            var s = Math.Clamp(Sample(frame.Waveform, c, Columns) * 1.8f, -1f, 1f);
            var row = (int)MathF.Round(middle - (s * middle));
            var from = previous < 0 ? row : previous;
            for (var r = Math.Min(from, row); r <= Math.Max(from, row); r++)
            {
                _pixel.Color = palette.Sample(MathF.Abs(r - middle) / middle);
                canvas.DrawRect(left + (c * cell), top + (r * cell), size, size, _pixel);
            }

            previous = row;
        }
    }

    // The waveform filled to its centre line, the fill brightest where it swings furthest.
    private void FilledScope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var cy = height / 2f;
        var amplitude = height * 0.38f;
        var points = Math.Max(2, width / 3);
        _path.Reset();
        _path.MoveTo(0, cy);
        for (var p = 0; p < points; p++)
        {
            var s = Math.Clamp(Sample(frame.Waveform, p, points) * 1.6f, -1f, 1f);
            _path.LineTo(width * p / (float)(points - 1), cy - (s * amplitude));
        }

        _path.LineTo(width, cy);
        _path.Close();
        _fill.Shader = ScopeFill(height, amplitude, palette);
        canvas.DrawPath(_path, _fill);
        _fill.Shader = null;
        _stroke.StrokeWidth = MathF.Max(1f, height * 0.002f);
        _stroke.Color = palette.Highlight.WithAlpha(80);
        canvas.DrawLine(0, cy, width, cy, _stroke);
    }

    // The loudness of the last four seconds, mirrored about the centre and filled, the newest at
    // the right: the outline an editor draws of a recording, drawn as it plays.
    private void Envelope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        if (frame.Sequence != _envelopeSequence)
        {
            _envelopeSequence = frame.Sequence;
            var peak = 0f;
            foreach (var s in frame.Waveform) peak = MathF.Max(peak, MathF.Abs(s));
            _envelope[_envelopeHead] = Math.Clamp(peak * 1.25f, 0f, 1f);
            _envelopeHead = (_envelopeHead + 1) % EnvelopeLength;
        }

        var cy = height / 2f;
        var amplitude = height * 0.36f;
        var step = width / (float)(EnvelopeLength - 1);
        _path.Reset();
        for (var i = 0; i < EnvelopeLength; i++)
        {
            var level = _envelope[(_envelopeHead + i) % EnvelopeLength];
            var y = cy - (MathF.Max(0.004f, level) * amplitude);
            if (i == 0) _path.MoveTo(0, y);
            else _path.LineTo(i * step, y);
        }

        for (var i = EnvelopeLength - 1; i >= 0; i--)
        {
            var level = _envelope[(_envelopeHead + i) % EnvelopeLength];
            _path.LineTo(i * step, cy + (MathF.Max(0.004f, level) * amplitude));
        }

        _path.Close();
        _fill.Shader = ScopeFill(height, amplitude, palette);
        canvas.DrawPath(_path, _fill);
        _fill.Shader = null;
        _stroke.StrokeWidth = MathF.Max(1.2f, height * 0.0022f);
        _stroke.Color = palette.Highlight.WithAlpha(150);
        canvas.DrawPath(_path, _stroke);
    }

    // The waveform as dots, each coloured by its swing, the last two frames' dots fading behind it
    // as a phosphor's did.
    private void DotScope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var points = Math.Max(2, width / 7);
        var cy = height / 2f;
        var amplitude = height * 0.38f;
        var current = _dotTrail[_dotHead];
        if (current.Length != points) _dotTrail[_dotHead] = current = new float[points];
        for (var p = 0; p < points; p++) current[p] = Math.Clamp(Sample(frame.Waveform, p, points) * 1.6f, -1f, 1f);

        var radius = MathF.Max(1.5f, height * 0.0042f);
        for (var age = DotTrail - 1; age >= 0; age--)
        {
            var dots = _dotTrail[((_dotHead - age) % DotTrail + DotTrail) % DotTrail];
            if (dots.Length != points) continue;
            var fade = age == 0 ? 1f : 0.45f / age;
            for (var p = 0; p < points; p++)
            {
                var x = width * p / (float)(points - 1);
                var y = cy - (dots[p] * amplitude);
                var colour = palette.Sample(0.2f + (0.8f * MathF.Abs(dots[p])));
                if (age == 0)
                {
                    _fill.Color = colour.WithAlpha(46);
                    canvas.DrawCircle(x, y, radius * 2.6f, _fill);
                }

                _fill.Color = colour.WithAlpha((byte)(255 * fade));
                canvas.DrawCircle(x, y, radius, _fill);
            }
        }

        _dotHead = (_dotHead + 1) % DotTrail;
    }

    // Left against right, turned a quarter so the mix runs up and down and the difference across:
    // mono is a line straight up, wide stereo a broad cloud, the frames before fading behind.
    private void Vectorscope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        Flat(canvas, palette);
        var cx = width / 2f;
        var cy = height / 2f;
        var reach = MathF.Min(width, height) * 0.4f;

        // The graticule: the diamond the scope fills at full scale, the mix's axis, left and right.
        _stroke.StrokeWidth = MathF.Max(1f, height * 0.0016f);
        _stroke.Color = palette.Sample(0.3f).WithAlpha(70);
        _path.Reset();
        _path.MoveTo(cx, cy - reach);
        _path.LineTo(cx + reach, cy);
        _path.LineTo(cx, cy + reach);
        _path.LineTo(cx - reach, cy);
        _path.Close();
        canvas.DrawPath(_path, _stroke);
        _stroke.Color = palette.Sample(0.3f).WithAlpha(40);
        canvas.DrawLine(cx, cy - reach, cx, cy + reach, _stroke);
        canvas.DrawLine(cx - (reach / 2), cy - (reach / 2), cx + (reach / 2), cy + (reach / 2), _stroke);
        canvas.DrawLine(cx + (reach / 2), cy - (reach / 2), cx - (reach / 2), cy + (reach / 2), _stroke);

        var left = frame.WaveformLeft;
        var right = frame.WaveformRight;
        var n = Math.Min(left.Length, right.Length);
        if (frame.Sequence != _goniometerSequence)
        {
            _goniometerSequence = frame.Sequence;
            _goniometerHead = (_goniometerHead + 1) % _goniometer.Length;
            var points = _goniometer[_goniometerHead];
            if (points.Length != n) _goniometer[_goniometerHead] = points = new SKPoint[n];
            for (var i = 0; i < n; i++)
            {
                var mid = (left[i] + right[i]) * 0.5f;
                var side = (left[i] - right[i]) * 0.5f;
                points[i] = new SKPoint(cx + (Math.Clamp(side * 1.6f, -1f, 1f) * reach), cy - (Math.Clamp(mid * 1.6f, -1f, 1f) * reach));
            }
        }

        _stroke.StrokeWidth = MathF.Max(1.6f, height * 0.0028f);
        for (var age = _goniometer.Length - 1; age >= 0; age--)
        {
            var points = _goniometer[((_goniometerHead - age) % _goniometer.Length + _goniometer.Length) % _goniometer.Length];
            if (points.Length == 0) continue;
            _stroke.Color = (age == 0 ? palette.Highlight : palette.Sample(0.6f)).WithAlpha((byte)(age == 0 ? 170 : 90 / age));
            canvas.DrawPoints(SKPointMode.Points, points, _stroke);
        }
    }

    // A closed curve whose radius follows the waveform round it, three lobes turning one way and a
    // fainter five turning the other, glowing: the scriptable scope of the old visualization studio.
    private void Superscope(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Points = 420;
        var cx = width / 2f;
        var cy = height / 2f;
        var size = MathF.Min(width, height) * 0.34f;
        for (var figure = 1; figure >= 0; figure--)
        {
            var lobes = figure == 0 ? 3 : 5;
            var turn = figure == 0 ? _time * 0.5f : -_time * 0.35f;
            _path.Reset();
            for (var i = 0; i <= Points; i++)
            {
                var u = i % Points / (float)Points;
                var angle = u * MathF.Tau;
                var s = Sample(frame.Waveform, ((int)(u * (Points - 1)) + (figure * 97)) % Points, Points);
                var r = size * (0.62f + (0.22f * MathF.Sin((lobes * angle) + turn)) + (Math.Clamp(s * 1.4f, -1f, 1f) * 0.34f)) * (figure == 0 ? 1f : 0.72f);
                var (sin, cos) = MathF.SinCos(angle + (turn * 0.3f));
                if (i == 0) _path.MoveTo(cx + (cos * r), cy + (sin * r));
                else _path.LineTo(cx + (cos * r), cy + (sin * r));
            }

            _path.Close();
            var colour = palette.Sample(0.5f + (0.5f * MathF.Sin((_time * 0.3f) + (figure * 2f))));
            _stroke.StrokeWidth = MathF.Max(6f, height * 0.016f);
            _stroke.Color = colour.WithAlpha((byte)(figure == 0 ? 60 : 30));
            canvas.DrawPath(_path, _stroke);
            _stroke.StrokeWidth = MathF.Max(1.6f, height * 0.0034f);
            _stroke.Color = figure == 0 ? palette.Highlight : colour.WithAlpha(170);
            canvas.DrawPath(_path, _stroke);
        }
    }

    // The waveform drawn out along the arms of a five-pointed star, each arm a different stretch of
    // it, the star turning, and turning harder on the beat.
    private void ScopeStar(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        const int Arms = 5;
        const int Points = 90;
        if (frame.Beat) _beats++;
        _starTurn += (0.006f + (0.05f * frame.BeatIntensity)) * (_beats % 2 == 0 ? 1 : -1);
        var cx = width / 2f;
        var cy = height / 2f;
        var length = MathF.Min(width, height) * 0.44f;
        var wave = frame.Waveform;
        for (var arm = 0; arm < Arms; arm++)
        {
            var (sin, cos) = MathF.SinCos(_starTurn + (arm * MathF.Tau / Arms));
            _path.Reset();
            for (var i = 0; i < Points; i++)
            {
                var along = i / (float)(Points - 1);
                var s = wave.Length < 2 ? 0f : wave[(int)((((arm * wave.Length / Arms) + (along * (wave.Length / Arms))) % wave.Length))];
                var swing = Math.Clamp(s * 1.5f, -1f, 1f) * length * 0.2f * MathF.Sin(along * MathF.PI);
                var d = length * along;
                var x = cx + (cos * d) - (sin * swing);
                var y = cy + (sin * d) + (cos * swing);
                if (i == 0) _path.MoveTo(x, y);
                else _path.LineTo(x, y);
            }

            var colour = palette.Sample(arm / (float)(Arms - 1));
            _stroke.StrokeWidth = MathF.Max(5f, height * 0.013f);
            _stroke.Color = colour.WithAlpha(55);
            canvas.DrawPath(_path, _stroke);
            _stroke.StrokeWidth = MathF.Max(1.5f, height * 0.003f);
            _stroke.Color = colour;
            canvas.DrawPath(_path, _stroke);
        }

        _fill.Color = palette.Highlight.WithAlpha((byte)(120 + (120 * frame.BeatIntensity)));
        canvas.DrawCircle(cx, cy, MathF.Max(3f, height * 0.008f), _fill);
    }

    // Two needle meters, left and right, backlit in the palette's colour. A volume unit meter's
    // needle takes about 300 ms to reach a steady level and swings a little past it, which a
    // spring with that settling time and a touch less than critical damping does; 0 on the scale is
    // 16 dB under full scale, and a lamp lights for a moment when a channel comes within 3 dB of it.
    private void VuMeters(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        Flat(canvas, palette);
        var faceWidth = MathF.Min(width * 0.43f, height * 0.72f * 1.55f);
        var faceHeight = faceWidth * 0.64f;
        var gap = faceWidth * 0.06f;
        var top = (height - faceHeight) / 2;
        for (var channel = 0; channel < 2; channel++)
        {
            var (rms, peak) = channel == 0 ? (frame.RmsLeft, frame.PeakLeft) : (frame.RmsRight, frame.PeakRight);
            var db = (rms - 1f) * 60f;
            var target = VuPosition(Math.Clamp(db + 16f, -22f, 4f));
            for (var step = dt; step > 0; step -= 0.004f)
            {
                var h = MathF.Min(step, 0.004f);
                const float Omega = MathF.Tau * 2.1f;
                const float Damping = 0.72f;
                var accel = (Omega * Omega * (target - _needle[channel])) - (2 * Damping * Omega * _needleSpeed[channel]);
                _needleSpeed[channel] += accel * h;
                _needle[channel] += _needleSpeed[channel] * h;
            }

            _overload[channel] = (peak - 1f) * 60f > -3f ? 0.35f : MathF.Max(0, _overload[channel] - dt);
            var left = (width / 2f) + (channel == 0 ? -gap / 2 - faceWidth : gap / 2);
            Meter(canvas, new SKRect(left, top, left + faceWidth, top + faceHeight), _needle[channel], _overload[channel] > 0, channel == 0 ? "L" : "R", palette);
        }
    }

    // Where on the scale, 0 to 1, a reading in volume units sits: the scale is laid out by
    // voltage, so -20 is at the left end, 0 about seven tenths along and +3 at the right end.
    private static float VuPosition(float vu) => (MathF.Pow(10, vu / 20) - 0.1f) / (MathF.Pow(10, 3f / 20) - 0.1f);

    private void Meter(SKCanvas canvas, SKRect face, float needle, bool overload, string channel, VisualizerPalette palette)
    {
        // All of the face but the lamp and the needle stays the same: drawn once per size and
        // palette (its backlight alone, a gradient, costs more than a whole frame should) and
        // copied in.
        if (_face is null || _faceFor != (face.Size, palette))
        {
            _face?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo((int)MathF.Ceiling(face.Width), (int)MathF.Ceiling(face.Height), SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            Face(surface.Canvas, new SKRect(0, 0, face.Width, face.Height), palette);
            _face = surface.Snapshot();
            _faceFor = (face.Size, palette);
        }

        canvas.DrawImage(_face, face.Left, face.Top);
        var radius = face.Height * 0.12f;
        var pivot = new SKPoint(face.MidX, face.Bottom + (face.Height * 0.18f));
        var arc = face.Height * 0.86f;
        var ink = Mix(palette.Highlight, palette.Sample(0.8f), 0.25f);
        var hot = palette.Stops[^1];
        _font.Size = face.Height * 0.07f;
        _fill.Color = ink.WithAlpha(200);
        canvas.DrawText(channel, face.Left + (face.Width * 0.08f), face.Bottom - (face.Height * 0.08f), SKTextAlign.Left, _font, _fill);

        // The lamp, and the needle on its pivot under the face.
        _fill.Color = overload ? hot : hot.WithAlpha(40);
        canvas.DrawCircle(face.Right - (face.Width * 0.08f), face.Top + (face.Height * 0.14f), face.Height * 0.035f, _fill);
        canvas.Save();
        _faceClip.Reset();
        _faceClip.AddRoundRect(face, radius, radius);
        canvas.ClipPath(_faceClip, antialias: true);
        var tip = OnScale(pivot, ScaleAngle(needle), arc + (face.Height * 0.1f));
        _stroke.StrokeCap = SKStrokeCap.Round;
        _stroke.StrokeWidth = MathF.Max(2f, face.Height * 0.013f);
        _stroke.Color = SKColors.Black.WithAlpha(90);
        canvas.DrawLine(pivot.X + 3, pivot.Y + 3, tip.X + 3, tip.Y + 3, _stroke);
        _stroke.Color = palette.Highlight;
        canvas.DrawLine(pivot, tip, _stroke);
        canvas.Restore();

        // Glass over it all.
        _fill.Color = SKColors.White.WithAlpha(12);
        canvas.DrawRoundRect(new SKRect(face.Left, face.Top, face.Right, face.MidY), radius, radius, _fill);
    }

    // Where the scale puts a position, 0 to 1: the needle sweeps 47 degrees either side of upright.
    private static float ScaleAngle(float position) => -MathF.PI / 2 - 0.82f + (2 * 0.82f * Math.Clamp(position, -0.02f, 1.04f));

    private static SKPoint OnScale(SKPoint pivot, float angle, float distance)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return new SKPoint(pivot.X + (cos * distance), pivot.Y + (sin * distance));
    }

    // The face as it stays: lit from behind, brightest low in the middle, with the scale on it.
    private void Face(SKCanvas canvas, SKRect face, VisualizerPalette palette)
    {
        var radius = face.Height * 0.12f;
        using (var light = SKShader.CreateRadialGradient(
            new SKPoint(face.MidX, face.Top + (face.Height * 0.72f)),
            face.Width * 0.62f,
            [Mix(palette.Background, palette.Sample(0.5f), 0.62f), Mix(palette.Background, palette.Sample(0.5f), 0.3f)],
            [0f, 1f],
            SKShaderTileMode.Clamp))
        {
            _fill.Color = SKColors.White;
            _fill.Shader = light;
            canvas.DrawRoundRect(face, radius, radius, _fill);
            _fill.Shader = null;
        }

        var pivot = new SKPoint(face.MidX, face.Bottom + (face.Height * 0.18f));
        var arc = face.Height * 0.86f;
        float Angle(float position) => ScaleAngle(position);
        SKPoint At(float angle, float distance) => OnScale(pivot, angle, distance);
        var ink = Mix(palette.Highlight, palette.Sample(0.8f), 0.25f);
        var hot = palette.Stops[^1];

        // The scale: the red of the last three units as a band, the marks, the numbers over them.
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeWidth = face.Height * 0.035f;
        _stroke.Color = hot.WithAlpha(210);
        _path.Reset();
        var zero = Angle(VuPosition(0));
        _path.AddArc(new SKRect(pivot.X - arc, pivot.Y - arc, pivot.X + arc, pivot.Y + arc), zero * 180 / MathF.PI, (Angle(1) - zero) * 180 / MathF.PI);
        canvas.DrawPath(_path, _stroke);
        _stroke.StrokeWidth = MathF.Max(1f, face.Height * 0.008f);
        _stroke.Color = ink.WithAlpha(200);
        _path.Reset();
        _path.AddArc(new SKRect(pivot.X - arc, pivot.Y - arc, pivot.X + arc, pivot.Y + arc), Angle(0) * 180 / MathF.PI, (zero - Angle(0)) * 180 / MathF.PI);
        canvas.DrawPath(_path, _stroke);
        foreach (var mark in VuMarks)
        {
            var angle = Angle(VuPosition(mark));
            _stroke.Color = (mark > 0 ? hot : ink).WithAlpha(230);
            canvas.DrawLine(At(angle, arc - (face.Height * 0.02f)), At(angle, arc + (face.Height * (Array.IndexOf(VuNumbers, mark) >= 0 ? 0.075f : 0.045f))), _stroke);
        }

        _font.Size = face.Height * 0.075f;
        for (var i = 0; i < VuNumbers.Length; i++)
        {
            var at = At(Angle(VuPosition(VuNumbers[i])), arc + (face.Height * 0.15f));
            _fill.Color = (VuNumbers[i] > 0 ? hot : ink).WithAlpha(235);
            canvas.DrawText(VuLabels[i], at.X, at.Y + (_font.Size * 0.35f), SKTextAlign.Center, _font, _fill);
        }

        _font.Size = face.Height * 0.11f;
        _fill.Color = ink.WithAlpha(200);
        canvas.DrawText("VU", face.MidX, face.Top + (face.Height * 0.74f), SKTextAlign.Center, _font, _fill);
        _stroke.StrokeCap = SKStrokeCap.Round;
    }
}
