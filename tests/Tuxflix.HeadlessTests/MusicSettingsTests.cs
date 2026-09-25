using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>The music options a settings page shows start from the settings and change them.</summary>
public sealed class MusicSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "music-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public MusicSettingsTests() => HeadlessSkia.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheOptionsStartFromTheSettingsAndChangeThem()
    {
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        var shell = new ShellViewModel(SettingsStore.Load(paths.SettingsFile), paths);
        shell.Settings.Music.VisualizerMode = "Scope";
        shell.Settings.Music.VisualizerPalette = "No such palette";

        var options = new MusicSettingsViewModel(shell);
        Assert.Equal("Scope", options.VisualizerMode);
        Assert.Equal("Album", options.VisualizerPalette);
        Assert.False(options.ShowLyrics);
        Assert.Contains("Particles", options.Modes);
        Assert.Contains("Ember", options.Palettes);

        options.VisualizerMode = "Radial";
        options.VisualizerPalette = "Ember";
        options.ShowLyrics = true;
        Assert.Equal("Radial", shell.Settings.Music.VisualizerMode);
        Assert.Equal("Ember", shell.Settings.Music.VisualizerPalette);
        Assert.True(shell.Settings.Music.ShowLyrics);
    }
}
