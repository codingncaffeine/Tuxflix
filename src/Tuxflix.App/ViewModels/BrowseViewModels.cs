using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>The shape of a grid's tiles, which sets how many fit across.</summary>
public enum TileShape
{
    /// <summary>A 2:3 poster: films, series, collections.</summary>
    Poster,

    /// <summary>A square cover: artists, albums, playlists.</summary>
    Square,

    /// <summary>A 16:9 still: episodes.</summary>
    Landscape,
}

/// <summary>A page whose body is a grid of tiles.</summary>
public interface IGridPage
{
    ObservableCollection<GridRowViewModel> Rows { get; }

    /// <summary>The grid's width or its tiles' size changed: as many columns as fit.</summary>
    /// <param name="scale">The tiles' size against their usual one (the grid size slider).</param>
    void Fit(double width, double scale);
}

/// <summary>The size of each shape of tile at the usual grid size; the grid size slider scales them.</summary>
public static class TileSizes
{
    public const double PosterWidth = 164;

    public const double PosterHeight = 246;

    public const double LandscapeWidth = 344;

    public const double LandscapeHeight = 194;

    public const double Square = 176;

    /// <summary>The smallest and largest the slider goes, and its steps between.</summary>
    public const double Smallest = 0.75;

    public const double Largest = 1.5;

    public const double Step = 0.125;

    /// <summary>A scale the slider can take: within its ends, on one of its steps.</summary>
    public static double Clamp(double scale) =>
        double.IsFinite(scale) ? Math.Round(Math.Clamp(scale, Smallest, Largest) / Step) * Step : 1;
}

/// <summary>One row of a grid: as many tiles as fit across the page.</summary>
public sealed class GridRowViewModel(IReadOnlyList<object> tiles, double spacing)
{
    public IReadOnlyList<object> Tiles { get; } = tiles;

    public double Spacing { get; } = spacing;

    /// <summary>The gap under the row; square tiles carry their own.</summary>
    public Avalonia.Thickness Gap { get; } = new(0, 0, 0, spacing);
}

/// <summary>
/// Tiles laid out in rows as wide as the page. The rows scroll in a virtualising panel, so only
/// the rows on screen have controls, and a library of thousands scrolls as lightly as one of ten.
/// New tiles complete the last row and add rows after it; nothing already on screen is rebuilt.
/// </summary>
public sealed class TileGrid(TileShape shape)
{
    private readonly List<object> _tiles = [];

    public ObservableCollection<GridRowViewModel> Rows { get; } = [];

    public TileShape Shape { get; } = shape;

    /// <summary>Square tiles carry their own margins; the others are spaced by the row.</summary>
    public double Spacing => Shape == TileShape.Square ? 0 : 18;

    /// <summary>The tiles' size against their usual one: the grid size slider.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>The width one column takes: a tile at the grid's size and the space after it.</summary>
    public double CellWidth => Shape switch
    {
        TileShape.Square => (TileSizes.Square * Scale) + 22,
        TileShape.Landscape => (TileSizes.LandscapeWidth * Scale) + 18,
        _ => (TileSizes.PosterWidth * Scale) + 18,
    };

    public int Columns { get; private set; } = 6;

    public int Count => _tiles.Count;

    public IReadOnlyList<object> Tiles => _tiles;

    /// <summary>Fits the columns to the width the grid has at the tiles' size; rows are laid out again only when the count changes.</summary>
    public void Fit(double width, double scale = 1)
    {
        Scale = scale;
        var columns = Math.Max(1, (int)((width + Spacing + (Shape == TileShape.Square ? 22 : 0)) / CellWidth));
        if (columns == Columns) return;
        Columns = columns;
        Rows.Clear();
        for (var start = 0; start < _tiles.Count; start += Columns) Rows.Add(Row(start));
    }

    public void Clear()
    {
        _tiles.Clear();
        Rows.Clear();
    }

    public void Add(IEnumerable<object> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var first = _tiles.Count;
        _tiles.AddRange(tiles);
        if (_tiles.Count == first) return;

        // A partial last row is replaced by a fuller one; everything above it stays.
        var start = first - (first % Columns);
        if (start < first) Rows[^1] = Row(start);
        for (start = start < first ? start + Columns : start; start < _tiles.Count; start += Columns) Rows.Add(Row(start));
    }

    /// <summary>The row a tile is in.</summary>
    public int RowOf(int index) => index / Columns;

