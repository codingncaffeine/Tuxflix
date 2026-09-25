using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Player;

namespace Tuxflix.App.Music;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>A track in the play queue.</summary>
public sealed partial class QueueEntry : ObservableObject
{
    private readonly MusicPlayer _player;

    internal QueueEntry(MusicPlayer player, MetadataItem track)
    {
        _player = player;
        Track = track;
    }

    public MetadataItem Track { get; internal set; }

    public string Title => Track.Title;

    /// <summary>The track's own artist when it differs from the album's (a guest, a compilation), else the album's.</summary>
    public string Artist => !string.IsNullOrWhiteSpace(Track.OriginalTitle) ? Track.OriginalTitle! : Track.GrandparentTitle ?? string.Empty;

    public string Album => Track.ParentTitle ?? string.Empty;

    public string? ArtPath => Track.ParentThumb ?? Track.Thumb ?? Track.GrandparentThumb;

    public string DurationText => Track.Duration is > 0 ? Format.Clock(Track.Duration.Value / 1000.0) : string.Empty;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private void Play() => _player.PlayEntry(this);

    [RelayCommand]
    private void Remove() => _player.Remove(this);
}

/// <summary>
/// Plays music: a play queue mirrored into mpv's own playlist, so one track runs into the next
/// without a gap, each track levelled to the server's loudness analysis.
/// </summary>
/// <remarks>
/// <para>
/// One audio-only mpv for the session, started on the first play and kept, so the queue survives
/// moving about the library. The queue here is the authority and mpv's playlist mirrors it entry
/// for entry: an index in one is the same index in the other. Everything the interface asks for is
/// queued with mpv and returns at once; reading tracks happens on workers.
/// </para>
/// <para>
/// Levelling uses the gain the server's loudness analysis gives each track, applied as mpv's
/// <c>volume-gain</c> for that file alone, and never above the level that would clip the track's
/// peak. A queue of one album uses the album's gain, so the album keeps its own quiet and loud
/// songs; a mixed queue uses each track's. Progress goes to the server every ten seconds and at
/// every change of state, and a track played to its end is marked played.
/// </para>
/// </remarks>
public sealed partial class MusicPlayer : ObservableObject, IDisposable
{
    private const int Batch = 100;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private readonly ShellViewModel _shell;
    private readonly ServerSession _session;
    private readonly List<QueueEntry> _unshuffled = [];
    private SharedPlayer? _player;
    private Task<SharedPlayer>? _starting;
    private DispatcherTimer? _reporter;
    private CancellationTokenSource? _filling;
    private QueueEntry? _loaded;
    private string _reported = string.Empty;
    private bool _useAlbumGain;
    private bool _disposed;

