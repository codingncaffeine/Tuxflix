using System.Collections.Concurrent;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The account's Watchlist as one server sees it: the titles, and each one's copy on the server.
/// </summary>
/// <remarks>
/// A Watchlist title is Plex's, not the server's: its copy is found by its guid, once, and
/// remembered for the session, a few lookups at a time so a long list does not flood the server.
/// Everything here runs on workers.
/// </remarks>
public sealed class WatchlistService(ServerSession session, PlexDiscoverClient discover)
{
    private readonly ConcurrentDictionary<string, Task<MetadataItem?>> _copies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _known = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lookups = new(4);

    public ServerSession Session => session;

    public PlexDiscoverClient Discover => discover;

    /// <summary>The Watchlist, most recently added first, up to <paramref name="limit"/> titles.</summary>
    public Task<IReadOnlyList<MetadataItem>> GetAsync(CancellationToken cancellation, int limit = int.MaxValue) =>
        Task.Run(() => discover.GetWatchlistAsync(cancellation, limit), cancellation);

    /// <summary>The server's copy of a Watchlist title, or null when the server does not have it.</summary>
    public Task<MetadataItem?> FindCopyAsync(MetadataItem title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return title.Guid is { } guid ? _copies.GetOrAdd(guid, g => Task.Run(() => LookUpAsync(g))) : Task.FromResult<MetadataItem?>(null);
    }

    /// <summary>Whether a title of the catalogue is on the Watchlist.</summary>
    public Task<bool> ContainsAsync(string catalogKey, CancellationToken cancellation) =>
        Task.Run(async () => _known[catalogKey] = await discover.IsOnWatchlistAsync(catalogKey, cancellation), cancellation);

    /// <summary>Puts a title of the catalogue on the Watchlist, or takes it off.</summary>
    public Task SetAsync(string catalogKey, bool on) =>
        Task.Run(async () =>
        {
            if (on) await discover.AddAsync(catalogKey, CancellationToken.None);
            else await discover.RemoveAsync(catalogKey, CancellationToken.None);
            _known[catalogKey] = on;
        });

    /// <summary>Whether a title was on the Watchlist when last asked or changed here; null when it never was.</summary>
    public bool? Known(string catalogKey) => _known.TryGetValue(catalogKey, out var on) ? on : null;

    private async Task<MetadataItem?> LookUpAsync(string guid)
    {
        await _lookups.WaitAsync().ConfigureAwait(false);
        try
        {
            return await session.Client.FindByGuidAsync(guid, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Not remembered: the next look asks again.
            _copies.TryRemove(guid, out _);
            throw;
        }
        finally
        {
            _lookups.Release();
        }
    }
}
