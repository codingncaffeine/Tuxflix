using System.Diagnostics;
using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tuxflix.App;
using Tuxflix.App.Controls;
using Tuxflix.App.Tv;
using Tuxflix.App.Tv.Pages;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views.Pages;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Settings;
using Tuxflix.Player;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The TV interface on the demo library, driven as a viewer would: the arrow keys and a
/// controller move focus across and between shelves, a shelf keeps its place, a title opens with
/// PLAY in focus and back returns to the tile, the on-screen keyboard types a search, the map
/// editor takes a pressed button and saves it, and the controller drives the player.
/// </summary>
public sealed class TvInterfaceTests
{
    public TvInterfaceTests() => HeadlessApp.Ensure();

    [Fact]
    public Task TheArrowsMoveAlongAShelfAndDownAndAShelfKeepsItsPlace() => HeadlessApp.Run(async () =>
    {
        using var tv = await TvHarness.OpenHomeAsync();
        var home = (HomePageViewModel)tv.Shell.Router.Current!;
        var first = home.Shelves[0].Tiles;

        // Where focus is, said in full when it is not where it should be.
        void FocusIs(Func<object?, bool> expected, string step)
        {
            var focused = tv.Focused?.DataContext;
            var shelf = home.Shelves.Select((s, i) => (s, i)).FirstOrDefault(p => focused is not null && p.s.Tiles.Contains(focused)) is { s: not null } found ? found.i : -1;
            Assert.True(expected(focused), $"{step}: focus on {TvHarness.Describe(tv.Focused)} (shelf {shelf} of {home.Shelves.Count}, the first shelf's tiles still the same: {ReferenceEquals(first, home.Shelves[0].Tiles)}), the page {tv.Shell.Router.Current?.GetType().Name}");
        }

        FocusIs(f => ReferenceEquals(f, first[0]), "at the start");
        tv.Key(Key.Right);
        FocusIs(f => ReferenceEquals(f, first[1]), "one right");
        tv.Key(Key.Right);
        FocusIs(f => ReferenceEquals(f, first[2]), "two right");

        tv.Key(Key.Down);
        FocusIs(f => f is not null && home.Shelves[1].Tiles.Contains(f), "down");

        // Along the second shelf until another tile of the first stands nearest above...
        for (var i = 0; i < 3; i++) tv.Key(Key.Right);
        FocusIs(f => f is not null && home.Shelves[1].Tiles.Contains(f), "along the second shelf");

        // ...and back up, focus returns to the tile it left, not the one nearest.
        tv.Key(Key.Up);
        FocusIs(f => ReferenceEquals(f, first[2]), "back up");

        // Up again reaches the tabs.
        tv.Key(Key.Up);
        FocusIs(f => f is TvTabViewModel, "up to the tabs");
    });

    [Fact]
    public Task AShelfHoldsFocusAtItsEndsAndTheTabBarLetsItGoOn() => HeadlessApp.Run(async () =>
    {
        using var tv = await TvHarness.OpenHomeAsync();
        var home = (HomePageViewModel)tv.Shell.Router.Current!;
        tv.Key(Key.Down);
        var shelf = home.Shelves[1].Tiles;
        Assert.Contains(tv.Focused!.DataContext, shelf);

        // Past the last tile focus stays on it, past the first on the first: a tile of another
        // shelf further along never takes it.
        for (var i = 0; i < shelf.Count + 2; i++) tv.Key(Key.Right);
        Assert.Same(shelf[^1], tv.Focused!.DataContext);
        for (var i = 0; i < shelf.Count + 2; i++) tv.Key(Key.Left);
        Assert.Same(shelf[0], tv.Focused!.DataContext);

        // The tabs are a bar, not a shelf: past the last one, focus goes on to what stands beside them.
        tv.Key(Key.Up);
        tv.Key(Key.Up);
        Assert.IsType<TvTabViewModel>(tv.Focused!.DataContext);
        var tabs = tv.Frame.FindControl<ItemsControl>("Tabs")!.ItemCount;
        for (var i = 0; i < tabs; i++) tv.Key(Key.Right);
        Assert.IsNotType<TvTabViewModel>(tv.Focused!.DataContext);
    });

