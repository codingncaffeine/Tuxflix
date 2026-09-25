using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views.Tiles;
using Tuxflix.Core;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The card beside a tile the pointer rests on: what it says for each kind of title, and when it
/// shows and goes. The pointer is the window's own, so these run alone.
/// </summary>
[Collection(nameof(KeyboardFocus))]
public sealed class HoverCardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "card-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SettingsStore _settings;
    private readonly ShellViewModel _shell;

    private readonly ITestOutputHelper _output;

    public HoverCardTests(ITestOutputHelper output)
    {
        _output = output;
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths) { NotificationSources = _ => new FakeNotifications(), Silent = true, ReportsPlayback = false };
        _shell.OpenDemo();
    }

    public void Dispose()
    {
        _shell.Session?.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    private ServerSession Session => _shell.Session!;

    private async Task<MetadataItem> Fresh(string key) => (await Session.Client.GetMetadataAsync(key, CancellationToken.None))!;

    [Fact]
    public async Task TheCardSaysWhatTheTitleIsWhatItIsAboutAndHowFarTheViewerGot()
    {
        var demo = Session.Demo!;

        // A film part-way through: its year, length, genres, rating, file, and the time left.
        var film = await Fresh(demo.Movies[2].RatingKey);
        var card = new HoverCardViewModel(new PosterTileViewModel(_shell, film));
        Assert.False(card.HasKicker);
        Assert.Equal(film.Title, card.Title);
        var length = TimeSpan.FromMilliseconds(film.Duration!.Value);
        Assert.StartsWith($"{film.Year}  ·  {(int)length.TotalHours}h {length.Minutes}m  ·  {film.Genre![0].TagText}, {film.Genre[1].TagText}  ·  {film.ContentRating}  ·  ★ ", card.Meta, StringComparison.Ordinal);
        Assert.Contains(film.Media![0].VideoResolution == "4k" ? "4K · HEVC" : "1080p · H.264", card.Quality, StringComparison.Ordinal);
        Assert.Equal(film.Summary, card.Summary);
        Assert.True(card.HasProgress);
        var left = TimeSpan.FromMilliseconds(film.Duration.Value - film.ViewOffset!.Value);
        Assert.Equal(left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m left" : $"{Math.Max(1, (int)Math.Round(left.TotalMinutes))}m left", card.Status);

        // A film seen: watched, and when.
        var seen = await Fresh(demo.Movies[1].RatingKey);
        Assert.StartsWith("Watched · ", new HoverCardViewModel(new PosterTileViewModel(_shell, seen)).Status, StringComparison.Ordinal);

        // An episode: its series above, its code with its title, its own still.
        var show = await Fresh(demo.Shows[3].RatingKey);
        var season = (await Session.Client.GetChildrenAsync(show.RatingKey, CancellationToken.None))[0];
        var episode = (await Session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None))[0];
        var episodeCard = new HoverCardViewModel(new LandscapeTileViewModel(_shell, episode));
        Assert.Equal(show.Title.ToUpperInvariant(), episodeCard.Kicker);
        Assert.Equal($"S{episode.ParentIndex} · E{episode.Index} · {episode.Title}", episodeCard.Title);
        Assert.Equal(Artwork.Still(episode), episodeCard.ArtPath);
        Assert.DoesNotContain(episode.Year?.ToString(CultureInfo.InvariantCulture) ?? "never", episodeCard.Meta, StringComparison.Ordinal);

        // A series: how many seasons, no file of its own; started, what is left of it.
        var showTile = new PosterTileViewModel(_shell, show);
        var showCard = new HoverCardViewModel(showTile);
        Assert.Contains(show.ChildCount == 1 ? "1 season" : $"{show.ChildCount} seasons", showCard.Meta, StringComparison.Ordinal);
        Assert.Empty(showCard.Quality);
        if (showTile.State.ViewedLeafCount is null or 0) Assert.True(await _shell.Viewer!.SetWatchedAsync(episode, watched: true));
        var toWatch = showTile.State.UnwatchedLeaves;
        Assert.InRange(toWatch, 1, int.MaxValue);
        Assert.Equal(toWatch == 1 ? "1 episode to watch" : $"{toWatch} episodes to watch", showCard.Status);
    }

    [Fact]
    public Task TheCardShowsAfterThePointerRestsSoonerBetweenTilesAndGoesOnAPress() => HeadlessApp.Run(async () =>
    {
        var demo = Session.Demo!;
        var first = new PosterTile { DataContext = new PosterTileViewModel(_shell, await Fresh(demo.Movies[4].RatingKey)) };
        var second = new PosterTile { DataContext = new PosterTileViewModel(_shell, await Fresh(demo.Movies[5].RatingKey)) };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18, Margin = new Thickness(20), Children = { first, second } };
        var window = new Window { Width = 1000, Height = 600, Content = row };
        window.Show();
        try
        {
            window.UpdateLayout();
            Button ButtonOf(PosterTile tile) => tile.GetLogicalChildren().OfType<Button>().Single();
            Point Middle(PosterTile tile) => tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), window)!.Value;

            // Timed from before the pointer moves, so the move's own work counts as waiting.
            async Task<TimeSpan> MoveUntil(PosterTile tile, Action? then = null)
            {
                var clock = Stopwatch.StartNew();
                window.MouseMove(Middle(tile));
                then?.Invoke();
                bool Shown() => HoverCard.ShowingAt is { } at && ReferenceEquals(at, ButtonOf(tile));
                while (!Shown() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5, TestContext.Current.CancellationToken);
                Assert.True(Shown(), "The card did not show within three seconds.");
                return clock.Elapsed;
            }

            // The first card makes its view and compiles its code: shown once, uncounted, then
            // the pointer rests away from the tiles long enough for the next card to wait in full.
            await MoveUntil(first);
            window.MouseMove(new Point(5, 5));
            await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Resting on a tile: nothing before the delay, the card for that tile after it.
            var rest = await MoveUntil(first);
            Assert.Equal(((PosterTileViewModel)first.DataContext!).Title, HoverCard.Showing!.Title);

            // On to the next tile while the card is up: the card goes at once, and comes back
            // there after the short delay.
            var between = await MoveUntil(second, () => Assert.Null(HoverCard.Showing));
            _output.WriteLine($"rest {rest.TotalMilliseconds:0} ms, between {between.TotalMilliseconds:0} ms");
            Assert.InRange(rest.TotalMilliseconds, HoverCard.ShowDelay.TotalMilliseconds - 5, HoverCard.ShowDelay.TotalMilliseconds + 250);
            Assert.InRange(between.TotalMilliseconds, HoverCard.BetweenDelay.TotalMilliseconds - 5, HoverCard.BetweenDelay.TotalMilliseconds + 250);

            // A press is a choice made: the card goes, and stays gone while the pointer stays,
            // even when the end of the press reports the pointer leaving the tile and coming back
            // without moving (raised here as the platform can raise it).
            window.MouseDown(Middle(second), MouseButton.Left);
            window.MouseUp(Middle(second), MouseButton.Left);
            Assert.Null(HoverCard.Showing);
            var pressed = ButtonOf(second);
            window.MouseMove(Middle(second) + new Vector(3, 3));
            Assert.True(pressed.IsPointerOver);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            foreach (var crossing in new[] { InputElement.PointerExitedEvent, InputElement.PointerEnteredEvent })
            {
                pressed.RaiseEvent(new PointerEventArgs(crossing, pressed, pointer, window, Middle(second), 0, PointerPointProperties.None, KeyModifiers.None));
            }

            await Task.Delay(HoverCard.ShowDelay + TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            Assert.Null(HoverCard.Showing);

            // A scroll over the tile, or a key anywhere in the window: the card goes too. Each is
            // raised where the card listens for it (the tile, the window), so no other way the
            // platform may close a popup stands in for it.
            await MoveUntil(first);
            var tile = ButtonOf(first);
            tile.RaiseEvent(new PointerWheelEventArgs(tile, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window, Middle(first), 0,
                PointerPointProperties.None, KeyModifiers.None, new Vector(0, -1)));
            Assert.Null(HoverCard.Showing);
            await MoveUntil(second);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = window });
            Assert.Null(HoverCard.Showing);

            // The TV interface has no pointer to rest: no card.
            window.MouseMove(new Point(5, 5));
            _shell.IsTv = true;
            window.MouseMove(Middle(first));
            await Task.Delay(HoverCard.ShowDelay + TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            Assert.Null(HoverCard.Showing);
        }
        finally
        {
            _shell.IsTv = false;
            window.Close();
        }
    });
}
