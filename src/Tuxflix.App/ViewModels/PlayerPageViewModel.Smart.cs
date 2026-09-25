using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>How the picture fills the window.</summary>
public enum PictureFit
{
    /// <summary>All of the picture, with bars where the shapes differ.</summary>
    Fit,

    /// <summary>The window filled, the picture's edges cut.</summary>
    Fill,

    /// <summary>The picture stretched to the window's shape.</summary>
    Stretch,
}

/// <summary>The smart half of the player: markers and skipping, the next episode, chapters, and the playback menu.</summary>
public sealed partial class PlayerPageViewModel
{
    private MarkerTimeline _markers = new([]);
    private ChapterViewModel? _currentChapter;
    private MetadataItem? _next;
    private bool _upNextDismissed;
    private DispatcherTimer? _countdown;
    private DispatcherTimer? _sleep;
    private DateTime _sleepAt;

    private Core.Settings.PlaybackSettings Playback => Owner.Settings.Playback;

    // ===== Skipping intros and credits =====

    /// <summary>The marker the playhead is in, when it can be skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSkip), nameof(SkipLabel))]
    public partial Marker? SkippableMarker { get; private set; }

    public bool ShowsSkip => SkippableMarker is not null && !ShowsUpNext;

    public string SkipLabel => SkippableMarker?.Type == "intro" ? "SKIP INTRO" : "SKIP CREDITS";

    [RelayCommand]
    private void Skip()
    {
        if (SkippableMarker is not { } marker) return;
        _markers.Skipped(marker);
        SeekTo(marker.EndTimeOffset / 1000.0);
        SkippableMarker = null;
    }

    // ===== The next episode =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSkip))]
    public partial bool ShowsUpNext { get; private set; }

    [ObservableProperty]
    public partial int UpNextSeconds { get; private set; }

    public string UpNextHeading => _next switch
    {
        { Type: "episode" } next => $"{Format.EpisodeCode(next)} · {next.Title}",
        { Year: { } year } next => $"{next.Title} · {year}",
        { } next => next.Title,
        null => string.Empty,
    };

    public string UpNextCaption => Playback.AutoPlayNext ? $"Starts in {UpNextSeconds}" : "Up next";

    public string? UpNextStill => _next is { } next ? Artwork.Still(next) : null;

    public bool HasNext => _next is not null;

    [RelayCommand]
    private void PlayNext()
    {
        if (_next is not { } next) return;
        StopCountdown();
        Log.Info($"Playing the next episode, {Format.EpisodeCode(next)}.");
        Owner.Router.Replace(new PlayerPageViewModel(Owner, Server, next, resume: next.ViewOffset is > 0, queue?.Advance()));
    }

    [RelayCommand]
    private void CancelUpNext()
    {
        _upNextDismissed = true;
        ShowsUpNext = false;
        StopCountdown();
    }

    // ===== Chapters =====

    public ObservableCollection<ChapterViewModel> Chapters { get; } = [];

    public bool HasChapters => Chapters.Count > 1;

    /// <summary>The window is the small picture-in-picture one (set by the window).</summary>
    [ObservableProperty]
    public partial bool IsPictureInPicture { get; set; }

    /// <summary>What the seek bar shows under the pointer; null until the full record is in.</summary>
    [ObservableProperty]
    public partial SeekPreviews? Previews { get; private set; }

    /// <summary>Where the chapters start, as fractions of the length: the ticks on the seek bar.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> ChapterMarks { get; private set; } = [];

    // ===== The playback menu =====

    [ObservableProperty]
    public partial double Speed { get; set; } = 1;

    [ObservableProperty]
    public partial double SubtitleDelay { get; set; }

    [ObservableProperty]
    public partial double AudioDelay { get; set; }

    [ObservableProperty]
    public partial PictureFit Fit { get; set; }

    /// <summary>A forced shape (<c>16:9</c>, <c>4:3</c>, <c>2.39:1</c>), or null for the file's own.</summary>
    [ObservableProperty]
    public partial string? Aspect { get; set; }

    /// <summary>Minutes of the sleep timer, 0 when off, -1 for the end of this item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepLabel))]
    public partial int SleepMinutes { get; private set; }

    public string SleepLabel => SleepMinutes switch
    {
        0 => "Off",
        -1 => "End of this",
        _ => $"In {Math.Max(0, (int)Math.Ceiling((_sleepAt - DateTime.UtcNow).TotalMinutes))} min",
    };

    public double SubtitleScale
    {
        get => Playback.SubtitleScale;
        set
        {
            Playback.SubtitleScale = Math.Clamp(value, 0.5, 2.5);
            Owner.SaveSettings();
            Player?.Player.PostNumber("sub-scale", Playback.SubtitleScale);
            OnPropertyChanged();
        }
    }

