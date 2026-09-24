using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>How numbers and dates read in the interface.</summary>
internal static class Format
{
    /// <summary>"2h 8m", "47m".</summary>
    public static string Runtime(long? milliseconds)
    {
        if (milliseconds is not > 0) return string.Empty;
        var span = TimeSpan.FromMilliseconds(milliseconds.Value);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m");
    }

    /// <summary>"1h 12m left".</summary>
    public static string Remaining(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Duration is not > 0 || item.ViewOffset is not > 0) return string.Empty;
        return $"{Runtime(item.Duration - item.ViewOffset)} left";
    }

    /// <summary>"S2 · E5".</summary>
    public static string EpisodeCode(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ParentIndex is { } season && item.Index is { } episode
            ? string.Create(CultureInfo.InvariantCulture, $"S{season} · E{episode}")
            : string.Empty;
    }

    /// <summary>"Yesterday", "3 days ago", "12 Mar 2026".</summary>
    public static string WhenWatched(long? unixSeconds)
    {
        if (unixSeconds is not > 0) return "Never";
        var when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).ToLocalTime();
        var days = (DateTimeOffset.Now.Date - when.Date).TotalDays;
        return days switch
        {
            < 1 => "Today",
            < 2 => "Yesterday",
            < 7 => string.Create(CultureInfo.InvariantCulture, $"{(int)days} days ago"),
            _ => when.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"4K · HEVC · TrueHD 7.1".</summary>
    public static string MediaSummary(Media? media)
    {
        if (media is null) return string.Empty;
        var parts = new List<string>();
        if (media.VideoResolution is { } resolution)
        {
            parts.Add(resolution.Equals("4k", StringComparison.OrdinalIgnoreCase) ? "4K" : resolution.All(char.IsDigit) ? resolution + "p" : resolution.ToUpperInvariant());
        }

        if (media.VideoCodec is { } video)
        {
            parts.Add(video.ToLowerInvariant() switch
            {
                "h264" => "H.264",
                "hevc" or "h265" => "HEVC",
                "mpeg2video" => "MPEG-2",
                "mpeg4" => "MPEG-4",
                "mpeg1video" => "MPEG-1",
                "vc1" => "VC-1",
                "msmpeg4v3" => "DivX",
                _ => video.ToUpperInvariant(),
            });
        }
        if (media.AudioCodec is { } audio)
        {
            var name = audio.ToLowerInvariant() switch
            {
                "truehd" => "TrueHD",
                "eac3" => "Dolby Digital Plus",
                "ac3" => "Dolby Digital",
                "dca" or "dts" => "DTS",
                "aac" => "AAC",
                "flac" => "FLAC",
                _ => audio.ToUpperInvariant(),
            };
            parts.Add(media.AudioChannels is { } channels ? $"{name} {Channels(channels)}" : name);
        }

        return string.Join(" · ", parts);
    }

    private static string Channels(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => channels.ToString(CultureInfo.InvariantCulture) + "ch",
    };
}
