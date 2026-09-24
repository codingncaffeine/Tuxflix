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
public sealed class DemoPlexHandler(DemoCatalog catalog, IDemoArtRenderer? art) : HttpMessageHandler
{
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
            ["library", "sections", var section, "all"] => SectionItems(section, query["sort"], start, size),
            ["hubs"] => Json(new MediaContainer { Size = catalog.HomeHubs().Count, Hub = [.. catalog.HomeHubs()] }),
            ["hubs", "continueWatching"] => Json(new MediaContainer { Hub = [catalog.HomeHubs()[0]] }),
            ["library", "metadata", var key] => catalog.Find(key) is { } item
                ? Json(new MediaContainer { Size = 1, Metadata = [item] })
                : NotFound(),
            ["library", "metadata", var key, "children"] => Json(new MediaContainer { Size = catalog.ChildrenOf(key).Count, Metadata = [.. catalog.ChildrenOf(key)] }),
            ["photo", ":", "transcode"] => Image(query["url"], ParseInt(query["width"]), ParseInt(query["height"])),
            ["services", "ultrablur", "colors"] => query["url"] is { } url && catalog.UltraBlurFor(url) is { } colours
                ? Json(new MediaContainer { Size = 1, UltraBlurColors = [colours] })
                : NotFound(),
            _ => NotFound(),
        };

        return response;
    }

    private HttpResponseMessage SectionItems(string section, string? sort, int? start, int? size)
    {
        IEnumerable<MetadataItem> items = section switch
        {
            DemoCatalog.MoviesSectionKey => catalog.Movies,
            DemoCatalog.ShowsSectionKey => catalog.Shows,
            _ => [],
        };

        items = (sort ?? "titleSort") switch
        {
            "addedAt:desc" => items.OrderByDescending(i => i.AddedAt),
            "year:desc" => items.OrderByDescending(i => i.Year).ThenBy(i => i.TitleSort, StringComparer.OrdinalIgnoreCase),
            "rating:desc" => items.OrderByDescending(i => i.Rating),
            "lastViewedAt:desc" => items.OrderByDescending(i => i.LastViewedAt ?? 0),
            _ => items.OrderBy(i => i.TitleSort ?? i.Title, StringComparer.OrdinalIgnoreCase),
        };

        var all = items.ToList();
        var offset = Math.Clamp(start ?? 0, 0, all.Count);
        var page = all.Skip(offset).Take(size ?? all.Count).ToList();
        return Json(new MediaContainer { Size = page.Count, TotalSize = all.Count, Offset = offset, Metadata = page });
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

    private static int ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 320;
}
