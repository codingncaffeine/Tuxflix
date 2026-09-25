using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// What the server says about music beyond the library listings: lyrics, loudness over time,
// stations and play queues, and the sonic analysis some servers make of every track.

public sealed partial class MediaContainer
{
    /// <summary>A lyric stream, as <c>/library/streams/{id}</c> answers when asked for JSON.</summary>
    [JsonPropertyName("Lyrics")]
    public List<LyricsInfo>? Lyrics { get; init; }

    /// <summary>A stream's loudness over time (<c>/library/streams/{id}/levels</c>), in decibels, oldest first.</summary>
    [JsonPropertyName("Level")]
    public List<LoudnessLevel>? Level { get; init; }

    /// <summary>How many loudness readings the server holds for the whole stream.</summary>
    [JsonPropertyName("totalSamples")]
    public int? TotalSamples { get; init; }

    /// <summary>A play queue the server made (<c>POST /playQueues</c>).</summary>
    [JsonPropertyName("playQueueID")]
    public long? PlayQueueId { get; init; }

    [JsonPropertyName("playQueueSelectedItemID")]
    public long? PlayQueueSelectedItemId { get; init; }

    [JsonPropertyName("playQueueTotalCount")]
    public int? PlayQueueTotalCount { get; init; }
}

public sealed partial class MetadataItem
{
    /// <summary>Set on a track the server has analysed sonically: it can find its neighbours and paths through them.</summary>
    [JsonPropertyName("musicAnalysisVersion")]
    public int? MusicAnalysisVersion { get; init; }

    /// <summary>An artist's radio stations, asked for with <c>includeStations=1</c>.</summary>
    [JsonPropertyName("Stations")]
    public StationList? Stations { get; init; }

    /// <summary>A station (<c>radio="1"</c>) rather than a stored playlist.</summary>
    [JsonPropertyName("radio")]
    public bool Radio { get; init; }

    /// <summary>A sonic neighbour's distance from the track it was found for: 0 the same, 1 far.</summary>
    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("playQueueItemID")]
    public long? PlayQueueItemId { get; init; }
}

public sealed partial class MediaStream
{
    /// <summary>Where lyrics came from: an agent, or a file beside the track.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>A lyric stream with a time on every line.</summary>
    [JsonPropertyName("timed")]
    public bool Timed { get; init; }

    /// <summary>A lyric stream.</summary>
    [JsonIgnore]
    public bool IsLyrics => StreamType == LyricsStreamType;

    /// <summary>The stream type of lyrics, beside 1 video, 2 audio and 3 subtitles.</summary>
    public const int LyricsStreamType = 4;
}

/// <summary>The lyrics of a track as the server reads them to a client.</summary>
public sealed class LyricsInfo
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("timed")]
    public bool Timed { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>The rights line the provider asks to be shown with its lyrics.</summary>
    [JsonPropertyName("by")]
    public string? By { get; init; }

    [JsonPropertyName("Line")]
    public List<LyricsLine>? Line { get; init; }
}

public sealed class LyricsLine
{
    /// <summary>Milliseconds from the start of the track; absent when the lyrics are not timed.</summary>
    [JsonPropertyName("startOffset")]
    public long? StartOffset { get; init; }

    [JsonPropertyName("endOffset")]
    public long? EndOffset { get; init; }

    /// <summary>The line's words; none on a gap between verses.</summary>
    [JsonPropertyName("Span")]
    public List<LyricsSpan>? Span { get; init; }
}

public sealed class LyricsSpan
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("startOffset")]
    public long? StartOffset { get; init; }

    [JsonPropertyName("endOffset")]
    public long? EndOffset { get; init; }
}

/// <summary>One loudness reading, dB.</summary>
public sealed class LoudnessLevel
{
    [JsonPropertyName("v")]
    public double V { get; init; }
}

public sealed class StationList
{
    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("Metadata")]
    public List<MetadataItem>? Metadata { get; init; }
}
