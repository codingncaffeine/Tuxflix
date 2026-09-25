using System.Text.Json.Serialization;

namespace Tuxflix.Core.Downloads;

/// <summary>Where a download stands.</summary>
public enum DownloadState
{
    /// <summary>Waiting its turn, or for its server to be open.</summary>
    Queued,

    Downloading,

    /// <summary>Held by the viewer; the bytes so far are kept for later.</summary>
    Paused,

    /// <summary>Kept: the whole file is on disk and its size matches what the server said.</summary>
    Done,

    /// <summary>Stopped, with a reason the viewer is told (the server refused, the disk is full).</summary>
    Failed,
}

/// <summary>
/// One film or episode kept for watching without the server, or on its way. The manager owns the
/// live record; everything outside it is handed copies.
/// </summary>
public sealed class DownloadRecord
{
    /// <summary>The server's machine identifier: the file came from there, and its watch state goes back there.</summary>
    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string RatingKey { get; set; } = string.Empty;

    /// <summary>movie or episode.</summary>
    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>An episode's series.</summary>
    public string? ShowTitle { get; set; }

    public string? ShowKey { get; set; }

    public string? SeasonKey { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public int? Year { get; set; }

    /// <summary>Length in milliseconds, as the server lists it.</summary>
    public long Duration { get; set; }

    /// <summary>The server's path for the picture shown while the download waits: an episode's still, a film's backdrop.</summary>
    public string? Picture { get; set; }

    /// <summary>The server's path for the file (<c>/library/parts/…</c>); empty until the full record has been read.</summary>
    public string PartKey { get; set; } = string.Empty;

    /// <summary>The folder the file, its metadata and its artwork are kept in.</summary>
    public string Folder { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>The file's size: the server's own figure once it has answered, the listing's before.</summary>
    public long TotalBytes { get; set; }

    /// <summary>The server stated <see cref="TotalBytes"/> in its answer, so the finished file is checked against it.</summary>
    public bool TotalConfirmed { get; set; }

    public long DoneBytes { get; set; }

    /// <summary>The file's ETag (else its Last-Modified): a resumed transfer must be of the same file.</summary>
    public string? Validator { get; set; }

    public DownloadState State { get; set; }

    /// <summary>Why it stopped, in words for the viewer.</summary>
    public string? Error { get; set; }

    /// <summary>A failure that may pass (the server stopped answering), tried again when the server is next open.</summary>
    public bool Retryable { get; set; }

    /// <summary>Unix seconds.</summary>
    public long AddedAt { get; set; }

    /// <summary>Unix seconds.</summary>
    public long? CompletedAt { get; set; }

    /// <summary>Where playback of the kept copy stopped, in milliseconds; 0 at the start or once watched.</summary>
    public long ViewOffset { get; set; }

    /// <summary>Watched here, whether or not the server has heard yet.</summary>
    public bool Watched { get; set; }

    /// <summary>Unix seconds: when it was first seen watched, here or on the server. A rule removes it a while after.</summary>
    public long? WatchedAt { get; set; }

    /// <summary>The rule that fetched it; only those are removed once watched.</summary>
    public string? RuleId { get; set; }

    /// <summary>When this copy was taken, in the manager's own count: a later copy of the same download has a higher one.</summary>
    [JsonIgnore]
    public long Sequence { get; set; }

    [JsonIgnore]
    public string Id => Key(ServerId, RatingKey);

    [JsonIgnore]
    public string MediaPath => Path.Combine(Folder, FileName);

    /// <summary>The file while it arrives; renamed to <see cref="MediaPath"/> once whole and checked.</summary>
    [JsonIgnore]
    public string PartialPath => MediaPath + ".part";

    [JsonIgnore]
    public string MetadataPath => Path.Combine(Folder, "metadata.json");

    [JsonIgnore]
    public double Fraction => State == DownloadState.Done ? 1 : TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1) : 0;

    /// <summary>A kept file, local artwork included, by the name it has beside the media.</summary>
    public string ArtworkPath(string name) => Path.Combine(Folder, name);

    public static string Key(string serverId, string ratingKey) => serverId + "/" + ratingKey;

    internal DownloadRecord Copy() => (DownloadRecord)MemberwiseClone();
}

/// <summary>Keeps the next few unwatched episodes of a series or a season downloaded, and lets watched ones go.</summary>
public sealed class DownloadRule
{
    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    /// <summary>The series or the season.</summary>
    public string RatingKey { get; set; } = string.Empty;

    /// <summary>show or season.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>"Lanternfall", or "Lanternfall · Season 2".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>How many unwatched episodes are kept ready.</summary>
    public int Keep { get; set; } = 3;

    /// <summary>
    /// Episodes this rule fetched and let go once watched: never fetched again, even while the server
    /// has not heard they were watched.
    /// </summary>
    public List<string> Released { get; set; } = [];

    [JsonIgnore]
    public string Id => DownloadRecord.Key(ServerId, RatingKey);

    internal DownloadRule Copy()
    {
        var copy = (DownloadRule)MemberwiseClone();
        copy.Released = [.. Released];
        return copy;
    }
}

/// <summary>A change of watch state made without the server, sent when it can be reached again.</summary>
public sealed class PendingWrite
{
    public const string Timeline = "timeline";
    public const string Scrobble = "scrobble";

    public string ServerId { get; set; } = string.Empty;

    public string RatingKey { get; set; } = string.Empty;

    /// <summary><see cref="Timeline"/> (where playback stopped) or <see cref="Scrobble"/> (watched).</summary>
    public string Kind { get; set; } = Timeline;

    public long Time { get; set; }

    public long Duration { get; set; }

    /// <summary>Unix seconds: when it happened.</summary>
    public long At { get; set; }
}

/// <summary>Everything the download manager keeps between runs, in one file.</summary>
public sealed class DownloadIndex
{
    /// <summary>In queue order; kept ones among them.</summary>
    public List<DownloadRecord> Items { get; set; } = [];

    public List<DownloadRule> Rules { get; set; } = [];

    /// <summary>In the order they happened.</summary>
    public List<PendingWrite> Pending { get; set; } = [];
}

/// <summary>A download that cannot go on until something changes: the reason is for the viewer.</summary>
public sealed class DownloadFailedException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DownloadIndex))]
internal sealed partial class DownloadJsonContext : JsonSerializerContext;
