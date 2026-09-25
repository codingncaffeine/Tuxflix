using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The demo's home screen, item extras and Watchlist: promoted shelves, related shelves, trailers,
// reviews, theme music, and a Plex catalogue (Discover) with a Watchlist that can be changed.
public sealed partial class DemoCatalog
{
    // Titles the demo's Discover knows that are not in the demo library, so the Watchlist shows
    // both kinds: one to open, one the server does not have.
    private static readonly (string Title, int Year, string Type, string Summary)[] DiscoverOnlySpecs =
    [
        ("The Weaver's Lantern", 2025, "movie", "A weaver in a mountain town stitches a tapestry that shows what will happen tomorrow."),
        ("Salt Flats Serenade", 2024, "movie", "A touring pianist stranded on a salt flat plays for the one town that still has a stage."),
        ("Orbit of Small Things", 2025, "show", "The crew of a tiny cargo station keeps the solar system's smallest deliveries on time."),
    ];

    private static readonly string[] Publications =
    [
        "The Harbour Review", "Northlight Weekly", "Tallis Film Journal", "Meridian Screen", "The Evening Reel", "Lanternlight",
    ];

    private static readonly string[] ReviewLines =
    [
        "Handsome, patient and sure of its own strange rhythm; it lingers well after the credits.",
        "The performances carry it further than the script deserves, and the last act almost lands.",
        "A crowd-pleaser with more craft than it lets on. The score alone is worth the ticket.",
        "Ambitious to a fault: it reaches for everything at once and holds on to half of it.",
        "Quietly devastating. Few films this year trust their audience as much.",
        "Pretty to look at and hard to care about; the story runs out long before the runtime does.",
    ];

    private readonly object _watchlistGate = new();
    private readonly Dictionary<string, MetadataItem> _catalogue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _watchlisted = new(StringComparer.Ordinal);

    /// <summary>A title's guid in Plex's catalogue, <c>plex://movie/…</c>, the same every run.</summary>
    internal static string CatalogGuid(string type, string key) => $"plex://{type}/{CatalogId(type + "/" + key)}";