    [Fact]
    public Task SelectOpensATitleWithPlayInFocusAndBackReturnsToTheTile() => HeadlessApp.Run(async () =>
    {
        using var tv = await TvHarness.OpenHomeAsync();
        tv.Key(Key.Right);
        var tile = (MediaTileViewModel)tv.Focused!.DataContext!;

        tv.Key(Key.Enter);
        await tv.UntilAsync(() => tv.Shell.Router.Current is ItemPageViewModel { IsLoading: false }, "the title's page");
        await tv.UntilAsync(() => tv.Focused?.Name == "PlayButton", "PLAY in focus");

        tv.Key(Key.Escape);
        await tv.UntilAsync(() => tv.Shell.Router.Current is HomePageViewModel, "home again");
        // Home loads again on the way back, with new tiles: focus finds the same title among them.
        await tv.UntilAsync(() => tv.Focused?.DataContext is MediaTileViewModel back && back.Item.RatingKey == tile.Item.RatingKey && back.GetType() == tile.GetType(), "the same title in focus");
    });

    [Fact]
    public Task TheMenuButtonOnATitleOpensItsOptionsAndBackComesOutALevelAtATime() => HeadlessApp.Run(async () =>
    {
        using var tv = await TvHarness.OpenHomeAsync();
        var frame = tv.Frame;
        var tile = Assert.IsAssignableFrom<MediaTileViewModel>(tv.Focused!.DataContext);
        await tv.UntilAsync(() => tv.Shell.Viewer!.Playlists.Count > 0, "the playlists");
        Assert.Equal("OPTIONS", frame.Model!.MenuLegend);
        string? Line() => (tv.Focused as Button)?.Tag is MenuEntry entry ? entry.Header : null;

        // The rows are the menu's own lines, and a room's menu has no line that needs a desktop:
        // no name to type, no panel beside the tile.
        tv.Act(TvAction.Menu);
        Assert.True(frame.IsOptionsOpen);
        Assert.False(frame.IsMenuOpen);
        await tv.UntilAsync(() => Line() is not null, "focus on the first option");
        var menu = ViewerMenu.Entries(tile, anchor: null);
        var lines = menu.Where(e => e.Header is not null).Select(e => e.Header).ToList();
        Assert.Equal(lines, frame.OptionButtons.Select(b => ((MenuEntry)b.Tag!).Header));
        Assert.Equal(lines[0], Line());
        Assert.DoesNotContain("Media info", lines);
        Assert.DoesNotContain("New playlist…", menu.Single(e => e.Header == "Add to playlist").Children!.Select(e => e.Header));
        Assert.Equal(tile.Item.Type == "episode" ? $"{tile.Item.GrandparentTitle} · {tile.Item.Title}" : tile.Item.Title, frame.OptionsHeading);

        // In and out of a line with more under it.
        while (Line() != "Rate") tv.Act(TvAction.Down);
        tv.Act(TvAction.Select);
        Assert.Equal("Rate", frame.OptionsHeading);
        await tv.UntilAsync(() => Line() == ((MenuEntry)frame.OptionButtons[0].Tag!).Header, "focus on the first star");
        Assert.Contains("5 stars", frame.OptionButtons.Select(b => ((MenuEntry)b.Tag!).Header));
        tv.Act(TvAction.Back);
        Assert.True(frame.IsOptionsOpen);
        Assert.NotEqual("Rate", frame.OptionsHeading);
        tv.Act(TvAction.Back);
        Assert.False(frame.IsOptionsOpen);
        await tv.UntilAsync(() => ReferenceEquals(tv.Focused?.DataContext, tile), "focus back on the title");

        // A line that acts closes the options first, then acts.
        var watched = tile.State.IsWatched;
        tv.Act(TvAction.Menu);
        await tv.UntilAsync(() => Line() is not null, "focus in the options");
        while (Line()?.StartsWith("Mark as", StringComparison.Ordinal) != true) tv.Act(TvAction.Down);
        tv.Act(TvAction.Select);
        Assert.False(frame.IsOptionsOpen);
        await tv.UntilAsync(() => tile.State.IsWatched != watched && tile.State.Pending == 0, "the title marked");

        // A right click opens the same options, and never the desktop's small menu under them.
        var button = tv.Window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, tile));
        button.RaiseEvent(new ContextRequestedEventArgs());
        Assert.True(frame.IsOptionsOpen);
        Assert.False(button.ContextFlyout?.IsOpen ?? false);
        tv.Act(TvAction.Back);

