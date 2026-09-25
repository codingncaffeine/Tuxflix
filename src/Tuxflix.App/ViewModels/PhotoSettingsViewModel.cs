using CommunityToolkit.Mvvm.ComponentModel;

namespace Tuxflix.App.ViewModels;

/// <summary>The photo options a settings page shows: how long a slideshow keeps each photo, and its order.</summary>
public sealed partial class PhotoSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly bool _ready;

    public PhotoSettingsViewModel(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        SlideshowSeconds = Math.Clamp(shell.Settings.Photos.SlideshowSeconds, 2, 60);
        SlideshowShuffle = shell.Settings.Photos.SlideshowShuffle;
        _ready = true;
    }

    [ObservableProperty]
    public partial double SlideshowSeconds { get; set; }

    [ObservableProperty]
    public partial bool SlideshowShuffle { get; set; }

    partial void OnSlideshowSecondsChanged(double value)
    {
        if (!_ready) return;
        _shell.Settings.Photos.SlideshowSeconds = (int)Math.Clamp(Math.Round(value), 2, 60);
        _shell.SaveSettings();
    }

    partial void OnSlideshowShuffleChanged(bool value)
    {
        if (!_ready) return;
        _shell.Settings.Photos.SlideshowShuffle = value;
        _shell.SaveSettings();
    }
}
