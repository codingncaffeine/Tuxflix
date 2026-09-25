using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The demo server's music answers: every track, lyrics, loudness, sonic neighbours and paths,
// stations and the play queues that play them.
public sealed partial class DemoPlexHandler
{
    private static readonly bool MusicRoutes = Add((handler, segments, query, request) => segments switch
    {
        ["library", "metadata", var key, "nearest"] => List(handler.Catalog.Nearest(key, ParseInt(query["limit"], 50))),
        ["library", "sections", DemoCatalog.MusicSectionKey, "computePath"] =>
            List(handler.Catalog.SonicPath(query["startID"] ?? string.Empty, query["endID"] ?? string.Empty)),
        ["hubs", "sections", DemoCatalog.MusicSectionKey] => Json(new MediaContainer
        {
            Size = 1,
            Hub = query["includeStations"] == "1"
                ? [new Hub { Title = "Stations", Type = "station", HubIdentifier = "music.stations." + DemoCatalog.MusicSectionKey, Size = handler.Catalog.Stations.Count, Metadata = [.. handler.Catalog.Stations] }]
                : [],
        }),
        ["library", "streams", var id, "levels"] when long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stream) =>
            handler.Catalog.LevelsFor(stream, ParseInt(query["subsample"], 128)) is { Count: > 0 } levels
                ? Json(new MediaContainer { Size = levels.Count, TotalSamples = levels.Count * 8, Level = [.. levels.Select(v => new LoudnessLevel { V = v })] })
                : NotFound(),
        ["playQueues"] when request.Method == HttpMethod.Post && query["uri"] is { } uri => Json(handler.Catalog.PlayQueueFor(uri)),
        _ => null,
    });

    private static HttpResponseMessage List(IReadOnlyList<MetadataItem> items) => Json(new MediaContainer { Size = items.Count, Metadata = [.. items] });

    /// <summary>A lyric stream's lyrics, or null when the stream is not one.</summary>
    private HttpResponseMessage? Lyrics(string id) =>
        long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stream) && Catalog.LyricsFor(stream) is { } lyrics
            ? Json(new MediaContainer { Size = 1, Lyrics = [lyrics] })
            : null;
}
