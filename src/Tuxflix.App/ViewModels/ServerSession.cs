using Tuxflix.App.Demo;
using Tuxflix.App.Imaging;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>One server the window is showing: its client and its artwork.</summary>
public sealed class ServerSession : IDisposable
{
    private readonly HttpClient _http;

    private ServerSession(HttpClient http, PlexServerClient client, string detail, string? diskCache, string? machineIdentifier, bool isRemote = false)
    {
        _http = http;
        Client = client;
        Images = new ImageLoader(client, diskCache);
        Detail = detail;
        MachineIdentifier = machineIdentifier;
        IsRemote = isRemote;
    }

    public PlexServerClient Client { get; }

    public ImageLoader Images { get; }

    public string Name => Client.Name;

    /// <summary>How the server is reached, for the status bar: "Built-in", "Local", "Remote", "Relay".</summary>
    public string Detail { get; }

    public string? MachineIdentifier { get; }

    /// <summary>Reached over the internet (a public address or Plex's relay) rather than the home network.</summary>
    public bool IsRemote { get; }

    public bool IsDemo => Client.IsDemo;

    public static ServerSession CreateDemo(PlexClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var catalog = DemoCatalog.Create(DateTimeOffset.Now);
        var http = identity.CreateHttpClient(new DemoPlexHandler(catalog, new ProceduralArt()));
        var client = new PlexServerClient(http, DemoPlexHandler.BaseUri, token: null, "Demo Library", isDemo: true);
        return new ServerSession(http, client, "Built-in", diskCache: null, DemoCatalog.MachineIdentifier);
    }

    /// <summary>A real server, over the connection the picker chose, with its artwork cached on disk.</summary>
    public static ServerSession CreateRemote(PlexClientIdentity identity, PlexResource server, ServerConnection connection, AppPaths paths, HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(paths);

        var http = identity.CreateHttpClient(network, disposeHandler: network is null);
        var client = new PlexServerClient(http, connection.Uri, server.AccessToken, server.Name);
        var cache = Path.Combine(paths.ImageCache, Sanitise(server.ClientIdentifier));
        return new ServerSession(http, client, connection.Describe(), cache, server.ClientIdentifier, isRemote: connection.Kind != ConnectionKind.Local);
    }

    public void Dispose() => _http.Dispose();

    private static string Sanitise(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