    private GridRowViewModel Row(int start) => new([.. _tiles.Skip(start).Take(Columns)], Spacing);
}

/// <summary>The tile for an item, by its kind.</summary>
public static class Tiles
{
    public static object For(ShellViewModel shell, MetadataItem item) => item.Type switch
    {
        "artist" or "album" => new AlbumTileViewModel(shell, item),
        "playlist" => new PlaylistTileViewModel(shell, item),
        "episode" or "clip" => new LandscapeTileViewModel(shell, item),
        _ => new PosterTileViewModel(shell, item),
    };

    public static TileShape ShapeOf(string? type) => type switch
    {
        "artist" or "album" or "playlist" => TileShape.Square,
        "episode" or "clip" => TileShape.Landscape,
        _ => TileShape.Poster,
    };
}

/// <summary>What a library page lists: its titles, its collections, or (music) its albums.</summary>
public enum LibraryView
{
    Titles,
    Albums,
    Collections,
}

/// <summary>
/// One library as a grid of its titles: sorted and filtered the ways the server offers, a letter
/// strip to jump through a title sort, and its collections beside it. Steam's library grid.
/// </summary>
/// <remarks>
/// The first page arrives quickly and the rest follow in larger pages while the first is on
/// screen; changing the sort, a filter or the view cancels a listing still arriving.
/// </remarks>
public sealed partial class LibraryPageViewModel : PageViewModel, IGridPage
{
    private const int FirstPage = 200;
    private const int NextPage = 500;

    /// <summary>The list filters worth a menu, in the order shown; a server offering others keeps them to itself.</summary>
    private static readonly string[] ListFilters = ["genre", "decade", "contentRating", "resolution"];

    private static readonly string[] ToggleFilters = ["unwatched", "inProgress", "hdr", "dovi", "atmos"];

    private readonly ShellViewModel _shell;
    private readonly ServerSession _session;
    private readonly List<MetadataItem> _items = [];
    private CancellationTokenSource? _listing;
    private double _width;
    private double _scale = 1;

    public LibraryPageViewModel(ShellViewModel shell, ServerSession session, LibraryDirectory section)
    {
        _shell = shell;
        _session = session;
        Section = section;
        IsMusic = section.Type == "artist";
        Grid = new TileGrid(IsMusic ? TileShape.Square : TileShape.Poster);
    }

    public LibraryDirectory Section { get; }

    public override string Title => Section.Title;

    public string Heading => Section.Title.ToUpperInvariant();

    public bool IsMusic { get; }

    public TileGrid Grid { get; private set; }

    /// <summary>The page's rows, for the view: a new grid (another view) is a new collection.</summary>
    public ObservableCollection<GridRowViewModel> Rows => Grid.Rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int TotalCount { get; private set; }

