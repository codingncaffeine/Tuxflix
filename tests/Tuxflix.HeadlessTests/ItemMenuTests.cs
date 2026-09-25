using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The menu on tiles and rows, against the demo server: every kind of item gets the lines Plex's
/// own menu gives it, each line does what it says, and the line a request settles (the
/// Watchlist) settles. Work the window would run on its UI thread runs here by hand.
/// </summary>
public sealed class ItemMenuTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "menu-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<Action> _ui = new();
    private readonly SettingsStore _settings;
    private readonly ShellViewModel _shell;

    public ItemMenuTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths)
        {
            PostToUi = _ui.Enqueue,
            LivePost = _ui.Enqueue,
            NotificationSources = _ => new FakeNotifications(),
            Silent = true,
            ReportsPlayback = false,
        };
        _shell.OpenDemo();
    }

    private ServerSession Session => _shell.Session!;

    private ViewerState Viewer => _shell.Viewer!;

    private DemoCatalog Demo => Session.Demo!;

    public async ValueTask DisposeAsync()
    {
        _shell.Router.Current?.Deactivate();
        _shell.StopDownloads();
        await _shell.DownloadsStopped;
        _shell.Session?.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public async Task AFilmNotStartedIsMarkedRatedWatchlistedAndDownloadedFromItsMenu()
    {
        await Viewer.LoadPlaylistsAsync();
        var film = await Fresh(Demo.Movies[8].RatingKey);
        Assert.Null(film.ViewOffset);
        var tile = new PosterTileViewModel(_shell, film);
        var menu = MenuOf(tile);
        Assert.Equal(["Play", null, "Mark as watched", "Rate", null, "Checking the Watchlist…", "Add to playlist", "Download", null, "Media info"], Headers(menu));

        // The Watchlist line settles when Plex answers; once it is known, it shows at once.
        Assert.False(menu[5].IsEnabled);
        var settled = await menu[5].Later!;
        Assert.Equal("Add to Watchlist", settled!.Header);
        settled.Act!();
        var key = PlexDiscoverClient.CatalogKey(film.Guid)!;
        await Until(() => Demo.WatchlistedAt(key) is not null, "the film to go on the Watchlist");
        await Until(() => _shell.Live.Summary == $"{film.Title} is on your Watchlist", "the status bar to say so");
        var again = MenuOf(tile)[5];
        Assert.Equal("Remove from Watchlist", again.Header);
        Assert.True(again.IsEnabled);
        Assert.Equal("Remove from Watchlist", (await again.Later!)!.Header);
        again.Act!();
        await Until(() => Demo.WatchlistedAt(key) is null, "the film to come off the Watchlist");

        // Rated from the menu, the tick follows; a half star shows among the whole ones.
        var stars = Line(menu, "Rate").Children!;
        Assert.Equal(["5 stars", "4 stars", "3 stars", "2 stars", "1 star"], Headers(stars));
        Assert.DoesNotContain(stars, s => s.IsChecked);
        Line(stars, "4 stars").Act!();
        await Until(() => tile.State.UserRating == 8 && tile.State.Pending == 0, "the rating to land");
        Assert.Equal(8, (await Fresh(film.RatingKey)).UserRating);
        stars = Line(MenuOf(tile), "Rate").Children!;
        Assert.Equal(["4 stars"], stars.Where(s => s.IsChecked).Select(s => s.Header));
        Assert.True(await Viewer.RateAsync(film, 7));
        stars = Line(MenuOf(tile), "Rate").Children!;
        Assert.Equal(["5 stars", "4 stars", "3½ stars", "3 stars", "2 stars", "1 star", null, "Clear my rating"], Headers(stars));
        Assert.Equal(["3½ stars"], stars.Where(s => s.IsChecked).Select(s => s.Header));
        Line(stars, "Clear my rating").Act!();
        await Until(() => tile.State.UserRating is null && tile.State.Pending == 0, "the rating to be cleared");
        Assert.Null((await Fresh(film.RatingKey)).UserRating);

        // Marked watched, the line turns round.
        Line(menu, "Mark as watched").Act!();
        await Until(() => tile.IsWatched && tile.State.Pending == 0, "the watched mark");
        Assert.True((await Fresh(film.RatingKey)).IsWatched);
        Assert.Equal("Mark as unwatched", MenuOf(tile)[2].Header);

        // Downloaded, the line becomes the download's own menu; deleted, it is a line again.
        Line(menu, "Download").Act!();
        var row = _shell.Downloads.RowFor(Session.MachineIdentifier!, film);
        await Until(() => row.IsDone, "the film to download");
        var download = Line(MenuOf(tile), "Downloaded");
        Assert.Equal(["Play the downloaded copy", "Delete the download", null, "Open downloads"], Headers(download.Children!));
        Line(download.Children!, "Delete the download").Act!();
        await Until(() => row.IsGone, "the download to be deleted");
        Assert.Equal("Download", MenuOf(tile)[^3].Header);
    }

    [Fact]
    public async Task AFilmPartWayThroughResumesFromItsPlaceOrStartsOver()
    {
        var film = await Fresh(Demo.Movies[2].RatingKey);
        var offset = TimeSpan.FromMilliseconds(film.ViewOffset!.Value);
        var tile = new PosterTileViewModel(_shell, film);
        var menu = MenuOf(tile);
        var clock = offset.ToString(offset.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
        Assert.Equal([$"Resume from {clock}", "Play from the start"], Headers(menu).Take(2));

        menu[0].Act!();
        var resumed = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        Assert.Equal(film.RatingKey, resumed.Item.RatingKey);
        Assert.True(resumed.Resumes);
        menu[1].Act!();
        var over = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        Assert.NotSame(resumed, over);
        Assert.False(over.Resumes);
    }

    [Fact]
    public async Task AnEpisodeGoesToItsSeasonAndSeriesButNotToThePageItIsOn()
    {
        var show = await Fresh(Demo.Shows[3].RatingKey);
        var season = (await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None))[1];
        var episode = (await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None)).First(e => e.ViewOffset is null && e.ViewCount is null or 0);
        var tile = new LandscapeTileViewModel(_shell, episode);
        var menu = MenuOf(tile);
        Assert.Equal(["Play", null, "Mark as watched", "Rate", null, "Add to playlist", "Download", null, "Go to the season", "Go to the series", "Media info"], Headers(menu));

        Line(menu, "Go to the season").Act!();
        var page = Assert.IsType<ItemPageViewModel>(_shell.Router.Current);
        await Until(() => !page.IsLoading && page.SelectedSeason?.Season.RatingKey == season.RatingKey, "the series page on that season");
        Assert.Equal(show.RatingKey, page.Item.RatingKey);
        Assert.DoesNotContain(MenuOf(tile), e => e.Header is "Go to the season" or "Go to the series");

        // On the series page with another season chosen, the episode's own season is somewhere else.
        page.Seasons.First(s => s.Season.RatingKey != season.RatingKey).SelectCommand.Execute(null);
        Assert.Equal(["Go to the season"], Headers(MenuOf(tile)).Where(h => h?.StartsWith("Go to", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task ASeriesPlaysItsNextEpisodeShufflesThemAllAndKeepsTheNextFewByRule()
    {
        var show = await Fresh(Demo.Shows[3].RatingKey);
        var tile = new PosterTileViewModel(_shell, show);
        var menu = MenuOf(tile);
        Assert.Equal(["Play", "Shuffle", null, tile.State.IsWatched ? "Mark as unwatched" : "Mark as watched", "Rate", null, "Checking the Watchlist…", "Add to playlist", "Download"], Headers(menu));
        Assert.Equal("Add to Watchlist", (await menu[6].Later!)!.Header);

        // The episode to play: the first started or unwatched one, seasons in order, specials last.
        var seasons = await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None);
        MetadataItem? next = null;
        foreach (var season in seasons.Where(s => s.Index is not 0).Concat(seasons.Where(s => s.Index is 0)))
        {
            next = (await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None)).FirstOrDefault(e => !e.IsWatched);
            if (next is not null) break;
        }

        Assert.NotNull(next);
        Line(menu, "Play").Act!();
        await Until(() => _shell.Router.Current is PlayerPageViewModel player && player.Item.RatingKey == next.RatingKey, "the next episode to play");
        var played = (PlayerPageViewModel)_shell.Router.Current!;
        Assert.Equal(next.Progress is > 0 and < 1, played.Resumes);

        // Shuffled: every episode once, from the start, and (three tries, so chance cannot pass it) not in order.
        var leaves = (await Session.Client.GetAllLeavesAsync(show.RatingKey, CancellationToken.None)).Select(e => e.RatingKey).ToList();
        Assert.True(leaves.Count >= 4);
        var orders = new List<List<string>>();
        for (var run = 0; run < 3; run++)
        {
            var before = _shell.Router.Current;
            Line(menu, "Shuffle").Act!();
            await Until(() => _shell.Router.Current is PlayerPageViewModel player && player != before, "a shuffled episode to play");
            var shuffled = (PlayerPageViewModel)_shell.Router.Current!;
            Assert.False(shuffled.Resumes);
            var order = shuffled.Queue!.Items.Select(e => e.RatingKey).ToList();
            Assert.Equal(shuffled.Item.RatingKey, order[0]);
            Assert.Equal(leaves.Order(StringComparer.Ordinal), order.Order(StringComparer.Ordinal));
            orders.Add(order);
        }

        Assert.Contains(orders, order => !order.SequenceEqual(leaves));

        // Kept by rule: the rule in force is ticked, and can be stopped from the same menu.
        var keep = Line(menu, "Download").Children!;
        Assert.Equal(["Keep the next 3 unwatched episodes", "Keep the next 5 unwatched episodes", "Keep the next 10 unwatched episodes"], Headers(keep));
        Line(keep, "Keep the next 5 unwatched episodes").Act!();
        var group = _shell.Downloads.Group(Session.MachineIdentifier!, show);
        await Until(() => group.Rule?.Keep == 5, "the rule to be set");
        keep = Line(MenuOf(tile), "Download").Children!;
        Assert.Equal(["Keep the next 5 unwatched episodes"], keep.Where(k => k.IsChecked).Select(k => k.Header));
        Assert.Equal([null, "Stop keeping episodes of the series"], Headers(keep).TakeLast(2));
        Line(keep, "Stop keeping episodes of the series").Act!();
        await Until(() => group.Rule is null, "the rule to be dropped");
    }

    [Fact]
    public async Task ASeasonDownloadsWholeOrByRuleAndGoesToItsSeries()
    {
        var show = await Fresh(Demo.Shows[3].RatingKey);
        var season = (await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None))[1];
        var tile = new PosterTileViewModel(_shell, season);
        var menu = MenuOf(tile);
        Assert.Equal(["Play", "Shuffle", null, tile.State.IsWatched ? "Mark as unwatched" : "Mark as watched", "Rate", null, "Add to playlist", "Download", null, "Go to the series"], Headers(menu));
        Assert.Equal([$"Download all of {season.Title}", $"Keep the next 3 unwatched of {season.Title}"], Headers(Line(menu, "Download").Children!));

        Line(menu, "Go to the series").Act!();
        var page = Assert.IsType<ItemPageViewModel>(_shell.Router.Current);
        await Until(() => !page.IsLoading, "the series page");
        Assert.Equal(show.RatingKey, page.Item.RatingKey);
    }

    [Fact]
    public async Task CollectionsAndPlaylistsPlayInOrderOrShuffled()
    {
        var collection = Demo.CollectionsOf(DemoCatalog.MoviesSectionKey)[0];
        var members = Demo.ChildrenOf(collection.RatingKey);
        var menu = MenuOf(new PosterTileViewModel(_shell, collection));
        Assert.Equal(["Play", "Shuffle"], Headers(menu));
        menu[0].Act!();
        await Until(() => _shell.Router.Current is PlayerPageViewModel player && player.Item.RatingKey == members[0].RatingKey, "the collection's first film");

        await Viewer.LoadPlaylistsAsync();
        var playlist = Viewer.Playlists.First(p => p.PlaylistType == "video");
        Assert.Equal(["Play", "Shuffle"], Headers(MenuOf(new PlaylistTileViewModel(_shell, playlist))));
    }

    [Fact]
    public async Task AnAlbumPlaysQueuesRatesAndGoesToItsArtist()
    {
        var album = await Fresh(Demo.Albums[0].RatingKey);
        var menu = MenuOf(new AlbumTileViewModel(_shell, album));
        Assert.Equal(["Play", "Shuffle", "Play next", "Add to the queue", null, "Rate", null, "Add to playlist", null, "Go to the artist"], Headers(menu));
        Line(menu, "Go to the artist").Act!();
        var artist = Assert.IsType<ArtistPageViewModel>(_shell.Router.Current);
        Assert.Equal(album.ParentRatingKey, artist.Artist.RatingKey);
        Assert.DoesNotContain(MenuOf(new AlbumTileViewModel(_shell, album)), e => e.Header == "Go to the artist");
    }

    [Fact]
    public async Task AWatchlistTitleOpensPlaysWhenTheServerHasItAndComesOff()
    {
        var watchlist = _shell.Watchlist!;
        var film = Demo.Movies[6];
        var title = (await watchlist.GetAsync(TestContext.Current.CancellationToken)).Single(t => t.Guid == film.Guid);
        var tile = new WatchlistTileViewModel(_shell, watchlist, title);
        Assert.Equal(["Open", null, "Remove from Watchlist"], Headers(ViewerMenu.Entries(tile, anchor: null)));

        await tile.CheckAsync();
        var menu = ViewerMenu.Entries(tile, anchor: null);
        Assert.Equal(["Play", "Open", null, "Remove from Watchlist"], Headers(menu));
        Line(menu, "Remove from Watchlist").Act!();
        await Until(() => Demo.WatchlistedAt(PlexDiscoverClient.CatalogKey(film.Guid)!) is null, "the title to come off the Watchlist");
    }

    [Fact]
    public async Task MediaInfoListsTheFileItsPictureEverySoundtrackAndEverySubtitle()
    {
        var film = Demo.Movies[3];
        var media = film.Media![0];
        var info = new MediaInfoViewModel(Session, new MetadataItem { RatingKey = film.RatingKey, Type = "movie", Title = film.Title, Year = film.Year });
        Assert.True(info.IsLoading);
        await info.LoadAsync();
        Assert.False(info.IsLoading);
        Assert.False(info.HasProblem);
        Assert.Equal($"{film.Title} ({film.Year})", info.Heading);

        // A tile's record has no streams: these come from the full record, read as the panel opens.
        var version = Assert.Single(info.Versions);
        Assert.Equal("FILE", version.Label);
        var size = media.Part![0].Size!.Value / (double)(1L << 30);
        var length = TimeSpan.FromMilliseconds(media.Duration!.Value);
        var video = (await Fresh(film.RatingKey)).Media![0].Part![0].Stream!.Single(s => s.StreamType == 1).DisplayTitle;
        Assert.Equal(
        [
            new InfoLine("File", $"{film.Title} ({film.Year}).mkv"),
            new InfoLine("Size", size.ToString("0.0", CultureInfo.InvariantCulture) + " GB"),
            new InfoLine("Length", $"{(int)length.TotalHours}h {length.Minutes}m"),
            new InfoLine("Container", "MKV"),
            new InfoLine("Bitrate", (media.Bitrate!.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"),
            new InfoLine("Video", $"{video} · 24p"),
            new InfoLine("Audio", $"English ({(media.AudioCodec == "truehd" ? "TrueHD 7.1" : "EAC3 5.1")})  ·  plays with"),
            new InfoLine(string.Empty, "Français (EAC3 5.1)"),
            new InfoLine(string.Empty, "Director's Commentary (English AAC Stereo)"),
            new InfoLine("Subtitles", "English (PGS)"),
            new InfoLine(string.Empty, "SDH (English PGS)"),
            new InfoLine(string.Empty, "Français (PGS)"),
            new InfoLine(string.Empty, "Español (SRT External)  ·  separate file"),
        ], version.Lines);
    }

    [Fact]
    public void AVersionAmongSeveralIsNumberedAndReadsItsOwnFieldsOnly()
    {
        var media = new Media
        {
            Bitrate = 700,
            Container = "mp4",
            Part =
            [
                new MediaPart { File = @"D:\Films\Short\part1.mp4", Size = 3L << 20 },
                new MediaPart { File = "/srv/films/short/part2.mp4", Size = 1L << 20 },
            ],
        };
        var version = new MediaVersionViewModel(media, 2, 3);
        Assert.Equal("VERSION 2 OF 3", version.Label);
        Assert.False(version.HasSummary);
        Assert.Equal(
        [
            new InfoLine("Part", "part1.mp4"),
            new InfoLine("Part", "part2.mp4"),
            new InfoLine("Size", "4 MB"),
            new InfoLine("Container", "MP4"),
            new InfoLine("Bitrate", "700 kbps"),
            new InfoLine("Subtitles", "None"),
        ], version.Lines);
    }

    private static IReadOnlyList<MenuEntry> MenuOf(IViewerItem item) => ViewerMenu.Entries(item, anchor: null);

    private static List<string?> Headers(IEnumerable<MenuEntry> menu) => [.. menu.Select(e => e.Header)];

    private static MenuEntry Line(IEnumerable<MenuEntry> menu, string header) => menu.Single(e => e.Header == header);

    private async Task<MetadataItem> Fresh(string key) => (await Session.Client.GetMetadataAsync(key, CancellationToken.None))!;

    /// <summary>Runs what the window would run on its UI thread, until <paramref name="done"/> holds; twenty seconds at most.</summary>
    private async Task Until(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            while (_ui.TryDequeue(out var work)) work();
            if (done()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Waited twenty seconds for {what}.");
    }
}
