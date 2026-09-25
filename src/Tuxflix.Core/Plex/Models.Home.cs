using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// What the home screen, the Watchlist and the item pages read beyond the core fields: theme music,
// extras, critics' reviews, and the viewer's state in Plex's own catalogue (Discover).

public sealed partial class MetadataItem
{
    /// <summary>The item's theme music, served by the server (<c>/library/metadata/…/theme/…</c>).</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    /// <summary>Trailers, featurettes and scenes, sent with the item when it is asked for with <c>includeExtras=1</c>.</summary>
    [JsonPropertyName("Extras")]
    public ExtrasList? Extras { get; init; }

    /// <summary>Critics' reviews, sent with the item when it is asked for with <c>includeReviews=1</c>.</summary>
    [JsonPropertyName("Review")]
    public List<Review>? Review { get; init; }
}

public sealed partial class MediaContainer
{
    /// <summary>What Discover's <c>/library/metadata/{id}/userState</c> answers with.</summary>
    [JsonPropertyName("UserState")]
    public List<UserState>? UserState { get; init; }
}

/// <summary>The extras an item carries, as its metadata nests them.</summary>
public sealed class ExtrasList
{
    [JsonPropertyName("size")]
    public int Size { get; init; }

    /// <summary>Each extra: a <c>clip</c> whose <c>subtype</c> says what kind (<c>trailer</c>, <c>behindTheScenes</c>).</summary>
    [JsonPropertyName("Metadata")]
    public List<MetadataItem>? Metadata { get; init; }
}

/// <summary>A critic's review of a film or a series, as Plex's metadata provider supplies it.</summary>
public sealed class Review
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>The critic.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>The verdict as an image name: <c>rottentomatoes://image.review.fresh</c> or <c>….rotten</c>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The whole review on the publication's own site.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>The publication.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary>The signed-in account's state for one title of Plex's catalogue.</summary>
public sealed class UserState
{
    [JsonPropertyName("ratingKey")]
    public string? RatingKey { get; init; }

    /// <summary>When the title went on the Watchlist, in Unix seconds; absent when it is not on it.</summary>
    [JsonPropertyName("watchlistedAt")]
    public long? WatchlistedAt { get; init; }
}
