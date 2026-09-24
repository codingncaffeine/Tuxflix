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
/// out; a file played to the end is marked watched. Playback starts with the audio and subtitles the
/// server selected for the viewer, and a track chosen here is told back to the server. The page
/// lets go of the player when it is left, and the video view lets go when it is taken off the
/// screen: whichever is last destroys it.
/// </remarks>
public sealed partial class PlayerPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem item, bool resume) : PageViewModel
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private MetadataItem _item = item;
    private MediaPart? _part;
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

        _part = part;

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
        shared.Player.FileLoaded += () => Dispatcher.UIThread.Post(() =>
        {
            ApplyServerChoice();
            RefreshTracks();
        });
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
        KeepAwake(true);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _reporter?.Stop();
        KeepAwake(false);
        if (Player is { } shared)
        {
            if (_loaded && !_finished) Report("stopped", force: true);
            shared.Player.Command("stop");
            shared.Release();
            Player = null;
        }
    }

    /// <summary>The screen stays awake while the picture moves, and may sleep when it is paused or gone.</summary>
    private static void KeepAwake(bool awake) =>
        _ = awake
            ? Platform.ScreenSaverInhibitor.Shared.InhibitAsync("Playing video")
            : Platform.ScreenSaverInhibitor.Shared.ReleaseAsync();

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
        var options = new Dictionary<string, string>
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
            // What the desktop's volume mixer shows for the sound: the application, then what plays.
            ["audio-client-name"] = "Tuxflix",
            ["force-media-title"] = MediaTitle(),
            ["title"] = "${media-title}",
            ["user-agent"] = $"Tuxflix/{BuildInfo.Version}",
            ["http-header-fields"] = headers,
        };

        // mpv's own volume, applied to the samples: the desktop's volume for the stream is left alone.
        if (shell.Silent) options["volume"] = "0";

        // The server's subtitle selection is applied once the file is open; until then none shows,
        // so a subtitle the file marks as its default cannot flash up first.
        if (_part is { } part && StreamChoice.IsKnown(part)) options["sid"] = "no";
        return options;
    }

    /// <summary>What is playing, in one line: the show, the episode and its name, or the film and its year.</summary>
    private string MediaTitle() =>
        string.Join(" · ", new[] { Heading, _item.Type == "episode" ? Format.EpisodeCode(_item) : null, _item.Type == "episode" ? _item.Title : _item.Year?.ToString(CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

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
                KeepAwake(!paused);
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
        KeepAwake(false);
        _reporter?.Stop();

        // Played to the end: the server hears "stopped" at the end, and the item is marked watched.
        var ratingKey = _item.RatingKey;
        var duration = (long)(Duration * 1000);
        if (shell.ReportsPlayback)
        {
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
        }

        shell.GoBackCommand.Execute(null);
    }

    private void Report(string state, bool force)
    {
        if (!_loaded || !shell.ReportsPlayback || (!force && state == _reported)) return;
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

    /// <summary>Starts with the audio and subtitles the server selected, not the file's own defaults.</summary>
    private void ApplyServerChoice()
    {
        if (Player is not { } shared || _part is not { } part || !StreamChoice.IsKnown(part)) return;
        var tracks = ListTracks(shared.Player).ConvertAll(t => t.Track);

        if (StreamChoice.Selected(part, StreamChoice.Audio) is { } audio && StreamChoice.TrackFor(audio, part, tracks) is { } audioTrack)
        {
            shared.Player.SetProperty("aid", audioTrack.Id);
        }

        var subtitle = StreamChoice.Selected(part, StreamChoice.Subtitle);
        if (subtitle is { IsExternal: true })
        {
            LoadSubtitle(subtitle);
        }
        else if (subtitle is not null && StreamChoice.TrackFor(subtitle, part, tracks) is { } subtitleTrack)
        {
            shared.Player.SetProperty("sid", subtitleTrack.Id);
        }

        Log.Info($"Tracks as the server selected them; subtitles {(subtitle is null ? "off" : subtitle.IsExternal ? "from a file beside the media" : "on")}.");
    }

    private void RefreshTracks()
    {
        if (Player is not { } shared) return;
        var player = shared.Player;
        var listed = ListTracks(player);
        var tracks = listed.ConvertAll(t => t.Track);
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        SubtitleTracks.Add(new TrackOption(this, "sid", "no", "Off", player.GetString("sid") is null or "no", stream: null));
        foreach (var (track, label, selected) in listed)
        {
            // The server's names for its streams are the ones every other Plex player shows.
            var stream = _part is { } part ? StreamChoice.StreamFor(track, part, tracks, SourceOf) : null;
            var name = stream?.ExtendedDisplayTitle ?? stream?.DisplayTitle ?? label;
            (track.Type == "audio" ? AudioTracks : SubtitleTracks).Add(new TrackOption(this, track.Type == "audio" ? "aid" : "sid", track.Id, name, selected, stream));
        }

        // Subtitle files beside the media are offered too, and loaded when chosen.
        if (_part is { } withFiles)
        {
            foreach (var external in StreamChoice.ExternalSubtitles(withFiles).Where(s => tracks.TrueForAll(t => t.Source != SourceOf(s))))
            {
                SubtitleTracks.Add(new TrackOption(this, "sid", string.Empty, external.ExtendedDisplayTitle ?? external.DisplayTitle ?? "Subtitles", false, external));
            }
        }
    }

    /// <summary>The audio and subtitle tracks the player lists now, with a label from the file's own tags.</summary>
    private static List<(PlayerTrack Track, string Label, bool Selected)> ListTracks(MpvPlayer player)
    {
        var listed = new List<(PlayerTrack, string, bool)>();
        var count = (int)(player.GetNumber("track-list/count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            var type = player.GetString($"track-list/{i}/type");
            var id = player.GetString($"track-list/{i}/id");
            if (id is null || type is not ("audio" or "sub")) continue;
            var external = player.GetString($"track-list/{i}/external") == "yes";
            var index = player.GetNumber($"track-list/{i}/ff-index");
            var track = new PlayerTrack(
                type,
                id,
                external || index is null ? null : (int)index.Value,
                external ? player.GetString($"track-list/{i}/external-filename") : null);
            var label = string.Join("  ·  ", new[]
            {
                player.GetString($"track-list/{i}/title"),
                Language(player.GetString($"track-list/{i}/lang")),
                player.GetString($"track-list/{i}/codec")?.ToUpperInvariant(),
            }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
            listed.Add((track, label.Length > 0 ? label : $"Track {id}", player.GetString($"track-list/{i}/selected") == "yes"));
        }

        return listed;
    }

    internal void Select(TrackOption option)
    {
        if (Player is not { } shared) return;
        if (option.Id.Length == 0 && option.Stream is { IsExternal: true } external)
        {
            // A file beside the media: it arrives in the next track list, already chosen.
            LoadSubtitle(external);
        }
        else
        {
            shared.Player.SetProperty(option.Property, option.Id);
        }

        foreach (var other in option.Property == "aid" ? AudioTracks : SubtitleTracks) other.IsSelected = ReferenceEquals(other, option);
        Remember(option);
    }

    /// <summary>Tells the server the viewer's choice, as Plex players do, so it holds on every device.</summary>
    private void Remember(TrackOption option)
    {
        if (!shell.ReportsPlayback || _part is not { Id: > 0 } part) return;
        long? audio = option.Property == "aid" ? option.Stream?.Id : null;
        long? subtitle = option.Property != "sid" ? null : option.Id == "no" ? 0 : option.Stream?.Id;
        if (audio is null && subtitle is null) return;

        var partId = part.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.SelectStreamsAsync(partId, audio, subtitle, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Warn("The server could not be told which track was chosen.", ex);
            }
        });
    }

    private void LoadSubtitle(MediaStream external) =>
        Player?.Player.Command("sub-add", SourceOf(external), "select", external.ExtendedDisplayTitle ?? external.DisplayTitle ?? "Subtitles");

    /// <summary>Where the player fetches a subtitle file kept beside the media; the token goes in a header.</summary>
    private string SourceOf(MediaStream stream) => session.Client.MediaUri(stream.Key!).ToString();

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
/// <param name="id">The player's track id; empty for a subtitle file the player has not loaded yet.</param>
/// <param name="stream">The server's stream for the track, when the server lists it.</param>
public sealed partial class TrackOption(PlayerPageViewModel page, string property, string id, string label, bool selected, MediaStream? stream) : ObservableObject
{
    public string Property { get; } = property;

    public string Id { get; } = id;

    public string Label { get; } = label;

    public MediaStream? Stream { get; } = stream;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = selected;

    [RelayCommand]
    private void Choose() => page.Select(this);
}
