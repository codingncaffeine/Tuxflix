using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.App.Themes;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The settings page: how Tuxflix looks, how films and episodes play, and what it is. Every change
/// is kept at once; the choices the player's own menu also makes (skipping, sound) are the same
/// settings seen from here.
/// </summary>
public sealed partial class SettingsPageViewModel(ShellViewModel shell) : PageViewModel
{
    public override string Title => "Settings";

    private AppSettings Settings => shell.Settings;

    private PlaybackSettings Playback => shell.Settings.Playback;

    // ===== How it looks =====

    public IReadOnlyList<Accent> AccentChoices => Accents.All;

    public Accent Accent
    {
        get => Accents.Find(Settings.Accent);
        set
        {
            if (value is null || value.Key == Settings.Accent) return;
            Settings.Accent = value.Key;
            Accents.Apply(value);
            Save();
        }
    }

    /// <summary>The desktop's own title bar in place of the one Tuxflix draws; from the next start.</summary>
    public bool UseSystemTitleBar
    {
        get => Settings.UseSystemTitleBar;
        set
        {
            Settings.UseSystemTitleBar = value;
            Save();
            OnPropertyChanged(nameof(TitleBarNote));
        }
    }

    public string TitleBarNote => "Takes effect the next time Tuxflix starts.";

    // ===== Sections that features bring =====

    /// <summary>Browsing: theme music and the home screen's shelves.</summary>
    public BrowseSettingsViewModel Browse { get; } = new(shell);

    /// <summary>Downloads: where they go, how many at once and how fast.</summary>
    public DownloadSettingsViewModel Downloads { get; } = new(shell);

    /// <summary>Music: the visualizer and the lyrics.</summary>
    public MusicSettingsViewModel Music { get; } = new(shell);

    /// <summary>Photos: the slideshow.</summary>
    public PhotoSettingsViewModel Photos { get; } = new(shell);

    /// <summary>The TV interface and the controller.</summary>
    public Views.Settings.TvSettingsViewModel Tv { get; } = new(shell);

    protected override Task LoadAsync(CancellationToken cancellation)
    {
        Tv.Attach();
        return Task.CompletedTask;
    }

    public override void Deactivate()
    {
        base.Deactivate();
        Tv.Detach();
    }

    // ===== How films and episodes play =====

    public IReadOnlyList<StreamQuality> QualityChoices => StreamQuality.All;

    public StreamQuality HomeQuality
    {
        get => StreamQuality.FromKbps(Playback.HomeQualityKbps);
        set => Set(() => Playback.HomeQualityKbps = value?.Kbps);
    }

    public StreamQuality RemoteQuality
    {
        get => StreamQuality.FromKbps(Playback.RemoteQualityKbps);
        set => Set(() => Playback.RemoteQualityKbps = value?.Kbps);
    }

    public bool AutoSkipIntro { get => Playback.AutoSkipIntro; set => Set(() => Playback.AutoSkipIntro = value); }

    public bool AutoSkipCredits { get => Playback.AutoSkipCredits; set => Set(() => Playback.AutoSkipCredits = value); }

    public bool AutoPlayNext { get => Playback.AutoPlayNext; set => Set(() => Playback.AutoPlayNext = value); }

    public bool SubtitlesRaised { get => Playback.SubtitlesRaised; set => Set(() => Playback.SubtitlesRaised = value); }

    public bool NightMode { get => Playback.NightMode; set => Set(() => Playback.NightMode = value); }

    public bool Stereo { get => Playback.Stereo; set => Set(() => Playback.Stereo = value); }

    public bool Passthrough { get => Playback.Passthrough; set => Set(() => Playback.Passthrough = value); }

    // ===== About =====

    public string Version => BuildInfo.Stamp;

    public string LogFolder => shell.Paths.Logs;

    public string Licence => "Free software under the GNU General Public License, version 3 or later.";

    private void Set(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change();
        Save();
        OnPropertyChanged(name);
    }

    private void Save() => shell.SaveSettings();
}
