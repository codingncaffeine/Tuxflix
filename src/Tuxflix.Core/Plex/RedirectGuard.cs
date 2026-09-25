using System.Net;

namespace Tuxflix.Core.Plex;

/// <summary>
/// Follows redirects as the runtime would, except that the sign-in stays behind: a hop to another
/// scheme, host or port loses the <c>X-Plex-Token</c> header, and it stays lost for the hops after.
/// </summary>
/// <remarks>
/// The runtime's own redirect handling clears <c>Authorization</c> and nothing else, so a custom
/// header such as the token would follow a redirect anywhere: a reverse proxy in front of a server
/// that bounces to a sign-in page on another host would be handed it. As curl, Go and browsers
/// treat credentials, a hop that changes origin drops it. A secure request is never redirected to
/// plain http, and a request with a body is not sent again. The handler below must not follow
/// redirects itself (<see cref="SocketsHttpHandler.AllowAutoRedirect"/> off), or this never sees them.
/// </remarks>
public sealed class RedirectGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Hops followed before the redirect itself is the answer.</summary>
    public const int MaxHops = 10;

    private static readonly string[] Credentials = ["X-Plex-Token", "Authorization", "Cookie"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = request.RequestUri!;
        var current = request;
        var keepCredentials = true;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var hop = 0; hop < MaxHops && Target(current, response) is { } target && NextMethod(current, response.StatusCode) is { } method; hop++)
        {
            keepCredentials &= SameOrigin(origin, target);
            var next = new HttpRequestMessage(method, target) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            foreach (var header in current.Headers)
            {
                if (!keepCredentials && Credentials.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = next;
            response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>Where a redirect points, when it is one to follow.</summary>
    private static Uri? Target(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return null;
        if (response.Headers.Location is not { } location) return null;

        // On Unix a bare "/path" can parse as an absolute file address: it is a path on the same server.
        var target = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(request.RequestUri!, location.OriginalString);
        if (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp) return null;
        return request.RequestUri!.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp ? null : target;
    }

    /// <summary>The method of the next hop, as the runtime chooses it; null when the body would have to be sent again.</summary>
    private static HttpMethod? NextMethod(HttpRequestMessage request, HttpStatusCode status) =>
        status == HttpStatusCode.SeeOther ? (request.Method == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get)
        : status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && request.Method == HttpMethod.Post ? HttpMethod.Get
        : request.Content is null ? request.Method
        : null;

    private static bool SameOrigin(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
}
