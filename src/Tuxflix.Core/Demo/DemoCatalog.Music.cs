using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The demo's music library: invented artists, albums and songs, with what a server with a Plex
// Pass and sonic analysis gives a player: loudness for levelling and waveforms, lyrics (timed and
// not), stations, and each track's neighbours in sound.
public sealed partial class DemoCatalog
{
    public const string MusicSectionKey = "31";

    private const int MusicIds = 31000;

    private static readonly bool MusicRegistered = AddLibrary(catalog => catalog.BuildMusic());

    private static readonly (string Artist, string Country, string Genre, string Style, (string Title, int Year, string Label, string[] Songs)[] Albums)[] MusicSpecs =
    [
        ("The Lanterns of Vell", "Ireland", "Folk", "Indie Folk",
        [
            ("Harbour Lights", 2019, "Saltmarsh Records", ["Lanterns on the Water", "Pier Lights", "The Long Way Round", "Salt and Cedar", "Night Ferry", "Anchor Song"]),
            ("Tidewater", 2022, "Saltmarsh Records", ["Open Door", "Maps in Salt", "Low Tide Radio", "Gull Weather", "Quiet Crossing", "Home by Morning"]),
        ]),
        ("Mira Castell", "Spain", "Pop", "Dream Pop",
        [
            ("Paper Suns", 2021, "Lumen Sound", ["Paper Suns", "Glass Garden", "Slow Satellite", "Cinnamon Static", "Afterglow Avenue", "Brighter Than the City"]),
        ]),
        ("Quiet Engine", "Canada", "Electronic", "Ambient Techno",
        [
            ("Low Orbit", 2018, "Northline", ["Ignition", "Low Orbit", "Signal Drift", "Parallax", "Cold Start", "Re-Entry"]),
            ("Signal Fires", 2024, "Northline", ["Beacon", "Turn the Dial", "Carrier Wave", "Fires on the Ridge", "Night Relay", "Last Transmission"]),
        ]),
    ];

    // Original lines, written for the demo.
    private static readonly string[] LyricLines =
    [
        "Lanterns on the water, low and gold",
        "We counted every light along the pier",
        "The ferry hums a song we used to know",
        "Hold the signal, keep the engine warm",
        "Every harbour leaves a door ajar",
        "Maps we drew in salt along the shore",
        "Say it slow and let the static fall",
        "Somewhere past the orbit of the moon",
        "We were brighter than the city ever was",
        "Turn the dial until the silence sings",
        "Carry me across the quiet tide",
        "Paper suns are folding into rain",
        "I left the porch light on for you",
        "The radio is talking in its sleep",
        "Follow the fires along the ridge",
        "All the clocks are running slow tonight",
        "Nothing here but gulls and weather",
        "Come home by morning, come home",
    ];

    private readonly List<MetadataItem> _artists = [];
    private readonly List<MetadataItem> _albums = [];
    private readonly List<MetadataItem> _tracks = [];
    private readonly Dictionary<long, MetadataItem> _audioStreams = [];
    private readonly Dictionary<long, (MetadataItem Track, bool Timed)> _lyricStreams = [];
    private int _playQueues;

    public IReadOnlyList<MetadataItem> Artists => _artists;

    public IReadOnlyList<MetadataItem> Albums => _albums;

    public IReadOnlyList<MetadataItem> Tracks => _tracks;

    /// <summary>The library's stations, as its hubs list them.</summary>
    public IReadOnlyList<MetadataItem> Stations { get; private set; } = [];

    /// <summary>Every track of an artist, album by album; an album's tracks for an album; the item itself for a track.</summary>
    public IReadOnlyList<MetadataItem> AllLeaves(string ratingKey) => Find(ratingKey)?.Type switch
    {
        "artist" => [.. ChildrenOf(ratingKey).SelectMany(album => ChildrenOf(album.RatingKey))],
        "album" => ChildrenOf(ratingKey),
        "track" => [Find(ratingKey)!],
        _ => [],
    };

