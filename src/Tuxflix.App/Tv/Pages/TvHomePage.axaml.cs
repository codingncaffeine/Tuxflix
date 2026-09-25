using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Tv.Pages;

/// <summary>The TV home: the hero follows focus across the shelves.</summary>
public partial class TvHomePage : UserControl
{
    /// <summary>Focus must rest this long before the hero changes: a held D-pad does not load every backdrop it passes.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(140);

    private readonly TvHeroViewModel _hero = new();
    private readonly DispatcherTimer _settle;
    private HomePageViewModel? _page;
    private MetadataItem? _next;

    public TvHomePage()
    {
        InitializeComponent();
        Backdrop.DataContext = _hero;
        Hero.DataContext = _hero;
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (_next is not null) _hero.Item = _next;
        };
        AddHandler(GotFocusEvent, OnFocus, RoutingStrategies.Bubble);
    }

    /// <summary>What the hero shows now; for tests and captures.</summary>
    internal TvHeroViewModel HeroModel => _hero;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null) _page.Shelves.CollectionChanged -= OnShelves;
        _page = DataContext as HomePageViewModel;
        if (_page is null) return;
        _page.Shelves.CollectionChanged += OnShelves;
        OnShelves(null, null);
    }

    /// <summary>Before anything has focus, the hero shows the first title on the first shelf.</summary>
    private void OnShelves(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        if (_hero.Item is null && _page?.Shelves.FirstOrDefault()?.Tiles.OfType<MediaTileViewModel>().FirstOrDefault() is { } first)
        {
            _hero.Item = first.Item;
        }
    }

    private void OnFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not Control { DataContext: MediaTileViewModel tile }) return;
        _next = tile.Item;
        _settle.Stop();
        _settle.Start();
    }
}

/// <summary>What the TV home's hero says about the title with focus.</summary>
public sealed partial class TvHeroViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kicker), nameof(Title), nameof(Meta), nameof(Summary), nameof(BackdropPath), nameof(LogoPath), nameof(HasLogo))]
    public partial MetadataItem? Item { get; set; }

    public string Kicker => Item?.Type switch
    {
        "episode" => "EPISODE",
        "show" or "season" => "SERIES",
        "movie" => "FILM",
        null => string.Empty,
        _ => Item.Type.ToUpperInvariant(),
    };

    public string Title => Item switch
    {
        { Type: "episode" } episode => episode.GrandparentTitle ?? episode.Title,
        { Type: "season" } season => season.ParentTitle ?? season.Title,
        { } item => item.Title,
        null => string.Empty,
    };

    public string Meta => Item is not { } item ? string.Empty : string.Join("   ·   ", new[]
    {
        item.Type == "episode" ? $"{Format.EpisodeCode(item)}  {item.Title}" : null,
        item.Year?.ToString(CultureInfo.InvariantCulture),
        item.Type is "show" && item.ChildCount is { } seasons ? (seasons == 1 ? "1 season" : $"{seasons} seasons") : Format.Runtime(item.Duration),
        item.ContentRating,
        item.Progress is > 0 and < 1 ? Format.Remaining(item) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    public string? Summary => Item?.Summary;

    public string? BackdropPath => Item is { } item ? Artwork.Backdrop(item) ?? Artwork.Still(item) : null;

    public string? LogoPath => Item is { Type: "movie" or "show" } item ? Artwork.Logo(item) : null;

    public bool HasLogo => LogoPath is not null;
}
