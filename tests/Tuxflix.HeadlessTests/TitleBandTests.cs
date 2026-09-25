using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views.Pages;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The pages that fill the window hide its title bar, so a band along their top stands in for it:
/// a press there moves the window and a double click maximises it, while the rest of the page keeps
/// its own presses. The headless window cannot move, so a double click, which maximises, is what
/// shows the band doing the title bar's work. The pointer is the window's own, so these run alone.
/// </summary>
[Collection(nameof(KeyboardFocus))]
public sealed class TitleBandTests
{
    public TitleBandTests() => HeadlessApp.Ensure();

    [Fact]
    public Task TheVisualizersTopMovesTheWindowWhileTheRestOfThePictureChangesTheMode() => HeadlessApp.Run(async () =>
    {
        using var app = await TvHarness.OpenHomeAsync(tv: false);
        var window = app.Window;
        app.Shell.ShowNowPlayingCommand.Execute(null);
        var now = Assert.IsType<NowPlayingPageViewModel>(app.Shell.Router.Current);
        now.SetVisualizer(true);
        Border? Shown() => window.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "VisualizerTitleBand" && b.IsEffectivelyVisible);
        await app.UntilAsync(() => Shown()?.TranslatePoint(default, window) is { Y: < 1 }, "the visualizer filling the window");
        Border Band() => Shown()!;

        // Presses at the top, away from the controls in its corner, are the window's: the mode stays,
        // and the second of two quick ones maximises the window and then puts it back.
        var mode = now.VisualizerMode;
        var top = new Point(window.Bounds.Width / 2, 40);
        Click(top);
        Assert.Equal(mode, now.VisualizerMode);
        DoubleClick(top + new Vector(200, 0));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        DoubleClick(top - new Vector(200, 0));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(mode, now.VisualizerMode);

        // Below the band the picture keeps its own press: the next mode.
        Click(new Point(window.Bounds.Width / 2, window.Bounds.Height / 2));
        Assert.NotEqual(mode, now.VisualizerMode);

        // Full screen has no window to move: there the band is picture like the rest.
        var before = now.VisualizerMode;
        window.WindowState = WindowState.FullScreen;
        app.Pump();
        Click(top - new Vector(200, 0));
        Assert.NotEqual(before, now.VisualizerMode);
        window.WindowState = WindowState.Normal;
        app.Pump();

        // The controls over the band keep their presses: its close button closes the visualizer.
        var close = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, now.ToggleVisualizerCommand));
        Assert.True(close.TranslatePoint(default, Band()) is { Y: < 120 }, "the close button is not over the band");
        Click(close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value);
        Assert.False(now.IsVisualizerOn);

        void Click(Point at)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            app.Pump();
        }

        // The headless window draws itself ten times around every press it is given, and here each
        // drawing holds a frame of the visualizer made in software, so two presses given one after
        // the other arrive further apart than a double click's time. The second is raised as the
        // platform counts it, the second of two; the photo's test below makes real double clicks.
        void DoubleClick(Point at)
        {
            Click(at);
            var target = (Interactive)window.InputHitTest(at)!;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var left = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, at, 0, left, KeyModifiers.None, clickCount: 2));
            app.Pump();
        }
    });

    [Fact]
    public Task ThePhotosTopMovesTheWindowAndItsDoubleClickIsNotAZoom() => HeadlessApp.Run(async () =>
    {
        using var app = await TvHarness.OpenHomeAsync(tv: false);
        var window = app.Window;
        var session = app.Shell.Session!;
        var album = session.Demo!.PhotoAlbums[1];
        var photos = await session.Client.GetChildrenAsync(album.RatingKey, TestContext.Current.CancellationToken);
        app.Shell.OpenItem(photos[0]);
        PhotoViewerPage Page() => window.GetVisualDescendants().OfType<PhotoViewerPage>().Single();
        await app.UntilAsync(() => window.GetVisualDescendants().OfType<PhotoViewerPage>().Any(p => p.IsEffectivelyVisible && p.Bounds.Height > 900), "the photo filling the window");

        // Two quick presses at the top maximise the window and leave the photo as it was; on the
        // photo's name, as on a title bar's, they put it back.
        var top = new Point(window.Bounds.Width / 2, 40);
        Click(top);
        Click(top);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Equal(1, Page().Zoom);
        var viewer = (PhotoViewerPageViewModel)app.Shell.Router.Current!;
        var name = Page().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == viewer.Heading);
        var onName = name.TranslatePoint(new Point(Math.Min(20, name.Bounds.Width / 2), name.Bounds.Height / 2), window)!.Value;
        Click(onName);
        Click(onName);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(1, Page().Zoom);
        Click(top);
        Click(top);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        // The same two presses on the photo below zoom it, as they always have.
        var middle = new Point(window.Bounds.Width / 3, window.Bounds.Height / 2);
        Click(middle);
        Click(middle);
        Assert.True(Page().Zoom > 1, $"a double click on the photo left it at {Page().Zoom}");
        Assert.Equal(WindowState.Maximized, window.WindowState);

        void Click(Point at)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            app.Pump();
        }
    });
}
