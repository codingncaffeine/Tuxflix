using System.Net;
using System.Web;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.Tests;

public sealed class ShelfLayoutTests
{
    [Fact]
    public void TheServersOrderStandsUntilTheViewerMovesAShelf()
    {
        var layout = new ShelfLayout();
        Assert.Equal(["cw", "watch", "movies", "tv"], layout.Arrange(["cw", "watch", "movies", "tv"]));
    }

    [Fact]
    public void AShelfTheServerAddsLaterComesAfterItsNeighbourInTheServersOrder()
    {
        var layout = new ShelfLayout { Order = ["tv", "cw", "movies"] };

        // "new" follows "cw" on the server, so it follows "cw" in the viewer's order too; "first" leads.
        Assert.Equal(["first", "tv", "cw", "new", "movies"], layout.Arrange(["first", "cw", "new", "movies", "tv"]));

        // A shelf the server stopped offering is left out, and comes back to its place.
        Assert.Equal(["tv", "movies"], layout.Arrange(["movies", "tv"]));
        Assert.Equal(["tv", "cw", "movies"], layout.Arrange(["cw", "movies", "tv"]));
    }

    [Fact]
    public void MovingStepsOverHiddenShelvesAndStopsAtTheEnds()
    {
        var layout = new ShelfLayout();
        string[] offered = ["a", "b", "c", "d"];
        layout.Hide("b");

        // Up one among the shown ones: past the hidden "b", in front of "a".
        Assert.True(layout.Move(layout.Arrange(offered), "c", -1));
        Assert.Equal(["c", "a", "b", "d"], layout.Arrange(offered));

        Assert.False(layout.Move(layout.Arrange(offered), "c", -1));
        Assert.False(layout.Move(layout.Arrange(offered), "d", 1));
        Assert.False(layout.Move(layout.Arrange(offered), "b", 1));

        Assert.True(layout.Move(layout.Arrange(offered), "c", 1));
        Assert.Equal(["a", "c", "b", "d"], layout.Arrange(offered));
        Assert.True(layout.Move(layout.Arrange(offered), "c", 1));
        Assert.Equal(["a", "b", "d", "c"], layout.Arrange(offered));
    }

    [Fact]
    public void AHiddenShelfComesBackWhereItWas()
    {
        var layout = new ShelfLayout();
        layout.Hide("b");
        layout.Hide("b");
        Assert.Equal(["b"], layout.Hidden);

        layout.Show("b");
        Assert.Empty(layout.Hidden);
        Assert.False(layout.IsHidden("b"));
    }

    [Fact]
    public void EachServersLayoutAndTheThemeSettingSurviveASaveAndALoad()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = SettingsStore.Load(file);
        var layout = store.Current.Browse.HomeOf("server-one");
        layout.Move(layout.Arrange(["a", "b", "c"]), "c", -1);
        layout.Hide("a");
        store.Current.Browse.ThemeMusic = false;
        store.Save();
        Assert.True(store.Flush(TimeSpan.FromSeconds(10)));

        var loaded = SettingsStore.Load(file).Current.Browse;
        Assert.Equal(["a", "c", "b"], loaded.HomeOf("server-one").Arrange(["a", "b", "c"]));
        Assert.True(loaded.HomeOf("server-one").IsHidden("a"));
        Assert.Equal(["a", "b", "c"], loaded.HomeOf("server-two").Arrange(["a", "b", "c"]));
        Assert.False(loaded.ThemeMusic);
    }
}

