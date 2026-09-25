using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>The demo's photo library answers as a server's does: albums, photos with their camera details, and pictures scaled by the transcoder.</summary>
public sealed class PhotoLibraryTests
{
    private sealed class Art : IDemoArtRenderer
    {
        public List<DemoArtRequest> Requests { get; } = [];

        public DemoImage Render(DemoArtRequest request)
        {
            Requests.Add(request);
            return new DemoImage([0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg");
        }
    }

    [Fact]
    public async Task ThePhotoLibraryListsAlbumsOfPhotosWithTheirCameras()
    {
        var catalog = DemoCatalog.Create(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var art = new Art();
        var identity = new PlexClientIdentity("test-client", "0.0.0", "tests");
        var client = new PlexServerClient(identity.CreateHttpClient(new DemoPlexHandler(catalog, art)), DemoPlexHandler.BaseUri, null, "Demo", isDemo: true);
        var cancel = TestContext.Current.CancellationToken;

        var photos = Assert.Single(await client.GetSectionsAsync(cancel), s => s.Type == "photo");
        var albums = (await client.GetSectionItemsAsync(photos.Key, "titleSort", cancel)).Metadata!;
        Assert.Equal(4, albums.Count);

        // An album is a photo item whose key lists its children, as a server sends it.
        Assert.All(albums, a => Assert.True(a.Type == "photo" && a.Key!.EndsWith("/children", StringComparison.Ordinal)));
        var children = await client.GetChildrenAsync(albums[0].RatingKey, cancel);
        Assert.Equal(albums[0].LeafCount, children.Count);
        var photo = children[0];
        var media = photo.Media![0];
        Assert.False(string.IsNullOrEmpty(media.Make));
        Assert.False(string.IsNullOrEmpty(media.Model));
        Assert.NotNull(media.Iso);
        Assert.NotNull(photo.OriginallyAvailableAt);

        // The transcoder scales the photo's own file to the size asked for, a picture with nothing written on it.
        var bytes = await client.GetBytesAsync(client.ImageUri(media.Part![0].Key!, 1920, 1080), cancel);
        Assert.NotEmpty(bytes);
        var drawn = Assert.Single(art.Requests);
        Assert.Equal(DemoArtKind.Photo, drawn.Kind);
        Assert.Equal((1920, 1080), (drawn.Width, drawn.Height));
    }
}
