using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// The item page beyond the item itself: its extras, the shelves related to it, critics' reviews,
// its place on the Watchlist, and its theme music while the page is open.
public sealed partial class ItemPageViewModel
{
    /// <summary>What the demo plays for a theme: its titles have no files, so a soft chord stands in.</summary>
    internal const string DemoTheme ="av://lavfi:aevalsrc=(0.1*sin(2*PI*220*t)+0.08*sin(2*PI*277.18*t)+0.07*sin(2*PI*329.63*t))*(0.8+0.2*sin(2*PI*0.25*t)):d=40";

    private const int ReviewsShown = 6;

    /// <summary>Similar titles, more from the cast, the collections it is in.</summary>
    public ObservableCollection<ShelfViewModel> Related { get; } = [];

    public ObservableCollection<ExtraTileViewModel> Extras { get; } = [];

    public ObservableCollection<ReviewViewModel> Reviews { get; } = [];

    public bool HasRelated => Related.Count > 0;

    public bool HasExtras => Extras.Count > 0;

    public bool HasReviews => Reviews.Count > 0;

    /// <summary>The extras, related shelves and Watchlist state, which load after the page itself so it never waits for them.</summary>
    public Task Extended { get; private set; } = Task.CompletedTask;

    private bool HasShelves => Item.Type is "movie" or "show";

    /// <summary>The title's key in Plex's catalogue: only a title the catalogue knows can go on the Watchlist.</summary>
    private string? CatalogKey => PlexDiscoverClient.CatalogKey(Item.Guid);

    public bool CanWatchlist => HasShelves && CatalogKey is not null && shell.Watchlist is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistLabel), nameof(WatchlistTip))]
    public partial bool IsOnWatchlist { get; private set; }

    public string WatchlistLabel => IsOnWatchlist ? "ON WATCHLIST" : "WATCHLIST";

    public string WatchlistTip => IsOnWatchlist ? "Take it off your Plex Watchlist" : "Add it to your Plex Watchlist, on every Plex app";

    /// <summary>The title has theme music of its own.</summary>
    public bool HasTheme => HasShelves && Item.Theme is not null;

    public bool IsThemeMusicOn => shell.Settings.Browse.ThemeMusic;

    public string ThemeTip => IsThemeMusicOn ? "Theme music is on: turn it off" : "Theme music is off: turn it on";

    public override void Deactivate()
    {
        shell.Themes.Stop();
        base.Deactivate();
    }

    [RelayCommand]
    private async Task ToggleWatchlistAsync()
    {
        if (CatalogKey is not { } key || shell.Watchlist is not { } watchlist) return;
        var on = !IsOnWatchlist;
        IsOnWatchlist = on;
        try
        {
            await watchlist.SetAsync(key, on);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The Watchlist could not be changed.", ex);
            IsOnWatchlist = !on;
            await SayAsync(on ? "It could not be added to the Watchlist. Plex did not answer." : "It could not be taken off the Watchlist. Plex did not answer.");
        }
    }

    [RelayCommand]
    private void ToggleThemeMusic()
    {
        shell.Settings.Browse.ThemeMusic = !shell.Settings.Browse.ThemeMusic;
        shell.SaveSettings();
        OnPropertyChanged(nameof(IsThemeMusicOn));
        OnPropertyChanged(nameof(ThemeTip));
        if (IsThemeMusicOn) PlayTheme();
        else shell.Themes.Stop();
    }

    /// <summary>Once the item itself is in: its theme starts, its reviews show, and the rest loads behind.</summary>
    private void Extend(CancellationToken cancellation)
    {
        OnPropertyChanged(nameof(HasTheme));
        OnPropertyChanged(nameof(CanWatchlist));
        PlayTheme();

        Reviews.Clear();
        foreach (var review in Item.Review?.Where(r => !string.IsNullOrWhiteSpace(r.Text)).Take(ReviewsShown) ?? []) Reviews.Add(new ReviewViewModel(shell, review));
        OnPropertyChanged(nameof(HasReviews));

        Extended = HasShelves ? LoadShelvesAsync(Item, cancellation) : Task.CompletedTask;
    }

    /// <summary>The title's theme, quietly, unless the viewer turned themes off or music is playing.</summary>
    private void PlayTheme()
    {
        if (!HasTheme || !IsThemeMusicOn || Item.Theme is not { } theme) return;
        if (shell.Music is { HasQueue: true, IsPaused: false }) return;
        if (session.IsDemo) shell.Themes.Play(DemoTheme, [], shell.Silent);
        else shell.Themes.Play(session.Client.MediaUri(theme).AbsoluteUri, session.Client.MediaHeaders(shell.Identity), shell.Silent);
    }

    private async Task LoadShelvesAsync(MetadataItem item, CancellationToken cancellation) =>
        await Task.WhenAll(
            Quietly(LoadExtrasAsync(item, cancellation), "extras"),
            Quietly(LoadRelatedAsync(item, cancellation), "related titles"),
            Quietly(LoadWatchlistStateAsync(cancellation), "Watchlist state"));

    private async Task LoadExtrasAsync(MetadataItem item, CancellationToken cancellation)
    {
        // The page's own request carries them; a server that leaves them out is asked directly.
        var extras = item.Extras?.Metadata ?? await Task.Run(() => session.Client.GetExtrasAsync(item.RatingKey, cancellation), cancellation);
        Extras.Clear();
        foreach (var extra in extras.Where(e => e.Media is { Count: > 0 } || e.Key is not null)) Extras.Add(new ExtraTileViewModel(shell, extra));
        OnPropertyChanged(nameof(HasExtras));
    }

    private async Task LoadRelatedAsync(MetadataItem item, CancellationToken cancellation)
    {
        var hubs = await Task.Run(() => session.Client.GetRelatedHubsAsync(item.RatingKey, cancellation), cancellation);
        Related.Clear();
        foreach (var hub in hubs)
        {
            var titles = hub.Metadata?.Where(i => i.Type is "movie" or "show" or "collection" && i.RatingKey != item.RatingKey).ToList() ?? [];
            if (titles.Count > 0) Related.Add(new ShelfViewModel(hub.Title, titles.Select(i => new PosterTileViewModel(shell, i))));
        }

        OnPropertyChanged(nameof(HasRelated));
    }

    private async Task LoadWatchlistStateAsync(CancellationToken cancellation)
    {
        if (CatalogKey is not { } key || shell.Watchlist is not { } watchlist) return;
        IsOnWatchlist = await watchlist.ContainsAsync(key, cancellation);
    }

    /// <summary>A part of the page that could not load is left out and logged; the rest of the page stands.</summary>
    private async Task Quietly(Task part, string what)
    {
        try
        {
            await part;
        }
        catch (OperationCanceledException)
        {
            // Left before it finished.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or PlexUnauthorizedException)
        {
            Log.Warn($"The {what} of {Item.Title} could not be loaded.", ex);
        }
    }
}