    /// <summary>The lyrics of a lyric stream, in the server's reading of them.</summary>
    public LyricsInfo? LyricsFor(long streamId)
    {
        if (!_lyricStreams.TryGetValue(streamId, out var entry)) return null;
        var (track, timed) = entry;
        var seed = Seed(track.Title);
        var lines = new List<LyricsLine>();
        var at = 7000L + (seed % 4000);
        var end = (track.Duration ?? 180_000) - 12_000;
        var verse = 0;
        while (at < end)
        {
            // Verses of four lines, a breath between them.
            for (var i = 0; i < 4 && at < end; i++)
            {
                var text = LyricLines[(seed + (verse * 5) + (i * 7)) % LyricLines.Length];
                var length = 3200L + (((seed >> (i + 2)) & 7) * 250);
                lines.Add(timed
                    ? new LyricsLine { StartOffset = at, EndOffset = at + length, Span = [new LyricsSpan { Text = text, StartOffset = at, EndOffset = at + length }] }
                    : new LyricsLine { Span = [new LyricsSpan { Text = text }] });
                at += length;
            }

            lines.Add(timed ? new LyricsLine { StartOffset = at, EndOffset = at + 4000 } : new LyricsLine());
            at += 4000;
            verse++;
        }

        return new LyricsInfo { Provider = "tv.plex.demo", Timed = timed, Author = "Demo Songwriters", By = "Written for the Tuxflix demo", Line = lines };
    }

    /// <summary>
    /// A track's loudness over time, dB, in <paramref name="readings"/> steps: a quiet intro, verses
    /// and louder choruses, a fade at the end, as a real song reads.
    /// </summary>
    public IReadOnlyList<double> LevelsFor(long streamId, int readings)
    {
        if (!_audioStreams.TryGetValue(streamId, out var track) || readings <= 0) return [];
        var seed = Seed(track.Title);
        var levels = new double[Math.Min(readings, 4000)];
        for (var i = 0; i < levels.Length; i++)
        {
            var t = (i + 0.5) / levels.Length;
            var chorus = (int)(t * 8 + (seed % 3)) % 3 == 2;
            var level = chorus ? -9.5 : -14.0;
            if (t < 0.06) level = -40 + ((level + 40) * (t / 0.06));
            if (t > 0.9) level -= (t - 0.9) / 0.1 * 30;
            var wobble = ((Seed(track.Title + i.ToString(CultureInfo.InvariantCulture)) % 1000) / 1000.0) - 0.5;
            levels[i] = Math.Round(level + (wobble * 3.0), 1);
        }

        return levels;
    }

    /// <summary>A track's neighbours in sound, nearest first: here, tracks close to it on one invented axis.</summary>
    public IReadOnlyList<MetadataItem> Nearest(string ratingKey, int limit)
    {
        if (Find(ratingKey) is not { Type: "track" } track) return [];
        var from = SonicAxis(track);
        return [.. _tracks.Where(t => t.RatingKey != ratingKey).OrderBy(t => Math.Abs(SonicAxis(t) - from)).Take(Math.Max(1, limit))];
    }

    /// <summary>A path in small steps of sound from one track to another, both ends included.</summary>
    public IReadOnlyList<MetadataItem> SonicPath(string startKey, string endKey)
    {
        if (Find(startKey) is not { Type: "track" } start || Find(endKey) is not { Type: "track" } end) return [];
        var (lo, hi) = (Math.Min(SonicAxis(start), SonicAxis(end)), Math.Max(SonicAxis(start), SonicAxis(end)));
        var between = _tracks.Where(t => t.RatingKey != startKey && t.RatingKey != endKey && SonicAxis(t) > lo && SonicAxis(t) < hi);
        var ordered = SonicAxis(start) <= SonicAxis(end) ? between.OrderBy(SonicAxis) : between.OrderByDescending(SonicAxis);
        return [start, .. ordered.Take(10), end];
    }

