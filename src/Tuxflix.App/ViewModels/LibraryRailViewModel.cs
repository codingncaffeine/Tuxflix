using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>Which titles the rail lists.</summary>
public enum RailFilter
{
    All,
    Unwatched,
    InProgress,
    Watched,
}

/// <summary>
/// The library rail: every library on the server as a collapsible group, every title in it as a
/// row, filtered as you type. Steam's game list, for films and series.
/// </summary>
/// <remarks>
/// The rows are one flat list (group headers and titles together) so a single virtualised panel
/// scrolls a library of thousands without building a control per title.
/// </remarks>
public sealed partial class LibraryRailViewModel(ShellViewModel shell) : ObservableObject
{
    private readonly List<RailSection> _sections = [];
    private RailItemRow? _selected;

    public ShellViewModel Shell => shell;

    public ObservableCollection<RailRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RailFilter Filter { get; set; } = RailFilter.All;

    public string FilterLabel => Filter switch
    {
        RailFilter.Unwatched => "UNWATCHED",
        RailFilter.InProgress => "IN PROGRESS",
        RailFilter.Watched => "WATCHED",
        _ => "ALL TITLES",
    };

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public async Task LoadAsync(ServerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IsLoading = true;
        try
        {
            var sections = await Task.Run(() => session.Client.GetSectionsAsync(CancellationToken.None));
            var loaded = new List<RailSection>();
            foreach (var directory in sections.Where(s => s.Type is "movie" or "show" or "artist"))
            {
                var container = await Task.Run(() => session.Client.GetSectionItemsAsync(directory.Key, "titleSort", CancellationToken.None));
                loaded.Add(new RailSection(this, directory, container.Metadata ?? []));
            }

            _sections.Clear();
            _sections.AddRange(loaded);
            Rebuild();
        }
        catch (Exception ex)
        {
            Log.Warn("The library rail could not be loaded.", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SetFilter(RailFilter filter) => Filter = filter;

    /// <summary>Marks the row for <paramref name="item"/> (or its show) as the one being viewed.</summary>
    public void Highlight(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = item.Type switch
        {
            "episode" or "track" => item.GrandparentRatingKey,
            "season" or "album" => item.ParentRatingKey,
            _ => item.RatingKey,
        };

        if (_selected is not null) _selected.IsSelected = false;
        _selected = Rows.OfType<RailItemRow>().FirstOrDefault(r => r.Item.RatingKey == key);
        if (_selected is not null) _selected.IsSelected = true;
    }

    public void ClearHighlight()
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = null;
    }

    /// <summary>Empties the rail, for a sign-out or a change of server.</summary>
    public void Clear()
    {
        _sections.Clear();
        _selected = null;
        Rows.Clear();
    }

    internal void Open(MetadataItem item) => shell.OpenItem(item);

    internal void Rebuild()
    {
        var text = FilterText.Trim();
        var rows = new List<RailRow>();
        foreach (var section in _sections)
        {
            // Music has no watched state: a watch filter hides it rather than listing every artist.
            if (section.IsMusic && Filter != RailFilter.All) continue;
            var items = section.Items.Where(Matches).Where(i => text.Length == 0 || i.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
            section.VisibleCount = items.Count;
            if (items.Count == 0 && text.Length > 0) continue;
            rows.Add(section.Header);
            if (!section.Header.IsExpanded) continue;
            rows.AddRange(items.Select(i => new RailItemRow(this, i) { IsSelected = _selected?.Item.RatingKey == i.RatingKey }));
        }

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        _selected = Rows.OfType<RailItemRow>().FirstOrDefault(r => r.IsSelected);
    }

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnFilterChanged(RailFilter value)
    {
        OnPropertyChanged(nameof(FilterLabel));
        Rebuild();
    }

    private bool Matches(MetadataItem item) => Filter switch
    {
        RailFilter.Unwatched => !item.IsWatched && item.Progress is null,
        RailFilter.InProgress => item.Progress is > 0 and < 1 || (item.ViewedLeafCount is > 0 && !item.IsWatched),
        RailFilter.Watched => item.IsWatched,
        _ => true,
    };

    private sealed class RailSection
    {
        public RailSection(LibraryRailViewModel rail, LibraryDirectory directory, IReadOnlyList<MetadataItem> items)
        {
            Items = items;
            IsMusic = directory.Type == "artist";
            Header = new RailSectionRow(rail, directory.Title, directory.Type switch
            {
                "show" => "Icon.TelevisionSimple",
                "artist" => "Icon.MusicNotes",
                _ => "Icon.FilmStrip",
            });
        }

        public IReadOnlyList<MetadataItem> Items { get; }

        public bool IsMusic { get; }

        public RailSectionRow Header { get; }

        public int VisibleCount
        {
            set => Header.Count = value;
        }
    }
}

/// <summary>A row of the rail: a library heading or a title.</summary>
public abstract class RailRow : ObservableObject;

public sealed partial class RailSectionRow(LibraryRailViewModel rail, string title, string iconKey) : RailRow
{
    public string Title { get; } = title.ToUpperInvariant();

    public string TipText => IsExpanded ? $"Hide {title}" : $"Show {title}";

    public Geometry? Icon => Application.Current?.FindResource(iconKey) as Geometry;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial int Count { get; set; }

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(TipText));

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        rail.Rebuild();
    }
}

public sealed partial class RailItemRow(LibraryRailViewModel rail, MetadataItem item) : RailRow
{
    public MetadataItem Item { get; } = item;

    public string Title => Item.Title;

    public string? ThumbPath => Item.Thumb;

    public bool IsWatched => Item.IsWatched;

    /// <summary>"42%" for something part-way through, "3" unwatched episodes for a series.</summary>
    public string Trailing => Item.Progress is > 0 and < 1 ? $"{Item.Progress.Value:P0}".Replace(" ", string.Empty, StringComparison.Ordinal)
        : Item.Type == "show" && Item.UnwatchedLeaves > 0 && Item.ViewedLeafCount > 0 ? Item.UnwatchedLeaves.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    public bool IsInProgress => Item.Progress is > 0 and < 1;

    public bool HasTrailing => Trailing.Length > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => rail.Open(Item);
}
