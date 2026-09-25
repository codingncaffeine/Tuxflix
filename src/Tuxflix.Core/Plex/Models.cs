using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// The shapes Plex Media Server answers with when asked for JSON. Property names are Plex's own;
// arrays of child elements are capitalised (Metadata, Media, Part) because that is how the server
// spells them. Fields are added as the interface starts reading them, not ahead of use.

/// <summary>Every server response wraps its payload in a MediaContainer.</summary>
public sealed class PlexEnvelope
{
    [JsonPropertyName("MediaContainer")]
    public MediaContainer? MediaContainer { get; init; }
}

public sealed class MediaContainer
{
    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("totalSize")]
    public int? TotalSize { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("title1")]
    public string? Title1 { get; init; }

    [JsonPropertyName("title2")]
    public string? Title2 { get; init; }

    [JsonPropertyName("friendlyName")]
    public string? FriendlyName { get; init; }

    [JsonPropertyName("machineIdentifier")]
    public string? MachineIdentifier { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("librarySectionID")]
    public int? LibrarySectionId { get; init; }

    [JsonPropertyName("librarySectionTitle")]
    public string? LibrarySectionTitle { get; init; }

    [JsonPropertyName("Directory")]
    public List<LibraryDirectory>? Directory { get; init; }

    [JsonPropertyName("Metadata")]
    public List<MetadataItem>? Metadata { get; init; }

    [JsonPropertyName("Hub")]
    public List<Hub>? Hub { get; init; }

    /// <summary>What <c>/services/ultrablur/colors</c> answers with.</summary>
    [JsonPropertyName("UltraBlurColors")]
    public List<UltraBlurColors>? UltraBlurColors { get; init; }

    // A playback decision's verdicts: 1000 and 1001 say yes, 2000 and above say no.
    [JsonPropertyName("generalDecisionCode")]
    public int? GeneralDecisionCode { get; init; }

    [JsonPropertyName("generalDecisionText")]
    public string? GeneralDecisionText { get; init; }

    [JsonPropertyName("directPlayDecisionCode")]
    public int? DirectPlayDecisionCode { get; init; }

    [JsonPropertyName("directPlayDecisionText")]
    public string? DirectPlayDecisionText { get; init; }

    [JsonPropertyName("transcodeDecisionCode")]
    public int? TranscodeDecisionCode { get; init; }

    [JsonPropertyName("transcodeDecisionText")]
    public string? TranscodeDecisionText { get; init; }

    [JsonPropertyName("mdeDecisionCode")]
    public int? MdeDecisionCode { get; init; }

    [JsonPropertyName("mdeDecisionText")]
    public string? MdeDecisionText { get; init; }

    [JsonPropertyName("TranscodeSession")]
    public List<TranscodeSessionInfo>? TranscodeSession { get; init; }
}

/// <summary>A conversion the server is running (<c>/transcode/sessions</c>).</summary>
public sealed class TranscodeSessionInfo
{
    /// <summary>The <c>session</c> the player asked with.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Whether this is the conversion started with <paramref name="session"/> (a path form ending in it counts too).</summary>
    public bool Is(string session) => Key is { } key && (key == session || key.EndsWith("/" + session, StringComparison.Ordinal));

    [JsonPropertyName("videoDecision")]
    public string? VideoDecision { get; init; }

    [JsonPropertyName("audioDecision")]
    public string? AudioDecision { get; init; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>How many times faster than real time it converts.</summary>
    [JsonPropertyName("speed")]
    public double? Speed { get; init; }

    [JsonPropertyName("transcodeHwEncoding")]
    public string? HardwareEncoding { get; init; }
}

/// <summary>A library section, or any other directory a listing returns.</summary>
public sealed class LibraryDirectory
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }

    [JsonPropertyName("art")]
    public string? Art { get; init; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; init; }

    [JsonPropertyName("scannedAt")]
    public long? ScannedAt { get; init; }

    [JsonPropertyName("hidden")]
    public int? Hidden { get; init; }

    /// <summary>A sort: the key that sorts it the other way (<c>titleSort:desc</c>).</summary>
    [JsonPropertyName("descKey")]
    public string? DescKey { get; init; }

    /// <summary>A sort: which way it runs first, <c>asc</c> or <c>desc</c>.</summary>
    [JsonPropertyName("defaultDirection")]
    public string? DefaultDirection { get; init; }

    /// <summary>A filter: the query parameter it sets (<c>genre</c>, <c>unwatched</c>).</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; init; }

    /// <summary>A filter: <c>boolean</c>, <c>string</c> or <c>integer</c>.</summary>
    [JsonPropertyName("filterType")]
    public string? FilterType { get; init; }
}

