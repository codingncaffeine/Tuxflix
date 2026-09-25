using System.ComponentModel;
using Tuxflix.App.Music;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Platform;

/// <summary>Covers for the desktop's media controls, as local files: a server address would carry its token.</summary>
internal sealed class CoverCache(string folder)
{
    /// <summary>The cover's file, fetched on a worker the first time; null when there is none.</summary>
    public async Task<string?> FileForAsync(ServerSession session, string? imagePath, string key)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        var file = Path.Combine(folder, string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')) + ".jpg");
        try
        {
            return await Task.Run(async () =>
            {
                if (File.Exists(file)) return file;
                Directory.CreateDirectory(folder);
                var bytes = await session.Client.GetBytesAsync(session.Client.ImageUri(imagePath, 512, 512), CancellationToken.None).ConfigureAwait(false);
                var partial = file + ".part";
                await File.WriteAllBytesAsync(partial, bytes).ConfigureAwait(false);
                File.Move(partial, file, overwrite: true);
                return file;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Log.Info($"Media controls: no cover for {key} ({ex.Message}).");
            return null;
        }
    }
}

/// <summary>The music queue, as the desktop sees it.</summary>
internal sealed class MusicSession : IMediaSession, IDisposable
{
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal)
    {
        nameof(MusicPlayer.IsPaused), nameof(MusicPlayer.Current), nameof(MusicPlayer.Duration), nameof(MusicPlayer.Volume),
        nameof(MusicPlayer.Repeat), nameof(MusicPlayer.IsShuffled), nameof(MusicPlayer.HasQueue), nameof(MusicPlayer.IsHalted),
    };

    private readonly MusicPlayer _music;
    private readonly ServerSession _session;
    private readonly CoverCache _covers;
    private string? _art;
    private string? _artFor;
    private double _lastPosition;
    private long _lastTaken = System.Diagnostics.Stopwatch.GetTimestamp();

    public MusicSession(MusicPlayer music, ServerSession session, CoverCache covers)
    {
        _music = music;
        _session = session;
        _covers = covers;
        _music.PropertyChanged += OnPropertyChanged;
        State = Take();
    }

    public MediaState State { get; private set; }

    public event Action? Changed;

    public event Action<double>? Seeked;

    public void Dispose() => _music.PropertyChanged -= OnPropertyChanged;

    public void Play() => _music.Resume();

    public void Pause() => _music.Pause();

    public void PlayPause() => _music.TogglePauseCommand.Execute(null);

    public void Stop() => _music.Halt();

    public void Next() => _music.NextCommand.Execute(null);

    public void Previous() => _music.PreviousCommand.Execute(null);

    public void SeekTo(double seconds) => _music.SeekTo(seconds);

    public void SetVolume(double volume) => _music.Volume = Math.Clamp(volume * 100, 0, 150);

    public void SetLoop(string loop) => _music.SetRepeat(loop switch
    {
        "Track" => RepeatMode.One,
        "Playlist" => RepeatMode.All,
        _ => RepeatMode.Off,
    });

    public void SetShuffle(bool shuffle) => _music.SetShuffle(shuffle);

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlayer.Position))
        {
            // Only a jump is news: the desktop runs the clock on from the last state.
            var expected = _lastPosition + (_music.IsPaused ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastTaken).TotalSeconds);
            var jumped = Math.Abs(_music.Position - expected) > 1.5;
            _lastPosition = _music.Position;
            _lastTaken = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!jumped) return;
            State = Take();
            Seeked?.Invoke(_music.Position);
            return;
        }

        if (e.PropertyName is null || !Watched.Contains(e.PropertyName)) return;
        State = Take();
        Changed?.Invoke();
        if (_music.Current?.Track is { } track && track.RatingKey != _artFor) _ = FetchCoverAsync(track);
    }

    private async Task FetchCoverAsync(MetadataItem track)
    {
        _artFor = track.RatingKey;
        var art = await _covers.FileForAsync(_session, track.ParentThumb ?? track.Thumb ?? track.GrandparentThumb, "album-" + (track.ParentRatingKey ?? track.RatingKey));
        if (_artFor != track.RatingKey) return;
        _art = art;
        State = Take();
        Changed?.Invoke();
    }

    private MediaState Take()
    {
        var current = _music.Current;
        var loaded = _music.HasQueue && current is not null;
        return new MediaState
        {
            HasMedia = loaded,
            Status = !loaded || _music.IsHalted ? "Stopped" : _music.IsPaused ? "Paused" : "Playing",
            TrackId = current?.Track.RatingKey,
            Title = current?.Title ?? string.Empty,
            Artists = string.IsNullOrEmpty(current?.Artist) ? [] : [current!.Artist],
            Album = current?.Album,
            ArtFile = current?.Track.RatingKey == _artFor ? _art : null,
            LengthSeconds = _music.Duration,
            PositionSeconds = _music.Position,
            Volume = _music.Volume / 100,
            CanGoNext = loaded,
            CanGoPrevious = loaded,
            CanSeek = loaded && _music.Duration > 0,
            LoopStatus = _music.Repeat switch
            {
                RepeatMode.One => "Track",
                RepeatMode.All => "Playlist",
                _ => "None",
            },
            Shuffle = _music.IsShuffled,
        };
    }
}