    public string CountText => TotalCount.ToString("N0", CultureInfo.CurrentCulture) + " " + (View switch
    {
        LibraryView.Collections => TotalCount == 1 ? "collection" : "collections",
        LibraryView.Albums => TotalCount == 1 ? "album" : "albums",
        _ when IsMusic => TotalCount == 1 ? "artist" : "artists",
        _ when Section.Type == "show" => TotalCount == 1 ? "series" : "series",
        _ => TotalCount == 1 ? "film" : "films",
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoadingMore { get; private set; }

    public bool IsEmpty => !IsLoading && !IsLoadingMore && Grid.Count == 0 && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    public string EmptyText => ActiveFilters.Count > 0 ? "Nothing here matches these filters." : "This library is empty. Titles appear here as the server adds them.";

    /// <summary>What the empty grid offers: taking the filters off, when they are why it is empty.</summary>
    public string? EmptyAction => ActiveFilters.Count > 0 ? "CLEAR FILTERS" : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTitles), nameof(ShowsAlbums), nameof(ShowsCollections), nameof(CountText), nameof(HasSorts), nameof(HasFilters), nameof(ShowsLetters))]
    public partial LibraryView View { get; private set; } = LibraryView.Titles;

    public bool ShowsTitles => View == LibraryView.Titles;

    public bool ShowsAlbums => View == LibraryView.Albums;

    public bool ShowsCollections => View == LibraryView.Collections;

    public string TitlesLabel => IsMusic ? "ARTISTS" : "LIBRARY";

    [ObservableProperty]
    public partial bool HasCollections { get; private set; }

    /// <summary>More than one view to choose from: the tabs show.</summary>
    public bool HasViews => IsMusic || HasCollections;

    partial void OnHasCollectionsChanged(bool value) => OnPropertyChanged(nameof(HasViews));

    public ObservableCollection<SortOptionViewModel> Sorts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(ShowsLetters))]
    public partial SortOptionViewModel? Sort { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionTip))]
    public partial bool Descending { get; private set; }

    public string SortLabel => (Sort?.Title ?? "Title").ToUpperInvariant();

    public string DirectionTip => Descending ? "Descending: tap for ascending" : "Ascending: tap for descending";

    public bool HasSorts => Sorts.Count > 0 && View != LibraryView.Collections;

    public ObservableCollection<FilterViewModel> Filters { get; } = [];

    public ObservableCollection<ActiveFilterViewModel> ActiveFilters { get; } = [];

    public bool HasFilters => Filters.Count > 0 && View == LibraryView.Titles;

    public bool HasActiveFilters => ActiveFilters.Count > 0;

    public ObservableCollection<LetterViewModel> Letters { get; } = [];

    public bool ShowsLetters => Letters.Count > 1 && Sort?.Key == "titleSort" && View != LibraryView.Collections;

    /// <summary>The view scrolls this row into sight: a letter was chosen.</summary>
    public event Action<int>? ScrollToRow;

    /// <summary>The page's width or the tiles' size changed: as many columns as fit.</summary>
    public void Fit(double width, double scale)
    {
        _width = width;
        _scale = scale;
        Grid.Fit(width, scale);
    }

    /// <summary>The tiles' size in every library grid: the slider beside the sort.</summary>
    public double GridScale
    {
        get => _shell.GridScale;
        set
        {
            _shell.GridScale = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private Task ShowView(LibraryView view)
    {
        if (View == view) return Task.CompletedTask;
        View = view;
        return ListSafelyAsync();
    }

    internal Task ChooseSortAsync(SortOptionViewModel sort)
    {
        if (ReferenceEquals(Sort, sort))
        {
            Descending = !Descending;
        }
        else
        {
            if (Sort is not null) Sort.IsSelected = false;
            Sort = sort;
            sort.IsSelected = true;
            Descending = sort.DefaultDescending;
        }

        return ListSafelyAsync();
    }

    [RelayCommand]
    private Task ToggleDirection()
    {
        Descending = !Descending;
        return ListSafelyAsync();
    }

    internal Task SetFilterAsync(FilterViewModel filter, FilterValueViewModel? value)
    {
        foreach (var other in filter.Values) other.IsSelected = ReferenceEquals(other, value);
        filter.IsOn = value is not null;
        var existing = ActiveFilters.FirstOrDefault(a => a.Filter == filter.Filter);
        if (existing is not null) ActiveFilters.Remove(existing);
        if (value is not null) ActiveFilters.Add(new ActiveFilterViewModel(this, filter, value.Key, filter.IsToggle ? filter.Title : $"{filter.Title}: {value.Title}"));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyAction));
        return ListSafelyAsync();
    }

    internal Task RemoveFilterAsync(ActiveFilterViewModel active) => SetFilterAsync(active.Owner, null);

    [RelayCommand]
    private Task ClearFilters()
    {
        foreach (var filter in Filters)
        {
            filter.IsOn = false;
            foreach (var value in filter.Values) value.IsSelected = false;
        }

        ActiveFilters.Clear();
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyAction));
        return ListSafelyAsync();
    }

    internal void Jump(LetterViewModel letter) => ScrollToRow?.Invoke(Grid.RowOf(letter.Index));

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var sorts = Task.Run(() => _session.Client.GetSortsAsync(Section.Key, cancellation), cancellation);
        var filters = Task.Run(() => _session.Client.GetFiltersAsync(Section.Key, cancellation), cancellation);
        var collections = IsMusic ? Task.FromResult<IReadOnlyList<MetadataItem>>([]) : Task.Run(() => _session.Client.GetCollectionsAsync(Section.Key, cancellation), cancellation);

        foreach (var sort in await Optional(sorts, "sorts"))
        {
            if (sort.Key == "random") continue;
            Sorts.Add(new SortOptionViewModel(this, sort));
        }

        Sort = Sorts.FirstOrDefault(s => s.Key == "titleSort") ?? Sorts.FirstOrDefault();
        if (Sort is not null) Sort.IsSelected = true;
        Descending = false;
        OnPropertyChanged(nameof(HasSorts));

        var offered = await Optional(filters, "filters");
        foreach (var name in ToggleFilters.Concat(ListFilters))
        {
            if (offered.FirstOrDefault(f => f.Filter == name) is { } filter) Filters.Add(new FilterViewModel(this, filter));
        }

        OnPropertyChanged(nameof(HasFilters));
        HasCollections = (await Optional(collections, "collections")).Count > 0;

        // The values of the list filters: a handful of small listings, side by side.
        await Task.WhenAll(Filters.Where(f => !f.IsToggle).Select(f => f.LoadValuesAsync(_session, Section.Key, cancellation)));

        await ListAsync();
    }

    public override void Deactivate()
    {
        _listing?.Cancel();
        base.Deactivate();
    }

    /// <summary>A listing that failed when asked for again says so on the page, not only in the log.</summary>
    private async Task ListSafelyAsync()
    {
        try
        {
            ErrorMessage = null;
            await ListAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            ErrorMessage = "This listing could not be loaded. The log has the details.";
            Log.Warn($"{Title}: the listing failed.", ex);
        }
    }

    private async Task ListAsync()
    {
        _listing?.Cancel();
        var listing = _listing = new CancellationTokenSource();
        var token = listing.Token;

        // A view of another shape is a new grid; the same shape keeps its columns.
        var shape = View == LibraryView.Collections ? TileShape.Poster : IsMusic ? TileShape.Square : TileShape.Poster;
        if (Grid.Shape != shape)
        {
            Grid = new TileGrid(shape);
            if (_width > 0) Grid.Fit(_width, _scale);
            OnPropertyChanged(nameof(Grid));
            OnPropertyChanged(nameof(Rows));
        }

        Grid.Clear();
        _items.Clear();
        Letters.Clear();
        OnPropertyChanged(nameof(ShowsLetters));
        TotalCount = 0;
        IsLoadingMore = true;
        try
        {
            if (View == LibraryView.Collections)
            {
                var collections = await Task.Run(() => _session.Client.GetCollectionsAsync(Section.Key, token), token);
                if (token.IsCancellationRequested) return;
                Show(collections, collections.Count);
                return;
            }

            var query = Query();
            var start = 0;
            var size = FirstPage;
            while (true)
            {
                var from = start;
                var count = size;
                var page = await Task.Run(() => _session.Client.BrowseAsync(Section.Key, query, from, count, token), token);
                if (token.IsCancellationRequested) return;
                var items = page.Metadata ?? [];
                Show(items, page.TotalSize ?? start + items.Count);
                start += items.Count;
                if (items.Count == 0 || start >= TotalCount) break;
                size = NextPage;
            }

            BuildLetters();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced by a newer listing.
        }
        finally
        {
            if (ReferenceEquals(listing, _listing)) IsLoadingMore = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void Show(IReadOnlyList<MetadataItem> items, int total)
    {
        _items.AddRange(items);
        TotalCount = total;
        Grid.Add(items.Select(i => Tiles.For(_shell, i)));
    }

    /// <summary>The query a listing sends: the type for albums, the sort and every active filter.</summary>
    internal string Query()
    {
        var parts = new List<string>();
        if (View == LibraryView.Albums) parts.Add("type=9");
        var sort = Sort is { } s ? (Descending ? s.DescKey : s.Key) : "titleSort";
        parts.Add("sort=" + Uri.EscapeDataString(sort));
        parts.AddRange(ActiveFilters.Select(a => $"{Uri.EscapeDataString(a.Filter)}={Uri.EscapeDataString(a.Value)}"));
        return string.Join('&', parts);
    }

    /// <summary>A letter for each initial the titles start with, pointing at its first title.</summary>
    private void BuildLetters()
    {
        Letters.Clear();
        if (Sort?.Key != "titleSort") return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _items.Count; i++)
        {
            var title = _items[i].TitleSort ?? _items[i].Title;
            var initial = title.Length == 0 || !char.IsLetter(title[0]) ? "#" : char.ToUpperInvariant(title[0]).ToString();
            if (seen.Add(initial)) Letters.Add(new LetterViewModel(this, initial, i));
        }

        OnPropertyChanged(nameof(ShowsLetters));
    }

    private async Task<IReadOnlyList<T>> Optional<T>(Task<IReadOnlyList<T>> task, string what)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // An older server without the listing still lists its titles.
            Log.Info($"{Title}: the server gave no {what} ({ex.Message}).");
            return [];
        }
    }
}

