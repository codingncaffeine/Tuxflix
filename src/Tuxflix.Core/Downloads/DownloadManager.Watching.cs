using System.Net;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Downloads;

/// <summary>
/// Watching kept copies: where playback stopped and what was finished are kept here, and sent to
/// the server (a timeline report, then a scrobble) once it can be reached.
/// </summary>
/// <remarks>
/// Changes queue in the order they happened and are sent in that order, so a film finished and
/// then started again ends up watched and part-way through, as it would have online. A position
/// replaces the one before it for the same item, unless something else about the item came in
/// between. A write the server refuses as unknown (the item is gone) is dropped; one it cannot
/// take now (unreachable, signed out) waits for the next time. What counts as watched is the
/// player's call: played to the end, or stopped past 90%, the server's own default.
/// </remarks>
public sealed partial class DownloadManager
{
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>Watch changes not yet sent, in order, as copies.</summary>
    public IReadOnlyList<PendingWrite> PendingWrites()
    {
        lock (_gate) return [.. _pending.Select(p => new PendingWrite { ServerId = p.ServerId, RatingKey = p.RatingKey, Kind = p.Kind, Time = p.Time, Duration = p.Duration, At = p.At })];
    }

    /// <summary>
    /// Playback of a kept copy moved or stopped: the record keeps the place, and the server is to
    /// hear of it. <paramref name="send"/> sends it now if the server is open (a pause or a stop;
    /// the steady reports while playing only keep the place).
    /// </summary>
    /// <param name="time">Where playback is, in milliseconds.</param>
    /// <param name="duration">The length, in milliseconds.</param>
    /// <param name="finished">Watched: played to the end, or stopped close enough to it.</param>
    public void RecordPlayback(string serverId, string ratingKey, long time, long duration, bool send, bool finished)
    {
        var watched = finished;
        var now = Time.GetUtcNow().ToUnixTimeSeconds();
        DownloadRecord? copy = null;
        lock (_gate)
        {
            if (Get(DownloadRecord.Key(serverId, ratingKey)) is { } record)
            {
                if (watched)
                {
                    record.Watched = true;
                    record.WatchedAt ??= now;
                    record.ViewOffset = 0;
                }
                else
                {
                    record.ViewOffset = time;
                }

                copy = Snap(record);
            }

            // A newer position replaces the last one for this item, if nothing came after it.
            var last = _pending.FindLastIndex(p => p.ServerId == serverId && p.RatingKey == ratingKey);
            if (last >= 0 && _pending[last].Kind == PendingWrite.Timeline) _pending.RemoveAt(last);
            _pending.Add(new PendingWrite { ServerId = serverId, RatingKey = ratingKey, Kind = PendingWrite.Timeline, Time = watched ? duration : time, Duration = duration, At = now });
            if (watched) _pending.Add(new PendingWrite { ServerId = serverId, RatingKey = ratingKey, Kind = PendingWrite.Scrobble, Time = duration, Duration = duration, At = now });
        }

        if (copy is not null) Changed?.Invoke(copy);
        SaveSoon();
        if (!send || !IsAttached(serverId)) return;
        _ = Task.Run(async () =>
        {
            await SyncAsync(serverId, CancellationToken.None).ConfigureAwait(false);

            // One watched frees a place a rule may fill.
            if (watched) await ApplyRulesAsync(serverId, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Sends the watch changes kept for a server, oldest first, while it answers. Returns how many
    /// went; what is left waits for the next time the server is open.
    /// </summary>
    public async Task<int> SyncAsync(string serverId, CancellationToken cancellation)
    {
        var sent = 0;
        await _sending.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            while (true)
            {
                PendingWrite? next;
                PlexServerClient? client;
                lock (_gate)
                {
                    client = _servers.GetValueOrDefault(serverId);
                    next = _pending.FirstOrDefault(p => p.ServerId == serverId);
                }

                if (client is null || next is null) break;
                try
                {
                    if (next.Kind == PendingWrite.Scrobble)
                    {
                        await client.ScrobbleAsync(next.RatingKey, cancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.ReportTimelineAsync(next.RatingKey, "stopped", next.Time, next.Duration, cancellation).ConfigureAwait(false);
                    }

                    sent++;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    Log.Info($"{client.Name} no longer knows item {next.RatingKey}; its offline {next.Kind} is dropped.");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
                {
                    Log.Info($"Watch changes for {client.Name} wait for the next connection: {ex.Message}");
                    break;
                }

                lock (_gate) _pending.Remove(next);
                SaveSoon();
            }
        }
        finally
        {
            _sending.Release();
        }

        if (sent > 0) Log.Info($"Sent {sent} watch change(s) made offline.");
        return sent;
    }
}
