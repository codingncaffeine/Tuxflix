using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// What a server says on its notification socket (/:/websockets/notifications): one
// NotificationContainer per message, its `type` naming which of the lists it carries. Numbers the
// server sometimes writes as text (sectionID, itemID) are read either way.

/// <summary>One message from the notification socket.</summary>
public sealed class NotificationEnvelope
{
    [JsonPropertyName("NotificationContainer")]
    public NotificationContainer? Container { get; init; }
}

/// <summary>A batch of notifications of one type.</summary>
public sealed class NotificationContainer
{
    /// <summary><c>playing</c>, <c>timeline</c>, <c>activity</c>, <c>progress</c>, <c>status</c>, <c>transcodeSession.update</c>, <c>transcodeSession.end</c>, <c>preference</c>, <c>backgroundProcessingQueue</c>, and others.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; init; }

    /// <summary><c>playing</c>: where a playback on any device of the server has got to.</summary>
    [JsonPropertyName("PlaySessionStateNotification")]
    public List<PlaySessionState>? Playing { get; init; }

    /// <summary><c>timeline</c>: library items added, changed, analysed or removed.</summary>
    [JsonPropertyName("TimelineEntry")]
    public List<TimelineEntry>? Timeline { get; init; }

    /// <summary><c>activity</c>: something the server is busy with, a library scan say, as it starts, moves and ends.</summary>
    [JsonPropertyName("ActivityNotification")]
    public List<ActivityNotification>? Activities { get; init; }

    /// <summary><c>progress</c>: a line of what a running task is doing.</summary>
    [JsonPropertyName("ProgressNotification")]
    public List<ProgressNotification>? Progress { get; init; }

    /// <summary><c>status</c>: a finished piece of work, "Library scan complete".</summary>
    [JsonPropertyName("StatusNotification")]
    public List<StatusNotification>? Status { get; init; }

    /// <summary><c>transcodeSession.update</c> and <c>transcodeSession.end</c>: a conversion's progress.</summary>
    [JsonPropertyName("TranscodeSession")]
    public List<TranscodeNotice>? TranscodeSessions { get; init; }

    /// <summary><c>preference</c>: a server setting changed.</summary>
    [JsonPropertyName("Setting")]
    public List<PreferenceNotice>? Settings { get; init; }

    /// <summary><c>backgroundProcessingQueue</c>: the queue of conversions for other devices changed.</summary>
    [JsonPropertyName("BackgroundProcessingQueueEventNotification")]
    public List<BackgroundQueueNotice>? BackgroundQueue { get; init; }

    /// <summary><c>reachability</c>: whether the server can be reached from outside.</summary>
    [JsonPropertyName("ReachabilityNotification")]
    public List<ReachabilityNotice>? Reachability { get; init; }

    /// <summary>Reads one message; null for anything that is not a notification the client can read.</summary>
    public static NotificationContainer? Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, PlexNotificationJsonContext.Default.NotificationEnvelope)?.Container;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where one playback is: its item, its position and whether it plays.</summary>
public sealed class PlaySessionState
{
    [JsonPropertyName("sessionKey")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SessionKey { get; init; }

    [JsonPropertyName("clientIdentifier")]
    public string? ClientIdentifier { get; init; }

    [JsonPropertyName("ratingKey")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? RatingKey { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Milliseconds into the item.</summary>
    [JsonPropertyName("viewOffset")]
    public long? ViewOffset { get; init; }

    /// <summary><c>playing</c>, <c>paused</c>, <c>buffering</c> or <c>stopped</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("transcodeSession")]
    public string? TranscodeSession { get; init; }
}

/// <summary>A library item the server added, changed or removed.</summary>
public sealed class TimelineEntry
{
    /// <summary><c>com.plexapp.plugins.library</c> for library items; other identifiers carry other timelines.</summary>
    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("sectionID")]
    public long? SectionId { get; init; }

    [JsonPropertyName("itemID")]
    public long? ItemId { get; init; }

    [JsonPropertyName("parentItemID")]
    public long? ParentItemId { get; init; }

    [JsonPropertyName("rootItemID")]
    public long? RootItemId { get; init; }

    /// <summary>The item's kind (1 film, 2 show, 3 season, 4 episode, 8 artist, 9 album, 10 track); -1 once it is deleted.</summary>
    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Walks from 0 while the item is scanned, matched and analysed; 5 is settled, 9 deleted.</summary>
    [JsonPropertyName("state")]
    public int? State { get; init; }

    [JsonPropertyName("metadataState")]
    public string? MetadataState { get; init; }

    [JsonPropertyName("mediaState")]
    public string? MediaState { get; init; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; init; }

    public const string LibraryIdentifier = "com.plexapp.plugins.library";

    /// <summary>The settled state: the item's record is complete.</summary>
    public const int Settled = 5;

    /// <summary>The deleted state.</summary>
    public const int Deleted = 9;

    /// <summary>A library item whose record is complete, or which is gone: time to read it again.</summary>
    [JsonIgnore]
    public bool IsLibraryChange => Identifier == LibraryIdentifier && (State is Settled or Deleted || Type == -1);

    [JsonIgnore]
    public bool IsRemoval => State == Deleted || Type == -1 || MetadataState == "deleted";
}

/// <summary>An activity starting, moving on or ending.</summary>
public sealed class ActivityNotification
{
    /// <summary><c>started</c>, <c>updated</c> or <c>ended</c>.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("Activity")]
    public ServerActivity? Activity { get; init; }
}

/// <summary>Something the server is busy with.</summary>
public sealed class ServerActivity
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary><c>library.update.section</c> for a scan, <c>library.refresh.items</c>, <c>media.generate.bif</c>, and more.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("cancellable")]
    public bool Cancellable { get; init; }

    [JsonPropertyName("userID")]
    public long? UserId { get; init; }

    /// <summary>"Scanning Movies".</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>What it is on now, a file or a title.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    /// <summary>Per cent done, 0 to 100; some activities never say.</summary>
    [JsonPropertyName("progress")]
    public int? Progress { get; init; }

    [JsonPropertyName("Context")]
    public ActivityContext? Context { get; init; }
}

public sealed class ActivityContext
{
    [JsonPropertyName("librarySectionID")]
    public long? LibrarySectionId { get; init; }
}

public sealed class ProgressNotification
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class StatusNotification
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary><c>LIBRARY_UPDATE</c>, and others.</summary>
    [JsonPropertyName("notificationName")]
    public string? NotificationName { get; init; }
}

/// <summary>A conversion's progress, as the notification socket reports it.</summary>
public sealed class TranscodeNotice
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("progress")]
    public double? Progress { get; init; }

    [JsonPropertyName("speed")]
    public double? Speed { get; init; }

    [JsonPropertyName("throttled")]
    public bool Throttled { get; init; }

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }
}

public sealed class PreferenceNotice
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }
}

public sealed class BackgroundQueueNotice
{
    [JsonPropertyName("queueID")]
    public long? QueueId { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }
}

public sealed class ReachabilityNotice
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>Reads a value the server sends as a number on some endpoints and as text on others, as text.</summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected text or a number, found {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}

[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(FlexibleBooleanConverter)])]
[JsonSerializable(typeof(NotificationEnvelope))]
public sealed partial class PlexNotificationJsonContext : JsonSerializerContext;
