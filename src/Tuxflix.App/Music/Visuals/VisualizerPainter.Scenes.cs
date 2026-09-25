using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// Scenes: a fountain, fireworks and a starfield, rings on the beat, and the cover in its own light.
internal sealed partial class VisualizerPainter
{
    private const int MaxJets = 1600;
    private const int MaxEmbers = 2400;
    private const int StarCount = 700;
    private const int MaxRings = 40;

    private readonly Jet[] _jets = new Jet[MaxJets];
    private readonly float[] _jetDebt = new float[64];
    private readonly Ember[] _embers = new Ember[MaxEmbers];
    private readonly Star[] _stars = new Star[StarCount];
    private readonly Ring[] _rings = new Ring[MaxRings];
    private readonly SKPoint[] _sky = new SKPoint[90];
    private readonly SKPath _coverClip = new();
    private int _jetCount;
    private int _emberCount;
    private int _ringCount;
    private bool _starsMade;
    private float _sinceLaunch;
    private float _sinceBurst;
    private float _sinceRing;
    private int _ringTone;
    private SKImage? _coverFor;
    private SKImage? _coverSmall;
    private float _vinylTurn;

    private struct Jet
    {
        public float X, Y, Vx, Vy, Age;
        public int Band;
    }

    private struct Ember
    {
        public float X, Y, Vx, Vy, Age, Life;
        public SKColor Colour;
        public bool Rocket;
    }

    private struct Star
    {
        public float X, Y, Z;
    }

    private struct Ring
    {
        public float Radius, Speed, Width;
        public SKColor Colour;
    }

    private void DisposeScenes()
    {
        _coverClip.Dispose();
        _coverSmall?.Dispose();
    }

    private float RandomUnit() => (NextRandom() & 0xFFFFFF) / (float)0x1000000;

    // A fountain along the floor, a jet over each band throwing sparks up as hard as the band is
    // loud, which arc over and fall back under gravity, fading as they go.
    private void Fountain(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        var bands = frame.Bands;
        var n = Math.Min(bands.Length, _jetDebt.Length);
        var left = width * 0.06f;
        var slot = width * 0.88f / Math.Max(1, n);
        var floor = height * 0.9f;
        var gravity = height * 1.45f;
        for (var b = 0; b < n; b++)
        {
            _jetDebt[b] += dt * MathF.Max(0f, bands[b] - 0.14f) * 80f;
            while (_jetDebt[b] >= 1f && _jetCount < MaxJets)
            {
                _jetDebt[b] -= 1f;
                _jets[_jetCount++] = new Jet
                {
                    X = left + ((b + 0.5f) * slot) + ((RandomUnit() - 0.5f) * slot * 0.4f),
                    Y = floor,
                    Vx = (RandomUnit() - 0.5f) * width * 0.05f,
                    Vy = -height * (0.55f + (0.95f * bands[b])) * (0.85f + (0.3f * RandomUnit())),
                    Band = b,
                };
            }

            _jetDebt[b] = MathF.Min(_jetDebt[b], 1f);
        }

        var radius = MathF.Max(1.5f, height * 0.0038f);
        for (var i = 0; i < _jetCount;)
        {
            ref var jet = ref _jets[i];
            jet.Age += dt;
            jet.Vy += gravity * dt;
            jet.X += jet.Vx * dt;
            jet.Y += jet.Vy * dt;
            if (jet.Y > floor || jet.Age > 3f)
            {
                _jets[i] = _jets[--_jetCount];
                continue;
            }

            _fill.Color = palette.Sample(jet.Band / (float)Math.Max(1, n - 1)).WithAlpha((byte)(255 * (1 - (jet.Age / 3f))));
            canvas.DrawCircle(jet.X, jet.Y, radius, _fill);
            i++;
        }

        // The jets' mouths, lit by their bands.
        for (var b = 0; b < n; b++)
        {
            _fill.Color = palette.Sample(b / (float)Math.Max(1, n - 1)).WithAlpha((byte)(60 + (195 * bands[b])));
            canvas.DrawOval(left + ((b + 0.5f) * slot), floor + (height * 0.01f), slot * 0.32f, height * 0.006f, _fill);
        }
    }

