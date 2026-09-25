using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Music;

// Radio: a station of the library or of an artist, a track's sonic neighbours, or a sonic
// adventure between two tracks. A station never runs out: as the queue nears its end, more is
// asked for and added behind it.
public sealed partial class MusicPlayer
{
    private const int NeighboursPerStep = 12;
    private const double NeighbourDistance = 0.3;

    private Func<CancellationToken, Task<IReadOnlyList<MetadataItem>>>? _radio;
    private bool _radioHooked;
    private bool _extending;

    /// <summary>The station playing, when the queue is one: it goes on as long as it is listened to.</summary>
    [ObservableProperty]
    public partial string? RadioTitle { get; private set; }

    /// <summary>A library's or an artist's station, as the server makes it: a play queue that grows.</summary>
    public Task<bool> PlayStationAsync(MetadataItem station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (station.Key is not { Length: > 0 } key || _session.MachineIdentifier is not { Length: > 0 } machine) return Task.FromResult(false);
        return StartRadioAsync(station.Title, async cancellation =>
            (await _session.Client.CreatePlayQueueAsync(machine, key, cancellation).ConfigureAwait(false)).Metadata ?? []);
    }

    /// <summary>
    /// Radio from a track, by sound: the track, then its nearest neighbours, then theirs, never the
    /// same track twice. Needs the server's sonic analysis of the track.
    /// </summary>
    public Task<bool> PlayTrackRadioAsync(MetadataItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var played = new HashSet<string>(StringComparer.Ordinal);
        var from = track;
        var first = true;
        return StartRadioAsync($"{track.Title} Radio", async cancellation =>
        {
            var near = await _session.Client.GetSonicNeighboursAsync(from.RatingKey, NeighboursPerStep * 2, NeighbourDistance, cancellation).ConfigureAwait(false);
            var next = near.Where(t => t.Type == "track" && !played.Contains(t.RatingKey) && t.RatingKey != track.RatingKey).Take(NeighboursPerStep).ToList();
            if (first) next.Insert(0, track);
            first = false;
            foreach (var t in next) played.Add(t.RatingKey);
            if (next.Count > 0) from = next[^1];
            return next;
        });
    }

    /// <summary>A sonic adventure: the server's path in small steps of sound from one track to another.</summary>
    public async Task<bool> PlaySonicAdventureAsync(MetadataItem from, MetadataItem to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if ((from.LibrarySectionId ?? to.LibrarySectionId) is not { } section) return false;
        try
        {
            var path = await Task.Run(() => _session.Client.GetSonicPathAsync(section, from.RatingKey, to.RatingKey, CancellationToken.None));
            if (path.Count < 2)
            {
                Log.Info("Music: the server found no sonic path between the two tracks.");
                return false;
            }

            await PlayAsync(path);
            RadioTitle = $"From {from.Title} to {to.Title}";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("Music: the sonic adventure could not be made.", ex);
            return false;
        }
    }

    private async Task<bool> StartRadioAsync(string title, Func<CancellationToken, Task<IReadOnlyList<MetadataItem>>> more)
    {
        try
        {
            var first = await Task.Run(() => more(CancellationToken.None));
            if (first.Count == 0)
            {
                Log.Info($"Music: {title} has nothing to play.");
                return false;
            }

            await PlayAsync(first);
            _radio = more;
            RadioTitle = title;
            if (!_radioHooked)
            {
                _radioHooked = true;
                TrackChanged += OnRadioTrackChanged;
            }

            Log.Info($"Music: playing {title}.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn($"Music: {title} could not start.", ex);
            return false;
        }
    }

    /// <summary>Anything else played ends the station.</summary>
    private void EndRadio()
    {
        _radio = null;
        RadioTitle = null;
    }

    // Three tracks from the end of a station's queue: the next handful, behind what is there.
    private async void OnRadioTrackChanged()
    {
        if (_radio is not { } more || _extending || Current is not { } current) return;
        if (Queue.Count - Queue.IndexOf(current) > 3) return;
        _extending = true;
        try
        {
            var tracks = await Task.Run(() => more(CancellationToken.None));
            if (!ReferenceEquals(_radio, more)) return;
            var queued = Queue.Select(q => q.Track.RatingKey).ToHashSet(StringComparer.Ordinal);
            var fresh = tracks.Where(t => !queued.Contains(t.RatingKey)).ToList();
            if (fresh.Count > 0) await EnqueueAsync(fresh, next: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("Music: the station could not add more.", ex);
        }
        finally
        {
            _extending = false;
        }
    }
}
