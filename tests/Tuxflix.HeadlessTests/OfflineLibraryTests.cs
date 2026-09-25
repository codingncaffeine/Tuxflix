using System.Collections.Concurrent;
using System.Diagnostics;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Downloads;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;
using Tuxflix.Player;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Downloads as the window drives them, against the demo server: from an item page, a series'
/// rule and a season, the kept copy played without the server, and a start with no server in
/// reach opening what is kept. Work the window would do on its UI thread runs here by hand.
/// </summary>
public sealed class OfflineLibraryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "offline-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<Action> _ui = new();
    private readonly List<ShellViewModel> _shells = [];
    private readonly List<SettingsStore> _settings = [];
    private readonly AppPaths _paths;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public OfflineLibraryTests()
    {
        HeadlessSkia.Ensure();
        _paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        _paths.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var shell in _shells)
        {
            shell.StopDownloads();
            await shell.DownloadsStopped;
            shell.Session?.Dispose();
        }

        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public async Task AFilmDownloadedFromItsPageIsKeptAndPlaysWithoutTheServer()
    {
        var shell = NewShell();
        shell.OpenDemo();
        var page = new ItemPageViewModel(shell, shell.Session!, _catalog.Movies[3]);
        await page.ActivateAsync();
        Assert.True(page.CanDownload);
        Assert.True(page.DownloadRow!.IsGone);
        Assert.Equal("DOWNLOAD", page.DownloadRow.ButtonLabel);

        page.DownloadCommand.Execute(null);
        await Until(() => page.DownloadRow.IsDone, "the film to be downloaded");
        var record = page.DownloadRow.Record;
        Assert.Equal("DOWNLOADED", page.DownloadRow.ButtonLabel);
        Assert.Same(page.DownloadRow, Assert.Single(shell.Downloads.Kept));
        Assert.True(File.Exists(record.MediaPath));
        Assert.StartsWith(Path.Combine(_paths.Data, "downloads") + Path.DirectorySeparatorChar, record.Folder, StringComparison.Ordinal);
        Assert.Equal("1 kept", shell.Downloads.Summary);

        // Signed out, no server open: the kept copy is what plays, with its own details.
        await shell.SignOutCommand.ExecuteAsync(null);
        Assert.Null(shell.Session);
        await shell.PlayDownloadAsync(record);
        var player = Assert.IsType<PlayerPageViewModel>(shell.Router.Current);
        await Until(() => !player.IsLoading, "the player to load");
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed here.");
        Assert.Null(player.ErrorMessage);
        Assert.Equal(record.MediaPath, player.Source);
        Assert.Equal("The downloaded file", player.StreamSummary);
        Assert.Equal(_catalog.Movies[3].Title, player.Heading);
        player.Deactivate();

        // Deleted from the Downloads page: the film's page offers Download again.
        page.DownloadRow.RemoveCommand.Execute(null);
        await Until(() => page.DownloadRow.IsGone, "the download to be deleted");
        Assert.Empty(shell.Downloads.Kept);
        await Until(() => !Directory.Exists(record.Folder), "its files to be deleted");
    }

    [Fact]
    public async Task ASeriesPageKeepsTheNextEpisodesAndDownloadsASeason()
    {
        var shell = NewShell();
        shell.OpenDemo();

        // Lanternfall: season 1 and five of season 2 are watched.
        var page = new ItemPageViewModel(shell, shell.Session!, _catalog.Shows[0]);
        await page.ActivateAsync();
        Assert.True(page.CanDownloadSeries);
        Assert.False(page.CanDownload);

        page.KeepNextCommand.Execute("3");
        await Until(() => page.SeriesDownloads!.KeptCount == 3, "the next three episodes");
        Assert.Equal(["20010206", "20010207", "20010208"], shell.Downloads.Kept.Select(r => r.Record.RatingKey).Order());
        Assert.True(page.SeriesDownloads!.HasRule);
        Assert.Equal("3 downloaded", page.SeriesDownloads.Label);
        Assert.Single(shell.Downloads.Rules);

        Assert.Equal("Season 2", page.SelectedSeason!.Title);
        await page.DownloadSeasonCommand.ExecuteAsync(null);
        await Until(() => page.SeasonDownloads!.KeptCount == 8, "all of season 2");
        Assert.Equal(8, shell.Downloads.Kept.Count);

        page.StopKeepingSeriesCommand.Execute(null);
        await Until(() => !page.SeriesDownloads.HasRule, "the rule to go");
        Assert.Empty(shell.Downloads.Rules);
        Assert.Equal(8, shell.Downloads.Kept.Count);
    }

    [Fact]
    public async Task AStartWithNoServerInReachOpensWhatIsDownloaded()
    {
        var first = NewShell();
        first.OpenDemo();
        var row = first.Downloads.Download(first.Session!, _catalog.Movies[1])!;
        await Until(() => row.IsDone, "the download");
        first.StopDownloads();
        await first.DownloadsStopped;

        // The next start remembers a sign-in, but neither plex.tv nor any server answers.
        var secrets = new MemorySecrets();
        var second = NewShell(new Unreachable(), secrets);
        secrets.Kept["plex-" + second.Identity.ClientIdentifier] = "account-token";
        second.Start(demo: false);

        await Until(() => second.Router.Current is DownloadsPageViewModel, "the offline library");
        var page = (DownloadsPageViewModel)second.Router.Current!;
        Assert.True(page.HasNotice);
        Assert.StartsWith("plex.tv could not be reached", page.Notice, StringComparison.Ordinal);
        Assert.True(second.IsOffline);
        Assert.Equal("Offline", second.ServerName);
        await Until(() => second.Downloads.Kept.Count == 1, "the kept film to be listed");
        Assert.Equal(_catalog.Movies[1].RatingKey, second.Downloads.Kept[0].Record.RatingKey);
    }

    [Fact]
    public async Task WithNothingDownloadedAStartWithNoServerSaysWhy()
    {
        var secrets = new MemorySecrets();
        var shell = NewShell(new Unreachable(), secrets);
        secrets.Kept["plex-" + shell.Identity.ClientIdentifier] = "account-token";
        shell.Start(demo: false);

        await Until(() => shell.Router.Current is WelcomePageViewModel, "the welcome page");
        Assert.Contains("plex.tv could not be reached", ((WelcomePageViewModel)shell.Router.Current!).Notice, StringComparison.Ordinal);
        Assert.False(shell.IsOffline);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task TheOptionsSetHowManyAtOnceHowFastAndWhereAndRefuseAFolderThatCannotBeWritten()
    {
        var shell = NewShell();
        var page = new DownloadsPageViewModel(shell);
        var loading = page.ActivateAsync();
        await Until(() => loading.IsCompleted, "the page to load");
        Assert.True(page.IsEmpty);
        Assert.Equal("Nothing queued", shell.Downloads.Summary);

        // What a settings page builds from the shell, and the Downloads page shows as its options.
        var options = new DownloadSettingsViewModel(shell);
        options.TwoAtATimeCommand.Execute(null);
        Assert.Equal(2, shell.Settings.Downloads.Simultaneous);
        Assert.True(options.TwoAtOnce);
        options.SetSpeedLimitCommand.Execute("0.5");
        Assert.Equal(500_000, shell.Settings.Downloads.SpeedLimit);
        Assert.Equal("At most 0.5 MB/s", options.SpeedLimitText);

        var chosen = Path.Combine(_root, "elsewhere");
        await options.SetFolderAsync(chosen);
        Assert.Null(options.FolderProblem);
        Assert.Equal(chosen, shell.Downloads.Manager.Folder);

        // A folder Tuxflix may not write to is refused in words, and the one before stays.
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await options.SetFolderAsync(Path.Combine(locked, "downloads"));
            Assert.StartsWith(Path.Combine(locked, "downloads") + " cannot be written to", options.FolderProblem, StringComparison.Ordinal);
            Assert.Equal(chosen, shell.Downloads.Manager.Folder);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await options.UseDefaultFolderCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(_paths.Data, "downloads"), shell.Downloads.Manager.Folder);
    }

    [Fact]
    public async Task ALateCopyOfARemovedDownloadDoesNotBringItBack()
    {
        var shell = NewShell();
        var centre = shell.Downloads;
        await Until(() => centre.Loaded.IsCompleted, "the list to be read");
        DownloadRecord Copy(string key, long sequence) => new()
        {
            ServerId = "server", RatingKey = key, Type = "movie", Title = "A Film " + key,
            State = DownloadState.Downloading, DoneBytes = sequence * 10, TotalBytes = 100, Sequence = sequence,
        };

        // Progress, then the removal, then a copy taken before the removal but arriving after it, in one batch.
        centre.Arrive("server/1", Copy("1", 3), 3);
        centre.Arrive("server/1", null, 5);
        centre.Arrive("server/1", Copy("1", 4), 4);

        // The same across two batches: the removal is shown before the late copy arrives.
        centre.Arrive("server/2", Copy("2", 6), 6);
        await Until(() => centre.Queue.Any(r => r.Record.RatingKey == "2"), "the second download to be listed");
        centre.Arrive("server/2", null, 8);
        await Until(() => centre.Queue.All(r => r.Record.RatingKey != "2"), "the second download to go");
        centre.Arrive("server/2", Copy("2", 7), 7);

        await Settle();
        Assert.Empty(centre.Queue);
        Assert.Null(centre.Row("server", "1"));
        Assert.Null(centre.Row("server", "2"));
    }

    private ShellViewModel NewShell(HttpMessageHandler? network = null, ISecretStore? keyring = null)
    {
        var settings = SettingsStore.Load(_paths.SettingsFile);
        _settings.Add(settings);
        var shell = new ShellViewModel(settings, _paths, network)
        {
            PostToUi = _ui.Enqueue,
            Keyring = keyring ?? new MemorySecrets(),
            Silent = true,
        };
        _shells.Add(shell);
        return shell;
    }

    /// <summary>Runs what the window would run on its UI thread, until <paramref name="condition"/> holds; thirty seconds at most.</summary>
    private async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            while (_ui.TryDequeue(out var action)) action();
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Waited thirty seconds for {what}.");
    }

    /// <summary>Runs what the window would run for half a second: long enough for any batch of changes to land.</summary>
    private async Task Settle()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            while (_ui.TryDequeue(out var action)) action();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Kept { get; } = [];

        public Task<string?> LookupAsync(string account) => Task.FromResult(Kept.GetValueOrDefault(account));

        public Task<bool> StoreAsync(string account, string label, string secret)
        {
            Kept[account] = secret;
            return Task.FromResult(true);
        }

        public Task ClearAsync(string account)
        {
            Kept.Remove(account);
            return Task.CompletedTask;
        }
    }

    /// <summary>A network with nothing on it: every request fails as an unreachable address does.</summary>
    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host."));
    }
}