    // Fireworks over a night sky: each beat bursts at once somewhere high up into sparks of one
    // colour from the ramp, more of them the heavier the bass, and between beats rockets climb and
    // burst smaller; the sparks slow, fall and fade, each drawn as a short streak along its way.
    private void Fireworks(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        Flat(canvas, palette);
        if (_sky[0] == default)
        {
            for (var i = 0; i < _sky.Length; i++) _sky[i] = new SKPoint(RandomUnit(), RandomUnit() * 0.7f);
        }

        _stroke.StrokeWidth = MathF.Max(1.5f, height * 0.002f);
        _stroke.Color = palette.Highlight.WithAlpha(38);
        for (var i = 0; i < _sky.Length; i++) canvas.DrawPoint(_sky[i].X * width, _sky[i].Y * height, _stroke);

        _sinceBurst += dt;
        if (frame.Beat && _sinceBurst > 0.12f)
        {
            _sinceBurst = 0;
            Burst(width * (0.15f + (0.7f * RandomUnit())), height * (0.15f + (0.4f * RandomUnit())), 60 + (int)(110 * bass), palette.Sample(0.35f + (0.65f * RandomUnit())), height, palette);
        }

        _sinceLaunch += dt;
        if (!frame.Silent && _sinceLaunch > 0.9f && _emberCount < MaxEmbers)
        {
            _sinceLaunch = 0;
            var peak = height * (0.2f + (0.3f * RandomUnit()));
            _embers[_emberCount++] = new Ember
            {
                X = width * (0.15f + (0.7f * RandomUnit())),
                Y = height,
                Vx = (RandomUnit() - 0.5f) * width * 0.06f,
                Vy = -MathF.Sqrt(2 * height * 1.8f * (height - peak)),
                Life = 4f,
                Colour = palette.Sample(0.35f + (0.65f * RandomUnit())),
                Rocket = true,
            };
        }

        var rise = height * 1.8f;
        var fall = height * 0.32f;
        var drag = MathF.Max(0f, 1f - (1.5f * dt));
        _stroke.StrokeWidth = MathF.Max(1.5f, height * 0.0045f);
        for (var i = 0; i < _emberCount;)
        {
            ref var e = ref _embers[i];
            e.Age += dt;
            if (e.Rocket)
            {
                e.Vy += rise * dt;
                if (e.Vy >= 0)
                {
                    var (x, y, colour) = (e.X, e.Y, e.Colour);
                    _embers[i] = _embers[--_emberCount];
                    Burst(x, y, 40 + (int)(60 * bass), colour, height, palette);
                    continue;
                }
            }
            else
            {
                e.Vx *= drag;
                e.Vy = (e.Vy * drag) + (fall * dt);
            }

            e.X += e.Vx * dt;
            e.Y += e.Vy * dt;
            if (e.Age >= e.Life || e.Y > height + 20)
            {
                _embers[i] = _embers[--_emberCount];
                continue;
            }

            var fade = e.Rocket ? 1f : 1f - (e.Age / e.Life);
            _stroke.Color = e.Colour.WithAlpha((byte)(255 * fade));
            canvas.DrawLine(e.X - (e.Vx * 0.06f), e.Y - (e.Vy * 0.06f), e.X, e.Y, _stroke);
            _fill.Color = Mix(e.Colour, SKColors.White, 0.5f).WithAlpha((byte)(255 * fade * fade));
            canvas.DrawCircle(e.X, e.Y, _stroke.StrokeWidth * 0.9f, _fill);
            i++;
        }
    }

    // Sparks spread evenly over a disc, as a sphere bursting looks from afar, a few in the highlight.
    private void Burst(float x, float y, int count, SKColor colour, int height, VisualizerPalette palette)
    {
        for (var s = 0; s < count && _emberCount < MaxEmbers; s++)
        {
            var (sin, cos) = MathF.SinCos(RandomUnit() * MathF.Tau);
            var speed = height * 0.42f * MathF.Sqrt(RandomUnit());
            _embers[_emberCount++] = new Ember
            {
                X = x,
                Y = y,
                Vx = cos * speed,
                Vy = sin * speed,
                Life = 1.1f + (0.8f * RandomUnit()),
                Colour = RandomUnit() < 0.18f ? palette.Highlight : colour,
            };
        }
    }

