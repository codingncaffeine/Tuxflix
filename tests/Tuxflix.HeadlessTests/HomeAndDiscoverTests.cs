using System.Net;
using Tuxflix.App.Music;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;
using Tuxflix.Player;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The home screen, the Watchlist, the item page's extras and theme music, and Play something,
/// against the demo library and its own Plex catalogue.
/// </summary>
public sealed class HomeAndDiscoverTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "home-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ThemeLog _themes = new();
    private readonly ShellViewModel _shell;
    private readonly SettingsStore _settings;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public HomeAndDiscoverTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths) { Themes = _themes, OpenUrl = _ => { } };
        _shell.OpenDemo();
    }

    private ServerSession Session => _shell.Session!;

    /// <summary>
    /// Waits for the home page the demo opened on. Nothing here runs on a dispatcher, so a page
    /// still loading would share the settings with the test's own pages from another thread,
    /// which the application, where every page runs on the interface thread, never does.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((_shell.Router.Current?.IsLoading == true || _shell.Rail.IsLoading) && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _shell.Router.Current?.Deactivate();
        Session.Dispose();
        // The settings are written on a worker: the last write lands before the folder goes.
        TestFolder.Delete(_root, _settings);
        return ValueTask.CompletedTask;
    }

    /// <summary>Records what the pages ask of theme music, and plays nothing.</summary>
    private sealed class ThemeLog : IThemeMusic
    {
        public List<string> Calls { get; } = [];

        public void Play(string source, IReadOnlyList<(string Name, string Value)> headers, bool muted) => Calls.Add("play " + source);

        public void Stop() => Calls.Add("stop");
    }

    private async Task<HomePageViewModel> HomeAsync()
    {
        var home = new HomePageViewModel(_shell, Session);
        await home.ActivateAsync();
        Assert.Null(home.ErrorMessage);
        return home;
    }

    private static IEnumerable<string> Ids(HomePageViewModel home) => home.Shelves.Select(s => s.Arrangement!.Id);

    [Fact]
    public async Task HomeLeadsWithTheLatestThingBeingWatchedAndKeepsItOffTheShelves()
    {
        var home = await HomeAsync();
        var continuing = _catalog.PromotedHubs()[0].Metadata!;

        Assert.NotNull(home.Hero);
        Assert.Equal(continuing[0].RatingKey, home.Hero.Item.RatingKey);
        Assert.Equal("RESUME", home.Hero.PlayLabel);

        // The rest of Continue Watching, the Watchlist, then the server's promoted shelves in its order.
        Assert.Equal(["home.continue", HomePageViewModel.WatchlistShelf, "movie.recentlyadded.1", "tv.recentlyadded.2", "movie.recentlyreleased.1"], Ids(home));
        var wide = home.Shelves[0].Tiles.OfType<LandscapeTileViewModel>().Select(t => t.Item.RatingKey).ToList();
        Assert.Equal(continuing.Skip(1).Select(i => i.RatingKey), wide);
        Assert.DoesNotContain(home.Hero.Item.RatingKey, wide);
        Assert.Equal(_catalog.Watchlist().Select(t => t.Guid), home.Shelves[1].Tiles.OfType<WatchlistTileViewModel>().Select(t => t.Item.Guid));

        // More info opens it; Resume plays it from where it was left.
        home.Hero.MoreInfoCommand.Execute(null);
        Assert.Equal(home.Hero.Item.RatingKey, Assert.IsType<ItemPageViewModel>(_shell.Router.Current).Item.RatingKey);
    }

    [Fact]
    public async Task ShelvesMoveHideAndComeBackAndTheArrangementIsKeptPerServer()
    {
        var home = await HomeAsync();
        var watchlist = home.Shelves.Single(s => s.Arrangement!.Id == HomePageViewModel.WatchlistShelf);
        var first = home.Shelves[0].Arrangement!;
        Assert.False(first.MoveUpCommand.CanExecute(null));
        Assert.False(home.Shelves[^1].Arrangement!.MoveDownCommand.CanExecute(null));

        watchlist.Arrangement!.MoveDownCommand.Execute(null);
        watchlist.Arrangement.MoveDownCommand.Execute(null);
        home.Shelves.Single(s => s.Arrangement!.Id == "tv.recentlyadded.2").Arrangement!.HideCommand.Execute(null);

        string[] arranged = ["home.continue", "movie.recentlyadded.1", HomePageViewModel.WatchlistShelf, "movie.recentlyreleased.1"];
        Assert.Equal(arranged, Ids(home));
        Assert.Equal(["tv.recentlyadded.2"], home.HiddenShelves.Select(h => h.Id));
        Assert.Equal("Recently Added in TV Shows", home.HiddenShelves[0].Name);

        // The same server's home comes back as it was left; another server's is untouched.
        var again = await HomeAsync();
        Assert.Equal(arranged, Ids(again));
        Assert.True(again.HasHiddenShelves);
        Assert.Equal([DemoCatalog.MachineIdentifier], _shell.Settings.Browse.Home.Keys);

        again.HiddenShelves.Single().ShowCommand.Execute(null);
        Assert.False(again.HasHiddenShelves);
        Assert.Equal(["home.continue", "movie.recentlyadded.1", "tv.recentlyadded.2", HomePageViewModel.WatchlistShelf, "movie.recentlyreleased.1"], Ids(again));
    }

    /// <summary>The Watchlist as the open demo's catalogue holds it now.</summary>
    private async Task<IReadOnlyList<MetadataItem>> WatchlistNowAsync() =>
        await _shell.Discover!.GetWatchlistAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheWatchlistMarksWhatTheServerHasAndOpensIt()
    {
        var page = new WatchlistPageViewModel(_shell, Session);
        await page.ActivateAsync();

        Assert.False(page.NeedsSignIn);
        Assert.Equal(7, page.Titles.Count);
        Assert.Equal(4, page.Titles.Count(t => t.HasCopy));
        Assert.Equal(3, page.Titles.Count(t => t.IsMissing));
        Assert.Contains("4 on Demo Library", page.CountText, StringComparison.Ordinal);

        var missing = page.Titles.First(t => t.IsMissing);
        await missing.OpenCommand.ExecuteAsync(null);
        Assert.IsNotType<ItemPageViewModel>(_shell.Router.Current);
        Assert.Equal($"{missing.Title} is not on Demo Library.", page.Notice);

        var here = page.Titles.First(t => t.HasCopy);
        await here.OpenCommand.ExecuteAsync(null);
        var item = Assert.IsType<ItemPageViewModel>(_shell.Router.Current);
        Assert.Equal(_catalog.FindByGuid(here.Item.Guid).Single().RatingKey, item.Item.RatingKey);

        // Taking a title off drops its tile and takes it off the Watchlist itself.
        var gone = page.Titles[^1];
        await gone.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(6, page.Titles.Count);
        Assert.DoesNotContain(await WatchlistNowAsync(), t => t.Guid == gone.Item.Guid);

        // The Discover tab is the Watchlist.
        _shell.ShowDiscoverCommand.Execute(null);
        Assert.IsType<WatchlistPageViewModel>(_shell.Router.Current);
    }

    [Fact]
    public async Task AnItemPagePutsItsTitleOnTheWatchlistAndTakesItOff()
    {
        var film = _catalog.Movies[0];
        var page = new ItemPageViewModel(_shell, Session, film);
        await page.ActivateAsync();
        await page.Extended;

        Assert.True(page.CanWatchlist);
        Assert.False(page.IsOnWatchlist);

        await page.ToggleWatchlistCommand.ExecuteAsync(null);
        Assert.True(page.IsOnWatchlist);
        Assert.Equal(film.Guid, (await WatchlistNowAsync())[0].Guid);

        // A page opened afterwards reads it back from the catalogue.
        var reopened = new ItemPageViewModel(_shell, Session, film);
        await reopened.ActivateAsync();
        await reopened.Extended;
        Assert.True(reopened.IsOnWatchlist);

        await reopened.ToggleWatchlistCommand.ExecuteAsync(null);
        Assert.False(reopened.IsOnWatchlist);
        Assert.DoesNotContain(await WatchlistNowAsync(), t => t.Guid == film.Guid);
    }

    [Fact]
    public async Task AnItemPageShowsItsExtrasRelatedTitlesAndReviewsAndPlaysAnExtra()
    {
        var film = _catalog.Movies[2];
        var page = new ItemPageViewModel(_shell, Session, film);
        await page.ActivateAsync();
        await page.Extended;

        Assert.Equal(film.Extras!.Metadata!.Select(e => e.RatingKey), page.Extras.Select(e => e.Item.RatingKey));
        Assert.Equal("TRAILER", page.Extras[0].Kind);
        Assert.Equal(_catalog.RelatedHubs(film.RatingKey).Select(h => h.Title.ToUpperInvariant()), page.Related.Select(s => s.Title));
        Assert.True(page.HasRelated);
        Assert.Equal(film.Review!.Count, page.Reviews.Count);
        Assert.All(page.Reviews, r => Assert.True(r.IsFresh || r.IsRotten));

        page.Extras[0].PlayCommand.Execute(null);
        var player = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        Assert.Equal(page.Extras[0].Item.RatingKey, player.Item.RatingKey);
        _shell.Router.Back();
    }

    [Fact]
    public async Task ThemeMusicPlaysWhileATitlesPageIsOpenAndTheViewerCanTurnItOff()
    {
        var show = _catalog.Shows[0];
        var page = new ItemPageViewModel(_shell, Session, show);
        await page.ActivateAsync();
        Assert.True(page.HasTheme);
        Assert.Single(_themes.Calls, c => c.StartsWith("play av://lavfi:", StringComparison.Ordinal));

        page.Deactivate();
        Assert.Equal("stop", _themes.Calls[^1]);

        // Turned off from the page: it stops now, stays off for the next page, and is kept.
        await page.ActivateAsync();
        page.ToggleThemeMusicCommand.Execute(null);
        Assert.False(_shell.Settings.Browse.ThemeMusic);
        Assert.Equal("stop", _themes.Calls[^1]);
        var calls = _themes.Calls.Count;
        var other = new ItemPageViewModel(_shell, Session, _catalog.Shows[1]);
        await other.ActivateAsync();
        Assert.DoesNotContain(_themes.Calls.Skip(calls), c => c.StartsWith("play", StringComparison.Ordinal));

        other.ToggleThemeMusicCommand.Execute(null);
        Assert.True(_shell.Settings.Browse.ThemeMusic);
        Assert.StartsWith("play", _themes.Calls[^1], StringComparison.Ordinal);

        // A film without a theme stays quiet.
        calls = _themes.Calls.Count;
        var quiet = _catalog.Movies.First(m => m.Theme is null);
        await new ItemPageViewModel(_shell, Session, quiet).ActivateAsync();
        Assert.DoesNotContain(_themes.Calls.Skip(calls), c => c.StartsWith("play", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSettingsTurnThemesOffAndPutTheServersHomeBack()
    {
        var home = await HomeAsync();
        var offered = Ids(home).ToList();
        var settings = new BrowseSettingsViewModel(_shell);
        Assert.False(settings.IsArranged);
        Assert.False(settings.RestoreHomeCommand.CanExecute(null));

        home.Shelves[0].Arrangement!.MoveDownCommand.Execute(null);
        home.Shelves[^1].Arrangement!.HideCommand.Execute(null);
        settings = new BrowseSettingsViewModel(_shell);
        Assert.True(settings.IsArranged);
        Assert.Equal("Arranged by you, one hidden.", settings.HomeSummary);

        settings.RestoreHomeCommand.Execute(null);
        Assert.False(settings.IsArranged);
        Assert.Equal(offered, Ids(await HomeAsync()));

        settings.ThemeMusic = false;
        Assert.False(_shell.Settings.Browse.ThemeMusic);
        Assert.Equal("stop", _themes.Calls[^1]);
    }

    [Fact]
    public async Task PlaySomethingStartsAnUnwatchedFilm()
    {
        var page = new LibraryPageViewModel(_shell, Session, _catalog.Sections.Single(s => s.Type == "movie"));
        Assert.True(page.CanPlaySomething);
        await page.PlaySomethingCommand.ExecuteAsync(null);

        var player = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        Assert.False(_catalog.Find(player.Item.RatingKey)!.IsWatched);
        _shell.Router.Back();
    }
}

/// <summary>The Watchlist of a signed-in account, against a stand-in plex.tv.</summary>
public sealed class AccountWatchlistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "account-" + Guid.NewGuid().ToString("N")[..8]);
    private SettingsStore? _settings;

    /// <summary>plex.tv with two servers (so none opens by itself) and a Watchlist that says whose token asked.</summary>
    private sealed class StandIn : HttpMessageHandler
    {
        public List<(string Host, string? Token)> Discover { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var token = request.Headers.TryGetValues("X-Plex-Token", out var values) ? values.Single() : null;
            if (uri.Host == "discover.provider.plex.tv") lock (Discover) Discover.Add((uri.Host, token));
            return Task.FromResult((uri.Host, uri.AbsolutePath) switch
            {
                ("clients.plex.tv", "/api/v2/user") => Json("""{"id":7,"username":"viewer","title":"Viewer"}"""),
                ("clients.plex.tv", "/api/v2/resources") => Json("""
                    [{"name":"Den","provides":"server","clientIdentifier":"machine-1","owned":true,"accessToken":"server-token","connections":[]},
                     {"name":"Cabin","provides":"server","clientIdentifier":"machine-2","owned":true,"accessToken":"server-token-2","connections":[]}]
                    """),
                ("discover.provider.plex.tv", "/library/sections/watchlist/all") => Json("""{"MediaContainer":{"size":1,"totalSize":1,"Metadata":[{"ratingKey":"abc","guid":"plex://movie/abc","type":"movie","title":"An Invented Film"}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) TestFolder.Delete(_root, _settings is { } settings ? [settings] : []);
    }

    [Fact]
    public async Task SignedOutThereIsNoWatchlistAndSignedInItIsTheAccountsAskedWithItsOwnToken()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        var network = new StandIn();
        _settings = SettingsStore.Load(paths.SettingsFile);
        var shell = new ShellViewModel(_settings, paths, network) { Keyring = new ReadOnlySecretStore(new NoSecrets()) };

        var signedOut = new WatchlistPageViewModel(shell, null);
        await signedOut.ActivateAsync();
        Assert.True(signedOut.NeedsSignIn);
        Assert.False(signedOut.IsEmpty);

        await shell.CompleteSignInAsync("account-token", remember: false, TestContext.Current.CancellationToken);
        Assert.Null(shell.Session);
        var noServer = new WatchlistPageViewModel(shell, null);
        await noServer.ActivateAsync();
        Assert.False(noServer.NeedsSignIn);
        Assert.True(noServer.NeedsServer);

        var titles = await shell.Discover!.GetWatchlistAsync(TestContext.Current.CancellationToken);
        Assert.Equal("An Invented Film", Assert.Single(titles).Title);
        Assert.Equal([("discover.provider.plex.tv", "account-token")], network.Discover);
    }

    private sealed class NoSecrets : Tuxflix.Core.Security.ISecretStore
    {
        public Task<string?> LookupAsync(string account) => Task.FromResult<string?>(null);

        public Task<bool> StoreAsync(string account, string label, string secret) => Task.FromResult(false);

        public Task ClearAsync(string account) => Task.CompletedTask;
    }
}

/// <summary>Theme music through a real mpv, with no sound device and the sound off.</summary>
public sealed class ThemeMusicTests
{
    [Fact]
    public async Task AThemeFadesInToItsLevelAndOutWhenStopped()
    {
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed.");
        var theme = new ThemeMusic(audioOutput: "null");
        var volumes = new List<(TimeSpan At, double Volume)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cancellation = TestContext.Current.CancellationToken;

        // The demo's own theme, so its source is proven to play too.
        theme.Play(ItemPageViewModel.DemoTheme, [], muted: true);
        MpvPlayer? player = null;
        while (player is null && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, cancellation);
            player = theme.CurrentPlayer;
        }

        Assert.NotNull(player);
        player.Changed += change =>
        {
            if (change is { Name: "volume", Number: { } volume }) lock (volumes) volumes.Add((clock.Elapsed, volume));
        };

        // The same theme asked for again carries on on the same player.
        theme.Play(ItemPageViewModel.DemoTheme, [], muted: true);
        Assert.Same(player, theme.CurrentPlayer);

        while (!Reached(ThemeMusic.Level) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, cancellation);
        var full = volumes.First(v => v.Volume >= ThemeMusic.Level - 0.01).At;
        Assert.False(string.IsNullOrEmpty(player.GetString("audio-codec-name")), "the theme has no sound to play");
        Assert.True(player.GetNumber("time-pos") > 0, "the theme is not playing");

        // The fade takes its time: no sooner than the fade, and well before twice it.
        Assert.InRange(full, ThemeMusic.FadeIn - TimeSpan.FromMilliseconds(100), ThemeMusic.FadeIn * 2);
        Assert.True(volumes.Where(v => v.At < full).All(v => v.Volume < ThemeMusic.Level), "the volume rose past the level before the fade ended");

        var stopped = clock.Elapsed;
        theme.Stop();
        await Task.WhenAll(theme.Runs).WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        var ended = clock.Elapsed - stopped;
        Assert.InRange(ended, ThemeMusic.FadeOut - TimeSpan.FromMilliseconds(100), ThemeMusic.FadeOut * 3);
        Assert.Null(theme.CurrentPlayer);

        // On the way out the volume only falls.
        List<double> fading;
        lock (volumes) fading = [.. volumes.Where(v => v.At > stopped).Select(v => v.Volume)];
        Assert.NotEmpty(fading);
        Assert.All(fading, v => Assert.True(v < ThemeMusic.Level));
        Assert.Equal(fading.OrderDescending(), fading);

        bool Reached(double level)
        {
            lock (volumes) return volumes.Any(v => v.Volume >= level - 0.01);
        }
    }
}
