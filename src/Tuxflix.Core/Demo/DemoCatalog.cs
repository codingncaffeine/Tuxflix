using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>
/// The built-in demo library: invented films and series with invented people, so every screen
/// can be built, tested and shown without a server and without anybody's copyrighted artwork.
/// </summary>
/// <remarks>
/// Deterministic: the same catalogue, watch states and artwork every run, measured from the
/// <c>now</c> it is created with, so tests can pin exact values.
/// </remarks>
public sealed class DemoCatalog
{
    public const string MoviesSectionKey = "1";
    public const string ShowsSectionKey = "2";
    public const string MachineIdentifier = "tuxflix-demo";

    private static readonly (string Title, int Year, string G1, string G2, int Minutes, string Rating, string Tagline, string Summary, string Director)[] MovieSpecs =
    [
        ("The Glass Meridian", 2023, "Science Fiction", "Drama", 128, "PG-13", "Every clock stops somewhere.", "A cartographer charting a line across the open ocean discovers that time runs differently on either side of it.", "Ines Marlow"),
        ("Northbound Static", 2021, "Thriller", "Mystery", 104, "R", "The signal found her first.", "A night-shift radio operator in the far north starts taking calls from a town that burned down forty years ago.", "Tomas Reyer"),
        ("Harbor of Small Lights", 2019, "Drama", "Romance", 117, "PG", "Some lighthouses guide you home.", "Two estranged sisters return to their fishing village to decide the fate of the lighthouse their father kept.", "Mae Okonkwo"),
        ("Kestrel Nine", 2024, "Action", "Science Fiction", 136, "PG-13", "One pilot. No ground control.", "A test pilot trapped aboard an experimental orbital glider has to bring it down before the atmosphere does.", "Aaron Vale"),
        ("A Quiet Engine", 2020, "Drama", "Family", 98, "PG", "Grief runs on its own schedule.", "A retired railway mechanic restores a steam locomotive with the grandson he barely knows.", "Lotte Brandt"),
        ("Paper Moons Over Tallis", 2018, "Animation", "Family", 92, "G", "Hang one up and make a wish.", "In a town where the moon is cut from paper every night, a girl sets out to find out who stole it.", "Kenji Arata"),
        ("The Long Signal", 2022, "Science Fiction", "Mystery", 141, "PG-13", "Something is answering.", "Astronomers decode a message from deep space that reads like a warning written by themselves.", "Priya Sandoval"),
        ("Low Tide Radio", 2017, "Comedy", "Music", 101, "PG-13", "Broadcasting from the edge of nowhere.", "A washed-up DJ takes over a pirate station on a sandbar that vanishes at every high tide.", "Colm Farrow"),
        ("Cinder & Salt", 2021, "Adventure", "Drama", 124, "PG-13", "The sea keeps what it takes.", "A disgraced ship's cook leads a mutinous crew across a volcanic archipelago.", "Ruth Adeyemi"),
        ("The Cartographer's Daughter", 2016, "Mystery", "Drama", 119, "PG", "Every map hides a door.", "After her father's death, a young archivist finds his map of a city that was never built.", "Henrik Solberg"),
        ("Midnight at the Aurora Hotel", 2022, "Mystery", "Comedy", 109, "PG-13", "Check in. Solve it. Check out.", "A snowbound hotel, eleven guests and one missing night manager.", "Dana Whitlock"),
        ("Iron Orchard", 2019, "Western", "Drama", 132, "R", "Nothing grows here but trouble.", "A widow defends her father's orchard from the railway barons circling her valley.", "Wes Calloway"),
        ("Sixteen Winters", 2023, "Drama", "Family", 115, "PG", "Some seasons change you.", "A family's history told through sixteen winters at the same frozen lake.", "Anneke Visser"),
        ("The Velvet Frequency", 2020, "Thriller", "Crime", 112, "R", "Tune in. Don't look back.", "A jazz club's sound engineer overhears a murder through the house microphones.", "Marcus Bell"),
        ("Blue Hour Heist", 2024, "Crime", "Comedy", 106, "PG-13", "Twenty minutes of perfect light.", "A crew of art restorers plans to lift a painting during the one hour a day its alarms go blind.", "Sofia Carrion"),
        ("Ghosts of Emberfall", 2018, "Horror", "Mystery", 97, "R", "The fire never really went out.", "Paranormal investigators spend a night in a mining town that has been burning underground for fifty years.", "Neil Harrow"),
        ("Last Train to Vireo", 2015, "Romance", "Drama", 111, "PG", "Some stops are forever.", "Two strangers share a sleeper cabin on the final run of a legendary night train.", "Elodie Marchand"),
        ("The Salt Road", 2022, "Adventure", "History", 146, "PG-13", "Worth its weight.", "A caravan of salt traders crosses the desert while a rival empire hunts their map.", "Karim Haddad"),
        ("Neon Tide", 2021, "Science Fiction", "Action", 118, "R", "The city floods at midnight.", "In a drowned megacity, a courier discovers the package she carries is somebody's memory.", "Yuna Seo"),
        ("The Understudy's Night", 2019, "Drama", "Thriller", 103, "R", "The show goes on. At any cost.", "An understudy's big break arrives when the lead disappears the night before opening.", "Clara Voss"),
        ("Wolves of the Ninth Sky", 2024, "Fantasy", "Adventure", 139, "PG-13", "Run with the storm.", "A shepherd girl bonds with a pack of sky-wolves that hunt the lightning.", "Tarek Lindqvist"),
        ("Late Harvest", 2016, "Drama", "Family", 108, "PG-13", "Every tree remembers.", "An old orchard keeper takes in two runaways during the harvest of his final season.", "Greta Holm"),
        ("Moth & Lantern", 2020, "Fantasy", "Romance", 114, "PG", "Drawn to the light.", "A lamplighter in an enchanted city falls for a woman who only appears at dusk.", "Ada Kovac"),
        ("Beacon Line", 2017, "Action", "Thriller", 121, "R", "Light it and run.", "A mountain rescue team becomes the target of the smugglers it was sent to save.", "Joel Ramsey"),
        ("The Tin Astronomer", 2021, "Animation", "Family", 95, "G", "Reach for the stars. Mind the rust.", "A tiny robot builds a telescope out of scrap to find its missing inventor.", "Mira Hollis"),
        ("Glasswater", 2023, "Mystery", "Thriller", 116, "R", "Clear water hides the deepest secrets.", "A diver searching a flooded quarry for a lost ring finds a car that should not be there.", "Owen Pryce"),
        ("Seven Bridges Home", 2018, "Comedy", "Drama", 102, "PG-13", "It's a long way back.", "A burned-out lawyer walks home across seven bridges and seven old grudges.", "Rosa Jimenez"),
        ("The Amber Archive", 2022, "Mystery", "Adventure", 127, "PG-13", "History was never lost. It was hidden.", "Two rival librarians race to find a vault of forbidden books beneath a sinking city.", "Felix Moreau"),
        ("Starling Season", 2019, "Romance", "Comedy", 99, "PG-13", "Love migrates.", "A birdwatcher and a wind-farm engineer clash over a coastal marsh.", "Hannah Price"),
        ("Redline Summer", 2023, "Action", "Drama", 113, "PG-13", "Fast isn't fast enough.", "A teenage mechanic rebuilds her brother's rally car for one last race.", "Diego Alvarez"),
        ("Frostbloom", 2020, "Fantasy", "Family", 104, "PG", "Some flowers only open in the cold.", "A botanist's daughter follows a glowing flower into a kingdom caught in an endless winter.", "Ilse Norgaard"),
        ("Minor Planets", 2024, "Science Fiction", "Comedy", 107, "PG-13", "Small worlds. Big problems.", "The crew of a budget asteroid-mining ship accidentally lays claim to a planet.", "Sam Okafor"),
        ("Dust Choir", 2018, "Western", "Music", 118, "PG-13", "Sing it to the canyon.", "A travelling gospel choir is stranded in a ghost town that keeps a secret.", "Lena Hart"),
        ("Halcyon Drive", 2021, "Crime", "Drama", 122, "R", "Paradise has a price.", "An estate agent in a perfect suburb uncovers the one house nobody will sell.", "Ray Castellan"),
    ];