/// <summary>An order a library can be listed in.</summary>
public sealed partial class SortOptionViewModel(LibraryPageViewModel page, LibraryDirectory sort) : ObservableObject
{
    public string Key { get; } = sort.Key;

    public string DescKey { get; } = sort.DescKey ?? sort.Key + ":desc";

    public string Title { get; } = sort.Title;

    public bool DefaultDescending { get; } = sort.DefaultDirection == "desc";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task Choose() => page.ChooseSortAsync(this);
}

/// <summary>A filter the server offers: a switch (unwatched) or a list of values (genre).</summary>
public sealed partial class FilterViewModel(LibraryPageViewModel page, LibraryDirectory filter) : ObservableObject
{
    public string Filter { get; } = filter.Filter ?? string.Empty;

    public string Title { get; } = filter.Title;

    public bool IsToggle { get; } = filter.FilterType == "boolean";

    public ObservableCollection<FilterValueViewModel> Values { get; } = [];

    public bool HasValues => Values.Count > 0;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [RelayCommand]
    private Task Toggle() => page.SetFilterAsync(this, IsOn ? null : new FilterValueViewModel(this, "1", Title));

    internal Task Choose(FilterValueViewModel value) => page.SetFilterAsync(this, value.IsSelected ? null : value);

    internal async Task LoadValuesAsync(ServerSession session, string sectionKey, CancellationToken cancellation)
    {
        try
        {
            var values = await Task.Run(() => session.Client.GetFilterValuesAsync(sectionKey, Filter, cancellation), cancellation);
            foreach (var value in values) Values.Add(new FilterValueViewModel(this, value.Key, value.Title));
            OnPropertyChanged(nameof(HasValues));
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            Log.Info($"The values of the {Title} filter could not be read ({ex.Message}).");
        }
    }
}