/// <summary>A stretch of an item the server found: <c>intro</c>, <c>credits</c> or <c>commercial</c>.</summary>
public sealed class Marker
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("startTimeOffset")]
    public long StartTimeOffset { get; init; }

    [JsonPropertyName("endTimeOffset")]
    public long EndTimeOffset { get; init; }

    /// <summary>The credits that run to the end: after them there is nothing left to watch.</summary>
    [JsonPropertyName("final")]
    public bool Final { get; init; }
}

/// <summary>A chapter of a film or an episode.</summary>
public sealed class Chapter
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("startTimeOffset")]
    public long StartTimeOffset { get; init; }

    [JsonPropertyName("endTimeOffset")]
    public long EndTimeOffset { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }
}

/// <summary>A tag a search or a listing returns: a person, a genre, a place.</summary>
public sealed class TagEntry
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    /// <summary>What kind of tag: 1 genre, 4 director, 6 actor, and more.</summary>
    [JsonPropertyName("tagType")]
    public int? TagType { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }

    /// <summary>The query that lists what carries it in its library: <c>actor=5090</c>.</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; init; }

    [JsonPropertyName("librarySectionID")]
    public int? LibrarySectionId { get; init; }

    [JsonPropertyName("librarySectionTitle")]
    public string? LibrarySectionTitle { get; init; }

    /// <summary>How many items in its library carry it.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }
}

public sealed class Hub
{
    [JsonPropertyName("hubKey")]
    public string? HubKey { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("hubIdentifier")]
    public string? HubIdentifier { get; init; }

    [JsonPropertyName("context")]
    public string? Context { get; init; }

    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("more")]
    public bool More { get; init; }

    [JsonPropertyName("style")]
    public string? Style { get; init; }

    [JsonPropertyName("promoted")]
    public bool Promoted { get; init; }

    [JsonPropertyName("Metadata")]
    public List<MetadataItem>? Metadata { get; init; }

    /// <summary>Tags rather than items: the people, genres and places of a search.</summary>
    [JsonPropertyName("Directory")]
    public List<TagEntry>? Directory { get; init; }
}

