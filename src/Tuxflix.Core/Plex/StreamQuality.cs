using System.Globalization;

namespace Tuxflix.Core.Plex;

/// <summary>
/// How a film or an episode is streamed: the original file, or a conversion the server makes to
/// a bitrate and a size.
/// </summary>
/// <remarks>The table is Plex Web's own custom-quality list, so a choice means the same in every Plex player.</remarks>
public sealed record StreamQuality(int? Kbps, string? Resolution, int? VideoQuality)
{
    public static StreamQuality Original { get; } = new(null, null, null);

    /// <summary>Original first, then the conversions from the highest bitrate down.</summary>
    public static IReadOnlyList<StreamQuality> All { get; } =
    [
        Original,
        new(20000, "1920x1080", 100),
        new(12000, "1920x1080", 90),
        new(10000, "1920x1080", 75),
        new(8000, "1920x1080", 60),
        new(4000, "1280x720", 100),
        new(3000, "1280x720", 75),
        new(2000, "1280x720", 60),
        new(1500, "720x480", 60),
        new(720, "576x320", 40),
        new(320, "420x240", 30),
    ];

    public bool IsOriginal => Kbps is null;

    /// <summary>The picture's height the conversion caps at (1080 for 1920x1080), or null for the original.</summary>
    public int? Height => Resolution?.Split('x') is [_, var height] && int.TryParse(height, CultureInfo.InvariantCulture, out var h) ? h : null;

    /// <summary>"Original", "8 Mbps · 1080p", "720 kbps · 320p".</summary>
    public string Label => Kbps switch
    {
        null => "Original",
        >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{Kbps.Value / 1000.0:0.#} Mbps · {Height}p"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Kbps} kbps · {Height}p"),
    };

    /// <summary>The quality kept in the settings by its bitrate (null for the original); an unknown one reads as the original.</summary>
    public static StreamQuality FromKbps(int? kbps) => All.FirstOrDefault(q => q.Kbps == kbps) ?? Original;

    /// <summary>
    /// Whether a source of this bitrate and height already fits under the cap, so converting it
    /// could only lose picture. An unknown bitrate or height does not fit.
    /// </summary>
    public bool Covers(int? sourceKbps, int? sourceHeight) =>
        Kbps is { } cap && Height is { } capHeight && sourceKbps is > 0 && sourceHeight is > 0 && sourceKbps <= cap && sourceHeight <= capHeight;
}