    private static readonly (string Title, int Year, string G1, string G2, string Rating, string Network, int Seasons, int Episodes, int Watched, string Summary)[] ShowSpecs =
    [
        ("Lanternfall", 2021, "Fantasy", "Drama", "TV-14", "Northlight", 3, 8, 13, "In a kingdom lit by captured stars, a lamplighter's apprentice uncovers the conspiracy dimming the sky."),
        ("The Archivists", 2019, "Mystery", "Comedy", "TV-PG", "Tallis One", 4, 10, 40, "A team of mismatched archivists solves cold cases buried in a city's forgotten records."),
        ("Deep Field", 2023, "Science Fiction", "Thriller", "TV-MA", "Meridian", 2, 8, 5, "The crew of a deep-space telescope station starts seeing things in the images that look back."),
        ("Night Shift at Kessler's", 2020, "Comedy", "Drama", "TV-14", "Tallis One", 3, 10, 0, "The overnight staff of a 24-hour superstore handle everything except the customers."),
        ("Harborline", 2022, "Crime", "Drama", "TV-MA", "Northlight", 2, 10, 18, "A harbour police unit weathers smugglers, storms and each other."),
        ("Signal & Noise", 2024, "Documentary", "Music", "TV-PG", "Meridian", 1, 6, 2, "A journey through the history of recorded sound, from the first wax cylinder to the last pirate station."),
        ("The Orchard House", 2018, "Drama", "Family", "TV-PG", "Northlight", 3, 8, 24, "Three generations of one family keep an orchard alive in a changing valley."),
        ("Paper Kingdoms", 2023, "Animation", "Adventure", "TV-Y7", "Tallis Kids", 2, 12, 7, "Origami heroes defend a desk-drawer empire from the dreaded Stapler King."),
    ];

