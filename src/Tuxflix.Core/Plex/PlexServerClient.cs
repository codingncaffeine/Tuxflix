using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Tuxflix.Core.Plex;

/// <summary>The server refused the token: signed out, or access to this server was removed.</summary>
public sealed class PlexUnauthorizedException(string message) : Exception(message);

/// <summary>
/// Talks to one Plex Media Server over one connection.
/// </summary>
/// <remarks>
/// The token travels in a header, never in a URL this class builds, so it cannot end up in a log
/// line that prints a URL. Paths follow the documented defaults; every list request can page
/// with <c>X-Plex-Container-Start</c> and <c>X-Plex-Container-Size</c>.
/// </remarks>
public sealed class PlexServerClient
{
    private readonly HttpClient _http;

    public PlexServerClient(HttpClient http, Uri baseUri, string? token, string name, bool isDemo = false)
    {
        _http = http;
        BaseUri = baseUri;
        Token = token;
        Name = name;
        IsDemo = isDemo;
    }

    public Uri BaseUri { get; }

    public string? Token { get; }

    public string Name { get; }

    /// <summary>The built-in demo library rather than a real server.</summary>
    public bool IsDemo { get; }

    public async Task<MediaContainer> GetAsync(string pathAndQuery, CancellationToken cancellation, int? start = null, int? size = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(pathAndQuery));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        if (start is not null) request.Headers.TryAddWithoutValidation("X-Plex-Container-Start", start.Value.ToString(CultureInfo.InvariantCulture));
        if (size is not null) request.Headers.TryAddWithoutValidation("X-Plex-Container-Size", size.Value.ToString(CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException($"{Name} refused the sign-in for {Describe(pathAndQuery)}.");
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var envelope = await JsonSerializer.DeserializeAsync(stream, PlexJsonContext.Default.PlexEnvelope, cancellation).ConfigureAwait(false);
            return envelope?.MediaContainer ?? new MediaContainer();
        }
    }

