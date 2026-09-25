using SkiaSharp;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

// The pictures made pixel by pixel, as the demos made them: a tunnel, plasma, fire, water, blobs
// and slow colour. Each works on a picture half the canvas's size or less (the canvas is already
// small for these, and the view scales it up), from tables made once per size, so a pixel costs
// a few look-ups; the colours come through the palette's table.
internal sealed partial class VisualizerPainter
{
    private const int TunnelTexture = 256;
    private const int SineSteps = 1024;
    private const int Blobs12 = 12;

    private static readonly float[] Sine = [.. Enumerable.Range(0, SineSteps).Select(i => MathF.Sin(i * MathF.Tau / SineSteps))];

    private readonly PixelField _field = new();
    private readonly int[] _cycle = new int[256];
    private VisualizerPalette? _cycleFor;
    private uint _seed = 2463534242;

    // The tunnel: for each pixel of an area half again the picture's size (so its middle can
    // wander), which way round (u) and how deep (v) it looks, and how lit its distance leaves it.
    private readonly byte[] _tunnelTexture = new byte[TunnelTexture * TunnelTexture];
    private readonly byte[] _tunnelRings = new byte[TunnelTexture];
    private byte[] _tunnelU = [];
    private int[] _tunnelV = [];
    private byte[] _tunnelShade = [];
    private int _tunnelW;
    private int _tunnelH;
    private float _tunnelSpin;
    private float _tunnelTravel;
    private int _tunnelRow;

    // Plasma: each pixel's distance from the middle as a step of the sine table, per size.
    private int[] _plasmaRing = [];
    private float[] _plasmaX = [];
    private float[] _plasmaSinA = [];
    private float[] _plasmaCosA = [];
    private float[] _plasmaY = [];
    private float[] _plasmaSinB = [];
    private float[] _plasmaCosB = [];
    private float _plasmaCycle;
    private float _plasmaTime;

    private byte[] _heat = [];
    private short[] _water = [];
    private short[] _waterWas = [];
    private int[] _pool = [];
    private VisualizerPalette? _poolFor;
    private float _nextDrop;

    private readonly float[] _blobLevels = new float[Blobs12];
    private float[] _blobField = [];
    private float[] _blobBest = [];
    private byte[] _blobOwner = [];

    private readonly byte[] _gauss = new byte[256];

    private void DisposeFields() => _field.Dispose();

