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
public sealed partial class PlayerPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem item, bool resume, VideoQueue? queue = null) : PageViewModel
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

    /// <summary>What is playing.</summary>
    public MetadataItem Item => _item;

    private ShellViewModel Owner => shell;

    private ServerSession Server => session;

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

        LoadSmart();

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

        // The quality chosen in the settings for this kind of network.
        Quality = StreamQuality.FromKbps(session.IsRemote ? shell.Settings.Playback.RemoteQualityKbps : shell.Settings.Playback.HomeQualityKbps);

        // The demo has no files; it plays a moving test pattern instead.
        if (session.IsDemo)
        {
            _source = "av://lavfi:testsrc2=size=1280x720:rate=30";
            StreamSummary = "The built-in test pattern";
        }
        else
        {
            _source = await RouteAsync(part, cancellation);
        }

        // A generated pattern cannot seek: resuming it would decode every frame up to the offset.
        _start = !session.IsDemo && resume && _item.ViewOffset is > 0 ? _item.ViewOffset.Value / 1000.0 : 0;
        Duration = (_item.Duration ?? 0) / 1000.0;

        // Starting mpv waits for its core: a worker does it, never the UI thread.
        var options = Options();
        var created = await Task.Run(() => new MpvPlayer(options), CancellationToken.None);
        if (cancellation.IsCancellationRequested)
        {
            _ = Task.Run(created.Dispose);
            return;
        }

        var shared = new SharedPlayer(created);
        shared.Player.Changed += change => Dispatcher.UIThread.Post(() => Apply(change));
        shared.Player.Ended += (reason, error) => Dispatcher.UIThread.Post(() => OnEnded(reason, error));
        shared.Player.FileLoaded += () => _ = Task.Run(() => ChooseTracks(shared));
        Player = shared;
        Log.Info($"Playing {(session.IsDemo ? "the demo pattern" : IsConverting ? "the server's conversion" : "directly from the server")}{(_start > 0 ? $", resuming at {Clock(_start)}" : string.Empty)}.");
    }

    /// <summary>The video surface can take frames now; start the file.</summary>
    public void SurfaceReady()
    {
        if (_loaded || Player is null || _source is null) return;
        _loaded = true;
        Player.Player.Load(_source, _start);

        _reporter = new DispatcherTimer { Interval = ReportEvery };
        _reporter.Tick += (_, _) =>
        {
            Report(IsPaused ? "paused" : "playing", force: true);
            KeepConversionAlive();
        };
        _reporter.Start();
        KeepAwake(true);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        StopSmart();
        if (_conversion is { } conversion) StopConversion(conversion.Session);
        RetireReplacedConversion();
        _reporter?.Stop();
        KeepAwake(false);
        if (Player is { } shared)
        {
            if (_loaded && !_finished) Report("stopped", force: true);
            shared.Player.PostCommand("stop");

            // The video view lets go of its own hold when it stops drawing; destroying the player
            // waits for mpv, so this hold is let go on a worker.
            Player = null;
            _ = Task.Run(shared.Release);
        }
    }

    /// <summary>
    /// The screen and the computer stay awake while the picture moves, and may sleep when it is
    /// paused or gone: the screensaver's hold alone does not stop the machine suspending mid-film.
    /// </summary>
    private static void KeepAwake(bool awake) =>
        _ = Task.Run(() => awake
            ? Task.WhenAll(Platform.ScreenSaverInhibitor.Shared.InhibitAsync("Playing video"), Platform.SuspendInhibitor.Shared.HoldAsync("video", "Playing video"))
            : Task.WhenAll(Platform.ScreenSaverInhibitor.Shared.ReleaseAsync(), Platform.SuspendInhibitor.Shared.ReleaseAsync("video")));

    // Every control below queues its request with mpv and returns at once; mpv's answer comes back
    // as a property change.
    [RelayCommand]
    private void TogglePause() => Player?.Player.PostFlag("pause", !IsPaused);

    [RelayCommand]
    private void SkipBack() => SeekBy(-10);

    [RelayCommand]
    private void SkipForward() => SeekBy(30);

    public void SeekBy(double seconds) => Player?.Player.PostCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "relative");

    public void SeekTo(double seconds) => Player?.Player.PostCommand("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    [RelayCommand]
    private void ToggleMute() => Player?.Player.PostFlag("mute", !IsMuted);

    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 150);

    [RelayCommand]
    private void Leave() => shell.GoBackCommand.Execute(null);

    partial void OnVolumeChanged(double value) => Player?.Player.PostNumber("volume", value);

    private Dictionary<string, string> Options()
    {
        var headers = string.Join(",", session.Client.MediaHeaders(shell.Identity).Select(h => $"{h.Name}: {h.Value}"));
        var options = new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            // NVDEC and VA-API first, drawn in place or copied back, then mpv's own safe list: left to
            // itself mpv picks vulkan-copy where the others cannot share with the window, and its
            // decoder stalled on the fourth stream opened in one player (no frame ever reached the output).
            ["hwdec"] = Tuxflix.App.Player.ProbeSwitches.HardwareDecoding ?? "nvdec,vaapi,nvdec-copy,vaapi-copy,auto-safe",
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

        // mpv's own mute, applied to the samples: the desktop's volume for the stream is left alone,
        // and the volume can still be changed without a sound.
        if (shell.Silent) options["mute"] = "yes";
        if (Tuxflix.App.Player.ProbeSwitches.AudioOutput is { } output) options["ao"] = output;

        // The server's subtitle selection is applied once the file is open; until then none shows,
        // so a subtitle the file marks as its default cannot flash up first.
        if (_part is { } part && StreamChoice.IsKnown(part)) options["sid"] = "no";
        AddPlaybackOptions(options);
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
                OnPosition(seconds);
                break;
            // The demo's endless pattern reports a length that grows as it plays: the film's own stands.
            case "duration" when change.Number is { } seconds && seconds > 0 && !session.IsDemo:
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
            case "track-list/count" when Player is { } shared:
                _ = Task.Run(() => ListTracksFor(shared));
                break;
            // A conversion's menus list the file's streams, not mpv's tracks: they mark themselves.
            case "aid" when change.Text is { } aid && !IsConverting:
                MarkSelected(AudioTracks, aid);
                break;
            case "sid" when change.Text is { } sid && !IsConverting:
                MarkSelected(SubtitleTracks, sid);
                break;
        }
    }

    private static void MarkSelected(IEnumerable<TrackOption> options, string id)
    {
        foreach (var option in options) option.IsSelected = option.Id == id;
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

        if (TryPlayNextAtEnd()) return;
        shell.GoBackCommand.Execute(null);
    }

    private void Report(string state, bool force)
    {
        if (!_loaded || !shell.ReportsPlayback || (!force && state == _reported)) return;
        _reported = state;
        var sessionIdentifier = _sessionIdentifier;
        var ratingKey = _item.RatingKey;
        var time = (long)(Position * 1000);
        var duration = (long)(Duration * 1000);
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.ReportTimelineAsync(ratingKey, state, time, duration, CancellationToken.None, sessionIdentifier);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Warn($"The server could not be told playback is {state}.", ex);
            }
        });
    }

    /// <summary>
    /// On a worker, once the file is open: starts with the audio and subtitles the server selected,
    /// not the file's own defaults, then lists the tracks for the menus.
    /// </summary>
    private void ChooseTracks(SharedPlayer shared)
    {
        // Reading mpv waits for its core; the hold keeps the player alive while this reads.
        if (!shared.Acquire()) return;
        try
        {
            RetireReplacedConversion();

            // A conversion already carries the chosen audio and burned subtitle; only a file beside the media loads here.
            if (IsConverting)
            {
                if (ExternalSubtitle() is { } beside) LoadSubtitle(shared.Player, beside);
            }
            else if (_part is { } part && StreamChoice.IsKnown(part))
            {
                var tracks = ListTracks(shared.Player).ConvertAll(t => t.Track);
                if (StreamChoice.Selected(part, StreamChoice.Audio) is { } audio && StreamChoice.TrackFor(audio, part, tracks) is { } audioTrack)
                {
                    shared.Player.PostProperty("aid", audioTrack.Id);
                }

                var subtitle = StreamChoice.Selected(part, StreamChoice.Subtitle);
                if (subtitle is { IsExternal: true })
                {
                    LoadSubtitle(shared.Player, subtitle);
                }
                else if (subtitle is not null && StreamChoice.TrackFor(subtitle, part, tracks) is { } subtitleTrack)
                {
                    shared.Player.PostProperty("sid", subtitleTrack.Id);
                }

                Log.Info($"Tracks as the server selected them; subtitles {(subtitle is null ? "off" : subtitle.IsExternal ? "from a file beside the media" : "on")}.");
            }

            ListTracksFor(shared);
        }
        finally
        {
            shared.Release();
        }
    }

    /// <summary>On a worker: reads the track list, then shows it on the UI thread.</summary>
    private void ListTracksFor(SharedPlayer shared)
    {
        if (!shared.Acquire()) return;
        try
        {
            var listed = ListTracks(shared.Player);
            var subtitles = shared.Player.GetString("sid");
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(shared, Player)) ShowTracks(listed, subtitles);
            });
        }
        finally
        {
            shared.Release();
        }
    }

    private void ShowTracks(List<(PlayerTrack Track, string Label, bool Selected)> listed, string? subtitles)
    {
        var tracks = listed.ConvertAll(t => t.Track);
        if (IsConverting)
        {
            ShowConvertedTracks(tracks, subtitles);
            return;
        }

        AudioTracks.Clear();
        SubtitleTracks.Clear();
        SubtitleTracks.Add(new TrackOption(this, "sid", "no", "Off", subtitles is null or "no", stream: null));
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

    /// <summary>The audio and subtitle tracks the player lists now, with a label from the file's own tags. A worker's job.</summary>
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
        if (IsConverting)
        {
            _ = SelectConvertedAsync(option);
            return;
        }

        // Kept for a conversion asked for later in this playback.
        if (option.Property == "aid") _audioStreamId = option.Stream?.Id ?? _audioStreamId;
        else _subtitleStreamId = option.Id == "no" ? 0 : option.Stream?.Id ?? _subtitleStreamId;

        if (option.Id.Length == 0 && option.Stream is { IsExternal: true } external)
        {
            // A file beside the media: it arrives in the next track list, already chosen.
            LoadSubtitle(shared.Player, external);
        }
        else
        {
            shared.Player.PostProperty(option.Property, option.Id);
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

    private void LoadSubtitle(MpvPlayer player, MediaStream external) =>
        player.PostCommand("sub-add", SourceOf(external), "select", external.ExtendedDisplayTitle ?? external.DisplayTitle ?? "Subtitles");

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