/// <summary>A film or an episode playing, as the desktop sees it.</summary>
internal sealed class VideoSession : IMediaSession, IDisposable
{
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal)
    {
        nameof(PlayerPageViewModel.IsPaused), nameof(PlayerPageViewModel.Duration), nameof(PlayerPageViewModel.Volume), nameof(PlayerPageViewModel.Player),
    };

    private readonly PlayerPageViewModel _page;
    private readonly ServerSession _session;
    private readonly CoverCache _covers;
    private string? _art;
    private double _lastPosition;

    public VideoSession(PlayerPageViewModel page, ServerSession session, CoverCache covers)
    {
        _page = page;
        _session = session;
        _covers = covers;
        _page.PropertyChanged += OnPropertyChanged;
        State = Take();
        _ = FetchCoverAsync();
    }

    public MediaState State { get; private set; }

    public PlayerPageViewModel Page => _page;

    public event Action? Changed;

    public event Action<double>? Seeked;

    public void Dispose() => _page.PropertyChanged -= OnPropertyChanged;

    public void Play()
    {
        if (_page.IsPaused) _page.TogglePauseCommand.Execute(null);
    }

    public void Pause()
    {
        if (!_page.IsPaused) _page.TogglePauseCommand.Execute(null);
    }

    public void PlayPause() => _page.TogglePauseCommand.Execute(null);

    public void Stop() => _page.LeaveCommand.Execute(null);

    public void Next()
    {
    }

    public void Previous() => _page.SeekTo(0);

    public void SeekTo(double seconds) => _page.SeekTo(seconds);

    public void SetVolume(double volume) => _page.Volume = Math.Clamp(volume * 100, 0, 150);

    public void SetLoop(string loop)
    {
    }

    public void SetShuffle(bool shuffle)
    {
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPageViewModel.Position))
        {
            var jumped = Math.Abs(_page.Position - _lastPosition) > 1.5;
            _lastPosition = _page.Position;
            if (!jumped) return;
            State = Take();
            Seeked?.Invoke(_page.Position);
            return;
        }

        if (e.PropertyName is null || !Watched.Contains(e.PropertyName)) return;
        State = Take();
        Changed?.Invoke();
    }

    private async Task FetchCoverAsync()
    {
        var item = _page.Item;
        _art = await _covers.FileForAsync(_session, item.Type == "episode" ? item.GrandparentThumb ?? item.Thumb : item.Thumb, "video-" + item.RatingKey);
        State = Take();
        Changed?.Invoke();
    }

    private MediaState Take()
    {
        var item = _page.Item;
        return new MediaState
        {
            HasMedia = true,
            Status = _page.Player is null ? "Stopped" : _page.IsPaused ? "Paused" : "Playing",
            TrackId = item.RatingKey,
            Title = item.Type == "episode" ? $"{Format.EpisodeCode(item)} · {item.Title}" : item.Title,
            Artists = item.Type == "episode" && item.GrandparentTitle is { } show ? [show] : [],
            Album = item.Type == "episode" ? item.ParentTitle : item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ArtFile = _art,
            LengthSeconds = _page.Duration,
            PositionSeconds = _page.Position,
            Volume = _page.Volume / 100,
            CanGoNext = false,
            CanGoPrevious = true,
            CanSeek = _page.Duration > 0,
        };
    }
}
