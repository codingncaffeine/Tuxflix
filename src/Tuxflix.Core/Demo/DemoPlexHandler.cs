using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>What an image of the demo library should show.</summary>
public enum DemoArtKind
{
    Poster,
    Backdrop,
    Still,
    Person,

    /// <summary>The title as a transparent logo, as a server's clearLogo image is.</summary>
    Logo,
}

public sealed record DemoArtRequest(DemoArtKind Kind, string Title, string? Subtitle, int Seed, int Width, int Height);

/// <summary>An encoded picture and its media type.</summary>
public sealed record DemoImage(byte[] Bytes, string ContentType);

/// <summary>Paints the demo library's artwork; the application supplies one that draws.</summary>
public interface IDemoArtRenderer
{
    DemoImage Render(DemoArtRequest request);
}

/// <summary>
/// Answers the requests a Plex Media Server would, from <see cref="DemoCatalog"/>, in process.
/// </summary>
/// <remarks>
/// It sits under the same <see cref="PlexServerClient"/> a real server does, so the demo walks
/// the real request, parsing and paging code rather than a parallel path that could drift.
/// </remarks>
public sealed partial class DemoPlexHandler(DemoCatalog catalog, IDemoArtRenderer? art) : HttpMessageHandler
{
    /// <summary>An answer to a request the core routes do not know, or null to let the next one try.</summary>
    private delegate HttpResponseMessage? Route(DemoPlexHandler handler, string[] segments, System.Collections.Specialized.NameValueCollection query, HttpRequestMessage request);

    // Routes beyond the core ones: a feature adds its own in a file of its own, as
    // `private static readonly bool Registered = Add((handler, segments, query, request) => ...);`.
    private static List<Route>? _routes;

    private static bool Add(Route route)
    {
        (_routes ??= []).Add(route);
        return true;
    }

    private DemoCatalog Catalog => catalog;

    private IDemoArtRenderer? Art => art;

    public static readonly Uri BaseUri = new("http://demo.tuxflix.invalid:32400/");

    // Answered on the thread pool, as a network reply would be: drawing artwork on the caller's
    // thread would stall whatever thread asked, and that is usually the UI.
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.Run(() => Answer(request), cancellationToken);

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request needs a URI.");
        var path = uri.AbsolutePath.TrimEnd('/');
        var query = HttpUtility.ParseQueryString(uri.Query);
        var start = HeaderInt(request, "X-Plex-Container-Start");
        var size = HeaderInt(request, "X-Plex-Container-Size");

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var response = segments switch
        {
            [] or ["identity"] => Json(new MediaContainer { FriendlyName = "Demo Library", MachineIdentifier = DemoCatalog.MachineIdentifier, Version = "1.43.0" }),
            ["library", "sections"] => Json(new MediaContainer { Size = catalog.Sections.Count, Directory = [.. catalog.Sections] }),
            ["library", "sections", var section, "all"] => SectionItems(section, query, start, size),
            ["library", "sections", _, "sorts"] => Json(new MediaContainer { Directory = [.. Sorts] }),
            ["library", "sections", var section, "filters"] => Json(new MediaContainer { Directory = [.. Filters(section)] }),
            ["library", "sections", var section, "collections"] => Json(new MediaContainer { Size = catalog.CollectionsOf(section).Count, Metadata = [.. catalog.CollectionsOf(section)] }),
            ["library", "sections", var section, var filter] => Json(new MediaContainer { Directory = [.. catalog.FilterValues(section, filter)] }),
            ["library", "collections", var key, "children"] => Json(new MediaContainer { Size = catalog.ChildrenOf(key).Count, Metadata = [.. catalog.ChildrenOf(key)] }),
            ["hubs", "search"] => Json(new MediaContainer { Hub = [.. catalog.Search(query["query"] ?? string.Empty, ParseInt(query["limit"], 10))] }),
            ["playlists"] => Json(new MediaContainer { Size = 0, Metadata = [] }),
            ["hubs"] => Json(new MediaContainer { Size = catalog.HomeHubs().Count, Hub = [.. catalog.HomeHubs()] }),
            ["hubs", "continueWatching"] => Json(new MediaContainer { Hub = [catalog.HomeHubs()[0]] }),
            ["library", "metadata", var key] => catalog.Find(key) is { } item
                ? Json(new MediaContainer { Size = 1, Metadata = [item] })
                : NotFound(),
            ["library", "metadata", var key, "children"] => Json(new MediaContainer { Size = catalog.ChildrenOf(key).Count, Metadata = [.. catalog.ChildrenOf(key)] }),
            ["photo", ":", "transcode"] => Image(query["url"], ParseInt(query["width"]), ParseInt(query["height"])),
            ["library", "parts", var part, "indexes", "sd"] => Previews(part),
            ["services", "ultrablur", "colors"] => query["url"] is { } url && catalog.UltraBlurFor(url) is { } colours
                ? Json(new MediaContainer { Size = 1, UltraBlurColors = [colours] })
                : NotFound(),
            _ => More(segments, query, request) ?? NotFound(),
        };