/// <summary>A movie, show, season, episode, or any other library item.</summary>
public sealed class MetadataItem
{
    [JsonPropertyName("ratingKey")]
    public string RatingKey { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("titleSort")]
    public string? TitleSort { get; init; }

    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("studio")]
    public string? Studio { get; init; }

    [JsonPropertyName("contentRating")]
    public string? ContentRating { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; init; }

    [JsonPropertyName("rating")]
    public double? Rating { get; init; }

    [JsonPropertyName("audienceRating")]
    public double? AudienceRating { get; init; }

    [JsonPropertyName("userRating")]
    public double? UserRating { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("parentIndex")]
    public int? ParentIndex { get; init; }

    [JsonPropertyName("parentRatingKey")]
    public string? ParentRatingKey { get; init; }

    [JsonPropertyName("parentTitle")]
    public string? ParentTitle { get; init; }

    [JsonPropertyName("parentThumb")]
    public string? ParentThumb { get; init; }

    [JsonPropertyName("grandparentRatingKey")]
    public string? GrandparentRatingKey { get; init; }

    [JsonPropertyName("grandparentTitle")]
    public string? GrandparentTitle { get; init; }

    [JsonPropertyName("grandparentThumb")]
    public string? GrandparentThumb { get; init; }

    [JsonPropertyName("grandparentArt")]
    public string? GrandparentArt { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }

    [JsonPropertyName("art")]
    public string? Art { get; init; }

    [JsonPropertyName("duration")]
    public long? Duration { get; init; }

    [JsonPropertyName("originallyAvailableAt")]
    public string? OriginallyAvailableAt { get; init; }

    [JsonPropertyName("addedAt")]
    public long? AddedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; init; }

    [JsonPropertyName("lastViewedAt")]
    public long? LastViewedAt { get; init; }

    [JsonPropertyName("viewCount")]
    public int? ViewCount { get; init; }

    [JsonPropertyName("viewOffset")]
    public long? ViewOffset { get; init; }

    [JsonPropertyName("leafCount")]
    public int? LeafCount { get; init; }

    [JsonPropertyName("viewedLeafCount")]
    public int? ViewedLeafCount { get; init; }

    [JsonPropertyName("childCount")]
    public int? ChildCount { get; init; }

    [JsonPropertyName("librarySectionID")]
    public int? LibrarySectionId { get; init; }

    [JsonPropertyName("librarySectionTitle")]
    public string? LibrarySectionTitle { get; init; }

    [JsonPropertyName("Media")]
    public List<Media>? Media { get; init; }

    [JsonPropertyName("Genre")]
    public List<Tag>? Genre { get; init; }

    [JsonPropertyName("Director")]
    public List<Tag>? Director { get; init; }

    [JsonPropertyName("Writer")]
    public List<Tag>? Writer { get; init; }

    [JsonPropertyName("Role")]
    public List<Tag>? Role { get; init; }

    [JsonPropertyName("Country")]
    public List<Tag>? Country { get; init; }

    /// <summary>Music: an album's styles, finer than its genres ("Neo-Traditionalist", "Country Pop").</summary>
    [JsonPropertyName("Style")]
    public List<Tag>? Style { get; init; }

    [JsonPropertyName("Mood")]
    public List<Tag>? Mood { get; init; }

    /// <summary>Music: the release's format ("Album") and kind ("Single", "EP", "Compilation").</summary>
    [JsonPropertyName("Format")]
    public List<Tag>? Format { get; init; }

    [JsonPropertyName("Subformat")]
    public List<Tag>? Subformat { get; init; }

    [JsonPropertyName("Image")]
    public List<ItemImage>? Image { get; init; }

    /// <summary>An object in an item's metadata, a list of one in a playback decision.</summary>
    [JsonPropertyName("UltraBlurColors")]
    [JsonConverter(typeof(ObjectOrFirstConverter<UltraBlurColors>))]
    public UltraBlurColors? UltraBlurColors { get; init; }

    /// <summary>Where the intro and the credits are, from the server's analysis.</summary>
    [JsonPropertyName("Marker")]
    public List<Marker>? Marker { get; init; }

    [JsonPropertyName("Chapter")]
    public List<Chapter>? Chapter { get; init; }

    /// <summary>A collection's kind of member (<c>movie</c>, <c>show</c>) or a playlist's.</summary>
    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; }

    /// <summary>A playlist: <c>video</c>, <c>audio</c> or <c>photo</c>.</summary>
    [JsonPropertyName("playlistType")]
    public string? PlaylistType { get; init; }

    /// <summary>A playlist or collection the server fills by its own rules.</summary>
    [JsonPropertyName("smart")]
    public bool Smart { get; init; }

    /// <summary>A playlist's mosaic of its members' artwork.</summary>
    [JsonPropertyName("composite")]
    public string? Composite { get; init; }

    [JsonIgnore]
    public bool IsWatched => LeafCount is > 0
        ? ViewedLeafCount >= LeafCount
        : ViewCount is > 0 && ViewOffset is null or 0;

    /// <summary>How far through the item is, 0 to 1, or null when it has not been started.</summary>
    [JsonIgnore]
    public double? Progress => ViewOffset is > 0 && Duration is > 0
        ? Math.Clamp((double)ViewOffset.Value / Duration.Value, 0, 1)
        : null;

    [JsonIgnore]
    public int UnwatchedLeaves => LeafCount is { } leaves ? Math.Max(0, leaves - (ViewedLeafCount ?? 0)) : 0;
}