    // Stars rushing past out of the middle, each drawn as a streak from where it was, nearer
    // stars brighter and thicker; the bass and the beat push the ship on.
    private void Starfield(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        Flat(canvas, palette);
        if (!_starsMade)
        {
            for (var i = 0; i < StarCount; i++) _stars[i] = new Star { X = (RandomUnit() * 2) - 1, Y = (RandomUnit() * 2) - 1, Z = 0.05f + (0.95f * RandomUnit()) };
            _starsMade = true;
        }

        var speed = 0.16f + (1.1f * bass) + (1.5f * frame.BeatIntensity);
        var cx = width / 2f;
        var cy = height / 2f;
        var focal = MathF.Max(width, height) * 0.5f;
        for (var i = 0; i < StarCount; i++)
        {
            ref var star = ref _stars[i];
            var was = star.Z;
            star.Z -= speed * dt;
            var x = cx + (star.X / star.Z * focal);
            var y = cy + (star.Y / star.Z * focal);
            if (star.Z <= 0.02f || x < -50 || y < -50 || x > width + 50 || y > height + 50)
            {
                star = new Star { X = (RandomUnit() * 2) - 1, Y = (RandomUnit() * 2) - 1, Z = 1f };
                continue;
            }

            var near = 1f - star.Z;
            _stroke.StrokeWidth = MathF.Max(1f, 3.2f * near * near * (height / 700f));
            _stroke.Color = Mix(palette.Sample(0.7f), palette.Highlight, near).WithAlpha((byte)(255 * Math.Clamp(near * 1.4f, 0.15f, 1f)));
            canvas.DrawLine(cx + (star.X / was * focal), cy + (star.Y / was * focal), x, y, _stroke);
        }
    }

    // Rings sent out from the middle: a broad one on each beat, as wide and quick as the bass is
    // heavy, and a fine one now and then while music plays, each fading as it spreads; in the middle
    // a light that swells with the level.
    private void Pulse(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        var size = MathF.Min(width, height);
        var furthest = MathF.Sqrt((width * width) + (height * height)) / 2;
        _sinceRing += dt;
        if (frame.Beat && _ringCount < MaxRings)
        {
            _ringTone++;
            _rings[_ringCount++] = new Ring { Radius = size * 0.04f, Speed = size * (0.35f + (0.5f * bass)), Width = size * (0.008f + (0.02f * bass)), Colour = palette.Sample(_ringTone % 5 / 4f) };
        }
        else if (!frame.Silent && _sinceRing > 0.45f && _ringCount < MaxRings)
        {
            _sinceRing = 0;
            _rings[_ringCount++] = new Ring { Radius = size * 0.04f, Speed = size * 0.22f, Width = MathF.Max(1f, size * 0.003f), Colour = palette.Sample(0.8f) };
        }

        for (var i = 0; i < _ringCount;)
        {
            ref var ring = ref _rings[i];
            ring.Radius += ring.Speed * dt;
            if (ring.Radius > furthest)
            {
                _rings[i] = _rings[--_ringCount];
                continue;
            }

            var fade = MathF.Pow(1f - (ring.Radius / furthest), 1.5f);
            _stroke.StrokeWidth = ring.Width;
            _stroke.Color = ring.Colour.WithAlpha((byte)(230 * fade));
            canvas.DrawCircle(width / 2f, height / 2f, ring.Radius, _stroke);
            i++;
        }

        var level = (frame.RmsLeft + frame.RmsRight) / 2;
        _fill.Color = palette.Sample(level).WithAlpha(70);
        canvas.DrawCircle(width / 2f, height / 2f, size * (0.05f + (0.07f * level)), _fill);
        _fill.Color = palette.Highlight.WithAlpha((byte)(150 + (100 * frame.BeatIntensity)));
        canvas.DrawCircle(width / 2f, height / 2f, size * (0.02f + (0.03f * level)), _fill);
    }

