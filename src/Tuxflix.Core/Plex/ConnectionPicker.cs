using System.Diagnostics;
using System.Net.Http.Json;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core.Plex;

/// <summary>How a server is reached, best first.</summary>
public enum ConnectionKind
{
    Local,
    Remote,
    Relay,
}

public sealed record ServerConnection(Uri Uri, ConnectionKind Kind, TimeSpan Latency)
{
    public bool IsSecure => Uri.Scheme == System.Uri.UriSchemeHttps;

    public string Describe() => Kind switch
    {
        ConnectionKind.Local => "Local",
        ConnectionKind.Remote => "Remote",
        _ => "Relay",
    };
}

/// <summary>
/// Chooses how to reach a server: every address plex.tv lists is tried at once, and the best one
/// that answers as that server wins.
/// </summary>
/// <remarks>
/// Local beats remote beats relay (Plex's own rule: relay bandwidth is capped), then the fastest
/// answer within a kind. A local secure address that the router's DNS-rebinding protection will not
/// resolve is also tried as plain <c>http://address:port</c>, unless the server requires secure
/// connections. An answer only counts if <c>/identity</c> names the server that was asked for, so a
/// different machine that happens to hold the address is never taken for it.
/// <para>
/// Plain http carries the token where anyone on the way can read it, so it is only spoken on the
/// server's own network, as Plex's apps allow by default: to a local address, and only when plex.tv
/// sees this client at the server's public address. A remote or relay address must be secure. The
/// probe itself carries no token (<c>/identity</c> answers without one), so a machine that is not
/// the server is never handed it.
/// </para>
/// </remarks>
public static class ConnectionPicker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ServerConnection?> PickAsync(HttpClient http, PlexResource server, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(server);

        var candidates = Candidates(server).ToList();
        if (candidates.Count == 0) return null;

        var probes = candidates.Select(c => ProbeAsync(http, server.ClientIdentifier, c.Uri, c.Kind, cancellation)).ToList();
        var answers = new List<ServerConnection>();
        while (probes.Count > 0)
        {
            var finished = await Task.WhenAny(probes).ConfigureAwait(false);
            probes.Remove(finished);
            if (await finished.ConfigureAwait(false) is not { } answer) continue;
            answers.Add(answer);

            // A secure local answer is as good as it gets; anything less waits for the rest, so a
            // plain-http answer that happens to arrive first never beats the secure one.
            if (answer.Kind == ConnectionKind.Local && answer.IsSecure) break;
        }

        var best = answers
            .OrderBy(a => a.Kind)
            .ThenByDescending(a => a.IsSecure)
            .ThenBy(a => a.Latency)
            .FirstOrDefault();
        Log.Info(best is null
            ? $"No address of {server.Name} answered ({candidates.Count} tried)."
            : $"Reaching {server.Name} over a {best.Describe().ToLowerInvariant()} connection ({best.Latency.TotalMilliseconds:0} ms).");
        return best;
    }

    /// <summary>Every address worth trying, with the kind of connection it would be.</summary>
    public static IEnumerable<(Uri Uri, ConnectionKind Kind)> Candidates(PlexResource server)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var connection in server.Connections ?? [])
        {
            if (!Uri.TryCreate(connection.Uri, UriKind.Absolute, out var uri)) continue;
            var kind = connection.Relay ? ConnectionKind.Relay : connection.Local ? ConnectionKind.Local : ConnectionKind.Remote;
            var plainAllowed = kind == ConnectionKind.Local && server.PublicAddressMatches && !server.HttpsRequired;
            if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && plainAllowed)) yield return (uri, kind);

            if (plainAllowed && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(connection.Address) && connection.Port > 0)
            {
                var host = connection.IPv6 ? $"[{connection.Address}]" : connection.Address;
                if (Uri.TryCreate($"http://{host}:{connection.Port}/", UriKind.Absolute, out var plain)) yield return (plain, kind);
            }
        }
    }

    private static async Task<ServerConnection?> ProbeAsync(HttpClient http, string machineIdentifier, Uri uri, ConnectionKind kind, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ProbeTimeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "identity"));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var identity = await response.Content.ReadFromJsonAsync(PlexJsonContext.Default.PlexEnvelope, timeout.Token).ConfigureAwait(false);
            return identity?.MediaContainer?.MachineIdentifier == machineIdentifier
                ? new ServerConnection(uri, kind, clock.Elapsed)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
