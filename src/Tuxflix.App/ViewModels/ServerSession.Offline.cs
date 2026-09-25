using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

public sealed partial class ServerSession
{
    /// <summary>
    /// A server out of reach, for playing what was downloaded from it: every request fails at once,
    /// as an unreachable address does, while artwork kept beside the downloads still loads.
    /// </summary>
    public static ServerSession CreateOffline(PlexClientIdentity identity, string serverId, string serverName)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var http = identity.CreateHttpClient(new Unreachable());
        var client = new PlexServerClient(http, new Uri("http://offline.tuxflix.invalid/"), token: null, serverName);
        return new ServerSession(http, client, "Offline", diskCache: null, serverId);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No server is in reach: Tuxflix is offline."));
    }
}