    // The cover, large and square in the middle, breathing with the level and the beat, over a
    // haze made of itself (drawn from a copy a few pixels across, which is the cheapest blur there
    // is) and a halo of its own colours that brightens on the beat. With no cover, a record spins.
    private void CoverArt(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        var cover = Cover;
        if (cover is null)
        {
            Background(canvas, width, height, palette, frame.BeatIntensity, bass);
            Vinyl(canvas, width, height, frame, palette, dt);
            return;
        }

        if (!ReferenceEquals(_coverFor, cover))
        {
            _coverSmall?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo(12, 12, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.DrawImage(cover, new SKRect(0, 0, 12, 12), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            _coverSmall = surface.Snapshot();
            _coverFor = cover;
        }

        var haze = _coverSmall!;
        var whole = Whole(width, height);
        canvas.DrawImage(haze, new SKRect(-width * 0.1f, -height * 0.1f, width * 1.1f, height * 1.1f), Linear);
        _fill.Color = palette.Background.WithAlpha(150);
        canvas.DrawRect(whole, _fill);

        var level = (frame.RmsLeft + frame.RmsRight) / 2;
        var side = MathF.Min(width, height) * 0.58f * (1 + (0.035f * level) + (0.03f * frame.BeatIntensity));
        var art = SKRect.Create((width - side) / 2, (height - side) / 2, side, side);
        var halo = side * (0.14f + (0.06f * frame.BeatIntensity));
        _tint.Color = SKColors.White.WithAlpha((byte)(120 + (120 * frame.BeatIntensity)));
        canvas.DrawImage(haze, SKRect.Inflate(art, halo, halo), Linear, _tint);

        var corner = side * 0.025f;
        _fill.Color = SKColors.Black.WithAlpha(110);
        canvas.DrawRoundRect(SKRect.Create(art.Left, art.Top + (side * 0.02f), side, side), corner, corner, _fill);
        canvas.Save();
        _coverClip.Reset();
        _coverClip.AddRoundRect(art, corner, corner);
        canvas.ClipPath(_coverClip, antialias: true);
        canvas.DrawImage(cover, art, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
        _stroke.StrokeWidth = 1f;
        _stroke.Color = SKColors.White.WithAlpha(40);
        canvas.DrawRoundRect(art, corner, corner, _stroke);
    }

    // A record at 33 and a third turns a minute, grooves catching the light, its label in the
    // palette's colours, swelling a little with the level.
    private void Vinyl(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        _vinylTurn = (_vinylTurn + (frame.Silent ? 0 : dt * MathF.Tau * 33.333f / 60f)) % MathF.Tau;
        var cx = width / 2f;
        var cy = height / 2f;
        var level = (frame.RmsLeft + frame.RmsRight) / 2;
        var radius = MathF.Min(width, height) * 0.36f * (1 + (0.02f * level));
        _fill.Color = new SKColor(0x0B, 0x0B, 0x0E);
        canvas.DrawCircle(cx, cy, radius, _fill);
        _stroke.StrokeWidth = MathF.Max(1f, radius * 0.004f);
        for (var g = 0; g < 18; g++)
        {
            _stroke.Color = SKColors.White.WithAlpha((byte)(g % 3 == 0 ? 22 : 12));
            canvas.DrawCircle(cx, cy, radius * (0.42f + (0.55f * g / 17f)), _stroke);
        }

        // The light on the grooves: two faint glints opposite each other, as a lamp makes on a
        // record, which stay where the lamp is while the record turns under them.
        _stroke.StrokeWidth = radius * 0.5f;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.Color = SKColors.White.WithAlpha((byte)(9 + (12 * level)));
        _path.Reset();
        var groove = new SKRect(cx - (radius * 0.72f), cy - (radius * 0.72f), cx + (radius * 0.72f), cy + (radius * 0.72f));
        _path.AddArc(groove, 200, 22);
        _path.AddArc(groove, 20, 22);
        canvas.DrawPath(_path, _stroke);
        _stroke.StrokeCap = SKStrokeCap.Round;

        _fill.Color = palette.Sample(0.55f);
        canvas.DrawCircle(cx, cy, radius * 0.33f, _fill);
        _fill.Color = palette.Highlight.WithAlpha(160);
        var (sin, cos) = MathF.SinCos(_vinylTurn);
        canvas.DrawCircle(cx + (cos * radius * 0.2f), cy + (sin * radius * 0.2f), radius * 0.035f, _fill);
        _fill.Color = palette.Background;
        canvas.DrawCircle(cx, cy, radius * 0.03f, _fill);
    }
}