    public MusicPlayer(ShellViewModel shell, ServerSession session)
    {
        _shell = shell;
        _session = session;
        Queue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueue));

        // The equalizer as it was left; its chain goes on when mpv starts.
        var eq = shell.Settings.Equalizer;
        EqualizerPreamp = Math.Clamp(eq.Preamp, -12, 12);
        var bands = eq.Bands ?? [];
        for (var i = 0; i < 10 && i < bands.Length; i++) EqualizerBands[i] = Math.Clamp(bands[i], -12, 12);
        IsEqualizerOn = eq.On;
    }

    public ObservableCollection<QueueEntry> Queue { get; } = [];

    public bool HasQueue => Queue.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(Title), nameof(Artist), nameof(Album), nameof(ArtPath))]
    public partial QueueEntry? Current { get; private set; }

    public bool HasCurrent => Current is not null;

    public string Title => Current?.Title ?? string.Empty;

    public string Artist => Current?.Artist ?? string.Empty;

    public string Album => Current?.Album ?? string.Empty;

    public string? ArtPath => Current?.ArtPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseTip))]
    public partial bool IsPaused { get; private set; } = true;

    public string PlayPauseTip => IsPaused ? "Play" : "Pause";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double Position { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(SeekMaximum))]
    public partial double Duration { get; private set; }

    /// <summary>The seek bar's value: follows the position, except while the listener holds the thumb.</summary>
    [ObservableProperty]
    public partial double SeekValue { get; set; }

    public bool IsScrubbing { get; set; }

    public double SeekMaximum => Math.Max(1, Duration);

    public string PositionText => Format.Clock(Position);

    public string DurationText => Format.Clock(Duration);

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleTip))]
    public partial bool IsShuffled { get; private set; }

    public string ShuffleTip => IsShuffled ? "Shuffle is on" : "Shuffle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatTip), nameof(IsRepeating), nameof(IsRepeatingOne))]
    public partial RepeatMode Repeat { get; private set; }

    public bool IsRepeating => Repeat != RepeatMode.Off;

    public bool IsRepeatingOne => Repeat == RepeatMode.One;

    public string RepeatTip => Repeat switch
    {
        RepeatMode.All => "Repeating the queue",
        RepeatMode.One => "Repeating this track",
        _ => "Repeat",
    };

    /// <summary>The mpv playing the queue, once started.</summary>
    public SharedPlayer? Player => _player;

    // ===== The equalizer =====

    /// <summary>The ten bands' gains in dB, at Winamp's classic centres (60 Hz to 16 kHz), so a classic skin's labels hold.</summary>
    public double[] EqualizerBands { get; } = new double[10];

    /// <summary>The preamp, dB.</summary>
    public double EqualizerPreamp { get; private set; }

    [ObservableProperty]
    public partial bool IsEqualizerOn { get; private set; }

    /// <summary>A band, the preamp or the switch changed: the windows that draw the equalizer redraw.</summary>
    public event Action? EqualizerChanged;

    public void SetEqualizerBand(int band, double decibels)
    {
        if (band is < 0 or > 9) return;
        EqualizerBands[band] = Math.Clamp(decibels, -12, 12);
        if (IsEqualizerOn) Tune("g", Number(EqualizerBands[band]), $"equalizer@b{band}");
        RememberEqualizer();
    }

    public void SetEqualizerPreamp(double decibels)
    {
        EqualizerPreamp = Math.Clamp(decibels, -12, 12);
        if (IsEqualizerOn) Tune("volume", Number(EqualizerPreamp) + "dB", "volume@pre");
        RememberEqualizer();
    }

    public void SetEqualizer(bool on)
    {
        IsEqualizerOn = on;
        ApplyAudioChain();
        RememberEqualizer();
    }

    /// <summary>Sets every band and the preamp at once (a preset), without ten re-applications.</summary>
    public void SetEqualizer(double preamp, IReadOnlyList<double> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        EqualizerPreamp = Math.Clamp(preamp, -12, 12);
        for (var i = 0; i < 10 && i < bands.Count; i++) EqualizerBands[i] = Math.Clamp(bands[i], -12, 12);
        ApplyAudioChain();
        RememberEqualizer();
    }

    /// <summary>Keeps the equalizer for the next run, then tells the windows that draw it.</summary>
    private void RememberEqualizer()
    {
        var eq = _shell.Settings.Equalizer;
        eq.On = IsEqualizerOn;
        eq.Preamp = EqualizerPreamp;
        eq.Bands = [.. EqualizerBands];
        _shell.SaveSettings();
        EqualizerChanged?.Invoke();
    }

    /// <summary>Left (-1) to right (1): the classic player's balance slider.</summary>
    public double Balance { get; private set; }

    public void SetBalance(double balance)
    {
        Balance = Math.Clamp(balance, -1, 1);
        if (_chainHasBalance) Tune("balance_out", Number(Balance), "stereotools@bal");
        else if (Balance != 0) ApplyAudioChain();
    }

    private bool _chainHasBalance;
    private bool _rebuildQueued;

    /// <summary>
    /// Moves one filter of the chain. mpv builds a chain's graph only when audio next flows through
    /// it, so a command sent just after a rebuild (or while paused) is refused; the chain is then
    /// rebuilt, once, with every value as it stands by then, and nothing a slider did is lost.
    /// </summary>
    private void Tune(params string[] command) =>
        _player?.Player.PostCommand(() => Dispatcher.UIThread.Post(RebuildSoon), ["af-command", "fx", .. command]);

    private void RebuildSoon()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        DispatcherTimer.RunOnce(
            () =>
            {
                _rebuildQueued = false;
                ApplyAudioChain();
            },
            TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// The equalizer and the balance as one labelled mpv filter: ten peaking biquads, a preamp and
    /// a stereo balance, each named, so a slider moves one of them with <c>af-command</c> and the
    /// audio never re-initialises. With the equalizer off and the balance centred there is no filter
    /// at all, not a flat one: nothing touches the samples. A balance once moved stays in the chain
    /// until the chain is next rebuilt, so bringing it back to the centre never interrupts the sound.
    /// </summary>
    private void ApplyAudioChain()
    {
        if (_player is not { } shared) return;
        var parts = new List<string>();
        if (IsEqualizerOn)
        {
            parts.AddRange(Enumerable.Range(0, 10).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"equalizer@b{i}=f={Classic.ClassicSprites.EqFrequencies[i]}:t=q:w=1.1:g={EqualizerBands[i]:0.##}")));
            parts.Add($"volume@pre=volume={Number(EqualizerPreamp)}dB");
        }

        _chainHasBalance = Balance != 0;
        if (_chainHasBalance) parts.Add($"stereotools@bal=balance_out={Number(Balance)}");
        shared.Player.PostProperty("af", parts.Count == 0 ? string.Empty : $"@fx:lavfi=[{string.Join(",", parts)}]");
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>What is playing and where, readable from any thread: the visualizers follow it.</summary>
    public PlayClock Clock => _clock;

    private volatile PlayClock _clock = PlayClock.Nothing;

    /// <summary>The analysis the visualizers draw from, started when the first one shows.</summary>
    public VisualizerFeed Visuals => _visuals ??= new VisualizerFeed(this, ShadowOptions);

    private VisualizerFeed? _visuals;

    /// <summary>The options a second, silent decode of the same file needs: the server's headers.</summary>
    private Dictionary<string, string> ShadowOptions() => new()
    {
        ["user-agent"] = $"Tuxflix/{BuildInfo.Version}",
        ["http-header-fields"] = string.Join(",", _session.Client.MediaHeaders(_shell.Identity).Select(h => $"{h.Name}: {h.Value}")),
    };

    private void UpdateClock() =>
        _clock = new PlayClock(
            Current is { } entry ? Url(entry.Track) : null,
            Current?.Track.RatingKey,
            Position,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            !IsPaused && Current is not null);

    /// <summary>The current track changed (or the queue ended): raised on the UI thread.</summary>
    public event Action? TrackChanged;

    /// <summary>Plays <paramref name="tracks"/> from <paramref name="start"/>, or all of them in a random order.</summary>
    public async Task PlayAsync(IReadOnlyList<MetadataItem> tracks, int start = 0, bool shuffle = false)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var playable = tracks.Where(t => t.Type == "track").ToList();
        if (playable.Count == 0 || _disposed) return;
        EndRadio();

        _filling?.Cancel();
        var filling = _filling = new CancellationTokenSource();
        var player = await EnsurePlayerAsync();
        if (filling.IsCancellationRequested) return;

        start = Math.Clamp(start, 0, playable.Count - 1);
        _unshuffled.Clear();
        _unshuffled.AddRange(playable.Select(t => new QueueEntry(this, t)));
        var first = _unshuffled[start];
        var order = shuffle ? Shuffled(_unshuffled, first) : [.. _unshuffled];
        if (shuffle) start = 0;
        IsShuffled = shuffle;
        _useAlbumGain = playable.Select(t => t.ParentRatingKey).Distinct(StringComparer.Ordinal).Count() == 1;

        Queue.Clear();
        foreach (var entry in order) Queue.Add(entry);
        SetCurrent(Queue[start]);
        Log.Info($"Music: playing {Queue.Count} track(s){(shuffle ? ", shuffled" : string.Empty)}.");

        // The starting track goes first so it plays at once; the rest fill in around it, in queue
        // order, with each track's loudness read in batches.
        await FillAsync(player.Player, start, filling.Token);
    }

    /// <summary>Plays an album from a track, or shuffled.</summary>
    public async Task PlayAlbumAsync(MetadataItem album, int start = 0, bool shuffle = false)
    {
        ArgumentNullException.ThrowIfNull(album);
        var tracks = await Task.Run(() => _session.Client.GetChildrenAsync(album.RatingKey, CancellationToken.None));
        await PlayAsync(tracks, start, shuffle);
    }

    /// <summary>Plays every track of an artist, in album order or shuffled.</summary>
    public async Task PlayArtistAsync(MetadataItem artist, bool shuffle)
    {
        ArgumentNullException.ThrowIfNull(artist);
        var tracks = await Task.Run(() => _session.Client.GetAllLeavesAsync(artist.RatingKey, CancellationToken.None));
        await PlayAsync(tracks, 0, shuffle);
    }

    /// <summary>Adds tracks after the current one, or at the end of the queue.</summary>
    public async Task EnqueueAsync(IReadOnlyList<MetadataItem> tracks, bool next)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var playable = tracks.Where(t => t.Type == "track").ToList();
        if (playable.Count == 0) return;
        if (!HasQueue)
        {
            await PlayAsync(playable);
            return;
        }

        var player = await EnsurePlayerAsync();
        var full = await Task.Run(() => FullRecordsAsync(playable, CancellationToken.None));
        var at = next && Current is { } current ? Queue.IndexOf(current) + 1 : Queue.Count;
        _useAlbumGain = false;
        foreach (var track in full)
        {
            var entry = new QueueEntry(this, track);
            Queue.Insert(at, entry);
            _unshuffled.Add(entry);
            player.Player.PostCommand("loadfile", Url(track), "insert-at", at.ToString(CultureInfo.InvariantCulture), Options(track));
            at++;
        }
    }

    [RelayCommand]
    private void ShowNowPlaying() => _shell.ShowNowPlayingCommand.Execute(null);

    [RelayCommand]
    private void ShowCompactPlayer() => _shell.ShowCompactPlayerCommand.Execute(null);

    [RelayCommand]
    private void TogglePause()
    {
        if (_player is not { } shared) return;
        if (!HasQueue) return;
        shared.Player.PostFlag("pause", !IsPaused);
    }

    [RelayCommand]
    private void Next() => _player?.Player.PostCommand("playlist-next", "force");

    /// <summary>Back to the start of the track, or to the one before when already near its start.</summary>
    [RelayCommand]
    private void Previous()
    {
        if (_player is not { } shared) return;
        if (Position > 3 || Current is { } current && Queue.IndexOf(current) == 0) shared.Player.PostCommand("seek", "0", "absolute");
        else shared.Player.PostCommand("playlist-prev", "force");
    }

    public void SeekTo(double seconds) =>
        _player?.Player.PostCommand("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    [RelayCommand]
    private void ToggleMute() => _player?.Player.PostFlag("mute", !IsMuted);

    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 150);

    partial void OnVolumeChanged(double value) => _player?.Player.PostNumber("volume", value);

    /// <summary>Playing music keeps the computer awake (the screen may still blank); a probe run leaves the desktop alone.</summary>
    partial void OnIsPausedChanged(bool value)
    {
        if (_shell.Silent) return;
        _ = Task.Run(() => value
            ? Platform.SuspendInhibitor.Shared.ReleaseAsync("music")
            : Platform.SuspendInhibitor.Shared.HoldAsync("music", "Playing music"));
    }

    /// <summary>Sets the repeat outright (the desktop's media controls ask for one), as the button cycles it.</summary>
    public void SetRepeat(RepeatMode mode)
    {
        if (Repeat == mode) return;
        Repeat = mode;
        ApplyRepeat();
    }

    /// <summary>Turns shuffle on or off outright.</summary>
    public void SetShuffle(bool shuffle)
    {
        if (IsShuffled != shuffle) ToggleShuffle();
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        ApplyRepeat();
    }

    /// <summary>
    /// Shuffles what is left around the current track, or puts the queue back in its own order;
    /// the track playing keeps playing either way.
    /// </summary>
    [RelayCommand]
    private void ToggleShuffle()
    {
        if (_player is not { } shared || Current is not { } current) return;
        IsShuffled = !IsShuffled;
        var order = IsShuffled ? Shuffled(Queue, current) : [.. _unshuffled.Where(Queue.Contains)];

        // mpv keeps the playing file through a clear; the rest go back in the new order around it.
        shared.Player.PostCommand("playlist-clear");
        var at = order.IndexOf(current);
        for (var i = 0; i < order.Count; i++)
        {
            if (i == at) continue;
            var track = order[i].Track;
            if (i < at) shared.Player.PostCommand("loadfile", Url(track), "insert-at", i.ToString(CultureInfo.InvariantCulture), Options(track));
            else shared.Player.PostCommand("loadfile", Url(track), "append", "-1", Options(track));
        }

        Queue.Clear();
        foreach (var entry in order) Queue.Add(entry);
    }

    public void PlayEntry(QueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var index = Queue.IndexOf(entry);
        if (index < 0 || _player is not { } shared) return;
        shared.Player.PostCommand("playlist-play-index", index.ToString(CultureInfo.InvariantCulture));
        shared.Player.PostFlag("pause", false);
    }

    public void Remove(QueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var index = Queue.IndexOf(entry);
        if (index < 0 || _player is not { } shared) return;

        // Removing the playing entry makes mpv go on to the next one by itself.
        shared.Player.PostCommand("playlist-remove", index.ToString(CultureInfo.InvariantCulture));
        Queue.RemoveAt(index);
        _unshuffled.Remove(entry);
        if (Queue.Count == 0) Stop();
    }

    /// <summary>Ends playback and empties the queue.</summary>
    [RelayCommand]
    public void Stop()
    {
        _filling?.Cancel();
        if (_player is { } shared)
        {
            ReportState("stopped");
            shared.Player.PostCommand("stop");
        }

        Queue.Clear();
        _unshuffled.Clear();
        SetCurrent(null);
        Position = Duration = 0;
        _reporter?.Stop();
    }

    /// <summary>Pauses without losing the place: a film starting, say.</summary>
    public void Pause()
    {
        if (!IsPaused) _player?.Player.PostFlag("pause", true);
    }

    /// <summary>The classic players' stop: playback halts at the start of the track, and the queue stays.</summary>
    [ObservableProperty]
    public partial bool IsHalted { get; private set; }

    public void Halt()
    {
        if (_player is not { } shared || Current is null) return;
        shared.Player.PostFlag("pause", true);
        shared.Player.PostCommand("seek", "0", "absolute");
        IsHalted = true;
    }

    /// <summary>Plays again after a pause or a halt; with nothing loaded, the queue from its start.</summary>
    public void Resume()
    {
        if (_player is not { } shared || !HasQueue) return;
        if (Current is null) PlayEntry(Queue[0]);
        shared.Player.PostFlag("pause", false);
        IsHalted = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _filling?.Cancel();
        _reporter?.Stop();
        if (Current is not null) ReportState("stopped");
        _visuals?.Dispose();
        var shared = _player;
        _player = null;

        // Destroying mpv waits for it: never on the UI thread.
        if (shared is not null) _ = Task.Run(shared.Release);
    }

    private async Task<SharedPlayer> EnsurePlayerAsync()
    {
        if (_player is { } ready) return ready;
        _starting ??= StartAsync();
        return await _starting;
    }

    private async Task<SharedPlayer> StartAsync()
    {
        var options = new Dictionary<string, string>
        {
            ["vid"] = "no",
            ["audio-display"] = "no",
            ["gapless-audio"] = "yes",
            ["prefetch-playlist"] = "yes",
            ["idle"] = "yes",
            ["keep-open"] = "no",
            ["config"] = "no",
            ["terminal"] = "no",
            ["input-default-bindings"] = "no",
            ["load-scripts"] = "no",
            ["ytdl"] = "no",
            ["cache"] = "yes",
            ["demuxer-max-bytes"] = "64MiB",
            ["volume-max"] = "150",
            ["audio-client-name"] = "Tuxflix",
            ["title"] = "${media-title}",
            ["user-agent"] = $"Tuxflix/{BuildInfo.Version}",
            ["http-header-fields"] = string.Join(",", _session.Client.MediaHeaders(_shell.Identity).Select(h => $"{h.Name}: {h.Value}")),
        };
        if (_shell.Silent) options["mute"] = "yes";
        if (Tuxflix.App.Player.ProbeSwitches.AudioOutput is { } output) options["ao"] = output;

        var player = new SharedPlayer(await Task.Run(() => new MpvPlayer(options)));
        player.Player.Changed += change => Dispatcher.UIThread.Post(() => Apply(change));
        player.Player.FileLoaded += () => OnFileLoaded(player);
        player.Player.Ended += (reason, error) => OnEnded(reason, error);
        _player = player;
        if (_disposed) _ = Task.Run(player.Release);
        ApplyAudioChain();
        return player;
    }

    /// <summary>Loads the queue into mpv: the starting track first, then the others around it in order.</summary>
    private async Task FillAsync(MpvPlayer player, int start, CancellationToken cancellation)
    {
        var entries = Queue.ToList();
        try
        {
            // The starting track's record first, so it can start at once with its own gain.
            var head = await Task.Run(() => FullRecordsAsync([entries[start].Track], cancellation), cancellation);
            if (head.Count > 0) entries[start].Track = head[0];
            player.PostCommand("loadfile", Url(entries[start].Track), "replace", "-1", Options(entries[start].Track));
            player.PostFlag("pause", false);
            ApplyRepeat();

            // Then the rest in batches: those before the start inserted ahead of it, those after appended.
            var inserted = 0;
            var order = Enumerable.Range(0, entries.Count).Where(i => i != start).ToList();
            foreach (var chunk in order.Chunk(Batch))
            {
                var records = await Task.Run(() => FullRecordsAsync([.. chunk.Select(i => entries[i].Track)], cancellation), cancellation);
                var byKey = records.ToDictionary(r => r.RatingKey, StringComparer.Ordinal);
                foreach (var i in chunk)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (byKey.TryGetValue(entries[i].Track.RatingKey, out var full)) entries[i].Track = full;
                    var track = entries[i].Track;
                    if (i < start)
                    {
                        player.PostCommand("loadfile", Url(track), "insert-at", inserted.ToString(CultureInfo.InvariantCulture), Options(track));
                        inserted++;
                    }
                    else
                    {
                        player.PostCommand("loadfile", Url(track), "append", "-1", Options(track));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Another queue replaced this one.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("Music: the queue could not be read in full.", ex);
        }
    }

    /// <summary>The tracks' full records (their loudness is only in those), in the order given.</summary>
    private async Task<IReadOnlyList<MetadataItem>> FullRecordsAsync(IReadOnlyList<MetadataItem> tracks, CancellationToken cancellation)
    {
        var found = await _session.Client.GetMetadataManyAsync([.. tracks.Select(t => t.RatingKey)], cancellation).ConfigureAwait(false);
        var byKey = found.ToDictionary(f => f.RatingKey, StringComparer.Ordinal);
        return [.. tracks.Select(t => byKey.GetValueOrDefault(t.RatingKey) ?? t)];
    }

    private string Url(MetadataItem track) =>
        _session.IsDemo ? DemoTune.Address(track)
        : track.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Key is { } key ? _session.Client.MediaUri(key).ToString() : "about:blank";

    /// <summary>mpv's per-file options for a track: its title for the desktop, and its levelling gain.</summary>
    private string Options(MetadataItem track)
    {
        var title = string.Join(" · ", new[] { track.OriginalTitle ?? track.GrandparentTitle, track.Title }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var options = $"force-media-title={Quote(title)}";
        if (Gain(track) is { } gain) options += string.Create(CultureInfo.InvariantCulture, $",volume-gain={gain:0.##}");
        return options;
    }

    /// <summary>The levelling gain in dB: the album's for a one-album queue, else the track's; never enough to clip the peak.</summary>
    private double? Gain(MetadataItem track)
    {
        var audio = track.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Stream?.FirstOrDefault(s => s.StreamType == StreamChoice.Audio);
        var gain = (_useAlbumGain ? audio?.AlbumGain : null) ?? audio?.Gain;
        if (gain is not { } value || !double.IsFinite(value)) return null;
        if (audio?.Peak is > 0 and var peak) value = Math.Min(value, -20 * Math.Log10(peak));
        return Math.Clamp(value, -30, 12);
    }

    /// <summary>mpv's option-list quoting: <c>%length%text</c> takes the text literally, commas and all.</summary>
    private static string Quote(string value) => $"%{System.Text.Encoding.UTF8.GetByteCount(value)}%{value}";

    private static List<QueueEntry> Shuffled(IEnumerable<QueueEntry> entries, QueueEntry first)
    {
        var rest = entries.Where(e => !ReferenceEquals(e, first)).ToArray();
        Random.Shared.Shuffle(rest);
        return [first, .. rest];
    }

    private void ApplyRepeat()
    {
        if (_player is not { } shared) return;
        shared.Player.PostProperty("loop-playlist", Repeat == RepeatMode.All ? "inf" : "no");
        shared.Player.PostProperty("loop-file", Repeat == RepeatMode.One ? "inf" : "no");
    }

    private void Apply(PlayerChange change)
    {
        switch (change.Name)
        {
            case "time-pos" when change.Number is { } seconds:
                Position = seconds;
                if (!IsScrubbing) SeekValue = seconds;
                UpdateClock();
                break;
            // The demo's synthesized songs report a length that grows as they play: the track's own stands.
            case "duration" when change.Number is { } seconds && seconds > 0 && !_session.IsDemo:
                Duration = seconds;
                break;
            case "pause" when change.Flag is { } paused:
                _pauseFlag = paused;
                if (!paused) IsHalted = false;
                UpdatePaused();
                break;
            case "mute" when change.Flag is { } muted:
                IsMuted = muted;
                break;
            case "playlist-pos" when change.Number is { } position:
                var index = (int)position;
                SetCurrent(index >= 0 && index < Queue.Count ? Queue[index] : null);
                break;
            case "idle-active" when change.Flag is { } idle:
                // mpv idles before the first file and after the last: either way nothing is playing.
                var ended = idle && !_idle;
                _idle = idle;
                UpdatePaused();
                if (ended) ReportState("stopped");
                break;
        }
    }

    private bool _pauseFlag;
    private bool _idle = true;

    /// <summary>Paused as the listener sees it: mpv paused, or with nothing to play.</summary>
    private void UpdatePaused()
    {
        var paused = _pauseFlag || _idle;
        if (paused == IsPaused) return;
        IsPaused = paused;
        UpdateClock();
        if (!_idle) ReportState(paused ? "paused" : "playing");
    }

    private void SetCurrent(QueueEntry? entry)
    {
        if (ReferenceEquals(entry, Current)) return;
        if (Current is { } previous) previous.IsCurrent = false;
        Current = entry;
        if (entry is not null) entry.IsCurrent = true;
        Position = 0;
        SeekValue = 0;
        Duration = (entry?.Track.Duration ?? 0) / 1000.0;
        _reported = string.Empty;
        EnsureReporter();
        UpdateClock();
        TrackChanged?.Invoke();
    }

    /// <summary>On mpv's event thread: which entry is now loaded, for marking it played at its end.</summary>
    private void OnFileLoaded(SharedPlayer shared)
    {
        if (!shared.Acquire()) return;
        try
        {
            var position = shared.Player.GetNumber("playlist-pos");
            Dispatcher.UIThread.Post(() =>
            {
                if (position is { } p && (int)p >= 0 && (int)p < Queue.Count) _loaded = Queue[(int)p];
                _reported = string.Empty;
                ReportState(IsPaused ? "paused" : "playing");
            });
        }
        finally
        {
            shared.Release();
        }
    }

    /// <summary>On mpv's event thread: a track ended. Played to its end, it is marked played.</summary>
    private void OnEnded(EndReason reason, string? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (reason == EndReason.Failed)
            {
                Log.Warn($"Music: a track could not be played: {error}");
                return;
            }

            if (reason != EndReason.Finished || _loaded is not { } finished || !_shell.ReportsPlayback) return;
            var ratingKey = finished.Track.RatingKey;
            var duration = finished.Track.Duration ?? 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _session.Client.ReportTimelineAsync(ratingKey, "stopped", duration, duration, CancellationToken.None);
                    await _session.Client.ScrobbleAsync(ratingKey, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
                {
                    Log.Warn("Music: the server could not be told a track was played.", ex);
                }
            });
        });
    }

    private void EnsureReporter()
    {
        if (_reporter is not null) return;
        _reporter = new DispatcherTimer { Interval = ReportEvery };
        _reporter.Tick += (_, _) =>
        {
            if (Current is not null && !IsPaused) ReportState("playing", force: true);
        };
        _reporter.Start();
    }

    private void ReportState(string state, bool force = false)
    {
        if (Current is not { } current || !_shell.ReportsPlayback || (!force && state == _reported)) return;
        _reported = state;
        var ratingKey = current.Track.RatingKey;
        var time = (long)(Position * 1000);
        var duration = (long)(Duration * 1000);
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.Client.ReportTimelineAsync(ratingKey, state, time, duration, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Warn($"Music: the server could not be told playback is {state}.", ex);
            }
        });
    }
}
