using Avalonia;
using Avalonia.Media;
using Tuxflix.App.Themes;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>The settings page keeps what it is told, and the rest of the application reads it.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "settings-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly ShellViewModel _shell;

    public SettingsTests()
    {
        HeadlessSkia.Ensure();
        _paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        _paths.EnsureCreated();
        _store = SettingsStore.Load(_paths.SettingsFile);
        _shell = new ShellViewModel(_store, _paths) { ReportsPlayback = false };
    }

    public void Dispose()
    {
        _shell.Router.Current?.Deactivate();
        _shell.Session?.Dispose();

        // Settings are written on a worker: the last write lands before the folder goes.
        _store.Flush(TimeSpan.FromSeconds(5));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AQualityChosenHereIsWhereTheNextFilmStarts()
    {
        var page = new SettingsPageViewModel(_shell);
        var fourMegabits = StreamQuality.FromKbps(4000);

        page.HomeQuality = fourMegabits;

        Assert.Equal(4000, _shell.Settings.Playback.HomeQualityKbps);
        Assert.Null(_shell.Settings.Playback.RemoteQualityKbps);
        await Until(() => File.Exists(_paths.SettingsFile) && File.ReadAllText(_paths.SettingsFile).Contains("\"homeQualityKbps\": 4000", StringComparison.Ordinal));

        _shell.OpenDemo();
        _shell.Play(DemoCatalog.Create(DateTimeOffset.Now).Movies[0], resume: false);
        var player = Assert.IsType<PlayerPageViewModel>(_shell.Router.Current);
        await Until(() => player.Quality == fourMegabits);
    }

    [Fact]
    public void AnAccentChosenHereRepaintsTheAccentBrushes()
    {
        var page = new SettingsPageViewModel(_shell);
        var sky = Accents.All.Single(a => a.Key == "sky");

        page.Accent = sky;

        Assert.Equal("sky", _shell.Settings.Accent);
        Assert.Equal(sky.Main, ((SolidColorBrush)Application.Current!.Resources["Brush.Accent"]!).Color);
        Assert.Equal(Color.FromArgb(0x2E, sky.Main.R, sky.Main.G, sky.Main.B), ((SolidColorBrush)Application.Current.Resources["Brush.Accent.Soft"]!).Color);
        Assert.Same(Accents.All[0], Accents.Find("no such colour"));

        page.Accent = Accents.All[0];
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "not within 20 s");
    }
}