    /// <summary>What a station plays next: a fresh handful of tracks, the artist's own and their neighbours for an artist's station.</summary>
    public MediaContainer PlayQueueFor(string uri)
    {
        var queue = Interlocked.Increment(ref _playQueues);
        var path = uri.Contains("/library/", StringComparison.Ordinal) ? uri[uri.IndexOf("/library/", StringComparison.Ordinal)..] : uri;
        IEnumerable<MetadataItem> pool = _tracks;
        if (path.Contains("/station/", StringComparison.Ordinal))
        {
            var artistKey = path.Split('/')[3];
            var own = AllLeaves(artistKey);
            pool = own.Concat(own.SelectMany(t => Nearest(t.RatingKey, 2))).DistinctBy(t => t.RatingKey);
        }
        else if (!path.Contains("/stations/", StringComparison.Ordinal))
        {
            pool = AllLeaves(path.Split('/').Last());
        }

        var tracks = pool.ToArray();
        new Random(Seed(path) + queue).Shuffle(tracks);
        var window = tracks.Take(12).ToList();
        return new MediaContainer
        {
            Size = window.Count,
            PlayQueueId = queue,
            PlayQueueSelectedItemId = 1,
            PlayQueueTotalCount = window.Count,
            Metadata = window,
        };
    }

    // Where a track sits "in sound": an invented coordinate, stable per track, so neighbours and paths are repeatable.
    private static double SonicAxis(MetadataItem track) => (Seed("sonic:" + track.Title) % 10_000) / 10_000.0;

