using System.Globalization;
using System.Net;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The viewer's own state, answered from memory as a server answers it: watched marks, ratings,
// stream choices, playlists and their editing, what plays now, and the watch history.
public sealed partial class DemoPlexHandler
{
    private static readonly bool ViewerRoutes = Add((handler, segments, query, request) =>
    {
        var catalog = handler.Catalog;
        var method = request.Method;
        return segments switch
        {
            [":", "scrobble"] => Done(catalog.MarkWatched(query["key"], watched: true)),
            [":", "unscrobble"] => Done(catalog.MarkWatched(query["key"], watched: false)),
            [":", "rate"] => Done(double.TryParse(query["rating"], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && catalog.Rate(query["key"], rating)),
            ["library", "parts", var part] when method == HttpMethod.Put =>
                Done(long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                     && catalog.ChooseStreams(id, Long(query["audioStreamID"]), Long(query["subtitleStreamID"]))),

            ["playlists"] when method == HttpMethod.Post => catalog.CreatePlaylist(query["title"], query["type"], query["uri"]) is { } made
                ? Json(new MediaContainer { Size = 1, Metadata = [made] })
                : new HttpResponseMessage(HttpStatusCode.BadRequest),
            ["playlists"] => Json(new MediaContainer { Size = catalog.Playlists().Count, Metadata = [.. catalog.Playlists()] }),
            ["playlists", var key] when method == HttpMethod.Put => Done(catalog.RenamePlaylist(key, query["title"])),
            ["playlists", var key] when method == HttpMethod.Delete => Done(catalog.DeletePlaylist(key)),
            ["playlists", var key] => catalog.Playlist(key) is { } playlist ? Json(new MediaContainer { Size = 1, Metadata = [playlist] }) : NotFound(),
            ["playlists", var key, "items"] when method == HttpMethod.Put => Done(catalog.AddToPlaylist(key, query["uri"])),
            ["playlists", var key, "items"] => catalog.PlaylistItems(key) is { } items ? Page(items, request) : NotFound(),
            ["playlists", var key, "items", var entry] when method == HttpMethod.Delete =>
                Done(Long(entry) is { } id && catalog.RemoveFromPlaylist(key, id)),
            ["playlists", var key, "items", var entry, "move"] when method == HttpMethod.Put =>
                Done(Long(entry) is { } id && catalog.MovePlaylistItem(key, id, Long(query["after"]))),

            ["status", "sessions"] => Json(new MediaContainer { Size = catalog.Sessions().Count, Metadata = [.. catalog.Sessions()] }),
            ["status", "sessions", "history", "all"] => Page(catalog.History(Long(query["accountID"]), Long(query["viewedAt>"])), request),
            _ => null,
        };
    });

    private static HttpResponseMessage Done(bool found) => found ? new HttpResponseMessage(HttpStatusCode.OK) : NotFound();

    /// <summary>One page of a list, by the paging headers, with the whole list's size.</summary>
    private static HttpResponseMessage Page(IReadOnlyList<MetadataItem> all, HttpRequestMessage request)
    {
        var offset = Math.Clamp(HeaderInt(request, "X-Plex-Container-Start") ?? 0, 0, all.Count);
        var page = all.Skip(offset).Take(HeaderInt(request, "X-Plex-Container-Size") ?? all.Count).ToList();
        return Json(new MediaContainer { Size = page.Count, TotalSize = all.Count, Offset = offset, Metadata = page });
    }

    private static long? Long(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
