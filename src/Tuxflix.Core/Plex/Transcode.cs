using System.Globalization;

namespace Tuxflix.Core.Plex;

/// <summary>How the server says an item should reach the player.</summary>
public enum PlaybackRoute
{
    /// <summary>The file as it is on the server's disk.</summary>
    DirectPlay,

    /// <summary>A stream the server makes from the file (converted, or repackaged), over HLS.</summary>
    Convert,

    /// <summary>The server would not, or could not say.</summary>
    Refused,
}

/// <summary>
/// The server's answer to "how should this play": the route, the reason in the server's words,
/// and the media as it will arrive (container, codecs, size and each stream's fate).
/// </summary>
public sealed record PlaybackDecision(PlaybackRoute Route, string Reason, Media? Media)
{
    /// <summary>Refused only because the conversion would arrive in a container other than the one asked for.</summary>
    public bool WrongContainer { get; init; }

    /// <summary>
    /// Reads a decision. Any code of 2000 or more is a refusal (as Plex Web reads them); otherwise
    /// the chosen part's own <c>decision</c> names the route. A conversion must arrive in the
    /// <paramref name="expectedContainer"/> the request asked for: a server that substitutes
    /// another would hand the player a stream nobody negotiated.
    /// </summary>
    public static PlaybackDecision From(MediaContainer container, string expectedContainer)
    {
        ArgumentNullException.ThrowIfNull(container);
        (int? Code, string? Text)[] verdicts =
        [
            (container.GeneralDecisionCode, container.GeneralDecisionText),
            (container.TranscodeDecisionCode, container.TranscodeDecisionText),
            (container.MdeDecisionCode, container.MdeDecisionText),
        ];
        if (verdicts.FirstOrDefault(v => v.Code >= 2000) is { Code: { } code } refusal)
        {
            return new PlaybackDecision(PlaybackRoute.Refused, refusal.Text ?? string.Create(CultureInfo.InvariantCulture, $"The server refused ({code})."), null);
        }

        var reason = verdicts.Select(v => v.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        var medias = container.Metadata?.FirstOrDefault()?.Media ?? [];
        var media = medias.FirstOrDefault(m => m.Selected) ?? medias.FirstOrDefault();
        var route = media?.Part?.FirstOrDefault()?.Decision switch
        {
            "directplay" => PlaybackRoute.DirectPlay,
            "transcode" or "copy" => PlaybackRoute.Convert,
            _ => PlaybackRoute.Refused,
        };
        if (route == PlaybackRoute.Refused)
        {
            return new PlaybackDecision(PlaybackRoute.Refused, reason.Length > 0 ? reason : "The server named no way to play it.", media);
        }

        if (route == PlaybackRoute.Convert && !string.Equals(media?.Container, expectedContainer, StringComparison.Ordinal))
        {
            return new PlaybackDecision(PlaybackRoute.Refused, $"The server offered {media?.Container ?? "no"} segments, not the {expectedContainer} asked for.", media) { WrongContainer = true };
        }

        return new PlaybackDecision(route, reason, media);
    }

    /// <summary>What the conversion delivers, in words: "H.264 1280×720 · AAC · 3.5 Mbps".</summary>
    public string Describe()
    {
        if (Media is not { } media) return string.Empty;
        var streams = media.Part?.FirstOrDefault()?.Stream ?? [];
        var video = streams.FirstOrDefault(s => s.StreamType == 1);
        var audio = streams.FirstOrDefault(s => s.StreamType == 2 && s.Selected) ?? streams.FirstOrDefault(s => s.StreamType == 2);
        var parts = new List<string>();
        if (video is not null)
        {
            var size = video is { Width: { } w, Height: { } h } ? $" {w}×{h}" : string.Empty;
            parts.Add($"{CodecNames.Of(video.Codec)}{size}{(video.Decision == "copy" ? " (as it is)" : string.Empty)}");
        }

        if (audio is not null) parts.Add($"{CodecNames.Of(audio.Codec)}{(audio.Decision == "copy" ? " (as it is)" : string.Empty)}");
        if (media.Bitrate is { } kbps) parts.Add(CodecNames.Mbps(kbps));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// One ask of the server's universal transcoder: the same parameters go to its decision and,
/// when it converts, to its HLS start, so what plays is what was decided.
/// </summary>
/// <remarks>
/// <para>
/// The base profile is Plex's <c>Generic</c> one, augmented: an fMP4 HLS target (H.264 or HEVC
/// video) and WebVTT subtitles. HEVC is never offered in MPEG-TS, where a server's hardware
/// encoder emits parameter sets mpv rejects; a server that will not honour fMP4 is asked again
/// for H.264 in MPEG-TS, the pair Plex's own older players use. <c>hasMDE=1</c> with direct play
/// allowed means the profile does not stand in the way of playing the original file, which mpv
/// plays whatever it is.
/// </para>
/// <para>
/// There is no <c>offset</c>: the stream always describes the whole title and the player seeks
/// within it. Starting the transcoder at the resume point makes it restart when the player reads
/// the first segment. Which subtitle is burned in comes from the part's saved selection, not
/// from a parameter; the caller selects it first. (Both learnt from Plezy's player, GPL-3.0.)
/// </para>
/// </remarks>
public sealed record TranscodeRequest(string RatingKey, StreamQuality Quality, string Session, string SessionIdentifier)
{
    public const string DecisionPath = "/video/:/transcode/universal/decision";
    public const string StartPath = "/video/:/transcode/universal/start.m3u8";

    /// <summary>Reached over the internet rather than the home network.</summary>
    public bool Remote { get; init; }

    /// <summary>The audio stream to carry, by the server's stream id; null for the part's own choice.</summary>
    public long? AudioStreamId { get; init; }

    /// <summary>Burn the part's selected subtitle into the picture (a conversion cannot carry it otherwise).</summary>
    public bool BurnSubtitles { get; init; }

    /// <summary>The fallback: H.264 in MPEG-TS segments, for a server that will not honour fMP4.</summary>
    public bool TransportStream { get; init; }

    /// <summary>The container a conversion must arrive in.</summary>
    public string Container => TransportStream ? "mpegts" : "mp4";

    /// <summary>What the <c>X-Plex-Client-Profile-Extra</c> parameter says, before encoding.</summary>
    public string ProfileExtra
    {
        get
        {
            var clauses = new List<string> { "add-settings(DirectPlayStreamSelection=true)" };
            if (Quality.Kbps is { } kbps)
            {
                clauses.Add(string.Create(CultureInfo.InvariantCulture, $"add-limitation(scope=videoCodec&scopeName=*&type=upperBound&name=video.bitrate&value={kbps}&replace=true)"));
            }

            clauses.Add(TransportStream
                ? "add-transcode-target(type=videoProfile&context=streaming&protocol=hls&container=mpegts&videoCodec=h264&audioCodec=aac,ac3,eac3,mp3)"
                : "add-transcode-target(type=videoProfile&context=streaming&protocol=hls&container=mp4&videoCodec=h264,hevc&audioCodec=aac,ac3,eac3,mp3)");
            clauses.Add("add-transcode-target(type=subtitleProfile&context=streaming&protocol=hls&container=webvtt&subtitleCodec=webvtt)");
            return string.Join('+', clauses);
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> Parameters(PlexClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var original = Quality.IsOriginal;
        var list = new List<KeyValuePair<string, string>>
        {
            new("hasMDE", "1"),
            new("path", "/library/metadata/" + RatingKey),
            new("mediaIndex", "0"),
            new("partIndex", "0"),
            new("protocol", "hls"),
            new("fastSeek", "1"),

            // Burning in is a conversion: asking for direct play at the same time is refused outright.
            new("directPlay", original && !BurnSubtitles ? "1" : "0"),
            new("directStream", original ? "1" : "0"),
            new("directStreamAudio", "1"),
            new("subtitleSize", "100"),
            new("audioBoost", "100"),
            new("location", Remote ? "wan" : "lan"),
            new("autoAdjustQuality", "0"),
            new("mediaBufferSize", "102400"),
            new("session", Session),
            new("subtitles", BurnSubtitles ? "burn" : "none"),
        };

        // The size and quality ride as their own parameters: the bitrate limit alone leaves a 4K file at 4K.
        if (Quality is { Kbps: { } kbps, Resolution: { } resolution, VideoQuality: { } videoQuality })
        {
            list.Add(new("maxVideoBitrate", kbps.ToString(CultureInfo.InvariantCulture)));
            list.Add(new("videoResolution", resolution));
            list.Add(new("videoQuality", videoQuality.ToString(CultureInfo.InvariantCulture)));
        }

        if (AudioStreamId is { } audio) list.Add(new("audioStreamID", audio.ToString(CultureInfo.InvariantCulture)));
        list.Add(new("X-Plex-Session-Identifier", SessionIdentifier));
        list.Add(new("X-Plex-Client-Profile-Name", "Generic"));
        list.Add(new("X-Plex-Client-Profile-Extra", ProfileExtra));
        list.Add(new("X-Plex-Incomplete-Segments", "1"));
        list.Add(new("X-Plex-Product", PlexClientIdentity.Product));
        list.Add(new("X-Plex-Version", identity.Version));
        list.Add(new("X-Plex-Client-Identifier", identity.ClientIdentifier));
        list.Add(new("X-Plex-Platform", "Linux"));
        list.Add(new("X-Plex-Device", "PC"));
        list.Add(new("X-Plex-Device-Name", identity.DeviceName));
        return list;
    }

    /// <summary>The query string, every name and value percent-encoded (the profile's brackets and asterisks included).</summary>
    public string Query(PlexClientIdentity identity) =>
        string.Join('&', Parameters(identity).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
}
