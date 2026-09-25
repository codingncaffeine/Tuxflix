using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Library home: what was being watched last, large, then the server's promoted shelves and the
/// Watchlist, in the viewer's own order for this server.
/// </summary>
/// <remarks>
/// The shelves are the server's own home screen (<c>/hubs/promoted</c>), as its owner arranged
/// them; the viewer can move a shelf, hide one and bring it back, and that is kept per server.
/// Continue Watching gives its most recent item to the spotlight and keeps the rest.
/// </remarks>
public sealed partial class HomePageViewModel(ShellViewModel shell, ServerSession session) : PageViewModel
{
    /// <summary>The Watchlist shelf's identifier among the server's own.</summary>
    public const string WatchlistShelf = "tuxflix.watchlist";

    private const int WatchlistShelfSize = 20;

    private readonly Dictionary<string, ShelfViewModel> _offered = new(StringComparer.Ordinal);
    private List<string> _order = [];

    public override string Title => "Library home";

    /// <summary>The shelves shown, in the viewer's order.</summary>
    public ObservableCollection<ShelfViewModel> Shelves { get; } = [];

    /// <summary>The shelves the viewer hid from this server's home, to bring back.</summary>
    public ObservableCollection<HiddenShelfViewModel> HiddenShelves { get; } = [];

    public bool HasHiddenShelves => HiddenShelves.Count > 0;

    /// <summary>The spotlight: the most recent thing in Continue Watching.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero), nameof(IsEmpty))]
    public partial HeroViewModel? Hero { get; private set; }

    public bool HasHero => Hero is not null;

    public bool IsEmpty => !IsLoading && Shelves.Count == 0 && Hero is null && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    private ShelfLayout Layout => shell.Settings.Browse.HomeOf(ShellViewModel.HomeLayoutKey(session));

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var hubs = Task.Run(() => session.Client.GetPromotedHubsAsync(cancellation), cancellation);
        var watchlist = LoadWatchlistAsync(cancellation);
        Build(await hubs, await watchlist);
        if (Hero is { } hero) await hero.LoadBlurAsync(cancellation);
    }

    /// <summary>The start of the Watchlist for its shelf; a Watchlist that cannot be read leaves the rest of home as it is.</summary>
    private async Task<IReadOnlyList<MetadataItem>?> LoadWatchlistAsync(CancellationToken cancellation)
    {
        if (shell.Watchlist is not { } watchlist) return null;
        try
        {
            return await watchlist.GetAsync(cancellation, WatchlistShelfSize);
        }
        catch (Exception ex) when ((ex is HttpRequestException or PlexUnauthorizedException or JsonException or TaskCanceledException) && !cancellation.IsCancellationRequested)
        {
            Log.Warn("The Watchlist could not be read for the home screen.", ex);
            return null;
        }
    }

    private void Build(IReadOnlyList<Hub> hubs, IReadOnlyList<MetadataItem>? watchlist)
    {
        _offered.Clear();
        var order = new List<string>();
        MetadataItem? hero = null;
        var afterContinuing = 0;

        // Servers send Continue Watching and On Deck side by side, and they overlap almost entirely;
        // an item shows once, on the first wide shelf that has it, and a shelf left empty is
        // dropped, as Plex's own apps merge the two.
        var shownWide = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hub in hubs)
        {
            // Films and series for now; music, photo and clip shelves arrive with their own phases.
            var items = hub.Metadata?.Where(i => i.Type is "movie" or "show" or "season" or "episode").ToList() ?? [];
            var continuing = IsContinuing(hub);
            if (continuing)
            {
                if (hero is null && items.Count > 0)
                {
                    hero = items[0];
                    shownWide.Add(hero.RatingKey);
                }

                items = [.. items.Where(i => shownWide.Add(i.RatingKey))];
            }

            var id = ShelfId(hub);
            if (items.Count > 0 && !_offered.ContainsKey(id))
            {
                IEnumerable<MediaTileViewModel> tiles = continuing
                    ? items.Select(i => new LandscapeTileViewModel(shell, i))
                    : items.Select(i => new PosterTileViewModel(shell, i));
                _offered[id] = new ShelfViewModel(hub.Title, tiles) { Arrangement = new ShelfArrangement(this, id, hub.Title) };
                order.Add(id);
            }

            if (continuing) afterContinuing = order.Count;
        }

        // The Watchlist comes straight after what is being watched, until the viewer moves it.
        if (watchlist is { Count: > 0 } && shell.Watchlist is { } service)
        {
            var tiles = watchlist.Select(t => new WatchlistTileViewModel(shell, service, t)).ToList();
            _offered[WatchlistShelf] = new ShelfViewModel("Watchlist", tiles) { Arrangement = new ShelfArrangement(this, WatchlistShelf, "Watchlist") };
            order.Insert(afterContinuing, WatchlistShelf);
            foreach (var tile in tiles) _ = tile.CheckAsync();
        }

        _order = order;
        Hero = hero is null ? null : new HeroViewModel(shell, session, hero);
        Arrange();
    }

    /// <summary>Puts the shelves in the viewer's order and leaves out the hidden ones, moving what is already shown rather than building it again.</summary>
    private void Arrange()
    {
        var arranged = Layout.Arrange(_order);
        var shown = arranged.Where(id => !Layout.IsHidden(id)).Select(id => _offered[id]).ToList();
        for (var i = Shelves.Count - 1; i >= 0; i--)
        {
            if (!shown.Contains(Shelves[i])) Shelves.RemoveAt(i);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            var at = Shelves.IndexOf(shown[i]);
            if (at < 0) Shelves.Insert(i, shown[i]);
            else if (at != i) Shelves.Move(at, i);
            shown[i].Arrangement!.Place(first: i == 0, last: i == shown.Count - 1);
        }

        HiddenShelves.Clear();
        foreach (var id in arranged.Where(Layout.IsHidden)) HiddenShelves.Add(new HiddenShelfViewModel(this, id, _offered[id].Arrangement!.Name));
        OnPropertyChanged(nameof(HasHiddenShelves));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Moves a shelf one place up (-1) or down (1) among those shown, and remembers it.</summary>
    internal void Move(string shelf, int by)
    {
        if (!Layout.Move(Layout.Arrange(_order), shelf, by)) return;
        shell.SaveSettings();
        Arrange();
    }

    internal void Hide(string shelf)
    {
        Layout.Hide(shelf);
        shell.SaveSettings();
        Arrange();
    }

    internal void Show(string shelf)
    {
        Layout.Show(shelf);
        shell.SaveSettings();
        Arrange();
    }

    private static bool IsContinuing(Hub hub) => hub.HubIdentifier is { } id
        && (id.StartsWith("home.continue", StringComparison.Ordinal) || id.StartsWith("home.ondeck", StringComparison.Ordinal) || id == "continueWatching");

    private static string ShelfId(Hub hub) => hub.HubIdentifier ?? hub.Key ?? hub.Title;
}

