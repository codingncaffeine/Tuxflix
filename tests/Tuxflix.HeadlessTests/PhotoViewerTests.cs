using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>Photos one at a time: moving through them, a slideshow's order, and a photo's details.</summary>
public sealed class PhotoViewerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "photos-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellViewModel _shell;
    private readonly SettingsStore _settings;
    private readonly ServerSession _session;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public PhotoViewerTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths);
        _session = ServerSession.CreateDemo(_shell.Identity);
    }

    public void Dispose()
    {
        _session.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public void ASlideshowShowsEveryPhotoOnceARoundFromTheOneShowing()
    {
        var onwards = new SlideshowOrder(5, 3, shuffle: false);
        Assert.Equal([4, 0, 1, 2, 3, 4], Enumerable.Range(0, 6).Select(_ => onwards.Next()));

        var shuffled = new SlideshowOrder(9, 4, shuffle: true, new Random(3));
        var round = Enumerable.Range(0, 9).Select(_ => shuffled.Next()).ToList();
        Assert.Equal(4, round[^1]);
        Assert.Equal(Enumerable.Range(0, 9), round.Order());
        Assert.NotEqual([5, 6, 7, 8, 0, 1, 2, 3, 4], round);
    }

    [Fact]
    public void APhotosDetailsReadAsAPhotographerSaysThem()
    {
        var photo = new MetadataItem
        {
            RatingKey = "1",
            Type = "photo",
            Title = "IMG_1",
            ParentTitle = "Coast",
            OriginallyAvailableAt = "2025-06-03 14:05:00",
            Media = [new() { Make = "Canon", Model = "Canon EOS R5", Lens = "RF 50mm", Aperture = "2.8", Exposure = "1/250", Iso = 400, Width = 8192, Height = 5464 }],
        };
        Assert.Equal(
            [("Taken", "3 June 2025, 14:05"), ("Album", "Coast"), ("Camera", "Canon EOS R5"), ("Lens", "RF 50mm"), ("Settings", "f/2.8   1/250 s   ISO 400"), ("Size", "8192 by 5464 pixels")],
            PhotoInfo.For(photo).Select(r => (r.Label, r.Value)));
        Assert.Equal("Fujifilm X-T5", PhotoInfo.Camera("Fujifilm", "X-T5"));
        Assert.Null(PhotoInfo.Aperture("0"));
        Assert.Empty(PhotoInfo.For(new MetadataItem { RatingKey = "2", Type = "photo", Title = "Bare" }));
    }

    [Fact]
    public async Task TheViewerMovesThroughAnAlbumAndStopsAtItsEnds()
    {
        var album = _catalog.PhotoAlbums[1];
        var photos = await _session.Client.GetChildrenAsync(album.RatingKey, TestContext.Current.CancellationToken);
        Assert.True(PhotoItems.IsAlbum(album));
        Assert.False(PhotoItems.IsAlbum(photos[0]));

        var viewer = new PhotoViewerPageViewModel(_shell, _session, photos, 0);
        Assert.True(viewer.IsImmersive);
        Assert.False(viewer.PreviousCommand.CanExecute(null));
        Assert.Equal($"1 of {photos.Count}", viewer.PositionText);
        Assert.Equal(PhotoItems.Picture(photos[0]), viewer.Picture);
        Assert.StartsWith("/demo/photo/", viewer.Picture, StringComparison.Ordinal);

        for (var i = 1; i < photos.Count; i++) viewer.NextCommand.Execute(null);
        Assert.Equal(photos.Count - 1, viewer.Index);
        Assert.False(viewer.NextCommand.CanExecute(null));
        viewer.PreviousCommand.Execute(null);
        Assert.Equal(photos.Count - 2, viewer.Index);

        // The slideshow's step goes round from the last photo to the first.
        viewer.NextCommand.Execute(null);
        viewer.Advance();
        Assert.Equal(0, viewer.Index);
        Assert.Contains(viewer.Info, r => r.Label == "Camera");
    }
}