    // Twenty-four hexadecimal digits, as Plex's catalogue identifiers are.
    private static string CatalogId(string text) =>
        string.Create(CultureInfo.InvariantCulture, $"5d{(uint)Seed(text):x8}{(uint)Seed(text + "#"):x8}{(uint)Seed("#" + text) & 0xFFFFFF:x6}");

    /// <summary>The home screen's promoted shelves, as <c>/hubs/promoted</c> gives them: Continue Watching, then each library's.</summary>
    public IReadOnlyList<Hub> PromotedHubs()
    {
        var continuing = ContinueWatching().ToList();
        var recentMovies = Movies.OrderByDescending(m => m.AddedAt).Take(20).ToList();
        var recentShows = Shows.OrderByDescending(s => s.AddedAt).ToList();
        var released = Movies.OrderByDescending(m => m.OriginallyAvailableAt, StringComparer.Ordinal).Take(20).ToList();
        return
        [
            new() { Title = "Continue Watching", HubIdentifier = "home.continue", Type = "mixed", Style = "hero", Promoted = true, Size = continuing.Count, Metadata = continuing, Key = "/hubs/home/continueWatching" },
            new() { Title = "Recently Added in Movies", HubIdentifier = "movie.recentlyadded.1", Type = "movie", Promoted = true, Size = recentMovies.Count, Metadata = recentMovies, Key = "/library/sections/1/all?sort=addedAt:desc" },
            new() { Title = "Recently Added in TV Shows", HubIdentifier = "tv.recentlyadded.2", Type = "mixed", Promoted = true, Size = recentShows.Count, Metadata = recentShows, Key = "/hubs/home/recentlyAdded?type=2&sectionID=2" },
            new() { Title = "Recently Released in Movies", HubIdentifier = "movie.recentlyreleased.1", Type = "movie", Promoted = true, Size = released.Count, Metadata = released, Key = "/library/sections/1/all?sort=originallyAvailableAt:desc" },
        ];
    }

    /// <summary>Shelves related to an item, as <c>/library/metadata/{id}/related</c> gives them.</summary>
    public IReadOnlyList<Hub> RelatedHubs(string ratingKey)
    {
        if (Find(ratingKey) is not { Type: "movie" or "show" } item) return [];
        var kind = item.Type == "movie" ? Movies : Shows;
        var genres = item.Genre?.Select(g => g.Id).ToHashSet() ?? [];
        var similar = kind.Where(o => o.RatingKey != item.RatingKey)
            .Select(o => (Item: o, Shared: o.Genre?.Count(g => genres.Contains(g.Id)) ?? 0))
            .Where(o => o.Shared > 0)
            .OrderByDescending(o => o.Shared).ThenByDescending(o => o.Item.AudienceRating)
            .Select(o => o.Item).Take(12).ToList();
        var hubs = new List<Hub>();
        if (similar.Count > 0)
        {
            hubs.Add(new()
            {
                Title = item.Type == "movie" ? "Related Movies" : "Related Shows",
                HubIdentifier = item.Type + ".similar",
                Context = $"hub.{item.Type}.similar",
                Type = item.Type,
                Size = similar.Count,
                Metadata = similar,
                Key = $"/library/metadata/{item.RatingKey}/similar",
            });
        }

        if (item.Role?.FirstOrDefault() is { } lead)
        {
            var together = Movies.Concat(Shows).Where(o => o.RatingKey != item.RatingKey && o.Role?.Any(r => r.Id == lead.Id) == true).ToList();
            if (together.Count > 0)
            {
                hubs.Add(new()
                {
                    Title = $"More with {lead.TagText}",
                    HubIdentifier = item.Type + ".byactor",
                    Type = "mixed",
                    Size = together.Count,
                    Metadata = together,
                    Key = $"/library/sections/{item.LibrarySectionId}/all?actor={lead.Id}",
                });
            }
        }

        return hubs;
    }

    /// <summary>What the library holds under a catalogue guid, as <c>/library/all?guid=</c> answers.</summary>
    public IReadOnlyList<MetadataItem> FindByGuid(string? guid) =>
        guid is null ? [] : [.. Movies.Concat(Shows).Where(i => i.Guid == guid)];

    /// <summary>The Watchlist, most recently added first.</summary>
    public IReadOnlyList<MetadataItem> Watchlist()
    {
        lock (_watchlistGate)
        {
            return [.. _watchlisted.OrderByDescending(w => w.Value).Select(w => _catalogue[w.Key])];
        }
    }

    /// <summary>When a catalogue title went on the Watchlist, or null.</summary>
    public long? WatchlistedAt(string catalogKey)
    {
        lock (_watchlistGate)
        {
            return _watchlisted.TryGetValue(catalogKey, out var at) ? at : null;
        }
    }

    /// <summary>Puts a catalogue title on the Watchlist or takes it off; false for a title the catalogue does not know.</summary>
    public bool SetWatchlisted(string catalogKey, bool on)
    {
        lock (_watchlistGate)
        {
            if (!_catalogue.ContainsKey(catalogKey)) return false;
            if (!on) _watchlisted.Remove(catalogKey);
            else if (!_watchlisted.ContainsKey(catalogKey)) _watchlisted[catalogKey] = Math.Max(Now.ToUnixTimeSeconds(), _watchlisted.Values.DefaultIfEmpty(0).Max() + 1);
            return true;
        }
    }

    /// <summary>The Plex catalogue's entry for a title, by its catalogue key.</summary>
    public MetadataItem? CatalogueEntry(string catalogKey)
    {
        lock (_watchlistGate)
        {
            return _catalogue.GetValueOrDefault(catalogKey);
        }
    }

    /// <summary>The catalogue and its starting Watchlist; the constructor runs it once the library is built.</summary>
    private void BuildDiscover()
    {
        foreach (var item in Movies.Concat(Shows))
        {
            var id = PlexDiscoverClient.CatalogKey(item.Guid)!;
            _catalogue[id] = new MetadataItem
            {
                RatingKey = id,
                Key = $"/library/metadata/{id}",
                Guid = item.Guid,
                Type = item.Type,
                Title = item.Title,
                Year = item.Year,
                Summary = item.Summary,
                Thumb = item.Thumb,
                Art = item.Art,
                ContentRating = item.ContentRating,
                AudienceRating = item.AudienceRating,
            };
        }

        for (var i = 0; i < DiscoverOnlySpecs.Length; i++)
        {
            var spec = DiscoverOnlySpecs[i];
            var guid = CatalogGuid(spec.Type, "discover-" + i.ToString(CultureInfo.InvariantCulture));
            var id = PlexDiscoverClient.CatalogKey(guid)!;
            var thumb = $"/demo/discover/{id}/thumb";
            var art = $"/demo/discover/{id}/art/1";
            _art[thumb] = (spec.Title, spec.Year.ToString(CultureInfo.InvariantCulture), Seed(spec.Title));
            _art[art] = (spec.Title, null, Seed(spec.Title));
            _catalogue[id] = new MetadataItem
            {
                RatingKey = id,
                Key = $"/library/metadata/{id}",
                Guid = guid,
                Type = spec.Type,
                Title = spec.Title,
                Year = spec.Year,
                Summary = spec.Summary,
                Thumb = thumb,
                Art = art,
            };
        }

        // Three films and a series from the library, then the three the library lacks.
        var start = new[] { Movies[6], Movies[20], Movies[27], Shows[2] }.Select(i => PlexDiscoverClient.CatalogKey(i.Guid)!)
            .Concat(DiscoverOnlySpecs.Select((spec, i) => PlexDiscoverClient.CatalogKey(CatalogGuid(spec.Type, "discover-" + i.ToString(CultureInfo.InvariantCulture)))!))
            .ToList();
        for (var i = 0; i < start.Count; i++) _watchlisted[start[i]] = Now.AddDays(-(i * 2) - 1).ToUnixTimeSeconds();
    }

    /// <summary>An item's trailer, and for some a behind-the-scenes featurette, each playable as a clip.</summary>
    private ExtrasList Extras(string key, string title, int year, int seed)
    {
        var kinds = seed % 2 == 0
            ? new[] { ("trailer", "Official Trailer", 135), ("behindTheScenes", $"Making {title}", 522) }
            : new[] { ("trailer", "Official Trailer", 135) };
        var extras = new List<MetadataItem>();
        for (var n = 0; n < kinds.Length; n++)
        {
            var (subtype, name, seconds) = kinds[n];
            var extraKey = (3_000_000 + (int.Parse(key, CultureInfo.InvariantCulture) * 10) + n).ToString(CultureInfo.InvariantCulture);
            var thumb = $"/library/metadata/{extraKey}/thumb/1700000000";
            _art[thumb] = (title, name, seed + 101 + n);
            var extra = new MetadataItem
            {
                RatingKey = extraKey,
                Key = $"/library/metadata/{extraKey}",
                Type = "clip",
                Subtype = subtype,
                Title = name,
                Year = year,
                Index = n + 1,
                Thumb = thumb,
                Duration = seconds * 1000L,
                Media =
                [
                    new()
                    {
                        Id = 8_000_000 + long.Parse(extraKey, CultureInfo.InvariantCulture),
                        Duration = seconds * 1000L,
                        Width = 1920,
                        Height = 1080,
                        VideoCodec = "h264",
                        VideoResolution = "1080",
                        AudioCodec = "aac",
                        AudioChannels = 2,
                        Container = "mp4",
                        Part = [new() { Id = 9_000_000 + long.Parse(extraKey, CultureInfo.InvariantCulture), Key = $"/library/parts/{extraKey}/1700000000/file.mp4", Duration = seconds * 1000L, Container = "mp4" }],
                    },
                ],
            };
            _byKey[extraKey] = extra;
            extras.Add(extra);
        }

        return new ExtrasList { Size = extras.Count, Metadata = extras };
    }

    /// <summary>Three critics' reviews in invented publications, fresh or rotten by the item's seed.</summary>
    private static List<Review> Reviews(int seed)
    {
        var reviews = new List<Review>();
        for (var r = 0; r < 3; r++)
        {
            var fresh = (seed + r) % 4 != 0;
            reviews.Add(new Review
            {
                Id = (seed % 100_000) * 10L + r,
                Tag = $"{FirstNames[(seed + r * 11) % FirstNames.Length]} {LastNames[(seed / 7 + r * 5) % LastNames.Length]}",
                Source = Publications[(seed + r) % Publications.Length],
                Text = ReviewLines[(seed + r * 2 + (fresh ? 0 : 3)) % ReviewLines.Length],
                Image = fresh ? "rottentomatoes://image.review.fresh" : "rottentomatoes://image.review.rotten",
            });
        }

        return reviews;
    }

    /// <summary>Every series has a theme, as on a real server; a film only now and then.</summary>
    private static string? ThemeOf(string key, string type, int seed) =>
        type == "show" || seed % 3 == 1 ? $"/library/metadata/{key}/theme/1700000000" : null;
}
