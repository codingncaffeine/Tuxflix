using Tuxflix.App.Demo;
using Tuxflix.App.Imaging;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>One server the window is showing: its client and its artwork.</summary>
public sealed class ServerSession
{
    private ServerSession(PlexServerClient client, string detail, string? diskCache)
    {
        Client = client;
        Images = new ImageLoader(client, diskCache);
        Detail = detail;
    }

    public PlexServerClient Client { get; }

    public ImageLoader Images { get; }

    public string Name => Client.Name;

    /// <summary>How the server is reached, for the status bar: "Built-in", "Local", "Remote", "Relay".</summary>
    public string Detail { get; }

    public bool IsDemo => Client.IsDemo;

    public static ServerSession CreateDemo(PlexClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var catalog = DemoCatalog.Create(DateTimeOffset.Now);
        var http = identity.CreateHttpClient(new DemoPlexHandler(catalog, new ProceduralArt()));
        var client = new PlexServerClient(http, DemoPlexHandler.BaseUri, token: null, "Demo Library", isDemo: true);
        return new ServerSession(client, "Built-in", diskCache: null);
    }
}
