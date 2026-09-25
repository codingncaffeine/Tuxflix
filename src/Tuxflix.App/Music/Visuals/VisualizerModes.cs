namespace Tuxflix.App.Music;

/// <summary>How finely a mode is drawn before the view scales it to the window.</summary>
public enum VisualizerDetail
{
    /// <summary>Up to <see cref="VisualizerRenderer.MaxLongSide"/> on the long side: lines, bars and dots stay sharp.</summary>
    Full,

    /// <summary>Half of that: soft effects with lines in them.</summary>
    Half,

    /// <summary>A third of that: pictures made pixel by pixel, which are soft anyway and cost by the pixel.</summary>
    Field,
}

/// <summary>A mode as the menus show it.</summary>
/// <param name="Title">Its name in the menus and the settings.</param>
/// <param name="Family">The menu it is listed under.</param>
public sealed record VisualizerModeInfo(VisualizerMode Mode, string Title, string Family, VisualizerDetail Detail);

/// <summary>
/// Every mode, in the order the menus list them and a click on the picture moves through them,
/// grouped by family: bars, the spectrum over time, scopes and meters, particles, and effects.
/// </summary>
public static class VisualizerModes
{
    public const string Bars = "Bars";
    public const string OverTime = "Over time";
    public const string Scopes = "Scopes and meters";
    public const string Sparks = "Particles";
    public const string Effects = "Effects";

    public static IReadOnlyList<VisualizerModeInfo> All { get; } =
    [
        new(VisualizerMode.Spectrum, "Spectrum", Bars, VisualizerDetail.Full),
        new(VisualizerMode.Mirror, "Mirror", Bars, VisualizerDetail.Full),
        new(VisualizerMode.Radial, "Radial", Bars, VisualizerDetail.Full),
        new(VisualizerMode.PixelBars, "Pixel bars", Bars, VisualizerDetail.Full),
        new(VisualizerMode.FlatBars, "Flat bars", Bars, VisualizerDetail.Full),
        new(VisualizerMode.GradientBars, "Gradient bars", Bars, VisualizerDetail.Full),
        new(VisualizerMode.GlowPills, "Glow pills", Bars, VisualizerDetail.Full),
        new(VisualizerMode.Led, "LED", Bars, VisualizerDetail.Full),
        new(VisualizerMode.DotMatrix, "Dot matrix", Bars, VisualizerDetail.Full),
        new(VisualizerMode.Curve, "Curve", Bars, VisualizerDetail.Full),

        new(VisualizerMode.Spectrogram, "Spectrogram", OverTime, VisualizerDetail.Field),
        new(VisualizerMode.Terrain, "Terrain", OverTime, VisualizerDetail.Half),
        new(VisualizerMode.DotPlane, "Dot plane", OverTime, VisualizerDetail.Full),

        new(VisualizerMode.Scope, "Scope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.PixelScope, "Pixel scope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.FilledScope, "Filled scope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.Envelope, "Envelope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.DotScope, "Dot scope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.Vectorscope, "Vectorscope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.Superscope, "Superscope", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.ScopeStar, "Scope star", Scopes, VisualizerDetail.Full),
        new(VisualizerMode.VuMeters, "VU meters", Scopes, VisualizerDetail.Full),

        new(VisualizerMode.Particles, "Particles", Sparks, VisualizerDetail.Full),
        new(VisualizerMode.Fountain, "Fountain", Sparks, VisualizerDetail.Full),
        new(VisualizerMode.Fireworks, "Fireworks", Sparks, VisualizerDetail.Full),
        new(VisualizerMode.Starfield, "Starfield", Sparks, VisualizerDetail.Full),

        new(VisualizerMode.Tunnel, "Tunnel", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Plasma, "Plasma", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Fire, "Fire", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Water, "Water", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Blobs, "Blobs", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Ambience, "Ambience", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Feedback, "Feedback", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Swirl, "Swirl", Effects, VisualizerDetail.Field),
        new(VisualizerMode.Kaleidoscope, "Kaleidoscope", Effects, VisualizerDetail.Full),
        new(VisualizerMode.BassSpin, "Bass spin", Effects, VisualizerDetail.Full),
        new(VisualizerMode.Pulse, "Pulse", Effects, VisualizerDetail.Full),
        new(VisualizerMode.Cover, "Cover", Effects, VisualizerDetail.Full),
    ];

    /// <summary>The families, in menu order.</summary>
    public static IReadOnlyList<string> Families { get; } = [.. All.Select(m => m.Family).Distinct()];

    private static readonly Dictionary<VisualizerMode, int> Index = All.Select((m, i) => (m.Mode, i)).ToDictionary(p => p.Mode, p => p.i);

    public static VisualizerModeInfo Info(VisualizerMode mode) => All[Index[mode]];

    public static string Title(VisualizerMode mode) => Info(mode).Title;

    /// <summary>The mode after <paramref name="mode"/>, round to the first after the last.</summary>
    public static VisualizerMode Next(VisualizerMode mode) => All[(Index[mode] + 1) % All.Count].Mode;

    /// <summary>A mode by its title or its saved name; null for neither.</summary>
    public static VisualizerMode? Find(string? name) =>
        All.FirstOrDefault(m => m.Title == name)?.Mode
        ?? (Enum.TryParse<VisualizerMode>(name, out var mode) && Index.ContainsKey(mode) ? mode : null);
}
