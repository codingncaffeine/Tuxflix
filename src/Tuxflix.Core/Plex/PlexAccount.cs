using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

/// <summary>A sign-in PIN: shown to plex.tv in the browser, then polled until it carries a token.</summary>
public sealed class PlexPin
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("authToken")]
    public string? AuthToken { get; init; }

    [JsonPropertyName("expiresIn")]
    public int? ExpiresIn { get; init; }

    /// <summary>An image of a QR code for the sign-in, for a phone.</summary>
    [JsonPropertyName("qr")]
    public string? Qr { get; init; }
}

/// <summary>The signed-in Plex account.</summary>
public sealed class PlexUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("friendlyName")]
    public string? FriendlyName { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }

    [JsonPropertyName("home")]
    public bool Home { get; init; }

    /// <summary>The name to show: the friendly name if set, else the title, else the username.</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName
        : !string.IsNullOrWhiteSpace(Title) ? Title
        : Username ?? "Plex user";
}

/// <summary>A device on the account: for Tuxflix, a Plex Media Server and how to reach it.</summary>
public sealed class PlexResource
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("productVersion")]
    public string? ProductVersion { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("clientIdentifier")]
    public string ClientIdentifier { get; init; } = string.Empty;

    [JsonPropertyName("provides")]
    public string? Provides { get; init; }

    [JsonPropertyName("owned")]
    public bool Owned { get; init; }

    /// <summary>For a server shared with this account, whose it is.</summary>
    [JsonPropertyName("sourceTitle")]
    public string? SourceTitle { get; init; }

    /// <summary>The token this account uses on this server; not the account token.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("presence")]
    public bool Presence { get; init; }

    [JsonPropertyName("httpsRequired")]
    public bool HttpsRequired { get; init; }

    [JsonPropertyName("dnsRebindingProtection")]
    public bool DnsRebindingProtection { get; init; }

    [JsonPropertyName("connections")]
    public List<PlexConnection>? Connections { get; init; }

    [JsonIgnore]
    public bool IsServer => Provides?.Split(',').Contains("server", StringComparer.Ordinal) == true;
}

public sealed class PlexConnection
{
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    [JsonPropertyName("local")]
    public bool Local { get; init; }

    [JsonPropertyName("relay")]
    public bool Relay { get; init; }

    [JsonPropertyName("IPv6")]
    public bool IPv6 { get; init; }
}

/// <summary>
/// The account side of Plex: signing in with a PIN, who is signed in, and which servers the
/// account can reach. Everything here talks to plex.tv, never to a server.
/// </summary>
/// <remarks>
/// The legacy PIN flow: create a strong PIN, send the browser to Plex's own sign-in page with it,
/// and poll the PIN until plex.tv attaches a token. Tuxflix never sees the password.
/// </remarks>
public sealed class PlexAccountClient(HttpClient http)
{
    private static readonly Uri Api = new("https://clients.plex.tv/api/v2/");

    public Task<PlexPin> CreatePinAsync(CancellationToken cancellation) => CreatePinAsync(strong: true, cancellation);

    /// <summary>
    /// A new PIN: a strong one for Plex's sign-in page, or (<paramref name="strong"/> false) a
    /// four-character code the viewer types at plex.tv/link on a phone, as Plex's TV apps ask.
    /// </summary>
    public async Task<PlexPin> CreatePinAsync(bool strong, CancellationToken cancellation)
    {
        using var response = await http.PostAsync(new Uri(Api, strong ? "pins?strong=true" : "pins"), content: null, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(PlexJsonContext.Default.PlexPin, cancellation).ConfigureAwait(false)
               ?? throw new HttpRequestException("plex.tv answered the sign-in request with nothing.");
    }

    public async Task<PlexPin?> CheckPinAsync(long id, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(new Uri(Api, $"pins/{id}"), cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(PlexJsonContext.Default.PlexPin, cancellation).ConfigureAwait(false);
    }

    /// <summary>Plex's sign-in page for <paramref name="pin"/>, to open in the browser.</summary>
    public static Uri SignInPage(string clientIdentifier, PlexPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new Uri(
            "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(clientIdentifier)
            + "&code=" + Uri.EscapeDataString(pin.Code)
            + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(PlexClientIdentity.Product));
    }

    public async Task<PlexUser> GetUserAsync(string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, "user"));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException("plex.tv no longer accepts this sign-in.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(PlexJsonContext.Default.PlexUser, cancellation).ConfigureAwait(false)
               ?? throw new HttpRequestException("plex.tv answered with no account.");
    }

    /// <summary>Every server the account can reach, its own and those shared with it.</summary>
    public async Task<IReadOnlyList<PlexResource>> GetServersAsync(string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, "resources?includeHttps=1&includeRelay=1&includeIPv6=1"));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException("plex.tv no longer accepts this sign-in.");
        }

        response.EnsureSuccessStatusCode();
        var resources = await response.Content.ReadFromJsonAsync(PlexJsonContext.Default.ListPlexResource, cancellation).ConfigureAwait(false) ?? [];
        return [.. resources.Where(r => r.IsServer).OrderByDescending(r => r.Owned).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
