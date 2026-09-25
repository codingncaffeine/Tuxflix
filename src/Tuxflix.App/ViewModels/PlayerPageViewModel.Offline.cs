using Tuxflix.Core.Downloads;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Playing a kept copy: mpv reads the file on disk, with or without the server. Where playback
/// stops and whether it was finished are kept with the download and sent to the server (a timeline
/// report, then a scrobble) when it is in reach, now or later.
/// </summary>
public sealed partial class PlayerPageViewModel
{
    /// <summary>The download being played, or null when the server streams the item.</summary>
    public DownloadRecord? Download { get; init; }

    /// <summary>What mpv was given: the file of a download, or the server's address for the item.</summary>
    internal string? Source => _source;

    /// <summary>
    /// Keeps the place with the download, from a worker. A pause or a stop is sent to the server at
    /// once if it is open; the steady reports while playing only keep the place.
    /// </summary>
    private void RecordDownload(string state, bool finished)
    {
        if (Download is not { } kept) return;
        var time = (long)(Position * 1000);
        var duration = kept.Duration > 0 ? kept.Duration : (long)(Duration * 1000);

        // Stopped past 90% counts as watched, as the server itself counts it.
        finished |= state == "stopped" && duration > 0 && time >= duration * 0.9;
        var manager = shell.Downloads.Manager;
        _ = Task.Run(() => manager.RecordPlayback(kept.ServerId, kept.RatingKey, time, duration, send: state != "playing", finished));
    }
}