    public bool SubtitlesRaised
    {
        get => Playback.SubtitlesRaised;
        set
        {
            Playback.SubtitlesRaised = value;
            Owner.SaveSettings();
            Player?.Player.PostNumber("sub-pos", value ? 88 : 100);
            OnPropertyChanged();
        }
    }

    public bool NightMode
    {
        get => Playback.NightMode;
        set
        {
            Playback.NightMode = value;
            Owner.SaveSettings();
            Player?.Player.PostProperty("af", value ? NightFilter : string.Empty);
            OnPropertyChanged();
        }
    }

    /// <summary>The codecs a receiver decodes itself, when passthrough is on.</summary>
    private const string PassthroughCodecs = "ac3,eac3,dts,dts-hd,truehd";

    public bool Passthrough
    {
        get => Playback.Passthrough;
        set
        {
            Playback.Passthrough = value;
            Owner.SaveSettings();
            Player?.Player.PostProperty("audio-spdif", value ? PassthroughCodecs : string.Empty);
            OnPropertyChanged();
        }
    }

    public bool Stereo
    {
        get => Playback.Stereo;
        set
        {
            Playback.Stereo = value;
            Owner.SaveSettings();
            Player?.Player.PostProperty("audio-channels", value ? "stereo" : "auto-safe");
            OnPropertyChanged();
        }
    }