public sealed class Media
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("duration")]
    public long? Duration { get; init; }

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("aspectRatio")]
    public double? AspectRatio { get; init; }

    [JsonPropertyName("audioChannels")]
    public int? AudioChannels { get; init; }

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; init; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; init; }

    [JsonPropertyName("videoResolution")]
    public string? VideoResolution { get; init; }

    [JsonPropertyName("container")]
    public string? Container { get; init; }

    [JsonPropertyName("videoFrameRate")]
    public string? VideoFrameRate { get; init; }

    [JsonPropertyName("videoProfile")]
    public string? VideoProfile { get; init; }

    /// <summary>In a playback decision: the version the server chose.</summary>
    [JsonPropertyName("selected")]
    public bool Selected { get; init; }

    /// <summary>In a playback decision: <c>hls</c> when the server converts.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("Part")]
    public List<MediaPart>? Part { get; init; }
}

public sealed class MediaPart
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("duration")]
    public long? Duration { get; init; }

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("container")]
    public string? Container { get; init; }

    /// <summary>In a playback decision: <c>directplay</c>, <c>transcode</c> or <c>copy</c>.</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    /// <summary><c>sd</c> when the server made seek previews for this part (<c>/library/parts/{id}/indexes/sd</c>).</summary>
    [JsonPropertyName("indexes")]
    public string? Indexes { get; init; }

    [JsonPropertyName("Stream")]
    public List<MediaStream>? Stream { get; init; }
}

public sealed class MediaStream
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>1 video, 2 audio, 3 subtitle.</summary>
    [JsonPropertyName("streamType")]
    public int StreamType { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("languageCode")]
    public string? LanguageCode { get; init; }

    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; init; }

    [JsonPropertyName("extendedDisplayTitle")]
    public string? ExtendedDisplayTitle { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("selected")]
    public bool Selected { get; init; }

    [JsonPropertyName("default")]
    public bool Default { get; init; }

    [JsonPropertyName("forced")]
    public bool Forced { get; init; }

    [JsonPropertyName("channels")]
    public int? Channels { get; init; }

    /// <summary>An audio stream's sample rate in hertz.</summary>
    [JsonPropertyName("samplingRate")]
    public int? SamplingRate { get; init; }

    /// <summary>
    /// The server's loudness analysis of an audio stream: the gain in dB that brings the track to
    /// the server's reference level. The server sends these as text, which the lenient reader takes.
    /// </summary>
    [JsonPropertyName("gain")]
    public double? Gain { get; init; }

    /// <summary>The same for the whole album, so an album keeps its own quiet and loud moments.</summary>
    [JsonPropertyName("albumGain")]
    public double? AlbumGain { get; init; }

    /// <summary>Integrated loudness, LUFS.</summary>
    [JsonPropertyName("loudness")]
    public double? Loudness { get; init; }

    /// <summary>Sample peak, as a fraction of full scale.</summary>
    [JsonPropertyName("peak")]
    public double? Peak { get; init; }

    /// <summary>The stream's place in the file, every kind counted from 0; absent for a subtitle file kept beside it.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    /// <summary>Where a subtitle file kept beside the media is served (<c>/library/streams/…</c>).</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>In a playback decision: <c>copy</c>, <c>transcode</c> or <c>burn</c>.</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>A subtitle kept in a file of its own beside the media, not inside it.</summary>
    [JsonIgnore]
    public bool IsExternal => Index is null && Key is not null;
}

public sealed class Tag
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("tag")]
    public string TagText { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }
}

public sealed class ItemImage
{
    /// <summary>coverPoster, background, snapshot or clearLogo.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("alt")]
    public string? Alt { get; init; }
}

/// <summary>The four corner colours Plex computes for an item's backdrop, as hex without '#'.</summary>
public sealed class UltraBlurColors
{
    [JsonPropertyName("topLeft")]
    public string? TopLeft { get; init; }

    [JsonPropertyName("topRight")]
    public string? TopRight { get; init; }

    [JsonPropertyName("bottomRight")]
    public string? BottomRight { get; init; }

    [JsonPropertyName("bottomLeft")]
    public string? BottomLeft { get; init; }
}
