using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// The fields the viewer's own state brings: a playlist entry's place, a history entry's moment,
// and a playback's device and viewer in the server's list of what is playing.
public sealed partial class MetadataItem
{
    /// <summary>A playlist entry's own id: the same item can stand in a playlist twice.</summary>
    [JsonPropertyName("playlistItemID")]
    public long? PlaylistItemId { get; init; }

    /// <summary>When the viewer rated it, seconds since 1970.</summary>
    [JsonPropertyName("lastRatedAt")]
    public long? LastRatedAt { get; init; }

    /// <summary>A history entry: when it was watched, seconds since 1970.</summary>
    [JsonPropertyName("viewedAt")]
    public long? ViewedAt { get; init; }

    /// <summary>A history entry: its own address on the server.</summary>
    [JsonPropertyName("historyKey")]
    public string? HistoryKey { get; init; }

    /// <summary>A history entry: the server's number for the account that watched.</summary>
    [JsonPropertyName("accountID")]
    public long? AccountId { get; init; }

    /// <summary>A history entry: the server's number for the device it was watched on.</summary>
    [JsonPropertyName("deviceID")]
    public long? DeviceId { get; init; }

    /// <summary>Something playing now: the playback's number on the server.</summary>
    [JsonPropertyName("sessionKey")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SessionKey { get; init; }

    /// <summary>Something playing now: the device playing it.</summary>
    [JsonPropertyName("Player")]
    public PlaybackPlayer? Player { get; init; }

    /// <summary>Something playing now: the account watching.</summary>
    [JsonPropertyName("User")]
    public PlaybackUser? User { get; init; }

    /// <summary>
    /// When the request that brought this record began, on <see cref="System.Diagnostics.Stopwatch"/>'s
    /// clock; 0 for a record made up in the client. Lets a newer record win over an older one.
    /// </summary>
    [JsonIgnore]
    public long FetchedAt { get; internal set; }
}

public sealed partial class MediaContainer
{
    /// <summary>Marks every item the container holds, its hubs' included, with when its request began.</summary>
    internal void Stamp(long fetchedAt)
    {
        foreach (var item in Metadata ?? []) item.FetchedAt = fetchedAt;
        foreach (var hub in Hub ?? [])
        {
            foreach (var item in hub.Metadata ?? []) item.FetchedAt = fetchedAt;
        }
    }
}

/// <summary>The device a playback runs on.</summary>
public sealed class PlaybackPlayer
{
    /// <summary>The device's name: "Living Room", "iPhone".</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The app playing: "Plex for Apple TV", "Plex Web".</summary>
    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    /// <summary>The installation's client identifier.</summary>
    [JsonPropertyName("machineIdentifier")]
    public string? MachineIdentifier { get; init; }

    /// <summary><c>playing</c>, <c>paused</c> or <c>buffering</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("local")]
    public bool Local { get; init; }
}

/// <summary>The account a playback belongs to.</summary>
public sealed class PlaybackUser
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }
}
