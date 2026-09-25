using SkiaSharp;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Music;

/// <summary>The full-window visualizer's ways of drawing the same analysis.</summary>
/// <remarks>
/// A mode is saved by its name, so names stay as they are and new modes go at the end;
/// <see cref="VisualizerModes"/> gives the order they are shown and cycled in.
/// </remarks>
public enum VisualizerMode
{
    /// <summary>Bars rising from a floor, with falling caps and a reflection: the analyser.</summary>
    Spectrum,

    /// <summary>Bars from a centre line, up and down, the bass in the middle.</summary>
    Mirror,

    /// <summary>Bars around a ring that swells on the beat.</summary>
    Radial,

    /// <summary>The waveform itself, as an oscilloscope draws it.</summary>
    Scope,

    /// <summary>Sparks thrown from the centre, a burst on every beat.</summary>
    Particles,

    /// <summary>The classic player's analyser at the window's size: square pixels, the ramp anchored to the rows.</summary>
    PixelBars,

    /// <summary>One flat colour for every bar, under caps that stand apart from them.</summary>
    FlatBars,

    /// <summary>Each bar in its band's colour, paling to its tip.</summary>
    GradientBars,

    /// <summary>Rounded bars in a halo of their own light.</summary>
    GlowPills,

    /// <summary>Lit segments, as a hi-fi's display shows them.</summary>
    Led,

    /// <summary>A grid of dots lit by level.</summary>
    DotMatrix,

    /// <summary>The spectrum as one smooth, filled line.</summary>
    Curve,

    /// <summary>Time across, pitch up, loudness as light.</summary>
    Spectrogram,

    /// <summary>The last few seconds of spectra as ridgelines, the newest in front.</summary>
    Terrain,

    /// <summary>A turning plane of dots the spectrum's recent past raises.</summary>
    DotPlane,

    /// <summary>The classic player's oscilloscope at the window's size.</summary>
    PixelScope,

    /// <summary>The waveform filled to its centre line.</summary>
    FilledScope,

    /// <summary>The waveform's outline mirrored about the centre, as an editor draws it.</summary>
    Envelope,

    /// <summary>The waveform as dots.</summary>
    DotScope,

    /// <summary>Left against right: stereo width across, the mix up and down.</summary>
    Vectorscope,

    /// <summary>A closed curve whose radius follows the waveform.</summary>
    Superscope,

    /// <summary>The waveform along the arms of a turning star.</summary>
    ScopeStar,

    /// <summary>A pair of needle meters, left and right.</summary>
    VuMeters,

    /// <summary>Sparks thrown up from the floor by each band, falling back.</summary>
    Fountain,

    /// <summary>Rockets on the beat, bursting into sparks.</summary>
    Fireworks,

    /// <summary>Stars rushing past, faster on the beat.</summary>
    Starfield,

    /// <summary>Down a tunnel that turns and hurries with the music.</summary>
    Tunnel,

    /// <summary>Waves of colour, as the demos drew them.</summary>
    Plasma,

    /// <summary>Flames rising from the spectrum.</summary>
    Fire,

    /// <summary>A pool the beat drops ripples into.</summary>
    Water,

    /// <summary>Soft blobs that swell with their bands and melt together.</summary>
    Blobs,

    /// <summary>Slow colour flowing with the spectrum.</summary>
    Ambience,

    /// <summary>Each frame drawn again, zoomed and turned, the waveform over it.</summary>
    Feedback,

    /// <summary>Each frame drawn again into a whirlpool.</summary>
    Swirl,

    /// <summary>The bands folded into a turning symmetry.</summary>
    Kaleidoscope,

    /// <summary>Two blades the bass spins.</summary>
    BassSpin,

    /// <summary>Rings sent out from the middle on the beat.</summary>
    Pulse,

    /// <summary>The cover playing, breathing with the music in its own light.</summary>
    Cover,
}

/// <summary>
/// A named set of colours every mode can wear: a background, a ramp from quiet to loud, and a
/// highlight for caps, rings and beats.
/// </summary>
/// <param name="Stops">Quiet to loud; a mode samples along it by level or by position.</param>
public sealed record VisualizerPalette(string Name, SKColor Background, SKColor[] Stops, SKColor Highlight)
{
    /// <summary>The colour <paramref name="t"/> (0..1) along the ramp.</summary>
    public SKColor Sample(float t)
    {
        t = Math.Clamp(t, 0f, 1f) * (Stops.Length - 1);
        var i = Math.Min((int)t, Stops.Length - 2);
        var f = t - i;
        var (a, b) = (Stops[i], Stops[i + 1]);
        return new SKColor(
            (byte)(a.Red + ((b.Red - a.Red) * f)),
            (byte)(a.Green + ((b.Green - a.Green) * f)),
            (byte)(a.Blue + ((b.Blue - a.Blue) * f)));
    }
}