        return response;
    }

    private HttpResponseMessage? More(string[] segments, System.Collections.Specialized.NameValueCollection query, HttpRequestMessage request)
    {
        foreach (var route in _routes ?? [])
        {
            if (route(this, segments, query, request) is { } answer) return answer;
        }

        return null;
    }

    private static readonly LibraryDirectory[] Sorts =
    [
        new() { Key = "titleSort", Title = "Title", DefaultDirection = "asc", DescKey = "titleSort:desc" },
        new() { Key = "year", Title = "Year", DefaultDirection = "desc", DescKey = "year:desc" },
        new() { Key = "addedAt", Title = "Date Added", DefaultDirection = "desc", DescKey = "addedAt:desc" },
        new() { Key = "audienceRating", Title = "Audience Rating", DefaultDirection = "desc", DescKey = "audienceRating:desc" },
        new() { Key = "lastViewedAt", Title = "Date Viewed", DefaultDirection = "desc", DescKey = "lastViewedAt:desc" },
    ];

    private static IEnumerable<LibraryDirectory> Filters(string section)
    {
        var path = $"/library/sections/{section}";
        yield return new() { Filter = "genre", FilterType = "string", Key = path + "/genre", Title = "Genre", Type = "filter" };
        yield return new() { Filter = "decade", FilterType = "integer", Key = path + "/decade", Title = "Decade", Type = "filter" };
        yield return new() { Filter = "contentRating", FilterType = "string", Key = path + "/contentRating", Title = "Content Rating", Type = "filter" };
        yield return new() { Filter = "unwatched", FilterType = "boolean", Key = path + "/unwatched", Title = "Unwatched", Type = "filter" };
        yield return new() { Filter = "inProgress", FilterType = "boolean", Key = path + "/inProgress", Title = "In Progress", Type = "filter" };
    }

    /// <summary>A section listing with the server's sort and filter parameters, as far as the demo has the data.</summary>
    private HttpResponseMessage SectionItems(string section, System.Collections.Specialized.NameValueCollection query, int? start, int? size)
    {
        IEnumerable<MetadataItem> items = catalog.SectionItems(section);
        if (query["genre"] is { } genre) items = items.Where(i => i.Genre?.Any(g => Same(g.Id, genre)) == true);
        if (query["decade"] is { } decade) items = items.Where(i => i.Year is { } year && (year / 10 * 10).ToString(CultureInfo.InvariantCulture) == decade);
        if (query["contentRating"] is { } rating) items = items.Where(i => i.ContentRating == rating);
        if (query["unwatched"] == "1") items = items.Where(i => !i.IsWatched && i.Progress is null);
        if (query["inProgress"] == "1") items = items.Where(i => i.Progress is > 0 and < 1 || (i.ViewedLeafCount is > 0 && !i.IsWatched));
        if (query["actor"] is { } actor) items = items.Where(i => i.Role?.Any(r => Same(r.Id, actor)) == true);
        if (query["director"] is { } director) items = items.Where(i => i.Director?.Any(d => Same(d.Id, director)) == true);

        var sort = (query["sort"] ?? "titleSort").Split(':');
        var descending = sort.Length > 1 && sort[1] == "desc";
        Func<MetadataItem, IComparable?> key = sort[0] switch
        {
            "year" or "originallyAvailableAt" => i => i.Year,
            "addedAt" => i => i.AddedAt,
            "audienceRating" or "rating" => i => i.AudienceRating,
            "lastViewedAt" => i => i.LastViewedAt ?? 0,
            "duration" => i => i.Duration,
            _ => i => (i.TitleSort ?? i.Title).ToUpperInvariant(),
        };
        items = descending ? items.OrderByDescending(key).ThenBy(i => i.TitleSort, StringComparer.OrdinalIgnoreCase) : items.OrderBy(key).ThenBy(i => i.TitleSort, StringComparer.OrdinalIgnoreCase);

        var all = items.ToList();
        var offset = Math.Clamp(start ?? 0, 0, all.Count);
        var page = all.Skip(offset).Take(size ?? all.Count).ToList();
        return Json(new MediaContainer { Size = page.Count, TotalSize = all.Count, Offset = offset, Metadata = page });

        static bool Same(long? id, string value) => id?.ToString(CultureInfo.InvariantCulture) == value;
    }

    private HttpResponseMessage Image(string? path, int width, int height)
    {
        if (art is null || string.IsNullOrEmpty(path) || catalog.ArtFor(path) is not { } subject)
        {
            return NotFound();
        }

        var kind = path.StartsWith("/demo/person/", StringComparison.Ordinal) ? DemoArtKind.Person
            : path.Contains("/clearLogo/", StringComparison.Ordinal) ? DemoArtKind.Logo
            : path.Contains("/art/", StringComparison.Ordinal) ? DemoArtKind.Backdrop
            : subject.Subtitle is { } sub && !sub.StartsWith("Season ", StringComparison.Ordinal) && !int.TryParse(sub, out _) ? DemoArtKind.Still
            : DemoArtKind.Poster;

        var image = art.Render(new DemoArtRequest(kind, subject.Title, subject.Subtitle, subject.Seed, Math.Clamp(width, 16, 3840), Math.Clamp(height, 16, 2160)));
        var content = new ByteArrayContent(image.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>A film's seek previews: a picture every so often, each naming its time, in one BIF file.</summary>
    private HttpResponseMessage Previews(string part)
    {
        if (art is null || !long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || catalog.FindByPart(id) is not { Duration: > 0 } item)
        {
            return NotFound();
        }

        const int Pictures = 60;
        var interval = (uint)(item.Duration!.Value / Pictures);
        var pictures = Enumerable.Range(0, Pictures)
            .Select(n => art.Render(new DemoArtRequest(DemoArtKind.Still, item.Title, TimeSpan.FromMilliseconds(n * (double)interval).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture), 11 * n, 240, 100)).Bytes)
            .ToList();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PreviewIndex.Build(pictures, interval)) };
    }

    private static HttpResponseMessage Json(MediaContainer container)
    {
        var json = JsonSerializer.Serialize(new PlexEnvelope { MediaContainer = container }, PlexJsonContext.Default.PlexEnvelope);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static int? HeaderInt(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int ParseInt(string? text, int fallback = 320) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
