using System.Net.Http.Headers;

namespace Tuxflix.Core.Plex;

/// <summary>
/// Who this client is, in the <c>X-Plex-*</c> headers every request carries.
/// </summary>
/// <remarks>
/// The product name and device name are what the account owner sees under Authorized Devices,
/// and the client identifier is how Plex tells installations apart, so it is generated once per
/// profile and kept.
/// </remarks>
public sealed record PlexClientIdentity(string ClientIdentifier, string Version, string DeviceName)
{
    public const string Product = "Tuxflix";

    public void Apply(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientIdentifier);
        headers.TryAddWithoutValidation("X-Plex-Product", Product);
        headers.TryAddWithoutValidation("X-Plex-Version", Version);
        headers.TryAddWithoutValidation("X-Plex-Platform", "Linux");
        headers.TryAddWithoutValidation("X-Plex-Platform-Version", Environment.OSVersion.Version.ToString());
        headers.TryAddWithoutValidation("X-Plex-Device", "PC");
        headers.TryAddWithoutValidation("X-Plex-Device-Name", DeviceName);
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>An HTTP client carrying this identity on every request.</summary>
    public HttpClient CreateHttpClient(HttpMessageHandler? handler = null, bool disposeHandler = true)
    {
        var client = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            : new HttpClient(handler, disposeHandler);
        client.Timeout = TimeSpan.FromSeconds(30);
        Apply(client.DefaultRequestHeaders);
        return client;
    }
}