/// <summary>A trailer, featurette or scene on an item page; it plays in the player.</summary>
public sealed partial class ExtraTileViewModel(ShellViewModel shell, MetadataItem extra)
{
    public MetadataItem Item => extra;

    public string Title => extra.Title;

    /// <summary>What kind of extra, as Plex's apps name them.</summary>
    public string Kind => extra.Subtype switch
    {
        "trailer" => "TRAILER",
        "behindTheScenes" => "BEHIND THE SCENES",
        "deletedScene" => "DELETED SCENE",
        "featurette" => "FEATURETTE",
        "interview" => "INTERVIEW",
        "sceneOrSample" => "SCENE",
        "short" => "SHORT",
        "musicVideo" => "MUSIC VIDEO",
        _ => "EXTRA",
    };

    public string Runtime => extra.Duration is > 0 ? Format.Clock(extra.Duration.Value / 1000.0) : string.Empty;

    public string? ImagePath => extra.Thumb;

    public string AccessibleName => $"{Kind.ToLowerInvariant()}: {Title}";

    public string PlayTip => $"Play the {Kind.ToLowerInvariant()}";

    [RelayCommand]
    private void Play() => shell.Play(extra, resume: false);
}

/// <summary>A critic's review on an item page.</summary>
public sealed partial class ReviewViewModel(ShellViewModel shell, Review review)
{
    public string Critic => review.Tag ?? "A critic";

    public string Source => review.Source ?? string.Empty;

    public string Text => review.Text ?? string.Empty;

    public bool IsFresh => review.Image?.EndsWith(".fresh", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsRotten => review.Image?.EndsWith(".rotten", StringComparison.OrdinalIgnoreCase) == true;

    public string Verdict => IsFresh ? "FRESH" : IsRotten ? "ROTTEN" : string.Empty;

    public bool HasVerdict => Verdict.Length > 0;

    public bool HasLink => Uri.TryCreate(review.Link, UriKind.Absolute, out var link) && link.Scheme is "https" or "http";

    public string LinkTip => $"Read the whole review at {(Source.Length > 0 ? Source : "its source")}";

    [RelayCommand]
    private void Read()
    {
        if (HasLink) shell.OpenUrl(review.Link!);
    }
}
