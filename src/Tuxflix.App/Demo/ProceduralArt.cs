using Avalonia.Platform;
using SkiaSharp;
using Tuxflix.Core.Demo;

namespace Tuxflix.App.Demo;

/// <summary>
/// Paints the demo library's posters, backdrops, episode stills and cast portraits in code.
/// </summary>
/// <remarks>
/// Every picture is drawn from its title's seed, so the same title always gets the same art.
/// Six scene families (horizon, orbit, monolith, waves, skyline, forest) give the shelves the
/// variety a real library has; posters add their title in one of three typographic treatments.
/// Nothing here comes from anybody's artwork.
/// </remarks>
public sealed class ProceduralArt : IDemoArtRenderer
{
    private readonly SKTypeface _bold;
    private readonly SKTypeface _light;
    private readonly SKTypeface _thin;

    public ProceduralArt()
    {
        _bold = LoadInter("Inter-Bold.ttf");
        _light = LoadInter("Inter-Light.ttf");
        _thin = LoadInter("Inter-Thin.ttf");
    }

    public DemoImage Render(DemoArtRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var palette = Palette.From(request.Seed);
        if (request.Kind == DemoArtKind.Logo)
        {
            return new DemoImage(Logo(request, palette), "image/png");
        }

        var info = new SKImageInfo(request.Width, request.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        if (request.Kind == DemoArtKind.Person)
        {
            Portrait(canvas, info, palette);
        }
        else
        {
            Scene(canvas, info, palette, request.Seed, request.Kind);
            if (request.Kind == DemoArtKind.Poster)
            {
                Title(canvas, info, palette, request.Title, request.Subtitle, request.Seed);
            }
        }

        Finish(canvas, info, request.Seed);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return new DemoImage(data.ToArray(), "image/jpeg");
    }

    /// <summary>The colours of one title, from the palette the catalogue also reports UltraBlur from.</summary>
    private readonly record struct Palette(float Hue, float Hue2, SKColor Deep, SKColor Mid, SKColor Glow, SKColor Light)
    {
        public static Palette From(int seed)
        {
            var grade = DemoPalette.From(seed);
            return new Palette(
                grade.GroundHue,
                grade.LightHue,
                SKColor.FromHsl(grade.GroundHue, 48, 9),
                SKColor.FromHsl(grade.GroundHue, 44, 24),
                SKColor.FromHsl(grade.LightHue, grade.Saturation, 60),
                SKColor.FromHsl(grade.LightHue, 70, 86));
        }
    }

    /// <summary>
    /// A clear logo: the title set tight on a transparent canvas, lit with the title's own light
    /// colour and edged in shadow so it holds up over any backdrop, as a studio logo does.
    /// </summary>
    private byte[] Logo(DemoArtRequest request, Palette p)
    {
        var upper = request.Title.ToUpperInvariant();
        var tracking = 0.04f;
        var lines = SplitForWidth(upper, _bold, request.Width * 0.96f, request.Height * 0.62f, tracking, out var size);
        using var font = new SKFont(_bold, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var lineHeight = size * 1.02f;
        var widest = lines.Max(line => TrackedWidth(line, font, tracking));
        var pad = size * 0.14f;
        var width = (int)Math.Ceiling(widest + pad * 2);
        var height = (int)Math.Ceiling(lineHeight * lines.Count + pad * 2);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(170), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size / 9) };
        using var ink = new SKPaint { IsAntialias = true };
        ink.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, pad), new SKPoint(0, height - pad),
            [SKColors.White, p.Light, Mix(p.Glow, SKColors.White, 0.35f)],
            [0f, 0.55f, 1f],
            SKShaderTileMode.Clamp);

        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = pad + lineHeight * (i + 0.82f);
            var centre = width / 2f;
            Tracked(canvas, lines[i], centre + size * 0.03f, baseline + size * 0.05f, font, shadow, tracking);
            Tracked(canvas, lines[i], centre, baseline, font, ink, tracking);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Scene(SKCanvas canvas, SKImageInfo info, Palette p, int seed, DemoArtKind kind)
    {
        var w = info.Width;
        var h = info.Height;
        var random = new Random(seed);

        using var sky = new SKPaint { IsAntialias = true };
        sky.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, h),
            [p.Deep, p.Mid, p.Glow.WithAlpha(210)],
            [0f, 0.55f, 1f],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, w, h, sky);

        // Stills of one show vary within its palette; everything else follows the title's family.
        var family = seed % 6;
        switch (family)
        {
            case 0: Horizon(canvas, w, h, p, random); break;
            case 1: Orbit(canvas, w, h, p, random); break;
            case 2: Monolith(canvas, w, h, p, random); break;
            case 3: Waves(canvas, w, h, p, random); break;
            case 4: Skyline(canvas, w, h, p, random); break;
            default: Forest(canvas, w, h, p, random); break;
        }

        // Backdrops and stills leave the lower third quiet for text laid over them.
        if (kind is DemoArtKind.Backdrop or DemoArtKind.Still)
        {
            using var shade = new SKPaint();
            shade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, h * 0.45f), new SKPoint(0, h),
                [SKColors.Transparent, p.Deep.WithAlpha(150)],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, shade);
        }
    }

    private static void Horizon(SKCanvas c, int w, int h, Palette p, Random r)
    {
        var sunY = h * (0.38f + (float)r.NextDouble() * 0.12f);
        var sunR = Math.Min(w, h) * (0.16f + (float)r.NextDouble() * 0.08f);
        Glow(c, w * (0.3f + (float)r.NextDouble() * 0.4f), sunY, sunR, p.Light, p.Glow);
        for (var layer = 0; layer < 3; layer++)
        {
            var baseY = h * (0.55f + layer * 0.12f);
            var colour = Mix(p.Mid, p.Deep, 0.35f + layer * 0.3f);
            Ridge(c, w, h, baseY, h * (0.12f - layer * 0.025f), colour, r, 5 + layer * 2);
        }
    }

    private static void Orbit(SKCanvas c, int w, int h, Palette p, Random r)
    {
        using var space = new SKPaint();
        space.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), [SKColor.FromHsl(p.Hue, 50, 5), p.Deep], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, w, h, space);
        Stars(c, w, h, r, 140);

        var cx = w * (0.35f + (float)r.NextDouble() * 0.3f);
        var cy = h * (0.35f + (float)r.NextDouble() * 0.2f);
        var radius = Math.Min(w, h) * (0.22f + (float)r.NextDouble() * 0.12f);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, w / 260f), Color = p.Light.WithAlpha(90) };
        for (var i = 1; i <= 3; i++)
        {
            c.DrawOval(new SKRect(cx - radius * (1.3f + i * 0.35f), cy - radius * (0.35f + i * 0.1f), cx + radius * (1.3f + i * 0.35f), cy + radius * (0.35f + i * 0.1f)), ring);
        }

        using var planet = new SKPaint { IsAntialias = true };
        planet.Shader = SKShader.CreateRadialGradient(new SKPoint(cx - radius * 0.4f, cy - radius * 0.4f), radius * 1.6f, [p.Light, p.Glow, p.Deep], [0f, 0.35f, 1f], SKShaderTileMode.Clamp);
        c.DrawCircle(cx, cy, radius, planet);
    }

    private static void Monolith(SKCanvas c, int w, int h, Palette p, Random r)
    {
        var ground = h * 0.72f;
        using var floor = new SKPaint { IsAntialias = true };
        floor.Shader = SKShader.CreateLinearGradient(new SKPoint(0, ground), new SKPoint(0, h), [p.Mid, p.Deep], SKShaderTileMode.Clamp);
        c.DrawRect(0, ground, w, h - ground, floor);

        var mw = w * (0.18f + (float)r.NextDouble() * 0.1f);
        var mh = h * (0.42f + (float)r.NextDouble() * 0.12f);
        var mx = w * 0.5f - mw / 2 + (float)(r.NextDouble() - 0.5) * w * 0.2f;

        using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(120) };
        using var path = new SKPath();
        path.MoveTo(mx, ground);
        path.LineTo(mx + mw, ground);
        path.LineTo(w * 1.1f, h);
        path.LineTo(mx + w * 0.5f, h);
        path.Close();
        c.DrawPath(path, shadow);

        using var slab = new SKPaint { IsAntialias = true };
        slab.Shader = SKShader.CreateLinearGradient(new SKPoint(mx, 0), new SKPoint(mx + mw, 0), [p.Deep, Mix(p.Mid, p.Glow, 0.25f)], SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(mx, ground - mh, mx + mw, ground), mw * 0.5f, mw * 0.5f, slab);

        // A tiny figure gives the slab its scale.
        using var figure = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(220) };
        var fx = mx - w * 0.06f;
        c.DrawRect(fx, ground - h * 0.035f, w * 0.008f, h * 0.035f, figure);
        c.DrawCircle(fx + w * 0.004f, ground - h * 0.04f, w * 0.006f, figure);
    }

    private static void Waves(SKCanvas c, int w, int h, Palette p, Random r)
    {
        Glow(c, w * 0.7f, h * 0.25f, Math.Min(w, h) * 0.12f, p.Light, p.Glow);
        for (var band = 0; band < 5; band++)
        {
            var y0 = h * (0.5f + band * 0.1f);
            var amplitude = h * (0.03f + (float)r.NextDouble() * 0.03f);
            var phase = (float)r.NextDouble() * 6f;
            using var path = new SKPath();
            path.MoveTo(0, h);
            for (var x = 0; x <= w; x += Math.Max(2, w / 80))
            {
                path.LineTo(x, y0 + MathF.Sin(x / (float)w * 9f + phase) * amplitude);
            }

            path.LineTo(w, h);
            path.Close();
            using var paint = new SKPaint { IsAntialias = true };
            var top = Mix(p.Glow, p.Mid, 0.3f + band * 0.14f);
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, y0 - amplitude), new SKPoint(0, h), [top.WithAlpha(230), p.Deep], SKShaderTileMode.Clamp);
            c.DrawPath(path, paint);
        }
    }

    private static void Skyline(SKCanvas c, int w, int h, Palette p, Random r)
    {
        Stars(c, w, (int)(h * 0.5f), r, 60);
        var horizon = h * 0.7f;
        using var neon = new SKPaint { IsAntialias = true, Color = p.Glow, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, h / 90f) };
        c.DrawRect(0, horizon - h * 0.01f, w, h * 0.02f, neon);

        using var building = new SKPaint { IsAntialias = true };
        using var window = new SKPaint { IsAntialias = true, Color = p.Light.WithAlpha(200) };
        var x = -w * 0.02f;
        while (x < w)
        {
            var bw = w * (0.06f + (float)r.NextDouble() * 0.09f);
            var bh = h * (0.12f + (float)r.NextDouble() * 0.38f);
            building.Color = Mix(p.Deep, SKColors.Black, (float)r.NextDouble() * 0.5f);
            c.DrawRect(x, horizon - bh, bw, bh + h, building);
            var cell = Math.Max(2f, w / 90f);
            for (var wy = horizon - bh + cell; wy < horizon - cell; wy += cell * 2.2f)
            {
                for (var wx = x + cell; wx < x + bw - cell; wx += cell * 2f)
                {
                    if (r.NextDouble() < 0.28) c.DrawRect(wx, wy, cell * 0.8f, cell, window);
                }
            }

            x += bw + w * 0.01f;
        }

        using var street = new SKPaint { Color = SKColors.Black.WithAlpha(200) };
        c.DrawRect(0, horizon, w, h - horizon, street);
    }

    private static void Forest(SKCanvas c, int w, int h, Palette p, Random r)
    {
        Glow(c, w * 0.5f, h * 0.3f, Math.Min(w, h) * 0.3f, p.Light.WithAlpha(160), p.Glow.WithAlpha(80));

        // Light shafts through the canopy.
        using var shaft = new SKPaint { IsAntialias = true, Color = p.Light.WithAlpha(26) };
        for (var s = 0; s < 4; s++)
        {
            var sx = w * (float)r.NextDouble();
            using var ray = new SKPath();
            ray.MoveTo(sx, 0);
            ray.LineTo(sx + w * 0.08f, 0);
            ray.LineTo(sx + w * 0.3f, h);
            ray.LineTo(sx + w * 0.12f, h);
            ray.Close();
            c.DrawPath(ray, shaft);
        }

        for (var layer = 0; layer < 3; layer++)
        {
            using var tree = new SKPaint { IsAntialias = true, Color = Mix(p.Mid, SKColors.Black, 0.3f + layer * 0.3f) };
            var baseY = h * (0.62f + layer * 0.14f);
            var count = 7 + layer * 3;
            for (var t = 0; t < count; t++)
            {
                var tx = w * (t + (float)r.NextDouble()) / count;
                var th = h * (0.18f + (float)r.NextDouble() * 0.14f + layer * 0.05f);
                var tw = th * 0.32f;
                using var pine = new SKPath();
                pine.MoveTo(tx, baseY - th);
                pine.LineTo(tx + tw / 2, baseY);
                pine.LineTo(tx - tw / 2, baseY);
                pine.Close();
                c.DrawPath(pine, tree);
            }

            c.DrawRect(0, baseY - 1, w, h, tree);
        }
    }

    private static void Portrait(SKCanvas c, SKImageInfo info, Palette p)
    {
        var w = info.Width;
        var h = info.Height;
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [p.Mid, p.Deep], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, w, h, bg);

        using var figure = new SKPaint { IsAntialias = true };
        figure.Shader = SKShader.CreateLinearGradient(new SKPoint(0, h * 0.2f), new SKPoint(0, h), [Mix(p.Light, p.Mid, 0.45f), Mix(p.Mid, p.Deep, 0.2f)], SKShaderTileMode.Clamp);
        c.DrawCircle(w * 0.5f, h * 0.4f, Math.Min(w, h) * 0.19f, figure);
        using var shoulders = new SKPath();
        shoulders.AddRoundRect(new SKRect(w * 0.16f, h * 0.64f, w * 0.84f, h * 1.2f), w * 0.3f, w * 0.3f);
        c.DrawPath(shoulders, figure);
    }

    private void Title(SKCanvas c, SKImageInfo info, Palette p, string title, string? subtitle, int seed)
    {
        var w = info.Width;
        var h = info.Height;
        var treatment = (seed / 5) % 3;
        var upper = title.ToUpperInvariant();
        var typeface = treatment switch { 0 => _bold, 1 => _thin, _ => _light };
        var tracking = treatment switch { 0 => 0.06f, 1 => 0.22f, _ => 0.14f };

        // Two lines when one would have to shrink too far.
        var lines = SplitForWidth(upper, typeface, w * 0.84f, h * (treatment == 1 ? 0.075f : 0.085f), tracking, out var size);
        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var lineHeight = size * 1.12f;
        var blockHeight = lineHeight * lines.Count;
        var top = treatment switch
        {
            1 => h * 0.1f,
            2 => h * 0.5f - blockHeight / 2,
            _ => h * 0.8f - blockHeight,
        };

        using var ink = new SKPaint { IsAntialias = true, Color = treatment == 2 ? p.Light : SKColors.White };
        using var shade = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size / 6) };
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = top + lineHeight * (i + 0.85f);
            Tracked(c, lines[i], w / 2f + size * 0.04f, baseline + size * 0.06f, font, shade, tracking);
            Tracked(c, lines[i], w / 2f, baseline, font, ink, tracking);
        }

        if (treatment == 2)
        {
            using var rule = new SKPaint { IsAntialias = true, Color = p.Glow, StrokeWidth = Math.Max(1, h / 300f) };
            c.DrawLine(w * 0.38f, top - size * 0.35f, w * 0.62f, top - size * 0.35f, rule);
        }

        if (!string.IsNullOrEmpty(subtitle))
        {
            using var small = new SKFont(_light, h * 0.03f) { Edging = SKFontEdging.Antialias };
            using var soft = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(190) };
            var y = treatment == 1 ? top + blockHeight + h * 0.05f : Math.Min(h * 0.93f, top + blockHeight + h * 0.055f);
            Tracked(c, subtitle.ToUpperInvariant(), w / 2f, y, small, soft, 0.3f);
        }
    }

    private static List<string> SplitForWidth(string text, SKTypeface typeface, float maxWidth, float preferred, float tracking, out float size)
    {
        using var probe = new SKFont(typeface, preferred);
        var single = TrackedWidth(text, probe, tracking);
        if (single <= maxWidth || !text.Contains(' ', StringComparison.Ordinal))
        {
            size = Math.Min(preferred, preferred * maxWidth / Math.Max(1, single));
            return [text];
        }

        var words = text.Split(' ');
        var best = 1;
        var bestWidth = float.MaxValue;
        for (var split = 1; split < words.Length; split++)
        {
            var width = Math.Max(TrackedWidth(string.Join(' ', words[..split]), probe, tracking), TrackedWidth(string.Join(' ', words[split..]), probe, tracking));
            if (width < bestWidth)
            {
                bestWidth = width;
                best = split;
            }
        }

        size = Math.Min(preferred, preferred * maxWidth / Math.Max(1, bestWidth));
        return [string.Join(' ', words[..best]), string.Join(' ', words[best..])];
    }

    private static float TrackedWidth(string text, SKFont font, float tracking)
    {
        var width = 0f;
        foreach (var ch in text) width += font.MeasureText(ch.ToString()) + font.Size * tracking;
        return width - font.Size * tracking;
    }

    private static void Tracked(SKCanvas c, string text, float centreX, float baseline, SKFont font, SKPaint paint, float tracking)
    {
        var x = centreX - TrackedWidth(text, font, tracking) / 2;
        foreach (var ch in text)
        {
            var glyph = ch.ToString();
            c.DrawText(glyph, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(glyph) + font.Size * tracking;
        }
    }

    private static void Finish(SKCanvas c, SKImageInfo info, int seed)
    {
        var w = info.Width;
        var h = info.Height;

        // Film grain, then a vignette that pulls the eye to the middle.
        using var grain = new SKPaint { BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(24) };
        grain.Shader = SKShader.CreatePerlinNoiseFractalNoise(0.9f, 0.9f, 2, seed % 97);
        c.DrawRect(0, 0, w, h, grain);

        using var vignette = new SKPaint();
        vignette.Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2f, h / 2f), Math.Max(w, h) * 0.75f, [SKColors.Transparent, SKColors.Black.WithAlpha(140)], [0.55f, 1f], SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, w, h, vignette);
    }

    private static void Glow(SKCanvas c, float x, float y, float radius, SKColor core, SKColor halo)
    {
        using var bloom = new SKPaint { IsAntialias = true };
        bloom.Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), radius * 3.2f, [halo.WithAlpha(150), halo.WithAlpha(0)], SKShaderTileMode.Clamp);
        c.DrawCircle(x, y, radius * 3.2f, bloom);
        using var disc = new SKPaint { IsAntialias = true };
        disc.Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), radius, [core, halo], SKShaderTileMode.Clamp);
        c.DrawCircle(x, y, radius, disc);
    }

    private static void Ridge(SKCanvas c, int w, int h, float baseY, float amplitude, SKColor colour, Random r, int peaks)
    {
        using var path = new SKPath();
        path.MoveTo(0, h);
        path.LineTo(0, baseY);
        for (var i = 1; i <= peaks; i++)
        {
            var x = w * i / (float)peaks;
            var peakX = x - w / (float)peaks / 2;
            var peakY = baseY - amplitude * (0.4f + (float)r.NextDouble());
            path.LineTo(peakX, peakY);
            path.LineTo(x, baseY - amplitude * 0.2f * (float)r.NextDouble());
        }

        path.LineTo(w, h);
        path.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = colour };
        c.DrawPath(path, paint);
    }

    private static void Stars(SKCanvas c, int w, int h, Random r, int count)
    {
        using var star = new SKPaint { IsAntialias = true };
        for (var i = 0; i < count; i++)
        {
            star.Color = SKColors.White.WithAlpha((byte)r.Next(40, 220));
            c.DrawCircle((float)r.NextDouble() * w, (float)r.NextDouble() * h, (float)(0.4 + r.NextDouble() * 1.2) * Math.Max(1, w / 400f), star);
        }
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    private static SKTypeface LoadInter(string file)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{file}"));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return SKTypeface.FromData(SKData.CreateCopy(copy.ToArray())) ?? SKTypeface.Default;
    }
}
