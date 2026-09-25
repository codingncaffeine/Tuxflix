using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Tuxflix.Core.Plex;

/// <summary>
/// Plex's own catalogue on plex.tv (Discover), for the signed-in account's Watchlist: the list of
/// films and series to watch that follows the viewer across every Plex app.
/// </summary>
/// <remarks>
/// Titles here are Plex's, not a server's: a title's rating key is the last part of its
/// <c>plex://movie/…</c> or <c>plex://show/…</c> guid, and a server finds its own copy by that guid.
/// The account token travels in a header, as for every plex.tv call; the client's own
/// <c>X-Plex-*</c> headers come with the <see cref="HttpClient"/>. Paging goes in the query, as
/// Plex's apps send it here.
/// </remarks>
public sealed class PlexDiscoverClient(HttpClient http, string token, Uri? baseUri = null)
{
    /// <summary>Plex's Discover provider.</summary>
    public static readonly Uri DefaultBaseUri = new("https://discover.provider.plex.tv/");

    /// <summary>Titles asked for at a time: Discover refuses pages much larger.</summary>
    public const int PageSize = 100;

    private readonly Uri _base = baseUri ?? DefaultBaseUri;

    /// <summary>
    /// The Watchlist, most recently added first, up to <paramref name="limit"/> titles.
    /// </summary>
    public async Task<IReadOnlyList<MetadataItem>> GetWatchlistAsync(CancellationToken cancellation, int limit = int.MaxValue)
    {
        var items = new List<MetadataItem>();
        while (items.Count < limit)
        {
            var size = Math.Min(PageSize, limit - items.Count);
            var page = await GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"library/sections/watchlist/all?includeGuids=1&sort=watchlistedAt%3Adesc&X-Plex-Container-Start={items.Count}&X-Plex-Container-Size={size}"),
                cancellation).ConfigureAwait(false);
            var metadata = page.Metadata ?? [];
            items.AddRange(metadata);
            if (metadata.Count == 0 || items.Count >= (page.TotalSize ?? page.Size)) break;
        }

        return items;
    }

    /// <summary>Whether a title of the catalogue is on the Watchlist.</summary>
    public async Task<bool> IsOnWatchlistAsync(string ratingKey, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ratingKey);
        var state = await GetAsync($"library/metadata/{Uri.EscapeDataString(ratingKey)}/userState", cancellation).ConfigureAwait(false);
        return state.UserState?.FirstOrDefault()?.WatchlistedAt is not null;
    }

    /// <summary>Puts a title of the catalogue on the Watchlist.</summary>
    public Task AddAsync(string ratingKey, CancellationToken cancellation) => ActAsync("addToWatchlist", ratingKey, cancellation);

    /// <summary>Takes a title of the catalogue off the Watchlist.</summary>
    public Task RemoveAsync(string ratingKey, CancellationToken cancellation) => ActAsync("removeFromWatchlist", ratingKey, cancellation);

    /// <summary>
    /// A title's rating key in Plex's catalogue, from its guid (<c>plex://movie/5d77…</c> gives
    /// <c>5d77…</c>); null for a title the catalogue does not know, such as a home video.
    /// </summary>
    public static string? CatalogKey(string? guid)
    {
        if (guid is null || !guid.StartsWith("plex://", StringComparison.Ordinal)) return null;
        var parts = guid["plex://".Length..].Split('/');
        return parts is [_, { Length: > 0 } key] ? key : null;
    }

    private async Task ActAsync(string action, string ratingKey, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ratingKey);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_base, $"actions/{action}?ratingKey={Uri.EscapeDataString(ratingKey)}"));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        Check(response);
    }

    private async Task<MediaContainer> GetAsync(string pathAndQuery, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, pathAndQuery));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        Check(response);
        var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var envelope = await JsonSerializer.DeserializeAsync(stream, PlexJsonContext.Default.PlexEnvelope, cancellation).ConfigureAwait(false);
            return envelope?.MediaContainer ?? new MediaContainer();
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException("plex.tv no longer accepts this sign-in.");
        }

        response.EnsureSuccessStatusCode();
    }
}
