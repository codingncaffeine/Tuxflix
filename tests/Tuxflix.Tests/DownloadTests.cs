using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Downloads;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Tuxflix.Player;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>A clock a test moves by hand; timers still run on real time.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// Downloads against the demo server, which serves real ranges of generated videos: the queue's
/// order, pause and resume, a cut connection and a restart resumed with a range, the size check,
/// the speed limit, a refusal said plainly, rules, and watch changes sent once the server is back.
/// </summary>
public sealed class DownloadTests : IAsyncDisposable
{
    private const string Server = DemoCatalog.MachineIdentifier;

    private readonly Scratch _scratch = new();
    private readonly DemoCatalog _catalog = DemoCatalog.Create(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly DemoPlexHandler _server;
    private readonly HttpClient _http;
    private readonly PlexServerClient _client;
    private readonly DownloadSettings _settings = new();
    private readonly List<DownloadManager> _managers = [];

    public DownloadTests()
    {
        _server = new DemoPlexHandler(_catalog, new FlatArt());
        _http = new HttpClient(_server);

        // Not flagged as the demo: the watch reports then reach the demo server instead of stopping in the client.
        _client = new PlexServerClient(_http, DemoPlexHandler.BaseUri, "token", "Demo Library");
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private string IndexFile => Path.Combine(_scratch.Root, "downloads.json");

    private string Folder => Path.Combine(_scratch.Root, "downloads");

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers) await manager.DisposeAsync();
        _http.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public async Task OneAtATimeTheQueueRunsInItsOrderAndMoveToFrontGoesNext()
    {
        var manager = NewManager();
        var episodes = Episodes("200201", 3);
        foreach (var episode in episodes) manager.Enqueue(episode, Server, "Demo Library");
        manager.MoveToFront(episodes[2].RatingKey is var last ? Id(last) : string.Empty);
        var finished = new List<string>();
        manager.Changed += record =>
        {
            if (record.State == DownloadState.Done) lock (finished) finished.Add(record.RatingKey);
        };

        manager.Attach(Server, _client);
        foreach (var episode in episodes) await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        string[] expected = [episodes[2].RatingKey, episodes[0].RatingKey, episodes[1].RatingKey];
        Assert.Equal(expected, finished);
        Assert.Equal(expected.Select(k => $"{k} from 0"), _server.DownloadRequests);
    }

    [Fact]
    public async Task TwoAtATimeTheFirstTwoRunTogetherAndTheThirdWaits()
    {
        _settings.Simultaneous = 2;
        _settings.SpeedLimit = 3_000_000;
        var manager = NewManager();
        var episodes = Episodes("200301", 3);
        foreach (var episode in episodes) manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);

        await WaitFor(manager, episodes[1].RatingKey, r => r.State == DownloadState.Downloading && r.DoneBytes > 0);
        var states = manager.Snapshot().ToDictionary(r => r.RatingKey, r => r.State);
        Assert.Equal(DownloadState.Downloading, states[episodes[0].RatingKey]);
        Assert.Equal(DownloadState.Queued, states[episodes[2].RatingKey]);

        _settings.SpeedLimit = 0;
        await WaitFor(manager, episodes[2].RatingKey, r => r.State == DownloadState.Done);
    }

    [Fact]
    public async Task AKeptFileIsTheServersBytesWithItsMetadataAndArtworkBeside()
    {
        var manager = NewManager();
        var episode = Episodes("200101", 1)[0];
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        var done = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        var video = DemoVideo.For(episode.RatingKey);
        Assert.Equal(video.Length, done.TotalBytes);
        Assert.True(done.TotalConfirmed);
        Assert.Equal(video.ToArray(), await File.ReadAllBytesAsync(done.MediaPath, Cancel));
        Assert.False(File.Exists(done.PartialPath));
        Assert.Equal("Lanternfall - S01E01 - " + episode.Title + ".mkv", done.FileName);
        Assert.StartsWith(Path.Combine(Folder, Server), done.Folder, StringComparison.Ordinal);

        var kept = JsonSerializer.Deserialize(await File.ReadAllTextAsync(done.MetadataPath, Cancel), PlexJsonContext.Default.PlexEnvelope);
        Assert.Equal(episode.RatingKey, kept!.MediaContainer!.Metadata![0].RatingKey);
        Assert.Equal(FlatArt.Bytes, await File.ReadAllBytesAsync(done.ArtworkPath("poster.jpg"), Cancel));
        Assert.Equal(FlatArt.Bytes, await File.ReadAllBytesAsync(done.ArtworkPath("still.jpg"), Cancel));
    }

