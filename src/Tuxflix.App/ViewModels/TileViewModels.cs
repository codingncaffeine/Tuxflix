using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>One piece of artwork on a shelf or in a grid, and what clicking it does.</summary>
public abstract partial class MediaTileViewModel(ShellViewModel shell, MetadataItem item)
{
    public MetadataItem Item { get; } = item;

    /// <summary>Episodes are shown under their show's name; everything else under its own.</summary>
    public string Title => Item.Type == "episode" ? Item.GrandparentTitle ?? Item.Title : Item.Title;

    public abstract string? ImagePath { get; }

    public abstract string Subtitle { get; }

    public double Progress => Item.Progress ?? 0;

    public bool HasProgress => Item.Progress is > 0 and < 1;

    public bool IsWatched => Item.IsWatched && !HasProgress;

    public int UnwatchedCount => Item.Type is "show" or "season" ? Item.UnwatchedLeaves : 0;

    public bool HasUnwatchedCount => UnwatchedCount > 0 && !IsWatched;

    public string UnwatchedText => UnwatchedCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>Added in the last fortnight and never started: Steam's "new to library".</summary>
    public bool IsNew => Item.AddedAt is { } added
                         && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(added) < TimeSpan.FromDays(14)
                         && Item.ViewCount is null or 0
                         && Item.ViewedLeafCount is null or 0
                         && !HasProgress;

    /// <summary>What a screen reader says for the tile.</summary>
    public string AccessibleName => string.IsNullOrEmpty(Subtitle) ? Title : $"{Title}, {Subtitle}";

    [RelayCommand]
    private void Open() => shell.OpenItem(Item);

    /// <summary>Straight into the player, from where it was left.</summary>
    [RelayCommand]
    private void Play() => shell.Play(Item, resume: true);
}

/// <summary>A 2:3 poster, the shape of a Steam capsule.</summary>
public sealed class PosterTileViewModel(ShellViewModel shell, MetadataItem item) : MediaTileViewModel(shell, item)
{
    public override string? ImagePath => Artwork.Poster(Item);

    public override string Subtitle => Item.Type switch
    {
        "show" when Item.ChildCount is { } seasons => seasons == 1 ? "1 season" : $"{seasons} seasons",
        "season" => Item.ParentTitle ?? string.Empty,
        "episode" => Format.EpisodeCode(Item),
        "collection" => Item.ChildCount is { } count ? (count == 1 ? "1 title" : $"{count} titles") : "Collection",
        _ => Item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>A 16:9 card with the title laid over the artwork: Continue Watching.</summary>
public sealed class LandscapeTileViewModel(ShellViewModel shell, MetadataItem item) : MediaTileViewModel(shell, item)
{
    public override string? ImagePath => Artwork.Still(Item);

    public override string Subtitle => Item.Type == "episode"
        ? $"{Format.EpisodeCode(Item)}  ·  {Item.Title}"
        : Item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string RemainingText => Format.Remaining(Item);
}

/// <summary>A titled row of tiles, of any kind.</summary>
public sealed partial class ShelfViewModel(string title, IEnumerable<object> tiles)
{
    public string Title { get; } = title.ToUpperInvariant();

    public ObservableCollection<object> Tiles { get; } = new(tiles);

    public string CountText => Tiles.Count.ToString(CultureInfo.InvariantCulture);

    public bool IsLandscape => Tiles.Count > 0 && Tiles[0] is LandscapeTileViewModel;
}