    private DemoLibrary BuildMusic()
    {
        var section = new LibraryDirectory { Key = MusicSectionKey, Type = "artist", Title = "Music", Agent = "tv.plex.agents.music", Language = "en-US", Uuid = "demo-music" };
        var sectionId = int.Parse(MusicSectionKey, CultureInfo.InvariantCulture);
        var next = MusicIds;
        foreach (var spec in MusicSpecs)
        {
            var artistKey = (++next).ToString(CultureInfo.InvariantCulture);
            var artistSeed = Seed(spec.Artist);
            var photo = $"/demo/person/{artistKey}";
            var backdrop = $"/library/metadata/{artistKey}/art/1700000000";
            _art[photo] = (spec.Artist, null, artistSeed);
            _art[backdrop] = (spec.Artist, null, artistSeed);
            var albums = new List<MetadataItem>();
            foreach (var album in spec.Albums)
            {
                var albumKey = (++next).ToString(CultureInfo.InvariantCulture);
                var cover = $"/library/metadata/{albumKey}/thumb/1700000000";
                var albumSeed = Seed(album.Title);
                _art[cover] = (album.Title, album.Year.ToString(CultureInfo.InvariantCulture), albumSeed);
                var tracks = new List<MetadataItem>();
                for (var i = 0; i < album.Songs.Length; i++)
                {
                    var trackKey = (++next).ToString(CultureInfo.InvariantCulture);
                    var title = album.Songs[i];
                    var seed = Seed(title);
                    var duration = (150 + (seed % 110)) * 1000L;
                    var partId = long.Parse(trackKey, CultureInfo.InvariantCulture) * 10;
                    var audioId = partId + 1;
                    var lyricsId = partId + 2;
                    var withLyrics = i % 3 != 2;
                    var timed = withLyrics && (i % 3 == 0 || spec.Genre != "Electronic");
                    var streams = new List<MediaStream>
                    {
                        new()
                        {
                            Id = audioId, StreamType = 2, Codec = "flac", Channels = 2, SamplingRate = 48000, Selected = true, Index = 0,
                            Gain = Math.Round(-4 - ((seed % 40) / 10.0), 2), AlbumGain = Math.Round(-4 - ((albumSeed % 40) / 10.0), 2),
                            Loudness = -12.5, Peak = 0.97, DisplayTitle = "FLAC (Stereo)",
                        },
                    };
                    if (withLyrics)
                    {
                        streams.Add(new MediaStream
                        {
                            Id = lyricsId, StreamType = MediaStream.LyricsStreamType, Codec = timed ? "lrc" : "txt", Format = timed ? "lrc" : "txt",
                            Provider = "tv.plex.demo", Timed = timed, Key = $"/library/streams/{lyricsId}",
                        });
                    }

                    var track = new MetadataItem
                    {
                        RatingKey = trackKey,
                        Key = $"/library/metadata/{trackKey}",
                        Type = "track",
                        Title = title,
                        Index = i + 1,
                        ParentIndex = 1,
                        ParentRatingKey = albumKey,
                        ParentTitle = album.Title,
                        ParentThumb = cover,
                        GrandparentRatingKey = artistKey,
                        GrandparentTitle = spec.Artist,
                        GrandparentThumb = photo,
                        GrandparentArt = backdrop,
                        Thumb = cover,
                        Duration = duration,
                        Year = album.Year,
                        AddedAt = Now.AddDays(-(next % 50)).ToUnixTimeSeconds(),
                        LibrarySectionId = sectionId,
                        LibrarySectionTitle = "Music",
                        MusicAnalysisVersion = 1,
                        Media =
                        [
                            new()
                            {
                                Id = partId - 1, Duration = duration, AudioChannels = 2, AudioCodec = "flac", Container = "flac",
                                Part = [new() { Id = partId, Key = $"/library/parts/{partId}/1700000000/file.flac", Duration = duration, Container = "flac", Stream = streams }],
                            },
                        ],
                    };
                    _byKey[trackKey] = track;
                    _audioStreams[audioId] = track;
                    if (withLyrics) _lyricStreams[lyricsId] = (track, timed);
                    tracks.Add(track);
                    _tracks.Add(track);
                }

                var albumItem = new MetadataItem
                {
                    RatingKey = albumKey,
                    Key = $"/library/metadata/{albumKey}/children",
                    Type = "album",
                    Title = album.Title,
                    ParentRatingKey = artistKey,
                    ParentTitle = spec.Artist,
                    ParentThumb = photo,
                    Thumb = cover,
                    Year = album.Year,
                    Studio = album.Label,
                    LeafCount = tracks.Count,
                    AddedAt = Now.AddDays(-(next % 50)).ToUnixTimeSeconds(),
                    LibrarySectionId = sectionId,
                    LibrarySectionTitle = "Music",
                    Genre = [Genre(spec.Genre)],
                    Style = [new Tag { Id = TagId("style", spec.Style), TagText = spec.Style }],
                    Summary = $"{album.Title}, by {spec.Artist}: an album invented for the Tuxflix demo.",
                    UltraBlurColors = Blur(albumSeed),
                };
                _byKey[albumKey] = albumItem;
                _children[albumKey] = tracks;
                albums.Add(albumItem);
                _albums.Add(albumItem);
            }

            var artist = new MetadataItem
            {
                RatingKey = artistKey,
                Key = $"/library/metadata/{artistKey}/children",
                Type = "artist",
                Title = spec.Artist,
                TitleSort = SortTitle(spec.Artist),
                Thumb = photo,
                Art = backdrop,
                Summary = $"{spec.Artist} is a band invented for the Tuxflix demo, so music can be shown without anybody's recordings.",
                Genre = [Genre(spec.Genre)],
                Country = [new Tag { Id = TagId("country", spec.Country), TagText = spec.Country }],
                LibrarySectionId = sectionId,
                LibrarySectionTitle = "Music",
                AddedAt = Now.AddDays(-(next % 50)).ToUnixTimeSeconds(),
                ChildCount = albums.Count,
                UltraBlurColors = Blur(artistSeed),
                Stations = new StationList
                {
                    Size = 1,
                    Metadata = [Station($"/library/metadata/{artistKey}/station/{artistSeed:x8}?type=10", $"{spec.Artist} Radio")],
                },
            };
            _byKey[artistKey] = artist;
            _children[artistKey] = albums;
            _artists.Add(artist);
        }

        Stations =
        [
            Station($"/library/sections/{MusicSectionKey}/stations/1", "Library Radio"),
            Station($"/library/sections/{MusicSectionKey}/stations/8", "Deep Cuts Radio"),
            Station($"/library/sections/{MusicSectionKey}/stations/2", "Time Travel Radio"),
        ];

        return new DemoLibrary(section, type => type switch
        {
            "9" => _albums,
            "10" => _tracks,
            _ => _artists,
        });

        static MetadataItem Station(string key, string title) => new()
        {
            RatingKey = string.Empty,
            Key = key,
            Type = "playlist",
            Title = title,
            Radio = true,
            Smart = true,
            PlaylistType = "audio",
            LibrarySectionId = int.Parse(MusicSectionKey, CultureInfo.InvariantCulture),
        };
    }
}
