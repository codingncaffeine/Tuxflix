using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// How the picture reaches the player: the original file, or a conversion the server makes at a
/// chosen quality. The server is asked before every play, so its own limits (a remote viewer's
/// bitrate) hold too.
/// </summary>
/// <remarks>
/// A conversion carries one audio stream and at most one subtitle, burned into the picture. While
/// converting, the track menus therefore list the file's own streams, and a choice asks for a new
/// conversion at the same place; a subtitle file kept beside the media still loads beside the
/// stream. The server burns the subtitle its saved selection names, so that choice is saved first.
/// A paused conversion is kept alive. Each ask gets a session of its own: asking again under the
/// same one ends the running conversion at once, cutting mpv's read mid-segment, so the one
/// replaced is stopped only after the next stream has loaded; leaving the player stops it at once.
/// </remarks>
public sealed partial class PlayerPageViewModel
{
    private readonly string _sessionIdentifier = Guid.NewGuid().ToString("N");
    private TranscodeRequest? _conversion;

    /// <summary>A replaced conversion's session, stopped once the stream that replaces it has loaded.</summary>
    private string? _retiring;

    /// <summary>The audio stream chosen during this playback; null keeps the part's saved one.</summary>
    private long? _audioStreamId;

    /// <summary>The subtitle stream chosen during this playback, 0 for none; null keeps the part's saved one.</summary>
    private long? _subtitleStreamId;

    /// <summary>The quality asked for: the original file, or a conversion to a bitrate and size.</summary>
    [ObservableProperty]
    public partial StreamQuality Quality { get; private set; } = StreamQuality.Original;

    /// <summary>What arrives, in words, for the quality menu: the original file, or the server's conversion.</summary>
    [ObservableProperty]
    public partial string StreamSummary { get; private set; } = string.Empty;

    /// <summary>The same in figures: "H.264 1280×720 · AAC · 3.5 Mbps".</summary>
    [ObservableProperty]
    public partial string StreamDetail { get; private set; } = string.Empty;

    /// <summary>The server converts what plays, by its choice or the viewer's.</summary>
    public bool IsConverting => _conversion is not null;

    /// <summary>The running conversion's session, as the server's list of conversions names it.</summary>
    internal string? TranscodeSession => _conversion?.Session;

    /// <summary>The qualities worth offering: the original, and the conversions that would make it smaller.</summary>
    public IReadOnlyList<StreamQuality> Qualities =>
        [.. StreamQuality.All.Where(q => q.IsOriginal || !q.Covers(_item.Media?.FirstOrDefault()?.Bitrate, _item.Media?.FirstOrDefault()?.Height))];

    /// <summary>Plays at <paramref name="quality"/> from where playback is now.</summary>
    public async void SetQuality(StreamQuality quality)
    {
        if (quality == Quality || session.IsDemo || Download is not null) return;
        Log.Info($"Quality: {quality.Label}.");
        Quality = quality;
        await RestartAsync();
    }

