using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.App.Music;

namespace Tuxflix.App.ViewModels;

/// <summary>The music options a settings page shows: the visualizer's mode and colours, and the lyrics beside the cover.</summary>
public sealed partial class MusicSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly bool _ready;

    public MusicSettingsViewModel(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        var music = shell.Settings.Music;
        VisualizerMode = VisualizerModes.Title(VisualizerModes.Find(music.VisualizerMode) ?? Music.VisualizerMode.Spectrum);
        VisualizerPalette = VisualizerPalettes.Names.Contains(music.VisualizerPalette) ? music.VisualizerPalette : VisualizerPalettes.AlbumName;
        ShowLyrics = music.ShowLyrics;
        _ready = true;
    }

    /// <summary>Every mode by its title, in the menus' order; the setting keeps the mode's own name.</summary>
    public IReadOnlyList<string> Modes { get; } = [.. VisualizerModes.All.Select(m => m.Title)];

    public IReadOnlyList<string> Palettes => VisualizerPalettes.Names;

    [ObservableProperty]
    public partial string VisualizerMode { get; set; }

    [ObservableProperty]
    public partial string VisualizerPalette { get; set; }

    [ObservableProperty]
    public partial bool ShowLyrics { get; set; }

    partial void OnVisualizerModeChanged(string value) => Save(m => m.VisualizerMode = (VisualizerModes.Find(value) ?? Music.VisualizerMode.Spectrum).ToString());

    partial void OnVisualizerPaletteChanged(string value) => Save(m => m.VisualizerPalette = value);

    partial void OnShowLyricsChanged(bool value) => Save(m => m.ShowLyrics = value);

    private void Save(Action<Tuxflix.Core.Settings.MusicSettings> change)
    {
        // The constructor sets each value from the settings: nothing to save then.
        if (!_ready) return;
        change(_shell.Settings.Music);
        _shell.SaveSettings();
    }
}
