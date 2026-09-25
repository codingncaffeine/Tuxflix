using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Tuxflix.Core.Plex;

// The viewer's own state on a server: watched marks, ratings, playlists, stream choices, and what
// they have been watching. Unlike the player's reports these writes reach the demo library too,
// which keeps them in memory, so the demo behaves as a server does.
public sealed partial class PlexServerClient
{
    private const string Library = "com.plexapp.plugins.library";

    /// <summary>Marks an item watched; a show or a season marks every episode in it.</summary>
    public Task MarkWatchedAsync(string ratingKey, CancellationToken cancellation) =>
        WriteAsync(HttpMethod.Put, $"/:/scrobble?identifier={Library}&key={Uri.EscapeDataString(ratingKey)}", cancellation);

    /// <summary>Marks an item unwatched, forgetting where it was left; a show or a season does every episode.</summary>
    public Task MarkUnwatchedAsync(string ratingKey, CancellationToken cancellation) =>
        WriteAsync(HttpMethod.Put, $"/:/unscrobble?identifier={Library}&key={Uri.EscapeDataString(ratingKey)}", cancellation);

    /// <summary>Rates an item from 0 to 10 (five stars, in halves); null takes the rating away.</summary>
    public Task RateAsync(string ratingKey, double? rating, CancellationToken cancellation)
    {
        var value = rating is { } given ? Math.Clamp(Math.Round(given, MidpointRounding.AwayFromZero), 0, 10) : -1;
        return WriteAsync(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"/:/rate?identifier={Library}&key={Uri.EscapeDataString(ratingKey)}&rating={value}"), cancellation);
    }

    /// <summary>
    /// Remembers an audio or subtitle choice for an item before it plays, and for its other parts,
    /// as the player does for a choice made while playing.
    /// </summary>
    /// <param name="subtitleStreamId">The subtitle stream, or 0 for none.</param>
    public Task ChooseStreamsAsync(long partId, long? audioStreamId, long? subtitleStreamId, CancellationToken cancellation)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"/library/parts/{partId}?allParts=1");
        if (audioStreamId is { } audio) query += string.Create(CultureInfo.InvariantCulture, $"&audioStreamID={audio}");
        if (subtitleStreamId is { } subtitle) query += string.Create(CultureInfo.InvariantCulture, $"&subtitleStreamID={subtitle}");
        return WriteAsync(HttpMethod.Put, query, cancellation);
    }

    /// <summary>The address a playlist names its items by: <c>server://{machine}/com.plexapp.plugins.library/library/metadata/{keys}</c>.</summary>
    public static string ItemsUri(string machineIdentifier, IEnumerable<string> ratingKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentifier);
        ArgumentNullException.ThrowIfNull(ratingKeys);
        return $"server://{machineIdentifier}/{Library}/library/metadata/{string.Join(',', ratingKeys)}";
    }

    /// <summary>Makes a playlist of the given items, <c>video</c> or <c>audio</c>, and returns it as the server made it.</summary>
    public async Task<MetadataItem?> CreatePlaylistAsync(string title, string playlistType, string machineIdentifier, IReadOnlyList<string> ratingKeys, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistType);
        var uri = ItemsUri(machineIdentifier, ratingKeys);
        var container = await WriteAsync(
            HttpMethod.Post,
            $"/playlists?type={Uri.EscapeDataString(playlistType)}&title={Uri.EscapeDataString(title)}&smart=0&uri={Uri.EscapeDataString(uri)}",
            cancellation).ConfigureAwait(false);
        return container?.Metadata?.FirstOrDefault();
    }

    /// <summary>Adds items to the end of a playlist.</summary>
    public Task AddToPlaylistAsync(string playlistKey, string machineIdentifier, IReadOnlyList<string> ratingKeys, CancellationToken cancellation) =>
        WriteAsync(HttpMethod.Put, $"/playlists/{Uri.EscapeDataString(playlistKey)}/items?uri={Uri.EscapeDataString(ItemsUri(machineIdentifier, ratingKeys))}", cancellation);

    /// <summary>Takes one entry out of a playlist, by its place in the playlist (an item can be in it twice).</summary>
    public Task RemoveFromPlaylistAsync(string playlistKey, long playlistItemId, CancellationToken cancellation) =>
        WriteAsync(HttpMethod.Delete, string.Create(CultureInfo.InvariantCulture, $"/playlists/{Uri.EscapeDataString(playlistKey)}/items/{playlistItemId}"), cancellation);

    /// <summary>Moves an entry to just after another; with none, to the top.</summary>
    public Task MovePlaylistItemAsync(string playlistKey, long playlistItemId, long? afterPlaylistItemId, CancellationToken cancellation)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/playlists/{Uri.EscapeDataString(playlistKey)}/items/{playlistItemId}/move");
        if (afterPlaylistItemId is { } after) path += string.Create(CultureInfo.InvariantCulture, $"?after={after}");
        return WriteAsync(HttpMethod.Put, path, cancellation);
    }

    public Task RenamePlaylistAsync(string playlistKey, string title, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return WriteAsync(HttpMethod.Put, $"/playlists/{Uri.EscapeDataString(playlistKey)}?title={Uri.EscapeDataString(title)}", cancellation);
    }

    public Task DeletePlaylistAsync(string playlistKey, CancellationToken cancellation) =>
        WriteAsync(HttpMethod.Delete, $"/playlists/{Uri.EscapeDataString(playlistKey)}", cancellation);

    /// <summary>What is playing on the server now, on every device it may show this viewer (for the owner, everyone's).</summary>
    public async Task<IReadOnlyList<MetadataItem>> GetSessionsAsync(CancellationToken cancellation) =>
        (await GetAsync("/status/sessions", cancellation).ConfigureAwait(false)).Metadata ?? [];

    /// <summary>
    /// One page of watch history, newest first. The server shows a viewer who does not own it only
    /// their own; <paramref name="accountId"/> narrows the owner's to theirs (the owner is account 1).
    /// </summary>
    /// <param name="since">Only what was watched from this moment on, when given.</param>
    public Task<MediaContainer> GetHistoryAsync(long? accountId, DateTimeOffset? since, int start, int size, CancellationToken cancellation)
    {
        var query = "/status/sessions/history/all?sort=viewedAt:desc";
        if (accountId is { } account) query += string.Create(CultureInfo.InvariantCulture, $"&accountID={account}");
        if (since is { } from) query += string.Create(CultureInfo.InvariantCulture, $"&viewedAt%3E={from.ToUnixTimeSeconds()}");
        return GetAsync(query, cancellation, start, size);
    }

    /// <summary>Records for many items trimmed to what a listing shows (titles, lengths), a hundred to a request.</summary>
    public async Task<IReadOnlyList<MetadataItem>> GetLightAsync(IEnumerable<string> ratingKeys, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(ratingKeys);
        var found = new List<MetadataItem>();
        foreach (var chunk in ratingKeys.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            var path = $"/library/metadata/{string.Join(',', chunk.Select(Uri.EscapeDataString))}?excludeElements=Genre,Country,Director,Writer,Role,Media,Image,Chapter,Marker&excludeFields=summary,tagline";
            found.AddRange((await GetAsync(path, cancellation).ConfigureAwait(false)).Metadata ?? []);
        }

        return found;
    }

    /// <summary>
    /// Sends a change to the server, the demo library included, and reads back whatever container
    /// it answers with (a new playlist; most changes answer with nothing).
    /// </summary>
    private async Task<MediaContainer?> WriteAsync(HttpMethod method, string pathAndQuery, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, Resolve(pathAndQuery));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException($"{Name} refused the sign-in for {Describe(pathAndQuery)}.");
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        if (body.Length == 0 || body[0] != (byte)'{') return null;
        try
        {
            return JsonSerializer.Deserialize(body, PlexJsonContext.Default.PlexEnvelope)?.MediaContainer;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