    private static readonly string[] EpisodeTitles =
    [
        "First Light", "Low Water", "The Crossing", "The Long Night", "Salt and Iron", "Echoes", "The Keeper",
        "Fault Lines", "Homecoming", "Northern Lights", "The Ledger", "Glass Houses", "Undertow", "The Last Door",
        "Still Waters", "Ashes", "Open Water", "The Quiet Room", "Wildfire", "Paper Trails", "Tinder", "The Signal",
        "Old Friends", "Blackout", "Tidewater", "The Visitor", "Riverbend", "Stormglass", "Afterglow", "The Reckoning",
    ];

    private static readonly string[] FirstNames =
    [
        "Mara", "Julian", "Odette", "Rafael", "Nadia", "Theo", "Imogen", "Caspar", "Lucia", "Emeka", "Sabine", "Hugo",
        "Freya", "Anton", "Priya", "Declan", "Ines", "Rowan", "Talia", "Mateo", "Eliza", "Kofi", "Vera", "Silas",
    ];

    private static readonly string[] LastNames =
    [
        "Venn", "Ashdown", "Morrow", "Castell", "Ibarra", "Lindgren", "Quill", "Okafor", "Reyes", "Hartley", "Novak",
        "Sterling", "Achebe", "Fairweather", "Kowalski", "Delacroix", "Brandt", "Whitlock", "Serrano", "Havel",
    ];

