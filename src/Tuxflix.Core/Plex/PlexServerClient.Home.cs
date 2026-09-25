using System.Globalization;
using System.Net;

namespace Tuxflix.Core.Plex;

// The home screen, the item page's shelves and the random pick.
public sealed partial class PlexServerClient
{
    /// <summary>
    /// The home screen's shelves as the server's owner arranged them: each library's promoted
    /// shelves (<c>/hubs/promoted</c>, what Plex's own apps show), Continue Watching first. A server
    /// without that list gives its global hubs instead, the promoted ones first.
    /// </summary>
    public async Task<IReadOnlyList<Hub>> GetPromotedHubsAsync(CancellationToken cancellation)
    {
        try
        {
            var promoted = (await GetAsync("/hubs/promoted?count=20", cancellation).ConfigureAwait(false)).Hub;
            if (promoted is { Count: > 0 }) return promoted;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // An older server: its global hubs are the home screen.
        }

        var hubs = await GetHomeHubsAsync(cancellation).ConfigureAwait(false);
        return [.. hubs.Where(h => h.Promoted).Concat(hubs.Where(h => !h.Promoted))];
    }

    /// <summary>An item for its page: the full record with its extras and critics' reviews.</summary>
    public async Task<MetadataItem?> GetItemDetailsAsync(string ratingKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}?includeExtras=1&includeReviews=1&includeMarkers=1&includeChapters=1", cancellation).ConfigureAwait(false))
        .Metadata?.FirstOrDefault();

    /// <summary>Shelves related to an item: similar titles, more from its director or cast, its collections.</summary>
    public async Task<IReadOnlyList<Hub>> GetRelatedHubsAsync(string ratingKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}/related?count=20", cancellation).ConfigureAwait(false)).Hub ?? [];

    /// <summary>An item's trailers, featurettes and scenes.</summary>
    public async Task<IReadOnlyList<MetadataItem>> GetExtrasAsync(string ratingKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}/extras", cancellation).ConfigureAwait(false)).Metadata ?? [];

    /// <summary>The server's own copy of a title from Plex's catalogue, found by its <c>plex://</c> guid; null when it has none.</summary>
    public async Task<MetadataItem?> FindByGuidAsync(string guid, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        var found = (await GetAsync($"/library/all?guid={Uri.EscapeDataString(guid)}", cancellation).ConfigureAwait(false)).Metadata ?? [];

        // A guid names one title; a server with the same film in two libraries lists it twice.
        return found.FirstOrDefault(i => i.Guid == guid) ?? found.FirstOrDefault();
    }

    /// <summary>
    /// Something unwatched from one library, picked at random: a film, or a series with episodes
    /// left (<paramref name="type"/> 1 or 2). The server counts them, then hands over the one at a
    /// random place, so the pick is fair however large the library. Null when nothing is left.
    /// </summary>
    public async Task<MetadataItem?> GetRandomUnwatchedAsync(string sectionKey, int type, CancellationToken cancellation, Random? random = null)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={type}&unwatched=1");
        var count = (await GetAsync(path, cancellation, start: 0, size: 0).ConfigureAwait(false)).TotalSize ?? 0;
        if (count <= 0) return null;
        var at = (random ?? Random.Shared).Next(count);
        return (await GetAsync(path, cancellation, start: at, size: 1).ConfigureAwait(false)).Metadata?.FirstOrDefault();
    }

    /// <summary>
    /// Something to watch from a library of films or series, picked at random from what is
    /// unwatched: a film, or the next episode of a series (the first one not yet finished, so a
    /// series begun carries on and a new one starts at its beginning). Null when nothing is left.
    /// </summary>
    public async Task<MetadataItem?> PickSomethingAsync(LibraryDirectory section, CancellationToken cancellation, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Type is not ("movie" or "show")) return null;
        var pick = await GetRandomUnwatchedAsync(section.Key, section.Type == "show" ? 2 : 1, cancellation, random).ConfigureAwait(false);
        if (pick is not { Type: "show" }) return pick;
        var episodes = await GetAllLeavesAsync(pick.RatingKey, cancellation).ConfigureAwait(false);
        return episodes.FirstOrDefault(e => !e.IsWatched);
    }
}
