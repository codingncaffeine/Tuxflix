using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The demo's photo library: invented albums of pictures drawn in code, with the dates, cameras and
// settings a photo's file carries.
public sealed partial class DemoCatalog
{
    public const string PhotosSectionKey = "41";

    private const int PhotoIds = 41000;

    private static readonly bool PhotosRegistered = AddLibrary(catalog => catalog.BuildPhotos());

    private static readonly (string Album, int Year, int Month, int Count)[] PhotoSpecs =
    [
        ("Coastline Walks", 2025, 6, 9),
        ("Northern Lights", 2024, 2, 7),
        ("A Year in the Garden", 2023, 4, 12),
        ("City After Dark", 2025, 11, 8),
    ];

    private static readonly (string Make, string Model, string Lens)[] Cameras =
    [
        ("Fujifilm", "X-T5", "XF 23mm F1.4 R LM WR"),
        ("Sony", "ILCE-7M4", "FE 24-70mm F2.8 GM II"),
        ("Google", "Pixel 9 Pro", "Main camera"),
    ];

    private static readonly string[] Apertures = ["1.4", "2.8", "4", "5.6", "8"];

    private static readonly string[] Exposures = ["1/2000", "1/500", "1/125", "1/30", "4"];

    private readonly List<MetadataItem> _photoAlbums = [];

    /// <summary>The photo library's albums, each with its photos as children.</summary>
    public IReadOnlyList<MetadataItem> PhotoAlbums => _photoAlbums;

    private DemoLibrary BuildPhotos()
    {
        var section = new LibraryDirectory { Key = PhotosSectionKey, Type = "photo", Title = "Photos", Agent = "com.plexapp.agents.none", Language = "xn", Uuid = "demo-photos" };
        var sectionId = int.Parse(PhotosSectionKey, CultureInfo.InvariantCulture);
        var next = PhotoIds;
        foreach (var spec in PhotoSpecs)
        {
            var albumKey = (++next).ToString(CultureInfo.InvariantCulture);
            var photos = new List<MetadataItem>();
            for (var i = 0; i < spec.Count; i++)
            {
                var key = (++next).ToString(CultureInfo.InvariantCulture);
                var seed = Seed($"{spec.Album} {i}");
                var camera = Cameras[seed % Cameras.Length];
                var portrait = seed % 5 == 0;
                var (width, height) = portrait ? (3000, 4000) : (6000, 4000);
                var taken = new DateTime(spec.Year, spec.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i * 2 + (seed % 2)).AddMinutes(seed % 900);
                var thumb = $"/demo/photo/{key}/thumb/1700000000";
                var file = $"/demo/photo/{key}/file.jpg";
                var title = $"{spec.Album} {i + 1}";
                _art[thumb] = (title, null, seed);
                _art[file] = (title, null, seed);
                var photo = new MetadataItem
                {
                    RatingKey = key,
                    Key = $"/library/metadata/{key}",
                    Type = "photo",
                    Title = $"IMG_{1000 + (seed % 9000)}",
                    ParentRatingKey = albumKey,
                    ParentTitle = spec.Album,
                    Thumb = thumb,
                    Year = spec.Year,
                    Index = i + 1,
                    OriginallyAvailableAt = taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    AddedAt = Now.AddDays(-(next % 30)).ToUnixTimeSeconds(),
                    LibrarySectionId = sectionId,
                    LibrarySectionTitle = "Photos",
                    Media =
                    [
                        new()
                        {
                            Id = long.Parse(key, CultureInfo.InvariantCulture) * 10, Width = width, Height = height, Container = "jpeg",
                            AspectRatio = Math.Round(width / (double)height, 2),
                            Make = camera.Make, Model = camera.Model, Lens = camera.Lens,
                            Aperture = Apertures[seed % Apertures.Length], Exposure = Exposures[(seed / 7) % Exposures.Length], Iso = 100 << (seed % 5),
                            Part = [new() { Id = (long.Parse(key, CultureInfo.InvariantCulture) * 10) + 1, Key = file, Container = "jpeg", Size = 4_000_000 + (seed % 3_000_000) }],
                        },
                    ],
                };
                _byKey[key] = photo;
                photos.Add(photo);
            }

            // An album is a photo item whose key lists its children, as a server sends it.
            var album = new MetadataItem
            {
                RatingKey = albumKey,
                Key = $"/library/metadata/{albumKey}/children",
                Type = "photo",
                Title = spec.Album,
                Thumb = photos[0].Thumb,
                Year = spec.Year,
                LeafCount = photos.Count,
                ChildCount = photos.Count,
                AddedAt = Now.AddDays(-(next % 30)).ToUnixTimeSeconds(),
                LibrarySectionId = sectionId,
                LibrarySectionTitle = "Photos",
            };
            _byKey[albumKey] = album;
            _children[albumKey] = photos;
            _photoAlbums.Add(album);
        }

        return new DemoLibrary(section, _ => _photoAlbums);
    }
}