    [Fact]
    public async Task ACutConnectionCarriesOnWithARangeFromWhereTheFileStops()
    {
        var manager = NewManager();
        var episode = Episodes("200102", 1)[0];
        _server.CutNextDownloadAfter(1_000_000);
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        var done = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        Assert.Equal([$"{episode.RatingKey} from 0", $"{episode.RatingKey} from 1000000"], _server.DownloadRequests);
        Assert.Equal(DemoVideo.For(episode.RatingKey).ToArray(), await File.ReadAllBytesAsync(done.MediaPath, Cancel));
    }

    [Fact]
    public async Task AnAnswerThatEndsShortOfTheServersSizeIsNotKept()
    {
        var manager = NewManager();
        var episode = Episodes("200103", 1)[0];
        _server.CutNextDownloadAfter(700_000, quietly: true);
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        var done = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        // The first answer stopped at 700,000 bytes as if it were whole: the file was not kept, the rest was asked for.
        Assert.Equal([$"{episode.RatingKey} from 0", $"{episode.RatingKey} from 700000"], _server.DownloadRequests);
        Assert.Equal(DemoVideo.For(episode.RatingKey).Length, new FileInfo(done.MediaPath).Length);
        Assert.Equal(DemoVideo.For(episode.RatingKey).ToArray(), await File.ReadAllBytesAsync(done.MediaPath, Cancel));
    }

    [Fact]
    public async Task PauseKeepsTheBytesAndResumeAsksOnlyForTheRest()
    {
        _settings.SpeedLimit = 1_000_000;
        var manager = NewManager();
        var episode = Episodes("200202", 1)[0];
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        var running = await WaitFor(manager, episode.RatingKey, r => r.DoneBytes >= 300_000);

        manager.Pause(running.Id);
        var paused = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Paused);
        await Task.Delay(400, Cancel);
        var kept = new FileInfo(paused.PartialPath).Length;
        await Task.Delay(400, Cancel);
        Assert.Equal(kept, new FileInfo(paused.PartialPath).Length);
        Assert.InRange(kept, 300_000, DemoVideo.For(episode.RatingKey).Length - 1);

