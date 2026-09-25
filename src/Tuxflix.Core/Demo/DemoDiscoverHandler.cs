using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>
/// Answers the requests Plex's Discover provider would, for the demo: the Watchlist, and adding
/// to it and taking from it, kept in <see cref="DemoCatalog"/>.
/// </summary>
/// <remarks>
/// It sits under the same <see cref="PlexDiscoverClient"/> the real provider does. Changes must be
/// a PUT, as the provider requires, so a client that sends anything else is refused here too.
/// </remarks>
public sealed class DemoDiscoverHandler(DemoCatalog catalog) : HttpMessageHandler
{
    public static readonly Uri BaseUri = new("http://discover.demo.tuxflix.invalid/");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.Run(() => Answer(request), cancellationToken);

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request needs a URI.");
        var query = HttpUtility.ParseQueryString(uri.Query);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var get = request.Method == HttpMethod.Get;
        var put = request.Method == HttpMethod.Put;
        return segments switch
        {
            ["library", "sections", "watchlist", "all"] when get => Watchlist(query),
            ["library", "metadata", var key, "userState"] when get => catalog.CatalogueEntry(key) is { } entry
                ? Json(new MediaContainer { Size = 1, UserState = [new UserState { RatingKey = entry.RatingKey, WatchlistedAt = catalog.WatchlistedAt(key) }] })
                : new HttpResponseMessage(HttpStatusCode.NotFound),
            ["actions", "addToWatchlist" or "removeFromWatchlist"] when !put => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            ["actions", var action] when query["ratingKey"] is { Length: > 0 } key => catalog.SetWatchlisted(key, action == "addToWatchlist")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(MediaContainer container)
    {
        var json = JsonSerializer.Serialize(new PlexEnvelope { MediaContainer = container }, PlexJsonContext.Default.PlexEnvelope);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private HttpResponseMessage Watchlist(System.Collections.Specialized.NameValueCollection query)
    {
        var all = catalog.Watchlist();
        var start = Math.Clamp(Number(query["X-Plex-Container-Start"]) ?? 0, 0, all.Count);
        var size = Number(query["X-Plex-Container-Size"]) ?? 20;
        if (size > PlexDiscoverClient.PageSize) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var page = all.Skip(start).Take(size).ToList();
        return Json(new MediaContainer { Size = page.Count, TotalSize = all.Count, Offset = start, LibrarySectionTitle = "Watchlist", Metadata = page });
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
