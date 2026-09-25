using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The picture and the sound a file is known for, as short badges: 4K, DOLBY VISION, HDR10, HLG,
/// ATMOS, DTS:X. Each is said only where the server's record says it: a library listing tells a
/// file's resolution and its soundtrack's kind but nothing of its colour, so a poster can say 4K
/// and ATMOS; the item's full record has its streams, and its page says the rest.
/// </summary>
public static class QualityBadges
{
    /// <summary>What a listing's record says: the resolution and the soundtrack's kind.</summary>
    public static IReadOnlyList<string> Listed(Media? media)
    {
        if (media is null) return [];
        var badges = new List<string>();
        if (Is4K(media)) badges.Add("4K");
        if (Sound(media.AudioProfile) is { } sound) badges.Add(sound);
        return badges;
    }

    /// <summary>What the full record says: the resolution, the picture's dynamic range, and the best soundtrack's kind.</summary>
    public static IReadOnlyList<string> Full(Media? media)
    {
        if (media is null) return [];
        var badges = new List<string>();
        if (Is4K(media)) badges.Add("4K");
        var streams = media.Part?.SelectMany(p => p.Stream ?? []).ToList() ?? [];
        if (streams.FirstOrDefault(s => s.StreamType == 1) is { } picture)
        {
            if (picture.DolbyVision) badges.Add("DOLBY VISION");
            if (picture.ColorTrc?.ToLowerInvariant() switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => null } is { } range) badges.Add(range);
        }

        var sounds = streams.Where(s => s.StreamType == 2).Select(s => Sound(s.Profile)).Append(Sound(media.AudioProfile)).OfType<string>().ToList();
        if (sounds.Contains("ATMOS")) badges.Add("ATMOS");
        else if (sounds.Contains("DTS:X")) badges.Add("DTS:X");
        return badges;
    }

    private static bool Is4K(Media media) =>
        string.Equals(media.VideoResolution, "4k", StringComparison.OrdinalIgnoreCase) || media.Width >= 3200;

    /// <summary>ATMOS for any Dolby Atmos soundtrack (TrueHD or Digital Plus), DTS:X for DTS:X; null for anything else.</summary>
    private static string? Sound(string? profile) => profile?.ToLowerInvariant() switch
    {
        { } p when p.Contains("atmos", StringComparison.Ordinal) => "ATMOS",
        { } p when p.Contains("dts:x", StringComparison.Ordinal) => "DTS:X",
        _ => null,
    };
}
