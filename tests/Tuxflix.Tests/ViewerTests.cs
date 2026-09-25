using System.Net;
using System.Net.WebSockets;
using System.Text;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>The notification socket: its address, its pauses between attempts, and what it reads.</summary>
public sealed class NotificationTests
{
    [Fact]
    public void TheSocketAddressIsTheServersOwnWithNoTokenInIt()
    {
        Assert.Equal("wss://10-0-0-5.abc.plex.direct:32400/:/websockets/notifications", PlexNotificationClient.SocketUri(new Uri("https://10-0-0-5.abc.plex.direct:32400/")).AbsoluteUri);
        Assert.Equal("ws://192.168.1.4:32400/:/websockets/notifications", PlexNotificationClient.SocketUri(new Uri("http://192.168.1.4:32400")).AbsoluteUri);

        // A server behind a proxy keeps its path; a query on the base (a token, say) never travels.
        var proxied = PlexNotificationClient.SocketUri(new Uri("https://media.example.org/plex/?X-Plex-Token=secret"));
        Assert.Equal("wss://media.example.org/plex/:/websockets/notifications", proxied.AbsoluteUri);
        Assert.DoesNotContain("secret", proxied.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectionWaitsDoubleFromOneSecondToAMinute()
    {
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(1, 8).Select(n => PlexNotificationClient.ReconnectDelay(n).TotalSeconds));
        Assert.Equal(60, PlexNotificationClient.ReconnectDelay(500).TotalSeconds);

        // The jitter stretches or shrinks by a fifth at most.
        Assert.Equal(9.6, PlexNotificationClient.ReconnectDelay(4, 1).TotalSeconds, 6);
        Assert.Equal(6.4, PlexNotificationClient.ReconnectDelay(4, -1).TotalSeconds, 6);
        Assert.Equal(72, PlexNotificationClient.ReconnectDelay(9, 5).TotalSeconds, 6);
    }

    [Fact]
    public void EveryKindOfNotificationReadsWithNumbersWrittenEitherWay()
    {
        var playing = Parse("""{"NotificationContainer":{"type":"playing","size":1,"PlaySessionStateNotification":[{"sessionKey":"12","clientIdentifier":"abc","guid":"","ratingKey":"20883","url":"","key":"/library/metadata/20883","viewOffset":61000,"playQueueItemID":400,"playQueueID":7,"state":"paused"}]}}""");
        Assert.Equal("playing", playing.Type);
        var state = Assert.Single(playing.Playing!);
        Assert.Equal(("12", "20883", 61000L, "paused"), (state.SessionKey, state.RatingKey, state.ViewOffset!.Value, state.State));

        // Some servers write the numbers of a session as numbers.
        Assert.Equal("12", Assert.Single(Parse("""{"NotificationContainer":{"type":"playing","PlaySessionStateNotification":[{"sessionKey":12,"ratingKey":20883}]}}""").Playing!).SessionKey);

        var timeline = Parse("""{"NotificationContainer":{"type":"timeline","size":2,"TimelineEntry":[{"identifier":"com.plexapp.plugins.library","sectionID":"1","itemID":"10141","type":1,"state":5,"metadataState":"created","updatedAt":1790301266},{"identifier":"com.plexapp.plugins.library","sectionID":"2","itemID":"77","type":-1,"state":9}]}}""");
        Assert.Equal([10141L, 77L], timeline.Timeline!.Select(t => t.ItemId!.Value));
        Assert.True(timeline.Timeline![0].IsLibraryChange);
        Assert.False(timeline.Timeline![0].IsRemoval);
        Assert.True(timeline.Timeline![1].IsRemoval);

        var activity = Parse("""{"NotificationContainer":{"type":"activity","size":1,"ActivityNotification":[{"event":"updated","uuid":"u1","Activity":{"uuid":"u1","type":"library.update.section","cancellable":false,"userID":1,"title":"Scanning Movies","subtitle":"Harbor of Small Lights","progress":42,"Context":{"librarySectionID":"1"}}}]}}""");
        var scan = Assert.Single(activity.Activities!);
        Assert.Equal(("updated", "Scanning Movies", 42, 1L), (scan.Event, scan.Activity!.Title, scan.Activity.Progress!.Value, scan.Activity.Context!.LibrarySectionId!.Value));

        Assert.Equal("LIBRARY_UPDATE", Assert.Single(Parse("""{"NotificationContainer":{"type":"status","size":1,"StatusNotification":[{"title":"Library scan complete","description":"","notificationName":"LIBRARY_UPDATE"}]}}""").Status!).NotificationName);
        Assert.Equal("Scanning folder", Assert.Single(Parse("""{"NotificationContainer":{"type":"progress","size":1,"ProgressNotification":[{"message":"Scanning folder"}]}}""").Progress!).Message);
        var transcode = Assert.Single(Parse("""{"NotificationContainer":{"type":"transcodeSession.update","size":1,"TranscodeSession":[{"key":"/transcode/sessions/abc","throttled":false,"complete":false,"progress":12.5,"speed":3.1}]}}""").TranscodeSessions!);
        Assert.Equal(12.5, transcode.Progress);
        Assert.Equal("FriendlyName", Assert.Single(Parse("""{"NotificationContainer":{"type":"preference","size":1,"Setting":[{"id":"FriendlyName","type":"text","value":"Den"}]}}""").Settings!).Id);
        Assert.Equal("queueRegenerated", Assert.Single(Parse("""{"NotificationContainer":{"type":"backgroundProcessingQueue","size":1,"BackgroundProcessingQueueEventNotification":[{"queueID":1,"event":"queueRegenerated"}]}}""").BackgroundQueue!).Event);
    }

    [Fact]
    public void AMessageThatIsNotANotificationIsLeftOut()
    {
        Assert.Null(NotificationContainer.Parse("not json"u8));
        Assert.Null(NotificationContainer.Parse("""{"NotificationContainer":{"type":"timeline","TimelineEntry":[{"itemID":"not a number"}]}}"""u8));
        Assert.Null(NotificationContainer.Parse("""{"something":"else"}"""u8));
    }

    [Fact]
    public async Task TheClientSendsItsTokenInAHeaderAndComesBackAfterTheServerHangsUp()
    {
        // A local socket server that accepts only a token in the header, speaks once, and hangs up.
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var tokensSeen = new List<string?>();
        var queries = new List<string>();
        var serving = Task.Run(async () =>
        {
            for (var connection = 0; connection < 2; connection++)
            {
                var context = await server.GetContextAsync();
                lock (tokensSeen)
                {
                    tokensSeen.Add(context.Request.Headers["X-Plex-Token"]);
                    queries.Add(context.Request.Url!.Query);
                }

                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var message = $$$"""{"NotificationContainer":{"type":"timeline","size":1,"TimelineEntry":[{"identifier":"com.plexapp.plugins.library","itemID":"{{{connection + 1}}}","state":5}]}}""";
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }, TestContext.Current.CancellationToken);

        var received = new List<long>();
        var connections = new List<bool>();
        var identity = new PlexClientIdentity("test-client", "0.0.0", "tests");
        using (var client = new PlexNotificationClient(new Uri($"http://127.0.0.1:{port}/"), "secret-token", identity))
        {
            client.Received += c => { lock (received) received.Add(c.Timeline![0].ItemId!.Value); };
            client.ConnectionChanged += up => { lock (connections) connections.Add(up); };
            client.Start();

            // The second connection comes after the first pause, one second give or take a fifth.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (received)
                {
                    if (received.Count == 2) break;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L], received);
        Assert.Equal(["secret-token", "secret-token"], tokensSeen);
        Assert.All(queries, q => Assert.DoesNotContain("secret", q, StringComparison.Ordinal));
        Assert.Equal([true, false, true], connections.Take(3));
    }

    private static NotificationContainer Parse(string json) =>
        NotificationContainer.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidOperationException("It did not parse.");

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

/// <summary>The viewer's writes: the requests they send, and the demo server keeping them.</summary>
public sealed class ViewerWriteTests
{
    private static (PlexServerClient Client, List<HttpRequestMessage> Sent) Recording(string body = "")
    {
        var sent = new List<HttpRequestMessage>();
        var network = new FakeNetwork(request =>
        {
            lock (sent) sent.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });
        var http = new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);
        return (new PlexServerClient(http, new Uri("http://10.0.0.5:32400/"), "secret-token", "Den"), sent);
    }

    [Fact]
    public async Task EveryWriteSendsTheDocumentedRequestWithTheTokenInAHeader()
    {
        var (client, sent) = Recording();
        var none = CancellationToken.None;
        await client.MarkWatchedAsync("1001", none);
        await client.MarkUnwatchedAsync("1001", none);
        await client.RateAsync("1001", 7, none);
        await client.RateAsync("1001", null, none);
        await client.ChooseStreamsAsync(7000, 70002, 0, none);
        await client.AddToPlaylistAsync("8001", "machine-1", ["1001", "1002"], none);
        await client.RemoveFromPlaylistAsync("8001", 5003, none);
        await client.MovePlaylistItemAsync("8001", 5003, 5001, none);
        await client.MovePlaylistItemAsync("8001", 5003, null, none);
        await client.RenamePlaylistAsync("8001", "Rainy & Sunday", none);
        await client.DeletePlaylistAsync("8001", none);
        await client.CreatePlaylistAsync("Rainy Day", "video", "machine-1", ["1001"], none);

        Assert.Equal(
            [
                "PUT /:/scrobble?identifier=com.plexapp.plugins.library&key=1001",
                "PUT /:/unscrobble?identifier=com.plexapp.plugins.library&key=1001",
                "PUT /:/rate?identifier=com.plexapp.plugins.library&key=1001&rating=7",
                "PUT /:/rate?identifier=com.plexapp.plugins.library&key=1001&rating=-1",
                "PUT /library/parts/7000?allParts=1&audioStreamID=70002&subtitleStreamID=0",
                "PUT /playlists/8001/items?uri=server%3A%2F%2Fmachine-1%2Fcom.plexapp.plugins.library%2Flibrary%2Fmetadata%2F1001%2C1002",
                "DELETE /playlists/8001/items/5003",
                "PUT /playlists/8001/items/5003/move?after=5001",
                "PUT /playlists/8001/items/5003/move",
                "PUT /playlists/8001?title=Rainy%20%26%20Sunday",
                "DELETE /playlists/8001",
                "POST /playlists?type=video&title=Rainy%20Day&smart=0&uri=server%3A%2F%2Fmachine-1%2Fcom.plexapp.plugins.library%2Flibrary%2Fmetadata%2F1001",
            ],
            sent.Select(r => $"{r.Method} {r.RequestUri!.PathAndQuery}"));
        Assert.All(sent, r => Assert.Equal("secret-token", r.Headers.GetValues("X-Plex-Token").Single()));
        Assert.All(sent, r => Assert.DoesNotContain("secret", r.RequestUri!.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryAsksForOneAccountFromADateNewestFirst()
    {
        var (client, sent) = Recording("""{"MediaContainer":{"size":0}}""");
        await client.GetHistoryAsync(1, DateTimeOffset.FromUnixTimeSeconds(1789734168), 0, 50, CancellationToken.None);
        await client.GetHistoryAsync(null, null, 50, 50, CancellationToken.None);

        Assert.Equal("/status/sessions/history/all?sort=viewedAt:desc&accountID=1&viewedAt%3E=1789734168", sent[0].RequestUri!.PathAndQuery);
        Assert.Equal("/status/sessions/history/all?sort=viewedAt:desc", sent[1].RequestUri!.PathAndQuery);
        Assert.Equal("50", sent[1].Headers.GetValues("X-Plex-Container-Start").Single());
    }

    [Fact]
    public async Task ANewPlaylistComesBackAsTheServerMadeIt()
    {
        var (client, _) = Recording("""{"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"32114","key":"/playlists/32114/items","type":"playlist","title":"Rainy Day","smart":false,"playlistType":"video","leafCount":1}]}}""");
        var made = await client.CreatePlaylistAsync("Rainy Day", "video", "machine-1", ["1001"], CancellationToken.None);
        Assert.Equal(("32114", "video", 1), (made!.RatingKey, made.PlaylistType, made.LeafCount!.Value));
    }

    private static (PlexServerClient Client, DemoCatalog Catalog) Demo()
    {
        var catalog = DemoCatalog.Create(new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero));
        var http = new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(new DemoPlexHandler(catalog, art: null));
        return (new PlexServerClient(http, DemoPlexHandler.BaseUri, token: null, "Demo Library", isDemo: true), catalog);
    }

    [Fact]
    public async Task TheDemoKeepsWatchedMarksAndCountsThemUpTheSeries()
    {
        var (client, catalog) = Demo();
        var none = CancellationToken.None;
        var changes = new List<DemoChange>();
        catalog.Changed += c => changes.AddRange(c);

        var show = catalog.Shows[3];
        var season = (await client.GetChildrenAsync(show.RatingKey, none))[0];
        var episode = (await client.GetChildrenAsync(season.RatingKey, none))[0];
        Assert.False(episode.IsWatched);
        var watchedBefore = (await client.GetMetadataAsync(show.RatingKey, none))!.ViewedLeafCount ?? 0;

        await client.MarkWatchedAsync(episode.RatingKey, none);
        Assert.True((await client.GetMetadataAsync(episode.RatingKey, none))!.IsWatched);
        Assert.Equal(watchedBefore + 1, (await client.GetMetadataAsync(show.RatingKey, none))!.ViewedLeafCount);
        Assert.Equal(1, (await client.GetMetadataAsync(season.RatingKey, none))!.ViewedLeafCount);
        Assert.Contains(changes, c => c.RatingKey == episode.RatingKey);
        Assert.Contains(changes, c => c.RatingKey == show.RatingKey);

        await client.MarkUnwatchedAsync(episode.RatingKey, none);
        Assert.False((await client.GetMetadataAsync(episode.RatingKey, none))!.IsWatched);
        Assert.Equal(watchedBefore, (await client.GetMetadataAsync(show.RatingKey, none))!.ViewedLeafCount ?? 0);

        // A whole series at once.
        await client.MarkWatchedAsync(show.RatingKey, none);
        var marked = await client.GetMetadataAsync(show.RatingKey, none);
        Assert.Equal(marked!.LeafCount, marked.ViewedLeafCount);
        Assert.True(marked.IsWatched);
        foreach (var each in await client.GetChildrenAsync(show.RatingKey, none))
        {
            Assert.All(await client.GetChildrenAsync(each.RatingKey, none), e => Assert.True(e.IsWatched));
        }

        // A key the server does not have is a 404, as a server answers.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.MarkWatchedAsync("999999", none));
    }

    [Fact]
    public async Task TheDemoKeepsRatingsAndStreamChoices()
    {
        var (client, catalog) = Demo();
        var none = CancellationToken.None;
        var movie = catalog.Movies[1];

        await client.RateAsync(movie.RatingKey, 7, none);
        Assert.Equal(7, (await client.GetMetadataAsync(movie.RatingKey, none))!.UserRating);
        await client.RateAsync(movie.RatingKey, null, none);
        Assert.Null((await client.GetMetadataAsync(movie.RatingKey, none))!.UserRating);

        var part = (await client.GetMetadataAsync(movie.RatingKey, none))!.Media![0].Part![0];
        var french = part.Stream!.First(s => s.StreamType == StreamChoice.Audio && s.LanguageCode == "fra");
        var sdh = part.Stream!.First(s => s.StreamType == StreamChoice.Subtitle && s.Title == "SDH");
        Assert.Null(StreamChoice.Selected(part, StreamChoice.Subtitle));

        await client.ChooseStreamsAsync(part.Id, french.Id, sdh.Id, none);
        part = (await client.GetMetadataAsync(movie.RatingKey, none))!.Media![0].Part![0];
        Assert.Equal(french.Id, StreamChoice.Selected(part, StreamChoice.Audio)!.Id);
        Assert.Equal(sdh.Id, StreamChoice.Selected(part, StreamChoice.Subtitle)!.Id);

        // Subtitle 0 is none; the audio is left as it was when not named.
        await client.ChooseStreamsAsync(part.Id, null, 0, none);
        part = (await client.GetMetadataAsync(movie.RatingKey, none))!.Media![0].Part![0];
        Assert.Equal(french.Id, StreamChoice.Selected(part, StreamChoice.Audio)!.Id);
        Assert.Null(StreamChoice.Selected(part, StreamChoice.Subtitle));
    }

    [Fact]
    public async Task TheDemoEditsPlaylistsAsAServerDoes()
    {
        var (client, catalog) = Demo();
        var none = CancellationToken.None;
        var keys = catalog.Movies.Take(3).Select(m => m.RatingKey).ToList();

        var made = await client.CreatePlaylistAsync("Tonight", "video", DemoCatalog.MachineIdentifier, keys, none);
        Assert.NotNull(made);
        Assert.Contains(await client.GetPlaylistsAsync(none), p => p.RatingKey == made.RatingKey && p.Title == "Tonight" && p.LeafCount == 3);

        async Task<List<MetadataItem>> Items() => (await client.GetPlaylistItemsAsync(made.RatingKey, 0, 100, none)).Metadata ?? [];
        var items = await Items();
        Assert.Equal(keys, items.Select(i => i.RatingKey));
        Assert.All(items, i => Assert.NotNull(i.PlaylistItemId));

        // The same film twice is two entries, told apart by their own ids.
        await client.AddToPlaylistAsync(made.RatingKey, DemoCatalog.MachineIdentifier, [keys[0]], none);
        items = await Items();
        Assert.Equal([keys[0], keys[1], keys[2], keys[0]], items.Select(i => i.RatingKey));

        // The last entry to just after the first, then the third to the top: told apart by the
        // entries' own ids, as the film stands in the playlist twice.
        var ids = items.Select(i => i.PlaylistItemId!.Value).ToList();
        async Task<List<long>> Ids() => [.. (await Items()).Select(i => i.PlaylistItemId!.Value)];
        await client.MovePlaylistItemAsync(made.RatingKey, ids[3], ids[0], none);
        Assert.Equal([ids[0], ids[3], ids[1], ids[2]], await Ids());
        await client.MovePlaylistItemAsync(made.RatingKey, ids[2], null, none);
        Assert.Equal([ids[2], ids[0], ids[3], ids[1]], await Ids());
        Assert.Equal([keys[2], keys[0], keys[0], keys[1]], (await Items()).Select(i => i.RatingKey));

        await client.RemoveFromPlaylistAsync(made.RatingKey, ids[0], none);
        Assert.Equal([ids[2], ids[3], ids[1]], await Ids());

        await client.RenamePlaylistAsync(made.RatingKey, "Late Tonight", none);
        Assert.Contains(await client.GetPlaylistsAsync(none), p => p.RatingKey == made.RatingKey && p.Title == "Late Tonight");

        await client.DeletePlaylistAsync(made.RatingKey, none);
        Assert.DoesNotContain(await client.GetPlaylistsAsync(none), p => p.RatingKey == made.RatingKey);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RenamePlaylistAsync(made.RatingKey, "Gone", none));
    }

    [Fact]
    public async Task TheDemoHistoryKeepsToOneAccountAndAPeriodAndPages()
    {
        var (client, catalog) = Demo();
        var none = CancellationToken.None;
        var all = (await client.GetHistoryAsync(null, null, 0, 1000, none)).Metadata!;
        var mine = await client.GetHistoryAsync(DemoCatalog.ViewerAccountId, null, 0, 1000, none);
        Assert.Contains(all, h => h.AccountId == DemoCatalog.OtherAccountId);
        Assert.All(mine.Metadata!, h => Assert.Equal(DemoCatalog.ViewerAccountId, h.AccountId));
        Assert.Equal(mine.Metadata!.OrderByDescending(h => h.ViewedAt).Select(h => h.HistoryKey), mine.Metadata!.Select(h => h.HistoryKey));

        var weekAgo = catalog.Now.AddDays(-7);
        var recent = (await client.GetHistoryAsync(DemoCatalog.ViewerAccountId, weekAgo, 0, 1000, none)).Metadata!;
        Assert.NotEmpty(recent);
        Assert.True(recent.Count < mine.Metadata!.Count);
        Assert.All(recent, h => Assert.True(h.ViewedAt >= weekAgo.ToUnixTimeSeconds()));

        var second = await client.GetHistoryAsync(DemoCatalog.ViewerAccountId, null, 5, 5, none);
        Assert.Equal(mine.Metadata!.Skip(5).Take(5).Select(h => h.HistoryKey), second.Metadata!.Select(h => h.HistoryKey));
        Assert.Equal(mine.Metadata!.Count, second.TotalSize);
    }

    [Fact]
    public async Task TheDemoSessionMovesWithTheClockAndItsSourceReportsIt()
    {
        var (client, catalog) = Demo();
        var clock = catalog.Now;
        catalog.Clock = () => clock;
        var sessions = await client.GetSessionsAsync(CancellationToken.None);
        var mine = sessions.Single(s => s.User?.Id == "1");
        Assert.Contains(sessions, s => s.User?.Id != "1");
        Assert.Equal("Living Room", mine.Player!.Title);

        using var source = new DemoNotificationSource(catalog, TimeSpan.Zero);
        var received = new List<NotificationContainer>();
        source.Received += received.Add;
        source.Start();
        source.Tick();
        clock = clock.AddSeconds(30);
        source.Tick();
        var offsets = received.Where(r => r.Type == "playing").Select(r => r.Playing![0].ViewOffset!.Value).ToList();
        Assert.Equal(mine.ViewOffset, offsets[0]);
        Assert.Equal(30_000, offsets[1] - offsets[0]);

        // A change the viewer makes comes back as a settled timeline entry.
        await client.MarkWatchedAsync(catalog.Movies[0].RatingKey, CancellationToken.None);
        var timeline = received.Last(r => r.Type == "timeline");
        Assert.Contains(timeline.Timeline!, t => t.ItemId == long.Parse(catalog.Movies[0].RatingKey, System.Globalization.CultureInfo.InvariantCulture) && t.IsLibraryChange);
    }
}

/// <summary>Hours watched, summed from the history.</summary>
public sealed class WatchStatsTests
{
    private static MetadataItem Viewed(string key, string type, string at, string? show = null) => new()
    {
        RatingKey = key,
        Type = type,
        Title = key,
        GrandparentTitle = show,
        GrandparentRatingKey = show is null ? null : "show-" + show,
        ViewedAt = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
    };

    [Fact]
    public void WeeksMonthsAndSeriesAddUpFromTheirFirstDays()
    {
        var now = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Viewed("e1", "episode", "2026-09-24T21:00:00Z", "A"),
            Viewed("m1", "movie", "2026-09-21T22:00:00Z"),
            Viewed("t1", "track", "2026-09-25T10:00:00Z"),
            Viewed("e2", "episode", "2026-08-30T20:00:00Z", "A"),
            Viewed("e3", "episode", "2026-09-10T20:00:00Z", "B"),
            Viewed("gone", "movie", "2026-09-23T20:00:00Z"),
            Viewed("e4", "episode", "2026-01-05T20:00:00Z", "C"),
        };
        var durations = new Dictionary<string, long>
        {
            ["e1"] = 3_600_000, ["m1"] = 7_200_000, ["t1"] = 360_000, ["e2"] = 1_800_000, ["e3"] = 5_400_000, ["e4"] = 3_600_000,
        };

        var stats = WatchStats.Compute(history, durations, now, TimeZoneInfo.Utc, DayOfWeek.Monday);

        // 25 September 2026 is a Friday: this week began on Monday the 21st, the film's day.
        Assert.Equal([0, 0, 0, 0.5, 0, 1.5, 0, 3], stats.Weeks.Select(w => w.Hours));
        Assert.Equal(new DateOnly(2026, 9, 21), stats.Weeks[^1].Start);
        Assert.Equal(new DateOnly(2026, 8, 3), stats.Weeks[0].Start);
        Assert.Equal([0, 0, 0, 0, 0.5, 4.5], stats.Months.Select(m => m.Hours));
        Assert.Equal(new DateOnly(2026, 4, 1), stats.Months[0].Start);
        Assert.Equal((3, 4.5), (stats.ThisWeek, stats.ThisMonth));

        // Level on hours, the series with more episodes first; one watched before the months shown is left out.
        Assert.Equal([("A", 2, 1.5), ("B", 1, 1.5)], stats.TopShows.Select(s => (s.Title, s.Episodes, s.Hours)));
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), WatchStats.Since(now, TimeZoneInfo.Utc, 6));
    }

    [Fact]
    public void AWeekStartsOnTheDayTheCultureStartsIt()
    {
        var now = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
        var sunday = WatchStats.Compute([Viewed("e1", "episode", "2026-09-20T12:00:00Z", "A")], new Dictionary<string, long> { ["e1"] = 3_600_000 }, now, TimeZoneInfo.Utc, DayOfWeek.Sunday);
        var monday = WatchStats.Compute([Viewed("e1", "episode", "2026-09-20T12:00:00Z", "A")], new Dictionary<string, long> { ["e1"] = 3_600_000 }, now, TimeZoneInfo.Utc, DayOfWeek.Monday);
        Assert.Equal(new DateOnly(2026, 9, 20), sunday.Weeks[^1].Start);
        Assert.Equal(1, sunday.ThisWeek);
        Assert.Equal(0, monday.ThisWeek);
        Assert.Equal(1, monday.Weeks[^2].Hours);
    }
}