        // Up on the tabs nothing has options: the button is the menu, and a request there passes
        // on untouched to whatever menu the control has of its own.
        for (var i = 0; i < 6 && tv.Focused?.DataContext is not TvTabViewModel; i++) tv.Act(TvAction.Up);
        Assert.IsType<TvTabViewModel>(tv.Focused?.DataContext);
        Assert.Equal("MENU", frame.Model.MenuLegend);
        var request = new ContextRequestedEventArgs();
        tv.Focused!.RaiseEvent(request);
        Assert.False(request.Handled);
        Assert.False(frame.IsOptionsOpen);
        tv.Act(TvAction.Menu);
        Assert.True(frame.IsMenuOpen);
        Assert.False(frame.IsOptionsOpen);
    });

    [Fact]
    public Task TheOnScreenKeyboardTypesASearchAndTheResultsFollow() => HeadlessApp.Run(async () =>
    {
        using var tv = await TvHarness.OpenHomeAsync();
        var film = DemoCatalog.Create(DateTimeOffset.Now).Movies[0].Title;
        var word = film.Split(' ').First(w => w.Length > 3).ToLowerInvariant();

        tv.Act(TvAction.Search);
        await tv.UntilAsync(() => tv.Focused is Button { Tag: "a" }, "focus on the keyboard's a");
        var keys = tv.Find<TvSearchPage>().Keys;

        // The keys are a grid of seven: right is b, down is h.
        tv.Key(Key.Right);
        Assert.Equal("b", (tv.Focused as Button)?.Tag);
        tv.Key(Key.Down);
        Assert.Equal("i", (tv.Focused as Button)?.Tag);

        foreach (var letter in word)
        {
            tv.Window.Tv!.Focus.FocusOn(keys.KeyFor(letter.ToString()) ?? throw new InvalidOperationException($"No key for {letter}."));
            tv.Act(TvAction.Select);
        }

        Assert.Equal(word, tv.Shell.SearchText);

        // X takes the last letter away, Y types a space.
        tv.Act(TvAction.Play);
        Assert.Equal(word[..^1], tv.Shell.SearchText);
        tv.Act(TvAction.Search);
        Assert.Equal(word[..^1] + " ", tv.Shell.SearchText);
        tv.Act(TvAction.Play);
        tv.Window.Tv!.Focus.FocusOn(keys.KeyFor(word[^1].ToString())!);
        tv.Act(TvAction.Select);

        var search = (SearchPageViewModel)tv.Shell.Router.Current!;
        await tv.UntilAsync(() => search.Query == word && !search.IsSearching && search.Shelves.Count > 0, "results for " + word);
        Assert.Contains(search.Shelves.SelectMany(s => s.Tiles).OfType<MediaTileViewModel>(), t => t.Item.Title == film);
    });

    [Fact]
    public Task TheMapEditorTakesThePressedButtonAndSavesIt() => HeadlessApp.Run(async () =>
    {
        var pad = new FakePadSource();
        using var tv = await TvHarness.OpenHomeAsync(pad: pad);
        await tv.UntilAsync(() => pad.Opened, "the controller thread");

        tv.Shell.ShowControllerCommand.Execute(null);
        var editor = (TvControllerPageViewModel)tv.Shell.Router.Current!;
        var search = editor.Rows.Single(r => r.Action == TvAction.Search);
        search.AssignCommand.Execute(null);
        Assert.True(search.IsWaiting);

        pad.Push(PadEvent.Down(PadButton.RightStick));
        pad.Push(PadEvent.Up(PadButton.RightStick));
        await tv.UntilAsync(() => !search.IsWaiting, "the press to be taken");

        Assert.Equal(TvAction.Search, tv.Shell.Gamepads!.Map.ActionFor(PadButton.RightStick));
        Assert.Contains("Right stick press", search.Buttons, StringComparison.Ordinal);
        Assert.Equal("Search", tv.Shell.Settings.Tv.Gamepad["RightStick"]);
        Assert.True(tv.Settings.Flush(TimeSpan.FromSeconds(5)));
        Assert.Contains("\"RightStick\": \"Search\"", await File.ReadAllTextAsync(tv.Paths.SettingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // The new button works at once: it opens search.
        pad.Push(PadEvent.Down(PadButton.RightStick));
        pad.Push(PadEvent.Up(PadButton.RightStick));
        await tv.UntilAsync(() => tv.Shell.Router.Current is SearchPageViewModel, "search from the new button");
    });

    [Fact]
    public Task TheControllerDrivesThePlayer()
    {
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed here.");
        return HeadlessApp.Run(Drive);
    }

    private static async Task Drive()
    {
        using var tv = await TvHarness.OpenHomeAsync();
        tv.Shell.Play(DemoCatalog.Create(DateTimeOffset.Now).Movies[0], resume: false);
        await tv.UntilAsync(() => tv.Shell.Router.Current is PlayerPageViewModel, "the player");
        var model = (PlayerPageViewModel)tv.Shell.Router.Current!;
        await tv.UntilAsync(() => tv.Window.GetVisualDescendants().OfType<PlayerPage>().Any(p => p.DataContext == model), "the player's page");
        var page = tv.Window.GetVisualDescendants().OfType<PlayerPage>().First(p => p.DataContext == model);

        // The controls hide; up brings them back with play in focus.
        tv.Act(TvAction.Back);
        Assert.False(page.ControlsShown);
        tv.Act(TvAction.Up);
        Assert.True(page.ControlsShown);
        Assert.Same(model.TogglePauseCommand, (tv.Focused as Button)?.Command);

        // A held trigger scrubs forward and the seek waits for it to be let go.
        tv.Act(TvAction.SeekForward, TvPhase.Press, 1);
        tv.Act(TvAction.SeekForward, TvPhase.Repeat, 1);
        Assert.True(model.IsScrubbing);
        Assert.True(page.ScrubTarget >= 20, $"scrubbed to {page.ScrubTarget}");
        Assert.Equal(page.ScrubTarget, model.SeekValue);
        tv.Act(TvAction.SeekForward, TvPhase.Release, 0);
        Assert.True(model.IsScrubbing);
        await tv.UntilAsync(() => !model.IsScrubbing, "the seek after letting go", 3);

        // B hides the controls, then stops.
        tv.Act(TvAction.Back);
        Assert.False(page.ControlsShown);
        Assert.Same(model, tv.Shell.Router.Current);
        tv.Act(TvAction.Back);
        await tv.UntilAsync(() => tv.Shell.Router.Current is HomePageViewModel, "home after stopping");
    }

    [Fact]
    public Task TheSwitchAndTheMenuMoveBetweenTheInterfaces() => HeadlessApp.Run(async () =>
    {
        Assert.True(LaunchOptions.Parse(["--demo", "--tv"]).Tv);
        Assert.False(LaunchOptions.Parse(["--demo"]).Tv);

        using var tv = await TvHarness.OpenHomeAsync();
        Assert.True(tv.Window.IsTvShowing);
        Assert.Equal(Avalonia.Controls.WindowState.FullScreen, tv.Window.WindowState);

        // Start on a title opens its options; up on the tabs, where nothing has options, it opens
        // the menu, whose last-but-one row goes back to the desktop.
        tv.Act(TvAction.Menu);
        Assert.True(tv.Frame.IsOptionsOpen);
        tv.Act(TvAction.Back);
        for (var i = 0; i < 6 && tv.Focused?.DataContext is not TvTabViewModel; i++) tv.Act(TvAction.Up);
        tv.Act(TvAction.Menu);
        Assert.True(tv.Frame.IsMenuOpen);
        tv.Act(TvAction.Back);
        Assert.False(tv.Frame.IsMenuOpen);

        tv.Shell.ToggleTvCommand.Execute(null);
        tv.Pump();
        Assert.False(tv.Window.IsTvShowing);
        Assert.NotEqual(Avalonia.Controls.WindowState.FullScreen, tv.Window.WindowState);

        // From the desktop, the controller's Guide button opens the TV interface, as Steam's does.
        tv.Act(TvAction.Home);
        Assert.True(tv.Window.IsTvShowing);
    });

    [Fact]
    public async Task TheTvSignInShowsACodeForPlexTvLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "link-" + Guid.NewGuid().ToString("N")[..8]);
        SettingsStore? settings = null;
        try
        {
            var paths = AppPaths.Resolve(root, Environment.GetEnvironmentVariable);
            paths.EnsureCreated();
            var plex = new LinkStandIn();
            settings = SettingsStore.Load(paths.SettingsFile);
            var shell = new ShellViewModel(settings, paths, plex) { OpenUrl = _ => Assert.Fail("A browser was opened."), IsTv = true };

            shell.SignInCommand.Execute(null);
            var page = Assert.IsType<SignInPageViewModel>(shell.Router.Current);
            Assert.True(page.UseLinkCode);
            var clock = Stopwatch.StartNew();
            while (page.LinkCode is null && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.Equal("W X Y Z", page.LinkCode);
            Assert.Null(page.SignInLink);
            Assert.DoesNotContain(plex.Asked, uri => uri.Contains("strong=true", StringComparison.Ordinal));
            page.Deactivate();
        }
        finally
        {
            if (Directory.Exists(root)) TestFolder.Delete(root, settings is { } kept ? [kept] : []);
        }
    }

    /// <summary>plex.tv as far as a link code: a short PIN, never claimed.</summary>
    private sealed class LinkStandIn : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request.RequestUri!.PathAndQuery);
            var json = request.RequestUri!.AbsolutePath.EndsWith("/pins", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.Contains("/pins/", StringComparison.Ordinal)
                ? """{"id":5,"code":"wxyz","expiresIn":600}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
