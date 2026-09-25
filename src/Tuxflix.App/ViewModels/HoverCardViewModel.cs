using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// What a title's card says when the pointer rests on its tile, as Steam's library shows a game's:
/// its art, what it is, what it is about, and how far the viewer got. Made from what the tile
/// already has; nothing is asked of the server.
/// </summary>
public sealed class HoverCardViewModel(MediaTileViewModel tile)
{
    private MetadataItem Item => tile.Item;

    private ItemState State => tile.State;

    private bool IsEpisode => Item.Type == "episode";

    /// <summary>The art along the top: the episode's own still, else the title's backdrop.</summary>
    public string? ArtPath => IsEpisode ? Artwork.Still(Item) : Artwork.Backdrop(Item) ?? Artwork.Still(Item);

    /// <summary>An episode's series, a season's series: the line above the title.</summary>
    public string? Kicker => Item.Type switch
    {
        "episode" => Item.GrandparentTitle?.ToUpperInvariant(),
        "season" => Item.ParentTitle?.ToUpperInvariant(),
        _ => null,
    };

    public bool HasKicker => !string.IsNullOrEmpty(Kicker);

    public string Title => IsEpisode && Format.EpisodeCode(Item) is { Length: > 0 } code ? $"{code} · {Item.Title}" : Item.Title;

    /// <summary>"2019 · 1h 57m · Drama, Romance · PG-13 · ★ 7.4".</summary>
    public string Meta => string.Join("  ·  ", new[]
    {
        Item.Type == "episode" ? null : Item.Year?.ToString(CultureInfo.InvariantCulture),
        Item.Type switch
        {
            "show" when Item.ChildCount is { } seasons => seasons == 1 ? "1 season" : string.Create(CultureInfo.InvariantCulture, $"{seasons} seasons"),
            "season" when Item.LeafCount is { } episodes => episodes == 1 ? "1 episode" : string.Create(CultureInfo.InvariantCulture, $"{episodes} episodes"),
            "collection" when Item.ChildCount is { } count => count == 1 ? "1 title" : string.Create(CultureInfo.InvariantCulture, $"{count} titles"),
            _ => Format.Runtime(Item.Duration),
        },
        Item.Genre is { Count: > 0 } genres ? string.Join(", ", genres.Take(2).Select(g => g.TagText)) : null,
        Item.ContentRating,
        Item.AudienceRating is { } rating ? "★ " + rating.ToString("0.0", CultureInfo.InvariantCulture) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>"4K · HEVC · TrueHD 7.1": the file, for a film or an episode.</summary>
    public string Quality => Item.Type is "movie" or "episode" ? Format.MediaSummary(Item.Media?.FirstOrDefault()) : string.Empty;

    public bool HasQuality => Quality.Length > 0;

    public string? Summary => string.IsNullOrWhiteSpace(Item.Summary) ? null : Item.Summary.Trim();

    public bool HasSummary => Summary is not null;

    public bool HasProgress => State.HasProgress;

    public double Progress => State.Progress ?? 0;

    /// <summary>
    /// How far the viewer got: time left, the unwatched episodes, watched and when, or new to the
    /// library; nothing for a title never started that is not new.
    /// </summary>
    public string? Status
    {
        get
        {
            if (State is { HasProgress: true, Duration: { } duration, ViewOffset: { } offset })
            {
                return $"{Format.Runtime(duration - offset)} left";
            }

            if (Item.Type is "show" or "season" && State.UnwatchedLeaves > 0 && !State.IsUnplayed)
            {
                return State.UnwatchedLeaves == 1 ? "1 episode to watch" : string.Create(CultureInfo.InvariantCulture, $"{State.UnwatchedLeaves} episodes to watch");
            }

            if (State.IsWatched) return State.LastViewedAt is { } seen ? $"Watched · {Format.WhenWatched(seen)}" : "Watched";
            return tile.IsNew ? "New to the library" : null;
        }
    }

    public bool HasStatus => Status is not null;
}