// The home screen's arranging, carried by the shelves it arranges.
public sealed partial class ShelfViewModel
{
    /// <summary>Moving and hiding this shelf on the home screen; null on shelves that stay as they are.</summary>
    public ShelfArrangement? Arrangement { get; init; }

    public bool CanArrange => Arrangement is not null;
}

/// <summary>What a home shelf's menu does: move it up or down, or hide it.</summary>
public sealed partial class ShelfArrangement(HomePageViewModel home, string id, string name) : ObservableObject
{
    public string Id => id;

    /// <summary>The shelf's name as the server gives it.</summary>
    public string Name => name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; private set; }

    internal void Place(bool first, bool last)
    {
        CanMoveUp = !first;
        CanMoveDown = !last;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => home.Move(id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => home.Move(id, 1);

    [RelayCommand]
    private void Hide() => home.Hide(id);
}

/// <summary>A shelf hidden from home, offered back.</summary>
public sealed partial class HiddenShelfViewModel(HomePageViewModel home, string id, string name)
{
    public string Id => id;

    public string Name => name;

    public string ShowTip => $"Show {Name} on home again";

    [RelayCommand]
    private void Show() => home.Show(id);
}

/// <summary>The top of the home screen: what was being watched most recently, large, to carry on with.</summary>
public sealed partial class HeroViewModel(ShellViewModel shell, ServerSession session, MetadataItem item) : ObservableObject
{
    public MetadataItem Item => item;

    public bool IsEpisode => item.Type == "episode";

    /// <summary>The show for an episode, else the title itself.</summary>
    public string Heading => IsEpisode ? item.GrandparentTitle ?? item.Title : item.Title;

    /// <summary>"S2 · E6 · Tidewater", or the year and genres of a film.</summary>
    public string Subheading => IsEpisode
        ? $"{Format.EpisodeCode(item)}   ·   {item.Title}"
        : string.Join("   ·   ", new[]
        {
            item.Year?.ToString(CultureInfo.InvariantCulture),
            Format.Runtime(item.Duration),
            item.Genre is { Count: > 0 } genres ? string.Join(", ", genres.Take(2).Select(g => g.TagText)) : null,
        }.Where(part => !string.IsNullOrEmpty(part)));

    public string? Summary => item.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(item.Summary);

    public string? BackdropPath => Artwork.Backdrop(item);

    /// <summary>The title as artwork: a current server sends an episode with its show's logo.</summary>
    public string? LogoPath => Artwork.Logo(item);

    public bool HasLogo => LogoPath is not null;

    public bool ShowTextTitle => !HasLogo;

    public bool HasProgress => item.Progress is > 0 and < 1;

    public double Progress => item.Progress ?? 0;

    public string RemainingText => Format.Remaining(item);

    public string PlayLabel => HasProgress ? "RESUME" : "PLAY";

    public string PlayTip => HasProgress ? "Carry on from where you left off" : IsEpisode ? "Play the next episode" : "Play it";

    public string AccessibleName => IsEpisode ? $"Continue watching {Heading}, {item.Title}" : $"Continue watching {Heading}";

    /// <summary>The server's UltraBlur colours for the backdrop: the spotlight takes on the title's own light.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlur), nameof(BlurTop), nameof(BlurBottom))]
    public partial UltraBlurColors? Blur { get; private set; }

    public bool HasBlur => BlurTop is not null && BlurBottom is not null;

    public IBrush? BlurTop => Across(Blur?.TopLeft, Blur?.TopRight);

    public IBrush? BlurBottom => Across(Blur?.BottomLeft, Blur?.BottomRight);

    [RelayCommand]
    private void Play() => shell.Play(item, resume: true);

    [RelayCommand]
    private void MoreInfo() => shell.OpenItem(item);

    /// <summary>Current servers send the colours with the item; older ones compute them on request.</summary>
    internal async Task LoadBlurAsync(CancellationToken cancellation)
    {
        Blur = item.UltraBlurColors
               ?? (BackdropPath is { } backdrop
                   ? await Task.Run(() => session.Client.GetUltraBlurColorsAsync(backdrop, cancellation), cancellation)
                   : null);
    }

    private static LinearGradientBrush? Across(string? left, string? right)
    {
        if (!Color.TryParse("#" + left, out var from) || !Color.TryParse("#" + right, out var to)) return null;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
    }
}
