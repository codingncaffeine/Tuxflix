using System.Globalization;

namespace Tuxflix.Core.Plex;

/// <summary>The names people know codecs by, from the server's short names.</summary>
public static class CodecNames
{
    public static string Of(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => "?",
        "h264" => "H.264",
        "hevc" or "h265" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        "vc1" => "VC-1",
        "aac" => "AAC",
        "ac3" => "Dolby Digital",
        "eac3" => "Dolby Digital Plus",
        "truehd" => "Dolby TrueHD",
        "dca" => "DTS",
        "dca-ma" => "DTS-HD MA",
        "flac" => "FLAC",
        "alac" => "ALAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "pcm" => "PCM",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>A version of an item in one line: "H.264 1920×1080 · DTS-HD MA · 26.5 Mbps".</summary>
    public static string Describe(Media media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var parts = new List<string>();
        if (media.VideoCodec is not null) parts.Add(Of(media.VideoCodec) + (media is { Width: { } w, Height: { } h } ? $" {w}×{h}" : string.Empty));
        if (media.AudioCodec is not null) parts.Add(Of(media.AudioCodec));
        if (media.Bitrate is { } kbps) parts.Add(Mbps(kbps));
        return string.Join(" · ", parts);
    }

    public static string Mbps(int kbps) => string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.#} Mbps");
}
