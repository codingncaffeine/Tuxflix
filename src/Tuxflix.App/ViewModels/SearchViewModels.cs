using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// What the title bar's search found, across every library: shelves of films, series, episodes,
/// music, collections and people, and the tracks as a list to play from. It follows the search
/// box as it is typed into.
/// </summary>
public sealed partial class SearchPageViewModel(ShellViewModel shell, ServerSession session) : PageViewModel
{
    private const int PerKind = 16;

    /// <summary>The order Plex's own apps put search results in.</summary>
    private static readonly string[] Order = ["movie", "show", "episode", "collection", "artist", "album", "actor", "director"];

    private CancellationTokenSource? _searching;

    public override string Title => "Search";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(IsBlank))]
    public partial string Query { get; private set; } = string.Empty;

    public string Heading => Query.Length == 0 ? "SEARCH" : $"RESULTS FOR “{Query.ToUpperInvariant()}”";

    public ObservableCollection<ShelfViewModel> Shelves { get; } = [];

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];

    public bool HasTracks => Tracks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNothing), nameof(NothingText))]
    public partial bool IsSearching { get; private set; }

    /// <summary>A search ran and found nothing at all.</summary>
    public bool IsNothing => !IsSearching && Query.Length > 0 && Shelves.Count == 0 && Tracks.Count == 0 && !HasError;

    /// <summary>What a search that found nothing says, with what was asked.</summary>
    public string NothingText => $"Nothing in your libraries matches “{Query}”.";

    public bool IsBlank => Query.Length == 0;

    /// <summary>TRY AGAIN after a failed search runs the search again.</summary>
    protected override Task RetryCoreAsync() => SearchAsync(Query);

    /// <summary>Runs a search, replacing one still running.</summary>
    public async Task SearchAsync(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _searching?.Cancel();
        var searching = _searching = new CancellationTokenSource();
        Query = query.Trim();
        if (Query.Length == 0)
        {
            Shelves.Clear();
            Tracks.Clear();
            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(IsNothing));
            return;
        }

        IsSearching = true;
        ErrorMessage = null;
        try
        {
            var hubs = await Task.Run(() => session.Client.SearchAsync(Query, PerKind, searching.Token), searching.Token);
            if (searching.IsCancellationRequested) return;
            Show(hubs);
        }
        catch (OperationCanceledException) when (searching.IsCancellationRequested)
        {
            // A newer search took over.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = "The server could not be searched. The log has the details.";
            Log.Warn("A search failed.", ex);
        }
        finally
        {
            if (ReferenceEquals(searching, _searching)) IsSearching = false;
            OnPropertyChanged(nameof(IsNothing));
        }
    }

    public override void Deactivate()
    {
        _searching?.Cancel();
        base.Deactivate();
    }

    private void Show(IReadOnlyList<Hub> hubs)
    {
        Shelves.Clear();
        foreach (var hub in hubs.OrderBy(h => Array.IndexOf(Order, h.Type) is var at && at < 0 ? Order.Length : at))
        {
            if (hub.Type is "actor" or "director")
            {
                var people = (hub.Directory ?? []).Where(t => t.Id is not null).GroupBy(t => t.Id).Select(g => new PersonTileViewModel(shell, g.First(), g.Sum(t => t.Count ?? 0))).ToList();
                if (people.Count > 0) Shelves.Add(new ShelfViewModel(hub.Title, people));
                continue;
            }

            var items = (hub.Metadata ?? []).Where(i => i.Type != "track").ToList();
            if (items.Count > 0) Shelves.Add(new ShelfViewModel(hub.Title, items.Select(i => Tiles.For(shell, i))));
        }

        var tracks = hubs.Where(h => h.Type == "track").SelectMany(h => h.Metadata ?? []).ToList();
        Tracks.Clear();
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var line = string.Join(" — ", new[] { track.GrandparentTitle, track.ParentTitle }.Where(s => !string.IsNullOrEmpty(s)));
            Tracks.Add(new TrackRowViewModel(shell, track, i, line, index => PlayTracksAsync(tracks, index), numberFromTrack: false));
        }

        OnPropertyChanged(nameof(HasTracks));
    }

    private Task PlayTracksAsync(IReadOnlyList<MetadataItem> tracks, int index) =>
        shell.Music is { } music ? music.PlayAsync(tracks, index) : Task.CompletedTask;
}

/// <summary>A person on a shelf: a round portrait, their name, how many titles they are in.</summary>
public sealed partial class PersonTileViewModel(ShellViewModel shell, TagEntry person, int count)
{
    public string Name { get; } = person.Tag;

    public string? ThumbPath { get; } = person.Thumb;

    public string Caption { get; } = count switch
    {
        0 => string.Empty,
        1 => "1 title",
        _ => $"{count} titles",
    };

    public string Initials { get; } = string.Concat(person.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    [RelayCommand]
    private void Open()
    {
        if (person.Id is { } id) shell.OpenPerson(Name, ThumbPath, id);
    }
}

/// <summary>
/// Everything one person is in, across the video libraries: as an actor, and as a director.
/// </summary>
public sealed partial class PersonPageViewModel(ShellViewModel shell, ServerSession session, string name, string? thumb, long tagId) : PageViewModel, IGridPage
{
    public override string Title => name;

    public string Name => name;

    public string? ThumbPath => thumb;

    public string Initials { get; } = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public TileGrid Grid { get; } = new(TileShape.Poster);

    public ObservableCollection<GridRowViewModel> Rows => Grid.Rows;

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    public void Fit(double width, double scale) => Grid.Fit(width, scale);

    public bool IsEmpty => !IsLoading && Grid.Count == 0 && !HasError;

    public string EmptyText => $"Nothing on this server features {Name}.";

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var sections = await Task.Run(() => session.Client.GetSectionsAsync(cancellation), cancellation);
        var lookups = sections.Where(s => s.Type is "movie" or "show")
            .SelectMany(s => new[] { "actor", "director" }.Select(role => Task.Run(() => TaggedOrNone(s.Key, role, cancellation), cancellation)))
            .ToList();
        var found = (await Task.WhenAll(lookups)).SelectMany(items => items)
            .GroupBy(i => i.RatingKey, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(i => i.Year ?? 0).ThenBy(i => i.TitleSort ?? i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Grid.Clear();
        Grid.Add(found.Select(i => Tiles.For(shell, i)));
        CountText = found.Count switch
        {
            0 => "Nothing in your libraries",
            1 => "In 1 title in your libraries",
            _ => string.Create(CultureInfo.CurrentCulture, $"In {found.Count} titles in your libraries"),
        };
    }

    private async Task<IReadOnlyList<MetadataItem>> TaggedOrNone(string section, string role, CancellationToken cancellation)
    {
        try
        {
            return await session.Client.GetTaggedAsync(section, role, tagId, cancellation);
        }
        catch (HttpRequestException ex)
        {
            // A library without that filter (a music library, an older server) has nothing to add.
            Log.Info($"{name}: section {section} gave no {role} listing ({ex.Message}).");
            return [];
        }
    }
}