public sealed class DiscoverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static HttpClient Client(HttpMessageHandler network) =>
        new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);

    [Theory]
    [InlineData("plex://movie/5d776836999c64001ec2f847", "5d776836999c64001ec2f847")]
    [InlineData("plex://show/5d9c08714eefaa001f5dda22", "5d9c08714eefaa001f5dda22")]
    [InlineData("local://17258", null)]
    [InlineData("com.plexapp.agents.imdb://tt0111161?lang=en", null)]
    [InlineData("plex://movie/", null)]
    [InlineData(null, null)]
    public void OnlyTitlesOfPlexsCatalogueHaveACatalogueKey(string? guid, string? key) =>
        Assert.Equal(key, PlexDiscoverClient.CatalogKey(guid));

    [Fact]
    public async Task TheWatchlistIsReadAPageAtATimeWithTheAccountToken()
    {
        const int total = 230;
        var sizes = new List<(int Start, int Size)>();
        string? token = null;
        var network = new FakeNetwork(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var start = int.Parse(query["X-Plex-Container-Start"]!, System.Globalization.CultureInfo.InvariantCulture);
            var size = int.Parse(query["X-Plex-Container-Size"]!, System.Globalization.CultureInfo.InvariantCulture);
            lock (sizes) sizes.Add((start, size));
            token = request.Headers.GetValues("X-Plex-Token").Single();
            var count = Math.Max(0, Math.Min(size, total - start));
            var items = string.Join(",", Enumerable.Range(start, count).Select(i => $$$"""{"ratingKey":"k{{{i}}}","guid":"plex://movie/k{{{i}}}","type":"movie","title":"Title {{{i}}}"}"""));
            return Task.FromResult(FakeNetwork.Json($$$"""{"MediaContainer":{"size":{{{count}}},"totalSize":{{{total}}},"offset":{{{start}}},"Metadata":[{{{items}}}]}}"""));
        });

        var discover = new PlexDiscoverClient(Client(network), "account-token");
        var watchlist = await discover.GetWatchlistAsync(TestContext.Current.CancellationToken);

        Assert.Equal(total, watchlist.Count);
        Assert.Equal(Enumerable.Range(0, total).Select(i => $"k{i}"), watchlist.Select(t => t.RatingKey));
        Assert.Equal([(0, 100), (100, 100), (200, 100)], sizes);
        Assert.All(sizes, s => Assert.InRange(s.Size, 1, PlexDiscoverClient.PageSize));
        Assert.Equal("account-token", token);
        Assert.All(network.Asked, url => Assert.StartsWith("https://discover.provider.plex.tv/library/sections/watchlist/all?", url, StringComparison.Ordinal));
        Assert.All(network.Asked, url => Assert.DoesNotContain("account-token", url, StringComparison.Ordinal));

        // A limit stops early and asks for no more than it needs.
        sizes.Clear();
        Assert.Equal(20, (await discover.GetWatchlistAsync(TestContext.Current.CancellationToken, limit: 20)).Count);
        Assert.Equal([(0, 20)], sizes);
    }

    [Fact]
    public async Task AddingAndRemovingArePutsNamingTheCatalogueKey()
    {
        var asked = new List<(HttpMethod Method, string Path, string? Key)>();
        var network = new FakeNetwork(request =>
        {
            lock (asked) asked.Add((request.Method, request.RequestUri!.AbsolutePath, HttpUtility.ParseQueryString(request.RequestUri.Query)["ratingKey"]));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var discover = new PlexDiscoverClient(Client(network), "account-token");

        await discover.AddAsync("5d776836999c64001ec2f847", TestContext.Current.CancellationToken);
        await discover.RemoveAsync("5d776836999c64001ec2f847", TestContext.Current.CancellationToken);

        Assert.Equal(
            [(HttpMethod.Put, "/actions/addToWatchlist", "5d776836999c64001ec2f847"), (HttpMethod.Put, "/actions/removeFromWatchlist", "5d776836999c64001ec2f847")],
            asked);
    }

    [Fact]
    public async Task ARefusedSignInSaysSo()
    {
        var network = new FakeNetwork(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var discover = new PlexDiscoverClient(Client(network), "expired");
        await Assert.ThrowsAsync<PlexUnauthorizedException>(() => discover.GetWatchlistAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PlexUnauthorizedException>(() => discover.AddAsync("x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheDemosWatchlistChangesAndItsTitlesAreFoundInItsLibrary()
    {
        var catalog = DemoCatalog.Create(Now);
        var discover = new PlexDiscoverClient(Client(new DemoDiscoverHandler(catalog)), "demo", DemoDiscoverHandler.BaseUri);
        var server = new PlexServerClient(Client(new DemoPlexHandler(catalog, art: null)), DemoPlexHandler.BaseUri, null, "Demo", isDemo: true);
        var cancellation = TestContext.Current.CancellationToken;

        var before = await discover.GetWatchlistAsync(cancellation);
        Assert.Equal(7, before.Count);

        // Four are in the library, found by their guid; three are not.
        var copies = new List<MetadataItem?>();
        foreach (var title in before) copies.Add(await server.FindByGuidAsync(title.Guid!, cancellation));
        Assert.Equal(4, copies.Count(c => c is not null));
        Assert.All(before.Zip(copies).Where(p => p.Second is not null), p => Assert.Equal(p.First.Guid, p.Second!.Guid));

        // A film not yet on it goes on at the top, and comes off again.
        var film = catalog.Movies[0];
        var key = PlexDiscoverClient.CatalogKey(film.Guid)!;
        Assert.False(await discover.IsOnWatchlistAsync(key, cancellation));
        await discover.AddAsync(key, cancellation);
        Assert.True(await discover.IsOnWatchlistAsync(key, cancellation));
        var after = await discover.GetWatchlistAsync(cancellation);
        Assert.Equal(film.Guid, after[0].Guid);
        await discover.RemoveAsync(key, cancellation);
        Assert.False(await discover.IsOnWatchlistAsync(key, cancellation));
        Assert.Equal(before.Select(t => t.RatingKey), (await discover.GetWatchlistAsync(cancellation)).Select(t => t.RatingKey));

        // The demo refuses what the provider refuses: a change that is not a PUT.
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(DemoDiscoverHandler.BaseUri, "actions/addToWatchlist?ratingKey=" + key));
        using var refused = await Client(new DemoDiscoverHandler(catalog)).SendAsync(post, cancellation);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
    }
}

public sealed class HomeServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static PlexServerClient Demo(DemoCatalog catalog) =>
        new(new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(new DemoPlexHandler(catalog, art: null)), DemoPlexHandler.BaseUri, null, "Demo", isDemo: true);

    [Fact]
    public async Task HomeTakesThePromotedListAndAnOlderServersGlobalHubsPromotedFirst()
    {
        var catalog = DemoCatalog.Create(Now);
        var promoted = await Demo(catalog).GetPromotedHubsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(catalog.PromotedHubs().Select(h => h.HubIdentifier), promoted.Select(h => h.HubIdentifier));

        var network = new FakeNetwork(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/hubs" => FakeNetwork.Json("""{"MediaContainer":{"size":3,"Hub":[{"hubIdentifier":"a","title":"A","promoted":false},{"hubIdentifier":"b","title":"B","promoted":true},{"hubIdentifier":"c","title":"C","promoted":1}]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));
        var older = new PlexServerClient(new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network), new Uri("http://older.invalid:32400/"), "t", "Older");
        var hubs = await older.GetPromotedHubsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["b", "c", "a"], hubs.Select(h => h.HubIdentifier));
        Assert.Contains(network.Asked, url => url.Contains("/hubs/promoted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnItemsPageCarriesItsExtrasReviewsAndThemeAndEachExtraCanBePlayed()
    {
        var catalog = DemoCatalog.Create(Now);
        var client = Demo(catalog);
        var cancellation = TestContext.Current.CancellationToken;
        var show = catalog.Shows[0];

        var details = await client.GetItemDetailsAsync(show.RatingKey, cancellation);
        Assert.NotNull(details);
        Assert.NotNull(details.Theme);
        Assert.NotEmpty(details.Review!);
        Assert.All(details.Review!, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));

        var extras = await client.GetExtrasAsync(show.RatingKey, cancellation);
        Assert.Equal(details.Extras!.Metadata!.Select(e => e.RatingKey), extras.Select(e => e.RatingKey));
        Assert.Contains(extras, e => e.Subtype == "trailer");
        foreach (var extra in extras)
        {
            var own = await client.GetMetadataAsync(extra.RatingKey, cancellation);
            Assert.Equal("clip", own!.Type);
            Assert.NotNull(own.Media![0].Part![0].Key);
        }

        var related = await client.GetRelatedHubsAsync(catalog.Movies[0].RatingKey, cancellation);
        Assert.NotEmpty(related);
        Assert.All(related, hub => Assert.DoesNotContain(hub.Metadata!, i => i.RatingKey == catalog.Movies[0].RatingKey));
    }

    [Fact]
    public async Task PlaySomethingPicksAnUnwatchedFilmOrTheNextEpisodeOfAnUnfinishedSeries()
    {
        var catalog = DemoCatalog.Create(Now);
        var client = Demo(catalog);
        var cancellation = TestContext.Current.CancellationToken;
        var movies = catalog.Sections.Single(s => s.Type == "movie");
        var shows = catalog.Sections.Single(s => s.Type == "show");

        var films = new List<MetadataItem>();
        for (var i = 0; i < 24; i++) films.Add((await client.PickSomethingAsync(movies, cancellation, new Random(i)))!);
        Assert.All(films, f => Assert.Contains(f.RatingKey, catalog.Movies.Where(m => !m.IsWatched && m.Progress is null or < 1).Select(m => m.RatingKey)));
        Assert.All(films, f => Assert.False(catalog.Find(f.RatingKey)!.IsWatched));
        Assert.True(films.Select(f => f.RatingKey).Distinct().Count() > 3);

        for (var i = 0; i < 12; i++)
        {
            var episode = (await client.PickSomethingAsync(shows, cancellation, new Random(i)))!;
            Assert.Equal("episode", episode.Type);
            var all = catalog.ChildrenOf(episode.GrandparentRatingKey!).SelectMany(season => catalog.ChildrenOf(season.RatingKey)).ToList();
            Assert.Equal(all.First(e => !e.IsWatched).RatingKey, episode.RatingKey);
        }

        // A library with nothing left to watch picks nothing.
        var watched = new FakeNetwork(_ => Task.FromResult(FakeNetwork.Json("""{"MediaContainer":{"size":0,"totalSize":0}}""")));
        var done = new PlexServerClient(new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(watched), new Uri("http://done.invalid:32400/"), "t", "Done");
        Assert.Null(await done.PickSomethingAsync(movies, cancellation));
        Assert.Single(watched.Asked);
    }
}

/// <summary>Numbers the servers write as words.</summary>
public sealed class LenientNumberTests
{
    [Fact]
    public void PlexTvsWatchlistNamesItsSectionWithAWord()
    {
        // The start of plex.tv's own answer for an empty Watchlist.
        const string answer = """{"MediaContainer":{"librarySectionID":"watchlist","librarySectionTitle":"Watchlist","identifier":"tv.plex.provider.discover","offset":0,"totalSize":0,"size":0}}""";

        var container = System.Text.Json.JsonSerializer.Deserialize(answer, Tuxflix.Core.Plex.PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!;

        Assert.Null(container.LibrarySectionId);
        Assert.Equal("Watchlist", container.LibrarySectionTitle);
    }

    [Theory]
    [InlineData("17", 17)]
    [InlineData("\"17\"", 17)]
    [InlineData("null", null)]
    [InlineData("\"watchlist\"", null)]
    public void ASectionIdReadsAsANumberWhenItIsOne(string written, int? expected)
    {
        var json = "{\"MediaContainer\":{\"librarySectionID\":" + written + ",\"Metadata\":[{\"ratingKey\":\"1\",\"librarySectionID\":" + written + "}]}}";

        var container = System.Text.Json.JsonSerializer.Deserialize(json, Tuxflix.Core.Plex.PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!;

        Assert.Equal(expected, container.LibrarySectionId);
        Assert.Equal(expected, container.Metadata![0].LibrarySectionId);
    }
}
