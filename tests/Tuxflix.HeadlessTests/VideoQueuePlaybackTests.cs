using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A collection of films played through: the player knows what comes next and offers it.</summary>
public sealed class VideoQueuePlaybackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "queue-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellViewModel _shell;
    private readonly SettingsStore _settings;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public VideoQueuePlaybackTests()
    {
        // The demo draws its art with the application's fonts, which the headless platform provides.
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths) { ReportsPlayback = false };
    }

    public void Dispose()
    {
        _shell.Router.Current?.Deactivate();
        _shell.Session?.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public async Task AFilmCollectionPlaysThroughWithTheNextFilmUpNext()
    {
        _shell.OpenDemo();
        var collection = _catalog.CollectionsOf(DemoCatalog.MoviesSectionKey)[0];
        var members = _catalog.ChildrenOf(collection.RatingKey);
        var page = new CollectionPageViewModel(_shell, _shell.Session!, collection);
        await page.ActivateAsync();

        Assert.True(page.CanPlay);
        page.PlayCommand.Execute(null);

        var player = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        Assert.Equal(members[0].RatingKey, player.Item.RatingKey);
        await Until(() => player.HasNext);
        Assert.Equal($"{members[1].Title} · {members[1].Year}", player.UpNextHeading);
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "not within 20 s");
    }
}
