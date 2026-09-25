using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>A playlist as a square tile: its mosaic, its name, how long it runs.</summary>
public sealed partial class PlaylistTileViewModel(ShellViewModel shell, MetadataItem playlist)
{
    public MetadataItem Playlist { get; } = playlist;

    public string Title => Playlist.Title;

    public string Caption => Playlists.Describe(Playlist);

    public string? CoverPath => Playlist.Composite ?? Playlist.Thumb;

    public bool IsSmart => Playlist.Smart;

    public string PlayTip => $"Play {Playlist.Title}";

    [RelayCommand]
    private void Open() => shell.OpenItem(Playlist);

    [RelayCommand]
    private Task PlayAsync() => Playlists.PlayAsync(shell, Playlist, shuffle: false);
}

/// <summary>Every playlist of the signed-in viewer, music and video alike.</summary>
public sealed partial class PlaylistsPageViewModel(ShellViewModel shell, ServerSession session) : PageViewModel, IGridPage
{
    public override string Title => "Playlists";

    public TileGrid Grid { get; } = new(TileShape.Square);

    public ObservableCollection<GridRowViewModel> Rows => Grid.Rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string CountText { get; private set; } = string.Empty;

    public bool IsEmpty => !IsLoading && Grid.Count == 0 && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    public void Fit(double width) => Grid.Fit(width);

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var playlists = await Task.Run(() => session.Client.GetPlaylistsAsync(cancellation), cancellation);
        Grid.Clear();
        Grid.Add(playlists.OrderBy(p => p.Smart).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Select(p => new PlaylistTileViewModel(shell, p)));
        CountText = playlists.Count == 1 ? "1 playlist" : $"{playlists.Count} playlists";
    }
}

/// <summary>A playlist: its items in order, paged in as they arrive, to play from any of them.</summary>
public sealed partial class PlaylistPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem playlist) : PageViewModel
{
    private const int PageSize = 500;
    private readonly List<MetadataItem> _items = [];

    public override string Title => Playlist.Title;

    public MetadataItem Playlist { get; } = playlist;

    public string Heading => Renamed ?? Playlist.Title;

    public string Kicker => (Playlist.Smart ? "SMART " : string.Empty) + (Playlist.PlaylistType == "audio" ? "MUSIC PLAYLIST" : "PLAYLIST");

    public string Meta => Playlists.Describe(Playlist);

    public string? CoverPath => Playlist.Composite ?? Playlist.Thumb;

    public bool IsMusic => Playlist.PlaylistType == "audio";

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];

    public ObservableCollection<PlaylistVideoRowViewModel> Videos { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    [RelayCommand]
    private Task Play() => Playlists.PlayAsync(shell, Playlist, shuffle: false, _items);

    [RelayCommand]
    private Task Shuffle() => Playlists.PlayAsync(shell, Playlist, shuffle: true, _items);

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        IsLoadingMore = true;
        try
        {
            var start = 0;
            while (true)
            {
                var from = start;
                var page = await Task.Run(() => session.Client.GetPlaylistItemsAsync(Playlist.RatingKey, from, PageSize, cancellation), cancellation);
                var items = page.Metadata ?? [];
                foreach (var item in items) Add(item);
                start += items.Count;
                if (items.Count == 0 || start >= (page.TotalSize ?? start)) break;
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void Add(MetadataItem item)
    {
        var index = _items.Count;
        _items.Add(item);
        if (item.Type == "track")
        {
            var line = string.Join(" — ", new[] { item.OriginalTitle ?? item.GrandparentTitle, item.ParentTitle }.Where(s => !string.IsNullOrEmpty(s)));
            Tracks.Add(new TrackRowViewModel(shell, item, index, line, PlayFromAsync, numberFromTrack: false));
        }
        else
        {
            Videos.Add(new PlaylistVideoRowViewModel(shell, item, index));
        }
    }

    private Task PlayFromAsync(int index) =>
        shell.Music is { } music ? music.PlayAsync([.. _items.Where(i => i.Type == "track")], index) : Task.CompletedTask;
}

/// <summary>A film or an episode in a playlist.</summary>
public sealed partial class PlaylistVideoRowViewModel(ShellViewModel shell, MetadataItem item, int index)
{
    public MetadataItem Item { get; } = item;

    public string Number { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    public string Heading => Item.Type == "episode" ? $"{Item.GrandparentTitle} · {Format.EpisodeCode(Item)}" : Item.Title;

    public string Detail => Item.Type == "episode" ? Item.Title : Item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string? ThumbPath => Artwork.Still(Item);

    public string Runtime => Format.Runtime(Item.Duration);

    [RelayCommand]
    private void Open() => shell.OpenItem(Item);

    [RelayCommand]
    private void Play() => shell.Play(Item, resume: true);
}

internal static class Playlists
{
    public static string Describe(MetadataItem playlist)
    {
        var count = playlist.LeafCount ?? 0;
        var noun = playlist.PlaylistType == "audio" ? (count == 1 ? "track" : "tracks") : (count == 1 ? "item" : "items");
        var length = Format.Runtime(playlist.Duration);
        return string.Join("  ·  ", new[] { string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}"), length }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Music plays in the music player; a video playlist plays through, in order or shuffled.</summary>
    public static async Task PlayAsync(ShellViewModel shell, MetadataItem playlist, bool shuffle, IReadOnlyList<MetadataItem>? loaded = null)
    {
        if (shell.Session is not { } session) return;
        var items = loaded is { Count: > 0 }
            ? loaded
            : (await Task.Run(() => session.Client.GetPlaylistItemsAsync(playlist.RatingKey, 0, 1000, CancellationToken.None))).Metadata ?? [];
        if (playlist.PlaylistType == "audio")
        {
            if (shell.Music is { } music) await music.PlayAsync([.. items.Where(i => i.Type == "track")], 0, shuffle);
        }
        else if (VideoQueue.Of(items, shuffle) is { } queue)
        {
            shell.Play(queue.Current, resume: !shuffle, queue);
        }
    }
}