        _settings.SpeedLimit = 0;
        manager.Resume(running.Id);
        var done = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);
        Assert.Equal([$"{episode.RatingKey} from 0", $"{episode.RatingKey} from {kept}"], _server.DownloadRequests);
        Assert.Equal(DemoVideo.For(episode.RatingKey).ToArray(), await File.ReadAllBytesAsync(done.MediaPath, Cancel));
    }

    [Fact]
    public async Task TheQueueSurvivesARestartAndTheNextRunResumesWithARange()
    {
        _settings.SpeedLimit = 1_000_000;
        var first = NewManager();
        var episodes = Episodes("200203", 2);
        foreach (var episode in episodes) first.Enqueue(episode, Server, "Demo Library");
        first.Attach(Server, _client);
        await WaitFor(first, episodes[0].RatingKey, r => r.DoneBytes >= 400_000);
        await first.DisposeAsync();
        _managers.Remove(first);

        var saved = File.ReadAllText(IndexFile);
        Assert.Contains("\"state\": \"Queued\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\": \"Downloading\"", saved, StringComparison.Ordinal);

        _settings.SpeedLimit = 0;
        var second = NewManager();
        var reloaded = second.Snapshot();
        Assert.Equal(episodes.Select(e => e.RatingKey), reloaded.Select(r => r.RatingKey));
        var partial = reloaded[0].DoneBytes;
        Assert.True(partial >= 400_000, $"only {partial} bytes were kept across the restart");
        Assert.Equal(partial, new FileInfo(reloaded[0].PartialPath).Length);

        second.Attach(Server, _client);
        var done = await WaitFor(second, episodes[0].RatingKey, r => r.State == DownloadState.Done);
        await WaitFor(second, episodes[1].RatingKey, r => r.State == DownloadState.Done);
        Assert.Contains($"{episodes[0].RatingKey} from {partial}", _server.DownloadRequests);
        Assert.Equal(DemoVideo.For(episodes[0].RatingKey).ToArray(), await File.ReadAllBytesAsync(done.MediaPath, Cancel));
    }

    [Fact]
    public async Task AServerOpenedBeforeTheListIsReadStillGetsItsQueueAndItsWatchChanges()
    {
        var first = NewManager();
        var episode = Episodes("200302", 1)[0];
        first.Enqueue(episode, Server, "Demo Library");
        first.RecordPlayback(Server, "1003", 45_000, 6_000_000, send: true, finished: false);
        await first.DisposeAsync();
        _managers.Remove(first);

        // At start the server can open before a worker has read the list.
        var second = new DownloadManager(IndexFile, Folder, () => _settings) { RetryDelay = TimeSpan.FromMilliseconds(20) };
        _managers.Add(second);
        second.Attach(Server, _client);
        second.Load();

        await WaitFor(second, episode.RatingKey, r => r.State == DownloadState.Done);
        var clock = Stopwatch.StartNew();
        while (!_server.Writes.Contains("timeline 1003 stopped 45000") && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, Cancel);
        Assert.Contains("timeline 1003 stopped 45000", _server.Writes);
    }

    [Fact]
    public async Task TheSpeedLimitHoldsTheTransferInsideItsBand()
    {
        const long Rate = 2_000_000;
        _settings.SpeedLimit = Rate;
        var manager = NewManager();
        var episode = Episodes("200204", 1)[0];
        var size = DemoVideo.For(episode.RatingKey).Length;
        manager.Enqueue(episode, Server, "Demo Library");

        var clock = Stopwatch.StartNew();
        manager.Attach(Server, _client);
        await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);
        var seconds = clock.Elapsed.TotalSeconds;

        // At most a quarter of a second's allowance goes ahead of the limit; twice the time is a limit working at half speed.
        var fastest = (size - (Rate / 4.0)) / Rate;
        var slowest = 2.0 * size / Rate;
        Assert.InRange(seconds, fastest, slowest);
    }

    [Fact]
    public async Task AWithheldDownloadSaysSoPlainlyAndTheQueueGoesOn()
    {
        var manager = NewManager();
        var withheld = _catalog.Movies.Single(m => m.Title == DemoPlexHandler.WithheldTitle);
        var episode = Episodes("200401", 1)[0];
        manager.Enqueue(withheld, Server, "Demo Library");
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);

        var refused = await WaitFor(manager, withheld.RatingKey, r => r.State == DownloadState.Failed);
        Assert.Equal("The owner of Demo Library does not allow this account to download.", refused.Error);
        Assert.False(refused.Retryable);
        await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);
        Assert.Single(_server.DownloadRequests, r => r.EndsWith("withheld", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingADownloadStopsItAndDeletesItsFilesByName()
    {
        _settings.SpeedLimit = 1_000_000;
        var manager = NewManager();
        var episode = Episodes("200402", 1)[0];
        var record = manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        await WaitFor(manager, episode.RatingKey, r => r.DoneBytes >= 200_000);

        manager.Remove(record.Id);
        await Gone(record.Folder);
        Assert.Null(manager.Find(Server, episode.RatingKey));
    }

    [Fact]
    public async Task ADownloadAskedForAgainWaitsUntilTheOldFilesAreGone()
    {
        var manager = NewManager();
        var episode = Episodes("200403", 1)[0];
        var record = manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        // Deleting is held up; the same episode is asked for again meanwhile.
        using var deleting = new SemaphoreSlim(0);
        using var holding = new SemaphoreSlim(0);
        manager.BeforeDelete = _ =>
        {
            holding.Release();
            deleting.Wait(TimeSpan.FromSeconds(10));
        };
        manager.Remove(record.Id);
        Assert.True(await holding.WaitAsync(TimeSpan.FromSeconds(10), Cancel), "the files were never deleted");
        manager.Enqueue(episode, Server, "Demo Library");
        await Task.Delay(300, Cancel);
        Assert.Equal(DownloadState.Queued, manager.Find(Server, episode.RatingKey)!.State);
        Assert.Single(_server.DownloadRequests);

        deleting.Release();
        var again = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);
        Assert.Equal(DemoVideo.For(episode.RatingKey).ToArray(), await File.ReadAllBytesAsync(again.MediaPath, Cancel));
        Assert.True(File.Exists(again.MetadataPath), "the new download lost its metadata to the old one's deletion");
    }

    [Fact]
    public async Task WatchChangesMadeOfflineWaitAndAreSentInOrderWhenTheServerIsBack()
    {
        var manager = NewManager();
        manager.RecordPlayback(Server, "1001", 60_000, 7_680_000, send: true, finished: false);
        manager.RecordPlayback(Server, "1001", 90_000, 7_680_000, send: false, finished: false);
        manager.RecordPlayback(Server, "1002", 6_000_000, 6_240_000, send: true, finished: true);
        manager.RecordPlayback(Server, "1002", 30_000, 6_240_000, send: true, finished: false);
        string[] expected = ["timeline 1001 stopped 90000", "timeline 1002 stopped 6240000", "scrobble 1002", "timeline 1002 stopped 30000"];
        Assert.Equal(expected, manager.PendingWrites().Select(Describe));

        // Still out of reach: nothing is lost, and the changes outlive a restart.
        _server.RefuseWrites = true;
        manager.Attach(Server, _client);
        await manager.SyncAsync(Server, Cancel);
        Assert.Empty(_server.Writes);
        await manager.DisposeAsync();
        _managers.Remove(manager);

        var next = NewManager();
        Assert.Equal(expected, next.PendingWrites().Select(Describe));
        _server.RefuseWrites = false;
        next.Attach(Server, _client);
        await next.SyncAsync(Server, Cancel);

        Assert.Equal(expected, _server.Writes);
        Assert.Empty(next.PendingWrites());

        static string Describe(PendingWrite write) =>
            write.Kind == PendingWrite.Scrobble ? $"scrobble {write.RatingKey}" : $"timeline {write.RatingKey} stopped {write.Time}";
    }

    [Fact]
    public async Task ARuleKeepsTheNextUnwatchedEpisodesAndLetsWatchedOnesGoAfterADelay()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
        var manager = NewManager(clock);
        manager.RemoveWatchedAfter = TimeSpan.FromHours(1);
        manager.Attach(Server, _client);

        // Lanternfall: 13 of its 24 episodes are watched, so the next three are S2E6 to S2E8.
        var rule = manager.SetRule(_catalog.Shows[0], Server, "Demo Library", keep: 3);
        await manager.ApplyRulesAsync(Server, Cancel);
        string[] first = ["20010206", "20010207", "20010208"];
        foreach (var key in first) await WaitFor(manager, key, r => r.State == DownloadState.Done);
        Assert.Equal(first, manager.Snapshot().Select(r => r.RatingKey));
        Assert.All(manager.Snapshot(), r => Assert.Equal(rule.Id, r.RuleId));

        // One watched here: the next one comes, and the watched one stays for now.
        var watched = manager.Find(Server, "20010206")!;
        manager.RecordPlayback(Server, "20010206", watched.Duration, watched.Duration, send: true, finished: true);
        await manager.ApplyRulesAsync(Server, Cancel);
        await WaitFor(manager, "20010301", r => r.State == DownloadState.Done);
        Assert.NotNull(manager.Find(Server, "20010206"));

        // A while later it goes; a film downloaded by hand never does.
        var film = manager.Enqueue(_catalog.Movies[0], Server, "Demo Library");
        await WaitFor(manager, film.RatingKey, r => r.State == DownloadState.Done);
        manager.RecordPlayback(Server, film.RatingKey, film.Duration, film.Duration, send: true, finished: true);
        clock.Advance(TimeSpan.FromHours(2));
        await manager.ApplyRulesAsync(Server, Cancel);
        Assert.Null(manager.Find(Server, "20010206"));
        await Gone(watched.Folder);

        // The demo server never hears of the watch, yet the episode let go does not come back.
        await manager.ApplyRulesAsync(Server, Cancel);
        Assert.Null(manager.Find(Server, "20010206"));
        Assert.NotNull(manager.Find(Server, film.RatingKey));
        Assert.Equal(["20010207", "20010208", "20010301", film.RatingKey], manager.Snapshot().Select(r => r.RatingKey));
    }

    [Fact]
    public async Task TheDemoServesRangesOfARealVideoAndTheWholeFileForAnotherVersion()
    {
        var key = _catalog.Movies[0].Media![0].Part![0].Key!;
        var video = DemoVideo.For(_catalog.Movies[0].RatingKey);
        var bytes = video.ToArray();
        Assert.StartsWith("YUV4MPEG2 W320 H180", System.Text.Encoding.ASCII.GetString(bytes, 0, 40), StringComparison.Ordinal);

        using var ranged = await _client.RequestPartAsync(key, 100, null, Cancel);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(100, video.Length - 1, video.Length), ranged.Content.Headers.ContentRange);
        Assert.Equal(bytes[100..], await ranged.Content.ReadAsByteArrayAsync(Cancel));

        using var changed = await _client.RequestPartAsync(key, 100, "\"another-version\"", Cancel);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(video.Length, changed.Content.Headers.ContentLength);

        using var beyond = await _client.RequestPartAsync(key, video.Length, null, Cancel);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
    }

    [Fact]
    public async Task ADownloadedDemoFilePlaysToTheEnd()
    {
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed here.");
        var manager = NewManager();
        var episode = Episodes("200501", 1)[0];
        manager.Enqueue(episode, Server, "Demo Library");
        manager.Attach(Server, _client);
        var done = await WaitFor(manager, episode.RatingKey, r => r.State == DownloadState.Done);

        using var player = new MpvPlayer(new Dictionary<string, string> { ["vo"] = "null", ["ao"] = "null", ["audio"] = "no", ["idle"] = "yes", ["config"] = "no", ["terminal"] = "no" });
        var ended = new TaskCompletionSource<EndReason>();
        var furthest = 0.0;
        player.Ended += (reason, _) => ended.TrySetResult(reason);
        player.Changed += change =>
        {
            if (change is { Name: "time-pos", Number: { } seconds }) furthest = Math.Max(furthest, seconds);
        };
        player.Load(done.MediaPath);

        Assert.Equal(EndReason.Finished, await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel));
        var length = DemoVideo.For(episode.RatingKey).Frames / (double)DemoVideo.FramesPerSecond;
        Assert.InRange(furthest, length * 0.8, length);
    }

    private DownloadManager NewManager(TimeProvider? time = null)
    {
        var manager = new DownloadManager(IndexFile, Folder, () => _settings, time) { RetryDelay = TimeSpan.FromMilliseconds(20) };
        manager.Load();
        _managers.Add(manager);
        return manager;
    }

    private List<MetadataItem> Episodes(string seasonKey, int count) => [.. _catalog.ChildrenOf(seasonKey).Take(count)];

    private static string Id(string ratingKey) => DownloadRecord.Key(Server, ratingKey);

    /// <summary>Files are deleted on a worker: waits for the folder to go, ten seconds at most.</summary>
    private static async Task Gone(string folder)
    {
        var clock = Stopwatch.StartNew();
        while (Directory.Exists(folder) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, Cancel);
        Assert.False(Directory.Exists(folder), $"{folder} is still there, holding {string.Join(", ", Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder).Select(Path.GetFileName) : [])}");
    }

    private static async Task<DownloadRecord> WaitFor(DownloadManager manager, string ratingKey, Func<DownloadRecord, bool> condition)
    {
        var clock = Stopwatch.StartNew();
        DownloadRecord? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            last = manager.Find(Server, ratingKey);
            if (last is not null && condition(last)) return last;
            await Task.Delay(10, Cancel);
        }

        Assert.Fail($"{ratingKey} never got there; it is {last?.State} at {last?.DoneBytes} bytes ({last?.Error}).");
        return last!;
    }

    /// <summary>Every picture the same few bytes, so kept artwork can be checked.</summary>
    private sealed class FlatArt : IDemoArtRenderer
    {
        public static readonly byte[] Bytes = [0xFF, 0xD8, 0xFF, 0xD9];

        public DemoImage Render(DemoArtRequest request) => new(Bytes, "image/jpeg");
    }
}

