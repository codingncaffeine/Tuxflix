using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// What the seek bar shows under the pointer, against the demo server, which serves each film's
/// previews as a BIF file and names its chapters.
/// </summary>
public sealed class SeekPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "preview-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ServerSession _session;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public SeekPreviewTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        var shell = new ShellViewModel(SettingsStore.Load(paths.SettingsFile), paths);
        _session = ServerSession.CreateDemo(shell.Identity);
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TheBarShowsTheChapterTheTimeAndTheServersPicture()
    {
        var film = await _session.Client.GetMetadataAsync(_catalog.Movies[0].RatingKey, TestContext.Current.CancellationToken);
        var part = film!.Media![0].Part![0];
        using var previews = new SeekPreviews(_session.Client, part, film.Chapter!);
        var seconds = (film.Chapter![2].StartTimeOffset / 1000.0) + 90;

        previews.Show(seconds);

        Assert.True(previews.IsShown);
        Assert.True(previews.HasPictures);
        Assert.Equal("The Plan", previews.ChapterTitle);
        Assert.Equal(Format.Clock(seconds), previews.Time);
        await Until(() => previews.Picture is not null);
        var first = previews.Picture!;
        Assert.Equal(SeekPreviews.PictureWidth, first.PixelSize.Width);

        // Three pictures on is another picture; coming back finds the first one kept.
        previews.Show(seconds + (film.Duration!.Value / 1000.0 / 60 * 3));
        await Until(() => previews.Picture is not null && !ReferenceEquals(previews.Picture, first));
        previews.Show(seconds);
        Assert.Same(first, previews.Picture);

        previews.Hide();
        Assert.False(previews.IsShown);
    }

    [Fact]
    public void WithoutNamedChaptersOrPicturesTheTimeShowsAlone()
    {
        using var previews = new SeekPreviews(_session.Client, new MediaPart { Id = 1 }, [new Chapter { Index = 1, StartTimeOffset = 0 }]);

        previews.Show(75);

        Assert.Equal(("1:15", null, false, null), (previews.Time, previews.ChapterTitle, previews.HasPictures, previews.Picture));
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "not within 20 s");
    }
}
