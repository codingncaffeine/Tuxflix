using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>Subtitles found online through the server, against the demo, which keeps the one added.</summary>
public sealed class SubtitleFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "finder-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ServerSession _session;
    private readonly SettingsStore _settings;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public SubtitleFinderTests()
    {
        // The demo draws its art with the application's fonts, which the headless platform provides.
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        var shell = new ShellViewModel(_settings, paths);
        _session = ServerSession.CreateDemo(shell.Identity);
    }

    public void Dispose()
    {
        _session.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public async Task ASubtitleFoundOnlineIsKeptWithTheItemAndTurnedOn()
    {
        var film = await _session.Client.GetMetadataAsync(_catalog.Movies[1].RatingKey, TestContext.Current.CancellationToken);
        var host = new Host(film!.Media![0].Part![0]);
        var finder = new SubtitleFinderViewModel(host, _session, film.RatingKey);

        finder.Language = SubtitleFinderViewModel.Languages.Single(l => l.Code == "fr");
        await Until(() => !finder.IsBusy && finder.Results.Count > 0);

        Assert.Equal(3, finder.Results.Count);
        Assert.True(finder.Results[0].Stream.Score >= finder.Results[1].Stream.Score);
        Assert.Contains("describes sounds too", finder.Results[2].Detail, StringComparison.Ordinal);

        await finder.Results[0].AddCommand.ExecuteAsync(null);

        Assert.NotNull(host.Taken);
        Assert.True(host.Taken!.IsExternal);
        Assert.Equal("fr", host.Taken.LanguageCode);
        Assert.DoesNotContain(host.Taken.Id, host.Before);
        Assert.Equal("Kept with this item and turned on.", finder.Status);
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "not within 20 s");
    }

    private sealed class Host(MediaPart part) : ISubtitleHost
    {
        public HashSet<long> Before { get; } = [.. (part.Stream ?? []).Where(s => s.StreamType == 3).Select(s => s.Id)];

        public MediaStream? Taken { get; private set; }

        public HashSet<long> SubtitleStreamIds() => [.. Before];

        public void TakeAddedSubtitle(MetadataItem item, MediaPart part, MediaStream added) => Taken = added;
    }
}