public sealed partial class FilterValueViewModel(FilterViewModel filter, string key, string title) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task Choose() => filter.Choose(this);
}

/// <summary>A filter in force, shown as a chip that takes it off.</summary>
public sealed partial class ActiveFilterViewModel(LibraryPageViewModel page, FilterViewModel owner, string value, string label)
{
    public FilterViewModel Owner { get; } = owner;

    public string Filter => Owner.Filter;

    public string Value { get; } = value;

    public string Label { get; } = label;

    public string RemoveTip => $"Remove the {Owner.Title} filter";

    [RelayCommand]
    private Task Remove() => page.RemoveFilterAsync(this);
}

/// <summary>An initial on the letter strip, and where its titles begin.</summary>
public sealed partial class LetterViewModel(LibraryPageViewModel page, string letter, int index)
{
    public string Letter { get; } = letter;

    public int Index { get; } = index;

    public string Tip => $"Jump to {Letter}";

    [RelayCommand]
    private void Jump() => page.Jump(this);
}

/// <summary>A collection: its poster and summary above a grid of its members.</summary>
public sealed partial class CollectionPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem collection) : PageViewModel, IGridPage
{
    public override string Title => Collection.Title;

    public MetadataItem Collection { get; } = collection;

    public string Heading => Collection.Title;

    public string? Summary => Collection.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Collection.Summary);

    public string? PosterPath => Collection.Thumb;

    public TileGrid Grid { get; } = new(Tiles.ShapeOf(collection.Subtype));

    public ObservableCollection<GridRowViewModel> Rows => Grid.Rows;

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    public void Fit(double width, double scale) => Grid.Fit(width, scale);

    public bool IsEmpty => !IsLoading && Grid.Count == 0 && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    private IReadOnlyList<MetadataItem> _members = [];

    /// <summary>A collection of films plays through, in its order or shuffled.</summary>
    public bool CanPlay => _members.Any(m => m.Type == "movie");

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var members = await Task.Run(() => session.Client.GetCollectionItemsAsync(Collection.RatingKey, cancellation), cancellation);
        _members = members;
        OnPropertyChanged(nameof(CanPlay));
        Grid.Clear();
        Grid.Add(members.Select(m => Tiles.For(shell, m)));
        CountText = members.Count == 1 ? "1 title" : $"{members.Count} titles";
    }

    [RelayCommand]
    private void Play()
    {
        if (VideoQueue.Of(_members, shuffle: false) is { } queue) shell.Play(queue.Current, resume: true, queue);
    }

    [RelayCommand]
    private void Shuffle()
    {
        if (VideoQueue.Of(_members, shuffle: true) is { } queue) shell.Play(queue.Current, resume: false, queue);
    }
}