    /// <summary>The address of a media part, for the player; the token travels in a header.</summary>
    public Uri MediaUri(string partKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partKey);
        return Resolve(partKey);
    }

    /// <summary>The headers a player sends with every request for media, token included.</summary>
    public IReadOnlyList<(string Name, string Value)> MediaHeaders(PlexClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var headers = new List<(string, string)>
        {
            ("X-Plex-Client-Identifier", identity.ClientIdentifier),
            ("X-Plex-Product", PlexClientIdentity.Product),
            ("X-Plex-Version", identity.Version),
        };
        if (Token is not null) headers.Add(("X-Plex-Token", Token));
        return headers;
    }

    /// <summary>
    /// Tells the server where playback is, so the place is kept and other clients show it.
    /// </summary>
    /// <param name="state">playing, paused, buffering or stopped.</param>
    public async Task ReportTimelineAsync(string ratingKey, string state, long time, long duration, CancellationToken cancellation)
    {
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/:/timeline?ratingKey={Uri.EscapeDataString(ratingKey)}&key={Uri.EscapeDataString("/library/metadata/" + ratingKey)}&state={state}&time={time}&duration={duration}&context=library");
        await SendAsync(HttpMethod.Post, query, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Remembers the viewer's audio or subtitle choice for a part, and the like streams in the
    /// item's other parts, so every Plex player starts with it next time.
    /// </summary>
    /// <param name="subtitleStreamId">The subtitle stream, or 0 for none.</param>
    public async Task SelectStreamsAsync(long partId, long? audioStreamId, long? subtitleStreamId, CancellationToken cancellation)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"/library/parts/{partId}?allParts=1");
        if (audioStreamId is { } audio) query += string.Create(CultureInfo.InvariantCulture, $"&audioStreamID={audio}");
        if (subtitleStreamId is { } subtitle) query += string.Create(CultureInfo.InvariantCulture, $"&subtitleStreamID={subtitle}");
        await SendAsync(HttpMethod.Put, query, cancellation).ConfigureAwait(false);
    }

    /// <summary>Marks an item watched.</summary>
    public async Task ScrobbleAsync(string ratingKey, CancellationToken cancellation) =>
        await SendAsync(HttpMethod.Put, $"/:/scrobble?identifier=com.plexapp.plugins.library&key={Uri.EscapeDataString(ratingKey)}", cancellation).ConfigureAwait(false);

    private async Task SendAsync(HttpMethod method, string pathAndQuery, CancellationToken cancellation)
    {
        if (IsDemo) return;
        using var request = new HttpRequestMessage(method, Resolve(pathAndQuery));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException($"{Name} refused the sign-in for {Describe(pathAndQuery)}.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The server's photo transcoder, scaling <paramref name="imagePath"/> to fit the size it will
    /// be drawn at. The path may be the server's own (<c>/library/metadata/…/thumb/…</c>) or an
    /// absolute URL, which the server fetches on the client's behalf.
    /// </summary>
    public Uri ImageUri(string imagePath, int width, int height, ImageFormat format = ImageFormat.Jpeg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var png = format == ImageFormat.Png ? "&format=png" : string.Empty;
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/photo/:/transcode?width={width}&height={height}&minSize=1&upscale=1{png}&url={Uri.EscapeDataString(imagePath)}");
        return Resolve(query);
    }

    /// <summary>
    /// The four corner colours the server extracts from an image, for an UltraBlur background.
    /// Null when the server cannot say (an older server, or an image it could not read).
    /// </summary>
    public async Task<UltraBlurColors?> GetUltraBlurColorsAsync(string imagePath, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        try
        {
            var container = await GetAsync($"/services/ultrablur/colors?url={Uri.EscapeDataString(imagePath)}", cancellation).ConfigureAwait(false);
            return container.UltraBlurColors?.FirstOrDefault();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LibraryDirectory>> GetSectionsAsync(CancellationToken cancellation) =>
        (await GetAsync("/library/sections", cancellation).ConfigureAwait(false)).Directory ?? [];

    /// <summary>The home screen's hubs: continue watching, recently added, and the rest.</summary>
    public async Task<IReadOnlyList<Hub>> GetHomeHubsAsync(CancellationToken cancellation) =>
        (await GetAsync("/hubs?count=20&includeLibraryPlaylists=0", cancellation).ConfigureAwait(false)).Hub ?? [];

    public async Task<IReadOnlyList<MetadataItem>> GetContinueWatchingAsync(CancellationToken cancellation)
    {
        var container = await GetAsync("/hubs/continueWatching", cancellation).ConfigureAwait(false);
        return container.Hub?.FirstOrDefault()?.Metadata ?? container.Metadata ?? [];
    }

    /// <summary>Every item of a section, lightly: the fields a listing shows and nothing heavier.</summary>
    public async Task<MediaContainer> GetSectionItemsAsync(string sectionKey, string sort, CancellationToken cancellation, int? start = null, int? size = null) =>
        await GetAsync(
            $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?sort={Uri.EscapeDataString(sort)}&excludeElements=Genre,Country,Director,Writer,Role,Media&excludeFields=summary,tagline",
            cancellation,
            start,
            size).ConfigureAwait(false);

    public async Task<MetadataItem?> GetMetadataAsync(string ratingKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}?includeExtras=1&includeMarkers=1&includeChapters=1", cancellation).ConfigureAwait(false))
        .Metadata?.FirstOrDefault();

    public async Task<IReadOnlyList<MetadataItem>> GetChildrenAsync(string ratingKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}/children", cancellation).ConfigureAwait(false)).Metadata ?? [];

    private Uri Resolve(string pathAndQuery) => new(BaseUri, pathAndQuery);

    // The path only: a query string can carry things that do not belong in a message.
    private static string Describe(string pathAndQuery) => pathAndQuery.Split('?')[0];
}