/// <summary>The palettes, by name; Album is made from the cover playing.</summary>
public static class VisualizerPalettes
{
    public const string AlbumName = "Album";

    public static IReadOnlyList<VisualizerPalette> Fixed { get; } =
    [
        new("Aurora", new SKColor(0x08, 0x0C, 0x1E), [new(0x22, 0xD3, 0xEE), new(0x81, 0x8C, 0xF8), new(0xE8, 0x79, 0xF9)], new SKColor(0xF5, 0xD0, 0xFE)),
        new("Ember", new SKColor(0x12, 0x07, 0x05), [new(0x9F, 0x12, 0x39), new(0xF9, 0x73, 0x16), new(0xFD, 0xE0, 0x47)], new SKColor(0xFF, 0xF7, 0xC2)),
        new("Lagoon", new SKColor(0x03, 0x12, 0x18), [new(0x0E, 0x74, 0x90), new(0x2D, 0xD4, 0xBF), new(0xBB, 0xF7, 0xD0)], new SKColor(0xEC, 0xFE, 0xFF)),
        new("Neon", new SKColor(0x09, 0x08, 0x12), [new(0xFF, 0x2E, 0x97), new(0x7C, 0x3A, 0xED), new(0x00, 0xE5, 0xFF)], new SKColor(0xFF, 0xFF, 0xFF)),
        new("Marquee", new SKColor(0x07, 0x0B, 0x18), [new(0xE8, 0x6F, 0x32), new(0xFF, 0x91, 0x50), new(0xFF, 0xD2, 0xA8)], new SKColor(0xFF, 0xF1, 0xE6)),
        new("Classic", new SKColor(0x00, 0x00, 0x00), [new(0x00, 0xB4, 0x00), new(0xDC, 0xDC, 0x00), new(0xFF, 0x30, 0x10)], new SKColor(0xE0, 0xE0, 0xE0)),
        new("Mono", new SKColor(0x0A, 0x0A, 0x0D), [new(0x52, 0x52, 0x5B), new(0xA1, 0xA1, 0xAA), new(0xFA, 0xFA, 0xFA)], new SKColor(0xFF, 0xFF, 0xFF)),
    ];

    /// <summary>Every palette name, Album first.</summary>
    public static IReadOnlyList<string> Names { get; } = [AlbumName, .. Fixed.Select(p => p.Name)];

    /// <summary>A palette by name; Album from <paramref name="cover"/> (Aurora when the cover has no colours).</summary>
    public static VisualizerPalette Resolve(string? name, UltraBlurColors? cover) =>
        name == AlbumName ? FromCover(cover) ?? Fixed[0]
        : Fixed.FirstOrDefault(p => p.Name == name) ?? Fixed[0];

    /// <summary>
    /// The cover's own light: its four corner colours, lifted to full brightness so the bars read
    /// against a background made from the darkest of them.
    /// </summary>
    public static VisualizerPalette? FromCover(UltraBlurColors? cover)
    {
        if (cover is null) return null;
        var colours = new[] { cover.BottomLeft, cover.TopLeft, cover.TopRight, cover.BottomRight }
            .Select(c => SKColor.TryParse("#" + c, out var colour) ? colour : (SKColor?)null)
            .OfType<SKColor>()
            .ToList();
        if (colours.Count < 2) return null;
        var lifted = colours.Select(c => Lift(c, 0.62f + (0.1f * colours.IndexOf(c)))).ToArray();
        var darkest = colours.OrderBy(Luma).First();
        darkest.ToHsv(out var h, out var s, out _);
        return new VisualizerPalette(AlbumName, SKColor.FromHsv(h, Math.Min(s, 70), 7), lifted, Lift(colours.OrderByDescending(Luma).First(), 0.96f));
    }

    private static SKColor Lift(SKColor colour, float value)
    {
        colour.ToHsv(out var h, out var s, out _);
        return SKColor.FromHsv(h, Math.Clamp(s * 1.15f, 35, 90), value * 100);
    }

    private static float Luma(SKColor c) => (0.2126f * c.Red) + (0.7152f * c.Green) + (0.0722f * c.Blue);
}