    /// <summary>
    /// Asks the server how to play at <see cref="Quality"/>, from a worker, and gives the address
    /// for mpv. A refusal, or no answer, falls back to the original file.
    /// </summary>
    private async Task<string> RouteAsync(MediaPart part, CancellationToken cancellation)
    {
        var burn = EmbeddedSubtitle() is not null;
        var request = new TranscodeRequest(_item.RatingKey, Quality, Guid.NewGuid().ToString("N"), _sessionIdentifier)
        {
            Remote = session.IsRemote,
            AudioStreamId = _audioStreamId,

            // Burning in rules out direct play, so the original is asked for without it.
            BurnSubtitles = burn && !Quality.IsOriginal,
        };
        string? refusal = null;
        try
        {
            var identity = shell.Identity;
            var (decision, asked) = await Task.Run(() => session.Client.DecideAsync(request, identity, cancellation), cancellation);

            // The server converts the original after all (a remote viewer's limit): the subtitle has to be burned in.
            if (decision.Route == PlaybackRoute.Convert && burn && !asked.BurnSubtitles)
            {
                (decision, asked) = await Task.Run(() => session.Client.DecideAsync(asked with { BurnSubtitles = true }, identity, cancellation), cancellation);
            }

            if (decision.Route == PlaybackRoute.Convert)
            {
                _conversion = asked;
                OnPropertyChanged(nameof(IsConverting));
                StreamSummary = "Converted by the server";
                StreamDetail = decision.Describe();
                Log.Info($"The server converts it ({decision.Reason}): {decision.Describe()}{(asked.TransportStream ? ", in MPEG-TS" : string.Empty)}{(asked.BurnSubtitles ? ", subtitles burned in" : string.Empty)}.");
                return session.Client.TranscodeAddress(asked, identity);
            }

            if (decision.Route == PlaybackRoute.Refused) refusal = decision.Reason;
            else Log.Info($"The server plays the original file ({decision.Reason}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException or System.Text.Json.JsonException)
        {
            refusal = "it did not answer";
            Log.Warn("The server could not be asked how to play it.", ex);
        }

        if (refusal is not null) Log.Warn($"Playing the original file: the server would not convert it ({refusal}).");
        _conversion = null;
        OnPropertyChanged(nameof(IsConverting));
        StreamSummary = refusal is null || Quality.IsOriginal ? "The original file" : "The original file: the server could not convert it";
        StreamDetail = refusal is not null && !Quality.IsOriginal ? refusal : _item.Media?.FirstOrDefault() is { } original ? CodecNames.Describe(original) : string.Empty;
        return session.Client.MediaUri(part.Key!)?.ToString()
               ?? throw new HttpRequestException("The server named a file away from itself; it is not played.");
    }

    /// <summary>Asks again for the current quality and tracks, and carries on from the same place.</summary>
    private async Task RestartAsync()
    {
        if (_part is not { } part || Player is not { } shared) return;
        var at = Position;
        var was = _conversion;
        var source = await RouteAsync(part, CancellationToken.None);
        if (!ReferenceEquals(shared, Player))
        {
            if (_conversion is { } orphan) StopConversion(orphan.Session);
            return;
        }

        if (was is not null) Interlocked.Exchange(ref _retiring, was.Session);
        _source = source;
        _start = at;
        shared.Player.Load(source, at);
    }

    /// <summary>The subtitle inside the file to show, or null for none or for a file kept beside the media.</summary>
    private MediaStream? EmbeddedSubtitle()
    {
        if (_part is not { } part) return null;
        var chosen = _subtitleStreamId switch
        {
            null => StreamChoice.Selected(part, StreamChoice.Subtitle),
            0 => null,
            { } id => part.Stream?.FirstOrDefault(s => s.Id == id),
        };
        return chosen is { IsExternal: false } ? chosen : null;
    }

    /// <summary>The subtitle file beside the media to load with the stream, or null.</summary>
    private MediaStream? ExternalSubtitle()
    {
        if (_part is not { } part) return null;
        var chosen = _subtitleStreamId switch
        {
            null => StreamChoice.Selected(part, StreamChoice.Subtitle),
            0 => null,
            { } id => part.Stream?.FirstOrDefault(s => s.Id == id),
        };
        return chosen is { IsExternal: true } ? chosen : null;
    }

    /// <summary>While converting, the menus list the file's own streams: the conversion carries only the chosen ones.</summary>
    private void ShowConvertedTracks(IReadOnlyList<PlayerTrack> tracks, string? subtitles)
    {
        if (_part is not { } part) return;
        var streams = part.Stream ?? [];
        var audioId = _audioStreamId ?? StreamChoice.Selected(part, StreamChoice.Audio)?.Id;
        var burned = EmbeddedSubtitle();
        var beside = tracks.Where(t => t.Id == subtitles && t.Source is not null).Select(t => t.Source).FirstOrDefault();
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        foreach (var audio in streams.Where(s => s.StreamType == StreamChoice.Audio))
        {
            AudioTracks.Add(new TrackOption(this, "aid", string.Empty, audio.ExtendedDisplayTitle ?? audio.DisplayTitle ?? "Audio", audio.Id == audioId, audio));
        }

        SubtitleTracks.Add(new TrackOption(this, "sid", "no", "Off", burned is null && beside is null, stream: null));
        foreach (var subtitle in streams.Where(s => s.StreamType == StreamChoice.Subtitle))
        {
            var on = subtitle.IsExternal ? beside == SourceOf(subtitle) : subtitle.Id == burned?.Id;
            SubtitleTracks.Add(new TrackOption(this, "sid", string.Empty, subtitle.ExtendedDisplayTitle ?? subtitle.DisplayTitle ?? "Subtitles", on, subtitle));
        }
    }

    /// <summary>While converting: an audio or subtitle choice asks for a new conversion at the same place.</summary>
    private async Task SelectConvertedAsync(TrackOption option)
    {
        foreach (var other in option.Property == "aid" ? AudioTracks : SubtitleTracks) other.IsSelected = ReferenceEquals(other, option);
        if (option.Property == "aid")
        {
            _audioStreamId = option.Stream?.Id;
            Remember(option);
            await RestartAsync();
            return;
        }

        var burning = EmbeddedSubtitle() is not null;
        _subtitleStreamId = option.Stream?.Id ?? 0;
        if (option.Stream is { IsExternal: true } external)
        {
            Remember(option);

            // Beside the stream it just loads; one burned into the picture has to go first.
            if (burning) await RestartAsync();
            else if (Player is { } shared) LoadSubtitle(shared.Player, external);
            return;
        }

        if (option.Stream is { } embedded && !await SaveChoiceAsync(embedded))
        {
            Log.Warn("The subtitle cannot be burned in: the server burns the one it has saved, and the choice could not be saved.");
            return;
        }

        if (option.Stream is null) Remember(option);
        await RestartAsync();
    }

    /// <summary>Saves a subtitle choice and waits for it: the server burns in the one it has saved.</summary>
    private async Task<bool> SaveChoiceAsync(MediaStream subtitle)
    {
        if (!shell.ReportsPlayback || _part is not { Id: > 0 } part) return false;
        try
        {
            var partId = part.Id;
            await Task.Run(() => session.Client.SelectStreamsAsync(partId, null, subtitle.Id, CancellationToken.None));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The server could not be told which subtitle was chosen.", ex);
            return false;
        }
    }

    /// <summary>A paused conversion: the server stops a transcoder nobody reads from.</summary>
    private void KeepConversionAlive()
    {
        if (!IsPaused || _conversion is not { Session: var key }) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.PingTranscodeAsync(key, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Info($"The paused conversion could not be kept alive ({ex.Message}).");
            }
        });
    }

    /// <summary>The stream that replaced a conversion has loaded: mpv reads it no more, so the server may stop it.</summary>
    private void RetireReplacedConversion()
    {
        if (Interlocked.Exchange(ref _retiring, null) is { } key) StopConversion(key);
    }

    /// <summary>Ends one of this player's conversions on the server, from a worker.</summary>
    private void StopConversion(string key)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Client.StopTranscodeAsync(key, CancellationToken.None);
                Log.Info("The server's conversion was stopped.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log.Info("The server had already ended the conversion.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
            {
                Log.Info($"The server's conversion could not be stopped ({ex.Message}); it ends on its own.");
            }
        });
    }
}
