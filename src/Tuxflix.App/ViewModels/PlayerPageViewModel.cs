using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Player;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Watching something: the file straight from the server (direct play), started where it was
/// left, with the server told where playback is so the place is kept everywhere.
/// </summary>
/// <remarks>
/// The token travels in a request header mpv sends, never in a URL. Progress is reported every
/// ten seconds while playing and at every change of state, and once more as "stopped" on the way
/// out; a file played to the end is marked watched. The page lets go of the player when it is
/// left, and the video view lets go when it is taken off the screen: whichever is last destroys it.
/// </remarks>
public sealed partial class PlayerPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem item, bool resume) : PageViewModel
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private MetadataItem _item = item;
    private string? _source;
    private double _start;
    private bool _loaded;
    private bool _finished;
    private DispatcherTimer? _reporter;
    private string _reported = string.Empty;

    public override string Title => "Now playing";

    public override bool ShowsRail => false;

    public override bool IsImmersive => true;

    [ObservableProperty]
    public partial SharedPlayer? Player { get; private set; }

    public string Heading => _item.Type == "episode" ? _item.GrandparentTitle ?? _item.Title : _item.Title;

    public string Subheading => _item.Type == "episode"
        ? $"{Format.EpisodeCode(_item)}  ·  {_item.Title}"
        : _item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(RemainingText))]
    public partial double Position { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(RemainingText), nameof(SeekMaximum))]
    public partial double Duration { get; private set; }

    /// <summary>The seek bar's value: follows the position, except while the viewer holds the thumb.</summary>
    [ObservableProperty]
    public partial double SeekValue { get; set; }

    public bool IsScrubbing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseTip))]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial bool IsBuffering { get; private set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    /// <summary>The seek bar's end: never zero, or an unknown length would draw as a full bar.</summary>
    public double SeekMaximum => Math.Max(1, Duration);

    public string PositionText => Clock(Position);

    public string DurationText => Clock(Duration);

    public string RemainingText => Duration > 0 ? "-" + Clock(Math.Max(0, Duration - Position)) : string.Empty;

    public string PlayPauseTip => IsPaused ? "Play" : "Pause";

    public ObservableCollection<TrackOption> AudioTracks { get; } = [];

    public ObservableCollection<TrackOption> SubtitleTracks { get; } = [];

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        if (await Task.Run(() => session.Client.GetMetadataAsync(_item.RatingKey, cancellation), cancellation) is { } full)
        {
            _item = full;
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(Subheading));
        }

        var part = _item.Media?.FirstOrDefault()?.Part?.FirstOrDefault();
        if (part?.Key is null)
        {
            ErrorMessage = "The server lists no playable file for this item.";
            return;
        }

        if (!MpvPlayer.IsAvailable)
        {
            ErrorMessage = "Playback needs libmpv, which is not installed (the mpv package on most distributions).";
            return;
        }

        // The demo has no files; it plays a moving test pattern instead.
        _source = session.IsDemo
            ? "av://lavfi:testsrc2=size=1280x720:rate=30"
            : session.Client.MediaUri(part.Key).ToString();
        // A generated pattern cannot seek: resuming it would decode every frame up to the offset.
        _start = !session.IsDemo && resume && _item.ViewOffset is > 0 ? _item.ViewOffset.Value / 1000.0 : 0;
        Duration = (_item.Duration ?? 0) / 1000.0;

        var shared = new SharedPlayer(new MpvPlayer(Options()));
        shared.Player.Changed += change => Dispatcher.UIThread.Post(() => Apply(change));
        shared.Player.Ended += (reason, error) => Dispatcher.UIThread.Post(() => OnEnded(reason, error));
        shared.Player.FileLoaded += () => Dispatcher.UIThread.Post(RefreshTracks);
        Player = shared;
        Log.Info($"Playing {(session.IsDemo ? "the demo pattern" : "directly from the server")}{(_start > 0 ? $", resuming at {Clock(_start)}" : string.Empty)}.");
    }

    /// <summary>The video surface can take frames now; start the file.</summary>
    public void SurfaceReady()
    {
        if (_loaded || Player is null || _source is null) return;
        _loaded = true;
        Player.Player.Load(_source, _start);

        _reporter = new DispatcherTimer { Interval = ReportEvery };
        _reporter.Tick += (_, _) => Report(IsPaused ? "paused" : "playing", force: true);
        _reporter.Start();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _reporter?.Stop();
        if (Player is { } shared)
        {
            if (_loaded && !_finished) Report("stopped", force: true);
            shared.Player.Command("stop");
            shared.Release();
            Player = null;
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (Player is null) return;
        Player.Player.SetFlag("pause", !IsPaused);
    }

    [RelayCommand]
    private void SkipBack() => SeekBy(-10);

    [RelayCommand]
    private void SkipForward() => SeekBy(30);

    public void SeekBy(double seconds) => Player?.Player.Command("seek", seconds.ToString(CultureInfo.InvariantCulture), "relative");

    public void SeekTo(double seconds) => Player?.Player.Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    [RelayCommand]
    private void ToggleMute() => Player?.Player.SetFlag("mute", !IsMuted);

    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 150);

    [RelayCommand]
    private void Leave() => shell.GoBackCommand.Execute(null);

    partial void OnVolumeChanged(double value) => Player?.Player.SetNumber("volume", value);

    private Dictionary<string, string> Options()
    {
        var headers = string.Join(",", session.Client.MediaHeaders(shell.Identity).Select(h => $"{h.Name}: {h.Value}"));
        return new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["hwdec"] = "auto-safe",
            ["idle"] = "yes",
            ["keep-open"] = "no",
            ["config"] = "no",
            ["terminal"] = "no",
            ["osc"] = "no",
            ["osd-level"] = "0",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["load-scripts"] = "no",
            ["ytdl"] = "no",
            ["cache"] = "yes",
            ["demuxer-max-bytes"] = "400MiB",
            ["demuxer-readahead-secs"] = "30",
            ["volume-max"] = "150",
            ["user-agent"] = $"Tuxflix/{BuildInfo.Version}",
            ["http-header-fields"] = headers,
        };
    }

    private void Apply(PlayerChange change)
    {
        switch (change.Name)
        {
            case "time-pos" when change.Number is { } seconds:
                Position = seconds;
                if (!IsScrubbing) SeekValue = seconds;
                break;
            case "duration" when change.Number is { } seconds && seconds > 0:
                Duration = seconds;
                break;
            case "pause" when change.Flag is { } paused:
                IsPaused = paused;
                Report(paused ? "paused" : "playing", force: false);
                break;
            case "paused-for-cache" when change.Flag is { } waiting:
                IsBuffering = waiting;
                break;
            case "mute" when change.Flag is { } muted:
                IsMuted = muted;
                break;
            case "hwdec-current" when change.Text is { } hwdec:
                Log.Info($"Hardware decoding: {hwdec}.");
                break;
            case "track-list/count":
                RefreshTracks();
                break;
        }
    }

    private void OnEnded(EndReason reason, string? error)
    {
        if (reason == EndReason.Failed)
        {
            ErrorMessage = "The file could not be played: " + (error ?? "mpv gave no reason") + ".";
            Log.Warn($"Playback failed: {error}");
            return;
        }

        if (reason != EndReason.Finished || _finished) return;
        _finished = true;
        _reporter?.Stop();

        // Played to the end: the server hears "stopped" at the end, and the item is marked watched.
        var ratingKey = _item.RatingKey;
        var duration = (long)(Duration * 1000);
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.ReportTimelineAsync(ratingKey, "stopped", duration, duration, CancellationToken.None);
                await session.Client.ScrobbleAsync(ratingKey, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Warn("The server could not be told the item was watched.", ex);
            }
        });

        shell.GoBackCommand.Execute(null);
    }

    private void Report(string state, bool force)
    {
        if (!_loaded || (!force && state == _reported)) return;
        _reported = state;
        var ratingKey = _item.RatingKey;
        var time = (long)(Position * 1000);
        var duration = (long)(Duration * 1000);
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.ReportTimelineAsync(ratingKey, state, time, duration, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Warn($"The server could not be told playback is {state}.", ex);
            }
        });
    }

    private void RefreshTracks()
    {
        if (Player is not { } shared) return;
        var player = shared.Player;
        var count = (int)(player.GetNumber("track-list/count") ?? 0);
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        SubtitleTracks.Add(new TrackOption(this, "sid", "no", "Off", player.GetString("sid") is null or "no"));
        for (var i = 0; i < count; i++)
        {
            var type = player.GetString($"track-list/{i}/type");
            var id = player.GetString($"track-list/{i}/id");
            if (id is null || type is not ("audio" or "sub")) continue;
            var label = string.Join("  ·  ", new[]
            {
                player.GetString($"track-list/{i}/title"),
                Language(player.GetString($"track-list/{i}/lang")),
                player.GetString($"track-list/{i}/codec")?.ToUpperInvariant(),
            }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
            var selected = player.GetString($"track-list/{i}/selected") == "yes";
            var option = new TrackOption(this, type == "audio" ? "aid" : "sid", id, label.Length > 0 ? label : $"Track {id}", selected);
            (type == "audio" ? AudioTracks : SubtitleTracks).Add(option);
        }
    }

    internal void Select(TrackOption option)
    {
        Player?.Player.SetProperty(option.Property, option.Id);
        foreach (var other in option.Property == "aid" ? AudioTracks : SubtitleTracks) other.IsSelected = ReferenceEquals(other, option);
    }

    private static string? Language(string? code) =>
        code is { Length: > 0 } && CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.ThreeLetterISOLanguageName == code || c.TwoLetterISOLanguageName == code) is { } culture
            ? culture.EnglishName
            : code;

    private static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>An audio or subtitle track the viewer can choose.</summary>
public sealed partial class TrackOption(PlayerPageViewModel page, string property, string id, string label, bool selected) : ObservableObject
{
    public string Property { get; } = property;

    public string Id { get; } = id;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = selected;

    [RelayCommand]
    private void Choose() => page.Select(this);
}
