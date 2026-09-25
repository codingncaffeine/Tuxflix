using System.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// What a player looks like to the desktop at one moment: made on the UI thread whenever something
/// changes, read from any thread. The position runs on from where it was taken while playing.
/// </summary>
internal sealed record MediaState
{
    public static MediaState Nothing { get; } = new();

    /// <summary><c>Playing</c>, <c>Paused</c> or <c>Stopped</c>, as MPRIS spells them.</summary>
    public string Status { get; init; } = "Stopped";

    /// <summary>Something is loaded: the desktop is shown a player at all.</summary>
    public bool HasMedia { get; init; }

    public string? TrackId { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Artists { get; init; } = [];

    public string? Album { get; init; }

    /// <summary>A local file with the cover: a server's image address would carry its token into the desktop.</summary>
    public string? ArtFile { get; init; }

    public double LengthSeconds { get; init; }

    public double PositionSeconds { get; init; }

    public long PositionTaken { get; init; } = Stopwatch.GetTimestamp();

    /// <summary>1 is full volume; a player may go over.</summary>
    public double Volume { get; init; } = 1;

    public bool CanGoNext { get; init; }

    public bool CanGoPrevious { get; init; }

    public bool CanSeek { get; init; }

    /// <summary><c>None</c>, <c>Track</c> or <c>Playlist</c>; null when the player has no repeat.</summary>
    public string? LoopStatus { get; init; }

    /// <summary>Null when the player has no shuffle.</summary>
    public bool? Shuffle { get; init; }

    public double PositionNow => Status == "Playing"
        ? PositionSeconds + Stopwatch.GetElapsedTime(PositionTaken).TotalSeconds
        : PositionSeconds;
}

/// <summary>A player the desktop can see and drive: the music queue, or a film playing.</summary>
internal interface IMediaSession
{
    /// <summary>The latest state; readable from any thread.</summary>
    MediaState State { get; }

    /// <summary>The state changed; raised on the UI thread.</summary>
    event Action? Changed;

    /// <summary>The position jumped (a seek), in seconds; raised on the UI thread.</summary>
    event Action<double>? Seeked;

    // The commands run on the UI thread.
    void Play();

    void Pause();

    void PlayPause();

    void Stop();

    void Next();

    void Previous();

    void SeekTo(double seconds);

    void SetVolume(double volume);

    void SetLoop(string loop);

    void SetShuffle(bool shuffle);
}