    public bool AutoSkipIntro
    {
        get => Playback.AutoSkipIntro;
        set
        {
            Playback.AutoSkipIntro = value;
            Owner.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool AutoSkipCredits
    {
        get => Playback.AutoSkipCredits;
        set
        {
            Playback.AutoSkipCredits = value;
            Owner.SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool AutoPlayNext
    {
        get => Playback.AutoPlayNext;
        set
        {
            Playback.AutoPlayNext = value;
            Owner.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Dialogue up and explosions down: a dynamic normaliser, gentle enough for music scores.</summary>
    private const string NightFilter = "@night:lavfi=[dynaudnorm=f=200:g=15:p=0.9:m=8]";

    partial void OnSpeedChanged(double value) => Player?.Player.PostNumber("speed", value);

    partial void OnSubtitleDelayChanged(double value) => Player?.Player.PostNumber("sub-delay", value);

    partial void OnAudioDelayChanged(double value) => Player?.Player.PostNumber("audio-delay", value);

    partial void OnFitChanged(PictureFit value)
    {
        Player?.Player.PostNumber("panscan", value == PictureFit.Fill ? 1 : 0);
        Player?.Player.PostFlag("keepaspect", value != PictureFit.Stretch);
    }

    /// <remarks>mpv's <c>no</c> means square pixels, which squeezes an anamorphic file; <c>-1</c> is the file's own shape.</remarks>
    partial void OnAspectChanged(string? value) => Player?.Player.PostProperty("video-aspect-override", value ?? "-1");

    /// <summary>How far the subtitles or the sound are moved against the picture, in words.</summary>
    public static string DelayText(double seconds) => Math.Abs(seconds) < 0.005
        ? "In step with the picture"
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(seconds):0.0#} s {(seconds > 0 ? "later" : "earlier")} than the picture");

    /// <summary>Pauses after <paramref name="minutes"/>; -1 at the end of this item (no next episode); 0 turns it off.</summary>
    public void SetSleep(int minutes)
    {
        _sleep?.Stop();
        _sleep = null;
        SleepMinutes = minutes;
        if (minutes <= 0) return;
        _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
        _sleep = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Normal, (_, _) =>
        {
            OnPropertyChanged(nameof(SleepLabel));
            if (DateTime.UtcNow < _sleepAt) return;
            Log.Info("The sleep timer paused playback.");
            SetSleep(0);
            if (!IsPaused) TogglePauseCommand.Execute(null);
        });
        _sleep.Start();
    }

    /// <summary>The options the playback menu keeps, for mpv's start.</summary>
    private void AddPlaybackOptions(Dictionary<string, string> options)
    {
        options["sub-scale"] = Playback.SubtitleScale.ToString("0.##", CultureInfo.InvariantCulture);
        if (Playback.SubtitlesRaised) options["sub-pos"] = "88";
        if (Playback.NightMode) options["af"] = NightFilter;
        if (Playback.Passthrough) options["audio-spdif"] = PassthroughCodecs;
        if (Playback.Stereo) options["audio-channels"] = "stereo";

        // A mix down to fewer speakers is kept from clipping.
        options["audio-normalize-downmix"] = "yes";
    }

    /// <summary>Once the full record is in: the markers, the chapters, and (for an episode) what comes next.</summary>
    private void LoadSmart()
    {
        _markers = new MarkerTimeline(_item.Marker ?? []);
        Chapters.Clear();
        var length = Math.Max(1, _item.Duration ?? 0);
        foreach (var chapter in (_item.Chapter ?? []).OrderBy(c => c.StartTimeOffset))
        {
            Chapters.Add(new ChapterViewModel(this, chapter));
        }

        ChapterMarks = [.. Chapters.Skip(1).Select(c => c.Chapter.StartTimeOffset / (double)length)];
        OnPropertyChanged(nameof(HasChapters));
        Previews?.Dispose();
        Previews = new SeekPreviews(session.Client, _item.Media?.FirstOrDefault()?.Part?.FirstOrDefault(), [.. Chapters.Select(c => c.Chapter)]);
        // A queue (a playlist, a collection) says what follows; an episode on its own goes on through its show.
        if (queue is not null)
        {
            _next = queue.Next;
            OnPropertyChanged(nameof(HasNext));
            OnPropertyChanged(nameof(UpNextHeading));
            OnPropertyChanged(nameof(UpNextStill));
        }
        else if (_item.Type == "episode" && _item.GrandparentRatingKey is { } show)
        {
            _ = FindNextAsync(show);
        }
    }

    private async Task FindNextAsync(string show)
    {
        try
        {
            var episodes = await Task.Run(() => Server.Client.GetAllLeavesAsync(show, CancellationToken.None));
            var at = episodes.ToList().FindIndex(e => e.RatingKey == _item.RatingKey);
            _next = at >= 0 && at + 1 < episodes.Count ? episodes[at + 1] : null;
            OnPropertyChanged(nameof(HasNext));
            OnPropertyChanged(nameof(UpNextHeading));
            OnPropertyChanged(nameof(UpNextStill));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"What follows {_item.Title} could not be found ({ex.Message}).");
        }
    }

    /// <summary>Each position: the skip button, the automatic skips, and the Up Next card.</summary>
    private void OnPosition(double seconds)
    {
        var ms = (long)(seconds * 1000);
        var skippable = _markers.SkippableAt(ms, _next is not null);
        if (skippable is not null && ((skippable.Type == "intro" && Playback.AutoSkipIntro) || (skippable.Type == "credits" && Playback.AutoSkipCredits)))
        {
            Log.Info($"Skipping the {skippable.Type} on its own.");
            _markers.Skipped(skippable);
            SeekTo(skippable.EndTimeOffset / 1000.0);
            skippable = null;
        }

        if (!ReferenceEquals(skippable, SkippableMarker)) SkippableMarker = skippable;

        // The chapter under the playhead is the one the list marks.
        var chapter = Chapters.LastOrDefault(c => c.Chapter.StartTimeOffset <= ms);
        if (!ReferenceEquals(chapter, _currentChapter))
        {
            _currentChapter?.IsCurrent = false;
            _currentChapter = chapter;
            chapter?.IsCurrent = true;
        }

        var wanted = _markers.OffersNext(ms, (long)(Duration * 1000)) && _next is not null && !_upNextDismissed && SleepMinutes != -1;
        if (wanted && !ShowsUpNext) StartUpNext();
        else if (!wanted && ShowsUpNext && !_upNextDismissed) HideUpNext();
    }

    private void StartUpNext()
    {
        ShowsUpNext = true;
        UpNextSeconds = 10;
        OnPropertyChanged(nameof(UpNextCaption));
        if (!Playback.AutoPlayNext) return;
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            if (IsPaused) return;
            UpNextSeconds--;
            OnPropertyChanged(nameof(UpNextCaption));
            if (UpNextSeconds <= 0) PlayNext();
        });
        _countdown.Start();
    }

    private void HideUpNext()
    {
        ShowsUpNext = false;
        StopCountdown();
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    /// <summary>The file ended: the next episode takes over, unless the viewer said otherwise.</summary>
    private bool TryPlayNextAtEnd()
    {
        if (_next is null || _upNextDismissed || !Playback.AutoPlayNext || SleepMinutes == -1) return false;
        PlayNext();
        return true;
    }

    private void StopSmart()
    {
        Previews?.Dispose();
        StopCountdown();
        _sleep?.Stop();
        _sleep = null;
    }
}

/// <summary>A chapter in the player's list: its picture, its name and where it starts.</summary>
public sealed partial class ChapterViewModel(PlayerPageViewModel page, Chapter chapter) : ObservableObject
{
    /// <summary>The playhead is in this chapter.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public Chapter Chapter { get; } = chapter;

    public string Title { get; } = string.IsNullOrWhiteSpace(chapter.Tag) ? $"Chapter {chapter.Index}" : chapter.Tag;

    public string StartText { get; } = Format.Clock(chapter.StartTimeOffset / 1000.0);

    public string? ThumbPath { get; } = chapter.Thumb;

    [RelayCommand]
    private void Go() => page.SeekTo(Chapter.StartTimeOffset / 1000.0);
}