/// <summary>Why a download folder cannot be used, in words: plainly, and inside the packaged sandbox, naming the folders that would work.</summary>
public sealed class DownloadFolderTests
{
    [Fact]
    public void ARefusalInsideTheSandboxNamesTheFoldersThatWouldWork()
    {
        var sandbox = new Dictionary<string, string?> { ["TUXFLIX_SANDBOX"] = "1", ["TUXFLIX_SANDBOX_WRITABLE"] = "/home/viewer/.local/share/tuxflix:/home/viewer/Videos: /home/viewer/Downloads " };
        var refused = new IOException("Read-only file system");

        Assert.Equal(["/home/viewer/.local/share/tuxflix", "/home/viewer/Videos", "/home/viewer/Downloads"], DownloadFolders.Writable(sandbox.GetValueOrDefault));
        Assert.Equal(
            "Tuxflix may not write to /srv/films: installed as a package, it may only write inside /home/viewer/.local/share/tuxflix, /home/viewer/Videos, /home/viewer/Downloads. Choose a download folder in one of those.",
            DownloadFolders.Explain("/srv/films", refused, sandbox.GetValueOrDefault));

        Assert.Null(DownloadFolders.Writable(_ => null));
        Assert.Equal("/srv/films cannot be written to: Read-only file system", DownloadFolders.Explain("/srv/films", refused, _ => null));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AFolderThatTakesAFileIsFineAndOneThatDoesNotSaysWhy()
    {
        using var scratch = new Scratch();
        Assert.Null(DownloadFolders.Check(Path.Combine(scratch.Root, "new", "downloads"), _ => null));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(scratch.Root, "new", "downloads")));

        var locked = Path.Combine(scratch.Root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.StartsWith(locked + " cannot be written to: ", DownloadFolders.Check(locked, _ => null), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