    // A quick generator for the effects' noise: one per pixel from Random costs more than the pixel.
    private uint NextRandom()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return _seed;
    }

    // 256 colours going round: dark, up the ramp to its loud end, and back, so a value that wraps
    // round never jumps from one colour to another.
    private int[] Cycle(VisualizerPalette palette)
    {
        if (ReferenceEquals(_cycleFor, palette)) return _cycle;
        _cycleFor = palette;
        for (var i = 0; i < 256; i++)
        {
            var up = 1f - MathF.Abs((2f * i / 256f) - 1f);
            _cycle[i] = Pack(Mix(palette.Background, palette.Sample(up), 0.3f + (0.7f * up)).WithAlpha(255));
        }

        return _cycle;
    }

    private static SKRect Whole(int width, int height) => new(0, 0, width, height);

    // Down a tunnel lined with a soft checker of rings and spokes. The music rides it: each ring
    // leaves the far end as bright as the music was when it set off and comes towards the viewer,
    // the tunnel hurries with the bass and turns harder on the beat, and its far end wanders.
    private void Tunnel(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        var lut = Lut(palette);
        _field.Ensure(width, height);
        var w = _field.Width;
        var h = _field.Height;
        var areaW = w + (w / 2);
        var areaH = h + (h / 2);
        if (_tunnelW != areaW || _tunnelH != areaH) BuildTunnel(areaW, areaH, Math.Min(w, h));

        _tunnelSpin += dt * (14f + (90f * frame.BeatIntensity));
        _tunnelTravel += dt * (30f + (170f * bass));

        // Kept small, a whole number of turns of the texture at a time, so they stay exact.
        if (_tunnelSpin > 65536) _tunnelSpin -= 65536;
        if (_tunnelTravel > 65536)
        {
            _tunnelTravel -= 65536;
            _tunnelRow -= 65536;
        }

        // The rows passing the far end since the last frame take the music's level now.
        var level = (byte)(70 + (185 * Math.Clamp((0.6f * bass) + (0.4f * frame.BeatIntensity), 0f, 1f)));
        var entering = (int)_tunnelTravel + 255;
        for (var row = _tunnelRow; row <= entering; row++) _tunnelRings[row & 255] = level;
        _tunnelRow = entering + 1;

        var spin = (int)_tunnelSpin;
        var depth = (int)_tunnelTravel;
        var ox = (int)(((areaW - w) / 2f) + (MathF.Sin(_time * 0.37f) * (areaW - w) * 0.42f));
        var oy = (int)(((areaH - h) / 2f) + (MathF.Cos(_time * 0.29f) * (areaH - h) * 0.42f));
        var pixels = _field.Pixels;
        for (var y = 0; y < h; y++)
        {
            var from = ((y + oy) * areaW) + ox;
            var to = y * w;
            for (var x = 0; x < w; x++)
            {
                var i = from + x;
                var v = (_tunnelV[i] + depth) & 255;
                var texel = _tunnelTexture[(v << 8) | ((_tunnelU[i] + spin) & 255)];
                pixels[to + x] = lut[(texel * _tunnelRings[v] * _tunnelShade[i]) >> 16];
            }
        }

        _field.Upload();
        _field.Draw(canvas, Whole(width, height), Linear);
    }

    private void BuildTunnel(int areaW, int areaH, int size)
    {
        _tunnelW = areaW;
        _tunnelH = areaH;
        _tunnelU = new byte[areaW * areaH];
        _tunnelV = new int[areaW * areaH];
        _tunnelShade = new byte[areaW * areaH];
        var reach = size * 0.62f;
        for (var y = 0; y < areaH; y++)
        {
            for (var x = 0; x < areaW; x++)
            {
                var dx = x - (areaW / 2f);
                var dy = y - (areaH / 2f);
                var distance = MathF.Max(1f, MathF.Sqrt((dx * dx) + (dy * dy)));
                var i = (y * areaW) + x;
                _tunnelU[i] = (byte)((int)(TunnelTexture * (MathF.Atan2(dy, dx) / MathF.Tau + 0.5f)) & 255);
                _tunnelV[i] = (int)(size * 12f / distance) & 0xFFFFFF;
                _tunnelShade[i] = (byte)(255 * Math.Clamp(MathF.Pow(distance / reach, 0.8f), 0f, 1f));
            }
        }

        for (var v = 0; v < TunnelTexture; v++)
        {
            for (var u = 0; u < TunnelTexture; u++)
            {
                var rings = MathF.Cos(v * MathF.Tau / 32f);
                var spokes = MathF.Cos(u * MathF.Tau * 8f / TunnelTexture);
                _tunnelTexture[(v << 8) | u] = (byte)(255 * (0.55f + (0.45f * rings * spokes)));
            }
        }
    }

    // The demos' plasma: four travelling waves summed (across, down, diagonally and round the
    // middle) and read through the palette going round, which drifts on its own and jumps on the
    // beat. The waves quicken and tighten with the music. The diagonal wave is split into a part
    // per column and a part per row, so a pixel costs two products, not a sine.
    private void Plasma(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float dt)
    {
        var colours = Cycle(palette);
        _field.Ensure(Math.Max(2, width / 2), Math.Max(2, height / 2));
        var w = _field.Width;
        var h = _field.Height;
        if (_plasmaRing.Length != w * h)
        {
            _plasmaRing = new int[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var dx = (x / (float)w) - 0.5f;
                    var dy = (y / (float)h) - 0.5f;
                    _plasmaRing[(y * w) + x] = (int)(MathF.Sqrt((dx * dx) + (dy * dy)) * 5f * SineSteps);
                }
            }

            _plasmaX = new float[w];
            _plasmaSinA = new float[w];
            _plasmaCosA = new float[w];
            _plasmaY = new float[h];
            _plasmaSinB = new float[h];
            _plasmaCosB = new float[h];
        }

        var energy = 0f;
        foreach (var b in frame.Bands) energy += b;
        energy = frame.Bands.Length == 0 ? 0 : energy / frame.Bands.Length;

        // Its own clock, which runs faster with the music: the elapsed time times a speed would
        // jump every time the speed changed.
        _plasmaTime += dt * (0.8f + (0.8f * energy));
        var t = _plasmaTime;
        var across = 2.2f + (2.4f * energy);
        for (var x = 0; x < w; x++)
        {
            var u = x / (float)w;
            _plasmaX[x] = MathF.Sin(((u * across) + (t * 0.7f)) * MathF.Tau);
            (_plasmaSinA[x], _plasmaCosA[x]) = MathF.SinCos(((u * 2f) + (t * 0.9f)) * MathF.Tau);
        }

        for (var y = 0; y < h; y++)
        {
            var v = y / (float)h;
            _plasmaY[y] = MathF.Sin(((v * 1.7f) - (t * 0.5f)) * MathF.Tau);
            (_plasmaSinB[y], _plasmaCosB[y]) = MathF.SinCos(v * 2f * MathF.Tau);
        }

        _plasmaCycle += (dt * 24f) + (frame.BeatIntensity * 5f);
        var shift = (int)_plasmaCycle;
        var phase = (int)(t * 1.3f * SineSteps);
        var pixels = _field.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var (py, sb, cb) = (_plasmaY[y], _plasmaSinB[y], _plasmaCosB[y]);
            for (var x = 0; x < w; x++)
            {
                var s = _plasmaX[x] + py + ((_plasmaSinA[x] * cb) + (_plasmaCosA[x] * sb)) + Sine[(_plasmaRing[row + x] - phase) & (SineSteps - 1)];
                pixels[row + x] = colours[((int)((s + 4f) * 32f) + shift) & 255];
            }
        }

        _field.Upload();
        _field.Draw(canvas, Whole(width, height), Linear);
    }

    // Fire as the old consoles drew it: each pass carries every cell's heat one row up, a column to
    // either side or straight up at random, losing a step of heat half the time; that is what
    // makes separate tongues that lick and flicker. The floor burns as hot as the band over it is
    // loud, so the flames stand tallest over the bands with the most in them, and the step lost
    // comes from the height, so a full flame dies about two thirds of the way up at any size.
    private void Fire(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        var lut = Lut(palette);
        if (_field.Ensure(Math.Max(4, width / 2), Math.Max(4, height / 2))) _heat = [];
        var w = _field.Width;
        var h = _field.Height;
        if (_heat.Length != w * h) _heat = new byte[w * h];
        var heat = _heat;
        var bands = frame.Bands;
        var loss = (int)MathF.Ceiling(255f / (h * 0.62f) * 2f);
        for (var x = 0; x < w; x++)
        {
            var level = LevelAt(bands, x * (bands.Length - 1) / (float)Math.Max(1, w - 1));
            heat[((h - 1) * w) + x] = (byte)Math.Clamp((int)(255 * (0.3f + (0.7f * level))) - (int)(NextRandom() % 24), 0, 255);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            for (var y = 1; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    var value = heat[row + x];
                    var chance = NextRandom();
                    var to = Math.Clamp(x + (int)(chance % 3) - 1, 0, w - 1);
                    heat[row - w + to] = (byte)Math.Max(0, value - ((chance >> 8) & 1) * loss);
                }
            }
        }

        var pixels = _field.Pixels;
        for (var i = 0; i < heat.Length; i++) pixels[i] = lut[heat[i]];
        _field.Upload();
        _field.Draw(canvas, Whole(width, height), Linear);
    }

    // A tiled pool the music drops ripples into: a big drop on each beat as heavy as the bass, a
    // patter of small ones as the treble rises. Each step is the classic ripple (a cell becomes the
    // mean of its neighbours less what it was, losing a little), and the floor is drawn bent by
    // each ripple's slope and lit on the side it faces.
    private void Water(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette, float bass, float dt)
    {
        var fresh = _field.Ensure(Math.Max(8, width / 2), Math.Max(8, height / 2));
        var w = _field.Width;
        var h = _field.Height;
        if (fresh || _water.Length != w * h)
        {
            _water = new short[w * h];
            _waterWas = new short[w * h];
            _poolFor = null;
        }

        if (!ReferenceEquals(_poolFor, palette)) BuildPool(w, h, palette);

        var treble = 0f;
        var bands = frame.Bands;
        for (var b = bands.Length * 2 / 3; b < bands.Length; b++) treble += bands[b];
        treble = bands.Length == 0 ? 0 : treble / Math.Max(1, bands.Length - (bands.Length * 2 / 3));
        if (frame.Beat) Drop(w, h, 3 + (int)(4 * bass), (short)(500 + (2200 * bass)));
        if ((NextRandom() & 1023) / 1024f < treble * treble * 0.9f) Drop(w, h, 1 + (int)(NextRandom() % 2), 260);
        _nextDrop -= dt;
        if (_nextDrop <= 0)
        {
            Drop(w, h, 2, 300);
            _nextDrop = 1.4f;
        }

        // One step of the ripple, into the older buffer, which then becomes the newer.
        var now = _water;
        var next = _waterWas;
        for (var y = 1; y < h - 1; y++)
        {
            for (var i = (y * w) + 1; i < ((y + 1) * w) - 1; i++)
            {
                var value = ((now[i - 1] + now[i + 1] + now[i - w] + now[i + w]) >> 1) - next[i];
                value -= value >> 5;
                next[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        }

        (_water, _waterWas) = (next, now);
        var height2 = _water;
        var pixels = _field.Pixels;
        var shine = palette.Highlight;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (y * w) + x;
                var dx = height2[i - 1] - height2[i + 1];
                var dy = height2[i - w] - height2[i + w];
                var sx = Math.Clamp(x + (dx >> 5), 0, w - 1);
                var sy = Math.Clamp(y + (dy >> 5), 0, h - 1);
                var floor = _pool[(sy * w) + sx];
                var light = Math.Clamp(256 + ((dx + dy) >> 2), 96, 420);
                var r = Math.Min(255, (((floor >> 16) & 255) * light) >> 8);
                var g = Math.Min(255, (((floor >> 8) & 255) * light) >> 8);
                var b = Math.Min(255, ((floor & 255) * light) >> 8);
                if (light > 330)
                {
                    var glint = (light - 330) * 2;
                    r = Math.Min(255, r + ((shine.Red * glint) >> 8));
                    g = Math.Min(255, g + ((shine.Green * glint) >> 8));
                    b = Math.Min(255, b + ((shine.Blue * glint) >> 8));
                }

                pixels[i] = b | (g << 8) | (r << 16) | unchecked((int)0xFF000000);
            }
        }

        _field.Upload();
        _field.Draw(canvas, new SKRect(1, 1, w - 1, h - 1), Whole(width, height), Linear);
    }

    private void Drop(int w, int h, int radius, short strength)
    {
        radius = Math.Min(radius, (Math.Min(w, h) / 2) - 2);
        if (radius < 1) return;
        var cx = radius + 1 + (int)(NextRandom() % (uint)Math.Max(1, w - (2 * radius) - 2));
        var cy = radius + 1 + (int)(NextRandom() % (uint)Math.Max(1, h - (2 * radius) - 2));
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if ((x * x) + (y * y) <= radius * radius) _water[((cy + y) * w) + cx + x] = (short)-strength;
            }
        }
    }

    // The pool's floor: tiles in the ramp's quiet colours, darker towards the far wall, with the
    // grout between them, which is what shows the water bending the light.
    private void BuildPool(int w, int h, VisualizerPalette palette)
    {
        _poolFor = palette;
        if (_pool.Length != w * h) _pool = new int[w * h];
        var tile = Math.Max(6, Math.Min(w, h) / 9);
        var grout = Math.Max(1, tile / 10);
        for (var y = 0; y < h; y++)
        {
            var deep = y / (float)h;
            var colour = Mix(Mix(palette.Background, palette.Sample(0.25f), 0.55f), palette.Sample(0.45f), deep * 0.6f);
            var line = Mix(colour, palette.Background, 0.55f);
            for (var x = 0; x < w; x++)
            {
                var edge = x % tile < grout || y % tile < grout;
                _pool[(y * w) + x] = Pack((edge ? line : colour).WithAlpha(255));
            }
        }
    }

    // A lava lamp: soft blobs, each riding up as its share of the bands grows and sinking as it
    // fades, swelling a little and drifting on its own; where two come near, their fields add and
    // they melt together. Each is added over the square it can still be felt in (four times its
    // size, where it adds a sixteenth), and each pixel takes the colour of the blob that gives it
    // most, lit towards its middle, its edge softened over a small band.
    private void Blobs(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        _field.Ensure(Math.Max(8, width / 2), Math.Max(8, height / 2));
        var w = _field.Width;
        var h = _field.Height;
        if (_blobField.Length != w * h)
        {
            _blobField = new float[w * h];
            _blobBest = new float[w * h];
            _blobOwner = new byte[w * h];
        }

        Array.Clear(_blobField);
        Array.Clear(_blobBest);
        Regroup(frame.Bands, _blobLevels);
        for (var b = 0; b < Blobs12; b++)
        {
            var level = _blobLevels[b];
            var bx = ((b + 0.5f) / Blobs12 * w) + (MathF.Sin((_time * 0.23f) + (b * 1.7f)) * w * 0.05f);
            var by = (h * 0.88f) - (level * level * h * 0.72f) + (MathF.Sin((_time * 0.4f) + (b * 2.3f)) * h * 0.05f);
            var r = h * (0.035f + (0.045f * level));
            var r2 = r * r;
            var reach = (int)(r * 4f);
            var x0 = Math.Max(0, (int)bx - reach);
            var x1 = Math.Min(w - 1, (int)bx + reach);
            var y0 = Math.Max(0, (int)by - reach);
            var y1 = Math.Min(h - 1, (int)by + reach);
            for (var y = y0; y <= y1; y++)
            {
                var dy = y - by;
                var row = y * w;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x - bx;
                    var add = r2 / ((dx * dx) + (dy * dy) + 1f);
                    var i = row + x;
                    _blobField[i] += add;
                    if (add > _blobBest[i])
                    {
                        _blobBest[i] = add;
                        _blobOwner[i] = (byte)b;
                    }
                }
            }
        }

        Span<int> colours = stackalloc int[Blobs12];
        for (var b = 0; b < Blobs12; b++) colours[b] = Pack(palette.Sample((0.2f + (0.8f * b / (Blobs12 - 1)) + _blobLevels[b]) / 2).WithAlpha(255));
        var ground = palette.Background;
        var shine = palette.Highlight;
        var pixels = _field.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var field = _blobField[i];
            if (field < 0.85f)
            {
                pixels[i] = Pack(ground) | unchecked((int)0xFF000000);
                continue;
            }

            var colour = colours[_blobOwner[i]];
            var edge = Math.Clamp((field - 0.85f) / 0.3f, 0f, 1f);
            var core = Math.Clamp((field - 1.6f) * 0.3f, 0f, 0.6f);
            var r = ((colour >> 16) & 255) + ((shine.Red - ((colour >> 16) & 255)) * core);
            var g = ((colour >> 8) & 255) + ((shine.Green - ((colour >> 8) & 255)) * core);
            var bl = (colour & 255) + ((shine.Blue - (colour & 255)) * core);
            r = ground.Red + ((r - ground.Red) * edge);
            g = ground.Green + ((g - ground.Green) * edge);
            bl = ground.Blue + ((bl - ground.Blue) * edge);
            pixels[i] = (int)bl | ((int)g << 8) | ((int)r << 16) | unchecked((int)0xFF000000);
        }

        _field.Upload();
        _field.Draw(canvas, Whole(width, height), Linear);
    }

    // Slow colour flowing across in two soft layers, each a band of light whose middle wanders and
    // whose width swells with the band of the spectrum under it, in colours that drift along the
    // ramp. A pixel is two look-ups in a table of the bell curve, and a blend.
    private void Ambience(SKCanvas canvas, int width, int height, AudioFrame frame, VisualizerPalette palette)
    {
        _field.Ensure(Math.Max(8, width / 2), Math.Max(8, height / 2));
        var w = _field.Width;
        var h = _field.Height;
        if (_gauss[0] == 0)
        {
            for (var i = 0; i < 256; i++) _gauss[i] = (byte)(255 * MathF.Exp(-i / 32f * 2.2f));
        }

        var t = _time;
        var ground = palette.Background;
        var pixels = _field.Pixels;
        for (var x = 0; x < w; x++)
        {
            var u = x / (float)w;
            var level = LevelAt(frame.Bands, u * (frame.Bands.Length - 1));
            var centre1 = 0.5f + (0.22f * MathF.Sin(((u * 1.3f) + (t * 0.07f)) * MathF.Tau)) + (0.1f * MathF.Sin(((u * 2.9f) - (t * 0.11f)) * MathF.Tau));
            var centre2 = 0.5f + (0.26f * MathF.Sin((((u * 0.8f) - (t * 0.05f)) * MathF.Tau) + 2f));
            var spread1 = h * (0.09f + (0.3f * level));
            var spread2 = h * (0.06f + (0.18f * level));
            var c1 = palette.Sample(Wave((u * 0.6f) + (t * 0.02f)));
            var c2 = palette.Sample(Wave((u * 0.6f) + (t * 0.02f) + 0.5f));
            var y1 = centre1 * h;
            var y2 = centre2 * h;
            for (var y = 0; y < h; y++)
            {
                var d1 = (y - y1) / spread1;
                var d2 = (y - y2) / spread2;
                var g1 = _gauss[Math.Min(255, (int)(d1 * d1 * 32f))];
                var g2 = _gauss[Math.Min(255, (int)(d2 * d2 * 32f))] * 0.7f;
                var r = Math.Min(255, ground.Red + ((c1.Red * g1) >> 8) + (int)(c2.Red * g2 / 256));
                var g = Math.Min(255, ground.Green + ((c1.Green * g1) >> 8) + (int)(c2.Green * g2 / 256));
                var b = Math.Min(255, ground.Blue + ((c1.Blue * g1) >> 8) + (int)(c2.Blue * g2 / 256));
                pixels[(y * w) + x] = b | (g << 8) | (r << 16) | unchecked((int)0xFF000000);
            }
        }

        _field.Upload();
        _field.Draw(canvas, Whole(width, height), Linear);
    }

    // 0 to 1 and back to 0 as t runs from 0 to 1, and round again.
    private static float Wave(float t)
    {
        t -= MathF.Floor(t);
        return 1f - MathF.Abs((2f * t) - 1f);
    }
}