    private static readonly string[] CharacterNames =
    [
        "The Captain", "Detective Hale", "Wren", "Mr. Oduya", "Aunt Rosalind", "The Stranger", "Dr. Ilse Maren",
        "Pike", "Commander Varga", "Young Tomas", "The Night Manager", "Sister Agnes", "Kit", "Mayor Quint",
    ];

    private readonly Dictionary<string, MetadataItem> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MetadataItem>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Title, string? Subtitle, int Seed)> _art = new(StringComparer.Ordinal);

    private DemoCatalog(DateTimeOffset now)
    {
        Now = now;
        Movies = BuildMovies();
        Shows = BuildShows();
    }

    public DateTimeOffset Now { get; }

    public IReadOnlyList<MetadataItem> Movies { get; }

    public IReadOnlyList<MetadataItem> Shows { get; }

    public static DemoCatalog Create(DateTimeOffset now) => new(now);

    public IReadOnlyList<LibraryDirectory> Sections =>
    [
        new() { Key = MoviesSectionKey, Type = "movie", Title = "Movies", Agent = "tv.plex.agents.movie", Language = "en-US", Uuid = "demo-movies" },
        new() { Key = ShowsSectionKey, Type = "show", Title = "TV Shows", Agent = "tv.plex.agents.series", Language = "en-US", Uuid = "demo-shows" },
    ];

    public MetadataItem? Find(string ratingKey) => _byKey.GetValueOrDefault(ratingKey);

    public IReadOnlyList<MetadataItem> ChildrenOf(string ratingKey) =>
        _children.TryGetValue(ratingKey, out var children) ? children : [];

    /// <summary>What to paint for an image path the catalogue handed out, or null.</summary>
    public (string Title, string? Subtitle, int Seed)? ArtFor(string imagePath) =>
        _art.TryGetValue(imagePath, out var art) ? art : null;

    public IReadOnlyList<MetadataItem> ContinueWatching()
    {
        var movies = Movies.Where(m => m.Progress is > 0 and < 1);
        var episodes = Shows
            .Select(show => NextEpisode(show.RatingKey))
            .Where(episode => episode is not null && episode.ViewOffset is > 0)
            .Select(episode => episode!);
        return [.. movies.Concat(episodes).OrderByDescending(item => item.LastViewedAt ?? 0)];
    }

    public IReadOnlyList<Hub> HomeHubs()
    {
        var recentMovies = Movies.OrderByDescending(m => m.AddedAt).Take(20).ToList();
        var recentShows = Shows.OrderByDescending(s => s.AddedAt).ToList();
        var released = Movies.OrderByDescending(m => m.Year).ThenBy(m => m.Title, StringComparer.Ordinal).Take(20).ToList();
        var continuing = ContinueWatching().ToList();

        return
        [
            new() { Title = "Continue Watching", HubIdentifier = "home.continue", Type = "mixed", Style = "hero", Promoted = true, Size = continuing.Count, Metadata = continuing, Key = "/hubs/continueWatching" },
            new() { Title = "Recently Added Movies", HubIdentifier = "home.movies.recent", Type = "movie", Promoted = true, Size = recentMovies.Count, Metadata = recentMovies, Key = "/hubs/home/recentlyAdded?type=1" },
            new() { Title = "Recently Added TV", HubIdentifier = "home.television.recent", Type = "show", Promoted = true, Size = recentShows.Count, Metadata = recentShows, Key = "/hubs/home/recentlyAdded?type=2" },
            new() { Title = "Recently Released Movies", HubIdentifier = "home.movies.released", Type = "movie", Promoted = true, Size = released.Count, Metadata = released, Key = "/hubs/home/recentlyReleased?type=1" },
        ];
    }

    private MetadataItem? NextEpisode(string showKey) =>
        ChildrenOf(showKey).SelectMany(season => ChildrenOf(season.RatingKey)).FirstOrDefault(episode => !episode.IsWatched);

    private List<MetadataItem> BuildMovies()
    {
        var movies = new List<MetadataItem>();
        for (var i = 0; i < MovieSpecs.Length; i++)
        {
            var spec = MovieSpecs[i];
            var key = (1001 + i).ToString(CultureInfo.InvariantCulture);
            var seed = Seed(spec.Title);
            var duration = spec.Minutes * 60_000L;

            // A spread of states: every fourth watched, every fifth part-way through.
            long? offset = i % 5 == 2 ? (long)(duration * (0.18 + ((seed % 60) / 100.0))) : null;
            int? views = i % 4 == 1 ? 1 : null;
            var lastViewed = offset is not null || views is not null ? Now.AddHours(-(i * 7) - 2).ToUnixTimeSeconds() : (long?)null;

            var thumb = $"/library/metadata/{key}/thumb/1700000000";
            var art = $"/library/metadata/{key}/art/1700000000";
            _art[thumb] = (spec.Title, spec.Year.ToString(CultureInfo.InvariantCulture), seed);
            _art[art] = (spec.Title, null, seed);

            var is4K = seed % 3 == 0;
            var movie = new MetadataItem
            {
                RatingKey = key,
                Key = $"/library/metadata/{key}",
                Guid = $"tuxflix-demo://movie/{key}",
                Type = "movie",
                Title = spec.Title,
                TitleSort = SortTitle(spec.Title),
                Studio = spec.Director.Split(' ')[^1] + " Pictures",
                ContentRating = spec.Rating,
                Summary = spec.Summary,
                Tagline = spec.Tagline,
                Rating = Math.Round(5.8 + (seed % 38) / 10.0, 1),
                AudienceRating = Math.Round(6.1 + (seed % 35) / 10.0, 1),
                Year = spec.Year,
                Thumb = thumb,
                Art = art,
                Duration = duration,
                OriginallyAvailableAt = $"{spec.Year}-{1 + seed % 12:00}-{1 + seed % 27:00}",
                AddedAt = Now.AddDays(-(i * 3) - 1).ToUnixTimeSeconds(),
                UpdatedAt = Now.AddDays(-(i * 3) - 1).ToUnixTimeSeconds(),
                LastViewedAt = lastViewed,
                ViewCount = views,
                ViewOffset = offset,
                LibrarySectionId = 1,
                LibrarySectionTitle = "Movies",
                Genre = [new() { TagText = spec.G1 }, new() { TagText = spec.G2 }],
                Director = [new() { TagText = spec.Director }],
                Image = Images(key, thumb, art, spec.Title, seed),
                UltraBlurColors = Blur(seed),
                Role = Cast(seed),
                Media =
                [
                    new()
                    {
                        Id = 5000 + i,
                        Duration = duration,
                        Bitrate = is4K ? 38_000 : 9_800,
                        Width = is4K ? 3840 : 1920,
                        Height = is4K ? 1608 : 804,
                        AspectRatio = 2.39,
                        AudioChannels = is4K ? 8 : 6,
                        AudioCodec = is4K ? "truehd" : "eac3",
                        VideoCodec = is4K ? "hevc" : "h264",
                        VideoResolution = is4K ? "4k" : "1080",
                        Container = "mkv",
                        VideoFrameRate = "24p",
                        Part =
                        [
                            new()
                            {
                                Id = 7000 + i,
                                Key = $"/library/parts/{7000 + i}/1700000000/file.mkv",
                                Duration = duration,
                                File = $"/media/movies/{spec.Title} ({spec.Year}).mkv",
                                Size = duration / 1000 * (is4K ? 4_750_000L : 1_225_000L),
                                Container = "mkv",
                            },
                        ],
                    },
                ],
            };

            _byKey[key] = movie;
            movies.Add(movie);
        }

        return movies;
    }

    private List<MetadataItem> BuildShows()
    {
        var shows = new List<MetadataItem>();
        for (var i = 0; i < ShowSpecs.Length; i++)
        {
            var spec = ShowSpecs[i];
            var showId = 2001 + i;
            var key = showId.ToString(CultureInfo.InvariantCulture);
            var seed = Seed(spec.Title);
            var thumb = $"/library/metadata/{key}/thumb/1700000000";
            var art = $"/library/metadata/{key}/art/1700000000";
            _art[thumb] = (spec.Title, null, seed);
            _art[art] = (spec.Title, null, seed);

            var seasons = new List<MetadataItem>();
            var watchedSoFar = 0;
            var totalEpisodes = spec.Seasons * spec.Episodes;
            for (var s = 1; s <= spec.Seasons; s++)
            {
                var seasonKey = (showId * 100 + s).ToString(CultureInfo.InvariantCulture);
                var seasonThumb = $"/library/metadata/{seasonKey}/thumb/1700000000";
                _art[seasonThumb] = (spec.Title, $"Season {s}", seed);

                var episodes = new List<MetadataItem>();
                for (var e = 1; e <= spec.Episodes; e++)
                {
                    var episodeKey = (showId * 10_000 + s * 100 + e).ToString(CultureInfo.InvariantCulture);
                    var ordinal = (s - 1) * spec.Episodes + e;
                    var watched = ordinal <= spec.Watched;
                    var next = ordinal == spec.Watched + 1 && spec.Watched > 0;
                    var duration = (38 + (seed + ordinal) % 18) * 60_000L;
                    var title = EpisodeTitles[(seed + ordinal * 7) % EpisodeTitles.Length];
                    var episodeThumb = $"/library/metadata/{episodeKey}/thumb/1700000000";
                    _art[episodeThumb] = (spec.Title, title, seed + ordinal * 31);

                    var episode = new MetadataItem
                    {
                        RatingKey = episodeKey,
                        Key = $"/library/metadata/{episodeKey}",
                        Type = "episode",
                        Title = title,
                        Index = e,
                        ParentIndex = s,
                        ParentRatingKey = seasonKey,
                        ParentTitle = $"Season {s}",
                        ParentThumb = seasonThumb,
                        GrandparentRatingKey = key,
                        GrandparentTitle = spec.Title,
                        GrandparentThumb = thumb,
                        GrandparentArt = art,
                        ContentRating = spec.Rating,
                        Summary = $"{spec.Title} continues as the story of season {s} turns on \"{title}\".",
                        Year = spec.Year + s - 1,
                        Thumb = episodeThumb,
                        Art = art,
                        Duration = duration,
                        AddedAt = Now.AddDays(-(i * 4) - (spec.Seasons - s) * 20 - 1).ToUnixTimeSeconds(),
                        ViewCount = watched ? 1 : null,
                        ViewOffset = next && i % 2 == 0 ? duration / 3 : null,
                        LastViewedAt = watched || next ? Now.AddHours(-(i * 5) - 1).ToUnixTimeSeconds() : null,
                        LibrarySectionId = 2,
                        LibrarySectionTitle = "TV Shows",
                        Media =
                        [
                            new()
                            {
                                Id = 90_000 + showId * 100 + ordinal,
                                Duration = duration,
                                Width = 1920,
                                Height = 1080,
                                VideoCodec = "hevc",
                                VideoResolution = "1080",
                                AudioCodec = "eac3",
                                AudioChannels = 6,
                                Container = "mkv",
                                Part = [new() { Id = 95_000 + showId * 100 + ordinal, Key = $"/library/parts/{episodeKey}/1700000000/file.mkv", Duration = duration, Container = "mkv" }],
                            },
                        ],
                    };

                    if (watched) watchedSoFar++;
                    _byKey[episodeKey] = episode;
                    episodes.Add(episode);
                }

                var season = new MetadataItem
                {
                    RatingKey = seasonKey,
                    Key = $"/library/metadata/{seasonKey}/children",
                    Type = "season",
                    Title = $"Season {s}",
                    Index = s,
                    ParentRatingKey = key,
                    ParentTitle = spec.Title,
                    ParentThumb = thumb,
                    Thumb = seasonThumb,
                    Art = art,
                    LeafCount = spec.Episodes,
                    ViewedLeafCount = episodes.Count(ep => ep.IsWatched),
                    LibrarySectionId = 2,
                };

                _byKey[seasonKey] = season;
                _children[seasonKey] = episodes;
                seasons.Add(season);
            }

            var show = new MetadataItem
            {
                RatingKey = key,
                Key = $"/library/metadata/{key}/children",
                Guid = $"tuxflix-demo://show/{key}",
                Type = "show",
                Title = spec.Title,
                TitleSort = SortTitle(spec.Title),
                Studio = spec.Network,
                ContentRating = spec.Rating,
                Summary = spec.Summary,
                Rating = Math.Round(6.4 + (seed % 33) / 10.0, 1),
                AudienceRating = Math.Round(6.8 + (seed % 30) / 10.0, 1),
                Year = spec.Year,
                Thumb = thumb,
                Art = art,
                AddedAt = Now.AddDays(-(i * 4) - 1).ToUnixTimeSeconds(),
                LastViewedAt = watchedSoFar > 0 ? Now.AddHours(-(i * 5) - 1).ToUnixTimeSeconds() : null,
                LeafCount = totalEpisodes,
                ViewedLeafCount = watchedSoFar,
                ChildCount = spec.Seasons,
                Image = Images(key, thumb, art, spec.Title, seed),
                UltraBlurColors = Blur(seed),
                LibrarySectionId = 2,
                LibrarySectionTitle = "TV Shows",
                Genre = [new() { TagText = spec.G1 }, new() { TagText = spec.G2 }],
                Role = Cast(seed),
            };

            _byKey[key] = show;
            _children[key] = seasons;
            shows.Add(show);
        }

        return shows;
    }

    private List<Tag> Cast(int seed)
    {
        var cast = new List<Tag>();
        for (var r = 0; r < 6; r++)
        {
            var name = $"{FirstNames[(seed + r * 5) % FirstNames.Length]} {LastNames[(seed / 3 + r * 7) % LastNames.Length]}";
            var thumb = $"/demo/person/{Uri.EscapeDataString(name)}";
            _art[thumb] = (name, null, Seed(name));
            cast.Add(new Tag { Id = r + 1, TagText = name, Role = CharacterNames[(seed + r * 3) % CharacterNames.Length], Thumb = thumb });
        }

        return cast;
    }

    /// <summary>The typed image list a current server sends, including a clear logo.</summary>
    private List<ItemImage> Images(string key, string thumb, string art, string title, int seed)
    {
        var logo = $"/library/metadata/{key}/clearLogo/1700000000";
        _art[logo] = (title, null, seed);
        return
        [
            new() { Type = "coverPoster", Url = thumb, Alt = title },
            new() { Type = "background", Url = art, Alt = title },
            new() { Type = "clearLogo", Url = logo, Alt = title },
        ];
    }

    private static UltraBlurColors Blur(int seed)
    {
        var (topLeft, topRight, bottomRight, bottomLeft) = DemoPalette.From(seed).UltraBlur();
        return new UltraBlurColors { TopLeft = topLeft, TopRight = topRight, BottomRight = bottomRight, BottomLeft = bottomLeft };
    }

    /// <summary>The UltraBlur colours for an image path the catalogue handed out.</summary>
    public UltraBlurColors? UltraBlurFor(string imagePath) => ArtFor(imagePath) is { } art ? Blur(art.Seed) : null;

    private static string SortTitle(string title) =>
        title.StartsWith("The ", StringComparison.Ordinal) ? title[4..]
        : title.StartsWith("A ", StringComparison.Ordinal) ? title[2..]
        : title;

    /// <summary>A stable hash: string.GetHashCode is randomised per process and would repaint every run.</summary>
    internal static int Seed(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
