using System.Collections.Concurrent;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A notification source the test speaks for.</summary>
internal sealed class FakeNotifications : INotificationSource
{
    public event Action<NotificationContainer>? Received;

    public event Action<bool>? ConnectionChanged;

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public void Start()
    {
        Started = true;
        ConnectionChanged?.Invoke(true);
    }

    public void Send(NotificationContainer message) => Received?.Invoke(message);

    public void Dispose() => Disposed = true;
}

/// <summary>
/// The viewer's own state against the demo server: watched marks kept in step everywhere, ratings,
/// stream choices, playlists and their editing, and the live news of the notification socket.
/// </summary>
public sealed class ViewerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "viewer-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<Action> _ui = new();
    private readonly ShellViewModel _shell;
    private FakeNotifications? _news;

    public ViewerTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _shell = new ShellViewModel(SettingsStore.Load(paths.SettingsFile), paths)
        {
            LivePost = _ui.Enqueue,
            NotificationSources = _ => _news = new FakeNotifications(),
        };
        _shell.OpenDemo();
        _shell.Live.LibraryPause = TimeSpan.FromMilliseconds(30);
        _shell.Live.ScanPause = TimeSpan.FromMilliseconds(30);
        _shell.Live.ReadPause = TimeSpan.FromMilliseconds(10);
    }

    private ServerSession Session => _shell.Session!;

    private ViewerState Viewer => _shell.Viewer!;

    private Tuxflix.Core.Demo.DemoCatalog Demo => Session.Demo!;

    public void Dispose()
    {
        _shell.Router.Current?.Deactivate();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Runs what was handed to the UI thread until <paramref name="done"/> holds.</summary>
    private async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            while (_ui.TryDequeue(out var work)) work();
            if (done()) return;
            Assert.True(DateTime.UtcNow < deadline, $"Waited ten seconds for {what}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task<MetadataItem> Fresh(string key) => (await Session.Client.GetMetadataAsync(key, CancellationToken.None))!;

    [Fact]
    public async Task AnEpisodeMarkedWatchedShowsAtOnceOnItsTileItsRowItsSeasonAndItsSeries()
    {
        var show = await Fresh(Demo.Shows[3].RatingKey);
        var season = (await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None))[0];
        var episode = (await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None))[0];
        var showTile = new PosterTileViewModel(_shell, show);
        var seasonTile = new PosterTileViewModel(_shell, season);
        var episodeTile = new LandscapeTileViewModel(_shell, episode);
        var row = new EpisodeRowViewModel(_shell, episode);
        var unwatched = showTile.UnwatchedCount;
        Assert.False(episodeTile.IsWatched);
        Assert.False(row.IsWatched);

        var told = new List<string>();
        showTile.PropertyChanged += (_, e) => told.Add(e.PropertyName!);

        // Shown before the server has answered.
        var marking = Viewer.SetWatchedAsync(episode, watched: true);
        Assert.True(episodeTile.IsWatched);
        Assert.True(row.IsWatched);
        Assert.Equal(unwatched - 1, showTile.UnwatchedCount);
        Assert.Equal(season.LeafCount - 1, seasonTile.UnwatchedCount);
        Assert.Contains(nameof(MediaTileViewModel.UnwatchedCount), told);

        Assert.True(await marking);
        Assert.True((await Fresh(episode.RatingKey)).IsWatched);
        Assert.Equal(unwatched - 1, showTile.UnwatchedCount);

        // And back, the same way.
        Assert.True(await Viewer.SetWatchedAsync(episode, watched: false));
        Assert.False(episodeTile.IsWatched);
        Assert.Equal(unwatched, showTile.UnwatchedCount);
        Assert.False((await Fresh(episode.RatingKey)).IsWatched);
    }

    [Fact]
    public async Task ASeasonMarkedWatchedMarksItsEpisodesOnScreenAndCountsUpItsSeries()
    {
        var show = await Fresh(Demo.Shows[3].RatingKey);
        var season = (await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None))[1];
        var rows = (await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None)).Select(e => new EpisodeRowViewModel(_shell, e)).ToList();
        var showTile = new PosterTileViewModel(_shell, show);
        var before = showTile.UnwatchedCount;

        var marking = Viewer.SetWatchedAsync(season, watched: true);
        Assert.All(rows, r => Assert.True(r.IsWatched));
        Assert.Equal(before - season.LeafCount, showTile.UnwatchedCount);
        Assert.True(await marking);
        Assert.All(await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None), e => Assert.True(e.IsWatched));
    }

    [Fact]
    public async Task AChangeTheServerRefusesIsTakenBack()
    {
        var missing = new MetadataItem { RatingKey = "999999", Type = "movie", Title = "Not on the server" };
        var tile = new PosterTileViewModel(_shell, missing);
        Assert.False(await Viewer.SetWatchedAsync(missing, watched: true));
        Assert.False(tile.IsWatched);
        Assert.False(await Viewer.RateAsync(missing, 8));
        Assert.Null(tile.State.UserRating);
    }

    [Fact]
    public async Task ARecordReadBeforeAChangeLandedDoesNotUndoIt()
    {
        var movie = Demo.Movies[0];
        var stale = await Fresh(movie.RatingKey);
        var state = Viewer.For(stale);
        Assert.False(state.IsWatched);

        // A record whose request began before the change, answered just after the server took
        // it (before the change's own read-back arrives): newer than the one held, but out of date.
        var late = await Fresh(movie.RatingKey);
        bool? heldAfterLateRecord = null;
        var answered = false;
        Viewer.Changed += changed =>
        {
            if (changed != state || changed.Pending != 0 || answered) return;
            answered = true;
            Viewer.Take(late);
            heldAfterLateRecord = state.IsWatched;
        };

        Assert.True(await Viewer.SetWatchedAsync(stale, watched: true));
        Assert.True(heldAfterLateRecord);
        Viewer.Take(stale);
        Assert.True(state.IsWatched);

        // A record read after the change is the server's word, whatever it says.
        Assert.True(await Viewer.SetWatchedAsync(stale, watched: false));
        Assert.False(state.IsWatched);
        Viewer.Take(await Fresh(movie.RatingKey));
        Assert.False(state.IsWatched);
    }

    [Fact]
    public async Task HalfStarsReachTheServerAndClickingTheSameRatingTakesItAway()
    {
        // Stars of 18 with gaps of 4: every 11 units along is half a star more.
        Assert.Equal([1, 1, 2, 2, 3, 10, 10], new[] { 1.0, 11, 12, 22, 23, 108, 500 }.Select(x => StarRating.PointsAt(x, 18)));
        Assert.Equal("3½ stars", StarRating.Describe(7));
        Assert.Equal("1 star", StarRating.Describe(2));
        Assert.Equal("No rating", StarRating.Describe(null));

        var movie = Demo.Movies[4];
        var page = new ItemPageViewModel(_shell, Session, movie);
        await page.ActivateAsync();
        page.UserRating = 7;
        await Until(() => Demo.Find(movie.RatingKey)!.UserRating == 7 && page.State.Pending == 0, "the rating to reach the demo server");
        Assert.Equal(7, page.UserRating);

        page.UserRating = null;
        await Until(() => Demo.Find(movie.RatingKey)!.UserRating is null && page.State.Pending == 0, "the rating to go");
    }

    [Fact]
    public async Task AudioAndSubtitlesChosenOnTheItemPageAreKeptAndGoToThePlayer()
    {
        var movie = Demo.Movies[3];
        var page = new ItemPageViewModel(_shell, Session, movie);
        await page.ActivateAsync();
        Assert.True(page.HasStreamChoices);
        Assert.Equal("None", page.SelectedSubtitle!.Title);
        Assert.Contains(page.AudioStreams, a => a.Title.Contains("Commentary", StringComparison.Ordinal));

        var french = page.AudioStreams.First(a => a.Stream!.LanguageCode == "fra");
        page.SelectedAudio = french;
        await Until(() => page.Item.Media![0].Part![0].Stream!.Single(s => s.Id == french.Id).Selected, "the page to read the choice back");
        var spanish = page.SubtitleStreams.First(s => s.Stream?.LanguageCode == "spa");
        page.SelectedSubtitle = spanish;
        await Until(() => page.Item.Media![0].Part![0].Stream!.Single(s => s.Id == spanish.Id).Selected, "the subtitle choice");

        // The item Play hands the player carries both, and the server keeps them.
        var part = (await Fresh(movie.RatingKey)).Media![0].Part![0];
        Assert.Equal(french.Id, StreamChoice.Selected(part, StreamChoice.Audio)!.Id);
        Assert.Equal(spanish.Id, StreamChoice.Selected(part, StreamChoice.Subtitle)!.Id);
        Assert.Equal(spanish.Id, page.SelectedSubtitle!.Id);
    }

    [Fact]
    public async Task TheMenuOffersTheWatchedMarkAndThePlaylistsOfTheItemsKind()
    {
        await Viewer.LoadPlaylistsAsync();
        var movie = await Fresh(Demo.Movies[8].RatingKey);
        var tile = new PosterTileViewModel(_shell, movie);
        var menu = ViewerMenu.Entries(tile, whole: true, anchor: null);
        Assert.Equal(["Mark as watched", "Add to playlist"], menu.Select(e => e.Header));
        var playlists = menu[1].Children!;
        Assert.Equal("New playlist…", playlists[0].Header);
        Assert.Null(playlists[1].Header);
        Assert.Equal(["Rainy Day Mysteries", "Weekend Marathon"], playlists.Skip(2).Select(p => p.Header));

        // Choosing one adds the film to its end.
        playlists.Single(p => p.Header == "Weekend Marathon").Act!();
        var marathon = Viewer.Playlists.Single(p => p.Title == "Weekend Marathon");
        await Until(() => Demo.PlaylistItems(marathon.RatingKey)!.Any(i => i.RatingKey == movie.RatingKey), "the film to join the playlist");
        await Until(() => _shell.Live.Summary == "Added to Weekend Marathon", "the status bar to say so");

        // Watched from the menu too; an item page's button offers the playlists alone.
        menu[0].Act!();
        await Until(() => tile.IsWatched && tile.State.Pending == 0, "the menu's watched mark");
        Assert.Equal("Mark as unwatched", ViewerMenu.Entries(tile, whole: true, anchor: null)[0].Header);
        Assert.Equal("New playlist…", ViewerMenu.Entries(tile, whole: false, anchor: null)[0].Header);
    }

    [Fact]
    public async Task MusicIsOfferedOnlyTheMusicPlaylistsAndNoWatchedMark()
    {
        await Session.Client.CreatePlaylistAsync("Road Trip", "audio", Session.MachineIdentifier!, ["777777"], CancellationToken.None);
        await Viewer.LoadPlaylistsAsync();
        var album = new AlbumTileViewModel(_shell, new MetadataItem { RatingKey = "4001", Type = "album", Title = "Tidewater Songs" });
        var menu = ViewerMenu.Entries(album, whole: true, anchor: null);
        Assert.Equal(["Add to playlist"], menu.Select(e => e.Header));
        Assert.Equal(["New playlist…", null, "Road Trip"], menu[0].Children!.Select(e => e.Header));
        Assert.Equal("audio", ViewerState.PlaylistTypeOf(new MetadataItem { Type = "track" }));
        Assert.Null(ViewerState.PlaylistTypeOf(new MetadataItem { Type = "collection" }));
    }

    [Fact]
    public async Task APlaylistMadeFromItemsIsRenamedReorderedTrimmedAndDeletedOnItsPage()
    {
        var movies = Demo.Movies.Skip(10).Take(3).ToList();
        var made = await Viewer.CreatePlaylistAsync("Tonight", [movies[0]]);
        Assert.NotNull(made);
        Assert.True(await Viewer.AddToPlaylistAsync(made, [movies[1], movies[2]]));
        Assert.Contains(Viewer.Playlists, p => p.Title == "Tonight" && p.LeafCount == 3);

        var page = new PlaylistPageViewModel(_shell, Session, made);
        _shell.Router.Navigate(new PlaylistsPageViewModel(_shell, Session));
        _shell.Router.Navigate(page);
        await Until(() => !page.IsLoading && !page.IsLoadingMore && page.Videos.Count == 3, "the playlist to load");

        IEnumerable<string> Order() => Demo.PlaylistItems(made.RatingKey)!.Select(i => i.RatingKey);
        await page.MoveDownCommand.ExecuteAsync(page.Videos[0]);
        Assert.Equal([movies[1].RatingKey, movies[0].RatingKey, movies[2].RatingKey], page.Videos.Select(v => v.Item.RatingKey));
        Assert.Equal(page.Videos.Select(v => v.Item.RatingKey), Order());
        Assert.Equal(["1", "2", "3"], page.Videos.Select(v => v.Number));

        await page.MoveToTopCommand.ExecuteAsync(page.Videos[2]);
        Assert.Equal([movies[2].RatingKey, movies[1].RatingKey, movies[0].RatingKey], Order());
        await page.RemoveCommand.ExecuteAsync(page.Videos[1]);
        Assert.Equal([movies[2].RatingKey, movies[0].RatingKey], Order());
        Assert.Equal(2, page.Videos.Count);

        page.StartRenameCommand.Execute(null);
        page.NewTitle = "Late Tonight";
        await page.SaveRenameCommand.ExecuteAsync(null);
        Assert.Equal("Late Tonight", page.Heading);
        Assert.Equal("Late Tonight", Demo.Playlist(made.RatingKey)!.Title);

        page.AskDeleteCommand.Execute(null);
        Assert.True(page.IsConfirmingDelete);
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Null(Demo.Playlist(made.RatingKey));
        Assert.IsType<PlaylistsPageViewModel>(_shell.Router.Current);
    }

    [Fact]
    public async Task ThePlaybackOnTheViewersOtherDeviceIsListedAndMovesTheProgressHere()
    {
        var clock = Demo.Now.AddMinutes(1);
        Demo.Clock = () => clock;
        await Until(() => _news?.Started == true, "the notification source to start");
        await _shell.Live.RefreshPlayingAsync();
        var playing = Assert.Single(_shell.Live.Playing);
        Assert.Equal("Living Room", playing.Device);
        Assert.Equal("Playing on Living Room", _shell.Live.Summary);

        var episode = await Fresh(playing.Item.RatingKey);
        var tile = new LandscapeTileViewModel(_shell, episode);
        var at = Demo.ViewerPlayback().ViewOffset!.Value + 90_000;
        _news!.Send(new NotificationContainer { Type = "playing", Playing = [new PlaySessionState { SessionKey = playing.SessionKey, RatingKey = episode.RatingKey, ViewOffset = at, State = "paused" }] });
        await Until(() => playing.ViewOffset == at, "the playback to move");
        Assert.True(playing.IsPaused);
        Assert.Equal(at, tile.State.ViewOffset);
        Assert.True(tile.HasProgress);

        // Another account's playback is not the viewer's: it is left off.
        Assert.DoesNotContain(_shell.Live.Playing, p => p.Device == "Robin's iPad");
    }

    [Fact]
    public async Task AScanShowsAsActivityAndItsEndRefreshesTheLibraryOnScreen()
    {
        await Until(() => _news?.Started == true, "the notification source to start");
        var section = (await Session.Client.GetSectionsAsync(CancellationToken.None)).First(s => s.Type == "movie");
        _shell.OpenSection(section);
        var library = (LibraryPageViewModel)_shell.Router.Current!;
        library.Fit(1200);
        await Until(() => !library.IsLoading && !library.IsLoadingMore && library.Grid.Count == Demo.Movies.Count, "the library to list");
        var first = library.Grid.Tiles[0];

        NotificationContainer Scan(string what, int progress) => new()
        {
            Type = "activity",
            Activities = [new ActivityNotification { Event = what, Uuid = "scan-1", Activity = new ServerActivity { Uuid = "scan-1", Type = "library.update.section", Title = "Scanning Movies", Progress = progress, Context = new ActivityContext { LibrarySectionId = 1 } } }],
        };

        _news!.Send(Scan("started", 0));
        _news.Send(Scan("updated", 40));
        await Until(() => _shell.Live.Summary == "Scanning Movies · 40%", "the scan in the status bar");
        _news.Send(new NotificationContainer { Type = "timeline", Timeline = [new TimelineEntry { Identifier = TimelineEntry.LibraryIdentifier, SectionId = 1, ItemId = 1001, Type = 1, State = TimelineEntry.Settled }] });
        _news.Send(Scan("ended", 100));
        await Until(() => !_shell.Live.HasActivity, "the scan to end");
        await Until(() => library.Grid.Count == Demo.Movies.Count && !ReferenceEquals(library.Grid.Tiles[0], first), "the grid to be listed again");

        // A timeline entry that is not settled, or not the library's, changes nothing.
        var now = library.Grid.Tiles[0];
        _news.Send(new NotificationContainer { Type = "timeline", Timeline = [new TimelineEntry { Identifier = TimelineEntry.LibraryIdentifier, SectionId = 1, ItemId = 1001, State = 2 }] });
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await Until(() => true, "the queue to run");
        Assert.Same(now, library.Grid.Tiles[0]);
    }

    [Fact]
    public async Task ActivityListsTheViewersHistoryAndSumsTheirHours()
    {
        var page = new ActivityPageViewModel(_shell, Session);
        await page.ActivateAsync();
        Assert.Null(page.ErrorMessage);
        Assert.NotEmpty(page.History);
        var mine = Demo.History(Tuxflix.Core.Demo.DemoCatalog.ViewerAccountId, null).Where(h => h.Type is "movie" or "episode").Select(h => h.HistoryKey).ToList();
        Assert.Equal(mine.Take(page.History.Count), page.History.Select(h => h.Entry.HistoryKey));
        Assert.Equal(8, page.Weeks.Count);
        Assert.Equal(6, page.Months.Count);
        Assert.True(page.Stats!.Months.Sum(m => m.Hours) > 0);
        Assert.NotEmpty(page.TopShows);
        Assert.Single(page.Weeks, w => w.IsCurrent);
        Assert.Equal(StatBarViewModel.Tallest, page.Weeks.Max(w => w.Height), 3);
    }

    [Fact]
    public async Task ClosingTheServerStopsItsNotifications()
    {
        await Until(() => _news?.Started == true, "the notification source to start");
        var first = _news!;
        _shell.OpenDemo();
        Assert.True(first.Disposed);
        Assert.NotSame(first, _news);
    }
}
