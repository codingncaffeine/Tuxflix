// Renders poses of the demo library to PNG files, headlessly, with the application's own styles.
//
//   dotnet run --project tools/Tuxflix.Capture -c Release -- <output-dir> [WIDTHxHEIGHT] [pose ...]
//
// Poses: home, home-hover, movie, series, welcome, discover, tooltip. With none given, all of them.
// With --real also: artist, album, nowplaying, and classic (the compact player over a playing album;
// CLASSIC_SKIN=path.wsz wears that skin, otherwise the base skin, fetched as the app fetches it;
// CLASSIC_SCALE=1.5 sets its size).
// Every run uses a throwaway profile under the output directory; nothing touches the real one.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App;
using Tuxflix.App.Imaging;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Tuxflix.Capture <output-dir> [WIDTHxHEIGHT] [pose ...]");
    return 2;
}

// --real: the signed-in account of the real profile instead of the demo. Its settings are read,
// never written; the token comes from the keyring; everything the run writes stays under the
// output directory.
var real = args.Contains("--real");
args = [.. args.Where(a => a != "--real")];

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var size = args.Length > 1 && args[1].Contains('x', StringComparison.Ordinal) ? args[1] : "1600x1000";
var width = double.Parse(size.Split('x')[0], System.Globalization.CultureInfo.InvariantCulture);
var height = double.Parse(size.Split('x')[1], System.Globalization.CultureInfo.InvariantCulture);
var poses = args.Skip(size == (args.Length > 1 ? args[1] : null) ? 2 : 1).ToList();
if (poses.Count == 0)
{
    poses = real ? ["home", "home-hover", "movie", "series"] : ["home", "home-hover", "movie", "series", "welcome", "discover", "tooltip"];
}

var (realClient, realServer) = real ? ReadRealProfile() : (null, null);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
    .SetupWithoutStarting();

// "census": every page of the demo walked for icon-only controls without a tooltip (decision 18); fails on any.
if (poses is ["census"]) return Census();

var failures = 0;
foreach (var pose in poses)
{
    try
    {
        Capture(pose);
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"{pose}: {ex}");
    }
}

return failures == 0 ? 0 : 1;

void Capture(string pose)
{
    var profile = Path.Combine(output, ".profile-" + pose);
    var paths = AppPaths.Resolve(profile, Environment.GetEnvironmentVariable);
    paths.EnsureCreated();
    var settings = SettingsStore.Load(paths.SettingsFile);
    if (real)
    {
        settings.Current.ClientIdentifier = realClient!;
        settings.Current.LastServerId = realServer;
    }

    // The keyring is the user's: a capture reads the real sign-in and never stores over or deletes it.
    // Nor does it make a sound or tell the server anything about what it plays.
    var shell = new ShellViewModel(settings, paths)
    {
        Keyring = new ReadOnlySecretStore(new Keyring()),
        ReportsPlayback = false,
        Silent = true,
    };
    var window = new MainWindow(shell, settings) { Width = width, Height = height };
    window.Show();

    if (pose == "welcome")
    {
        shell.Start(demo: false);
    }
    else if (real)
    {
        shell.Start(demo: false);
        var clock = Stopwatch.StartNew();
        while (shell.Session is null && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
        if (shell.Session is null) throw new InvalidOperationException($"No server opened; the page says: {shell.Router.Current?.Title}");
        Settle(shell);
        Pump(TimeSpan.FromSeconds(2));
        Settle(shell);
    }
    else
    {
        shell.Start(demo: true);
        Settle(shell);
    }

    if (real && pose is "movie" or "series")
    {
        var type = pose == "movie" ? "movie" : "show";
        var row = shell.Rail.Rows.OfType<RailItemRow>().FirstOrDefault(r => r.Item.Type == type)
                  ?? throw new InvalidOperationException($"The rail has no {type}.");
        shell.OpenItem(row.Item);
        Settle(shell);
    }

    if (real && pose is "artist" or "album" or "nowplaying" or "classic")
    {
        // An artist with a photo, by position in the rail (MUSIC_ARTIST picks another by name).
        var wanted = Environment.GetEnvironmentVariable("MUSIC_ARTIST");
        var artists = shell.Rail.Rows.OfType<RailItemRow>().Where(r => r.Item.Type == "artist" && r.Item.Thumb is not null).ToList();
        var row = (wanted is { Length: > 0 } ? artists.FirstOrDefault(r => r.Item.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)) : null)
                  ?? artists.Skip(artists.Count / 3).FirstOrDefault()
                  ?? throw new InvalidOperationException("The rail has no artist.");
        shell.OpenItem(row.Item);
        Settle(shell);

        if (pose is "album" or "nowplaying" or "classic")
        {
            var album = (shell.Router.Current as ArtistPageViewModel)?.Albums.FirstOrDefault()?.Album
                        ?? throw new InvalidOperationException("The artist has no album.");
            shell.OpenItem(album);
            Settle(shell);
        }

        if (pose is "nowplaying" or "classic")
        {
            var page = shell.Router.Current as AlbumPageViewModel ?? throw new InvalidOperationException("No album page.");
            page.PlayCommand.Execute(null);
            var clock = Stopwatch.StartNew();
            while (shell.Music?.Position is null or < 1.5 && clock.Elapsed < TimeSpan.FromSeconds(20)) Pump(TimeSpan.FromMilliseconds(100));
            shell.ShowNowPlayingCommand.Execute(null);
            Settle(shell);
            Pump(TimeSpan.FromSeconds(1));
            Settle(shell);
        }
    }

    if (pose == "classic")
    {
        var skinFile = Environment.GetEnvironmentVariable("CLASSIC_SKIN");
        if (skinFile is { Length: > 0 })
        {
            using var library = new Tuxflix.App.Classic.SkinLibrary(Path.Combine(paths.Data, "skins"));
            shell.Settings.Classic.Skin = Result(library.ImportAsync(skinFile));
        }

        var scale = Environment.GetEnvironmentVariable("CLASSIC_SCALE");
        if (scale is { Length: > 0 }) shell.Settings.Classic.Scale = double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture);
        Wait(window.ShowClassicAsync());
        var classic = window.Classic ?? throw new InvalidOperationException("The compact player did not open.");
        // CLASSIC_DRAG=dx,dy drags the resize corner that far, the way a hand would: press, ten moves, release.
        if (Environment.GetEnvironmentVariable("CLASSIC_DRAG") is { Length: > 0 } drag)
        {
            var by = drag.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var start = new Point(classic.ClientSize.Width - 4, classic.ClientSize.Height - 4);
            classic.MouseMove(start);
            Pump(TimeSpan.FromMilliseconds(100));
            classic.MouseDown(start, MouseButton.Left);
            for (var i = 1; i <= 10; i++)
            {
                classic.MouseMove(start + new Vector(by[0] * i / 10, by[1] * i / 10), RawInputModifiers.LeftMouseButton);
                Pump(TimeSpan.FromMilliseconds(50));
            }

            classic.MouseUp(start + new Vector(by[0], by[1]), MouseButton.Left);
            Pump(TimeSpan.FromMilliseconds(300));
            Console.WriteLine($"{pose}: dragged the corner by {by[0]},{by[1]}: size {classic.View.Scale:0.###}, window {classic.ClientSize.Width:0.##}x{classic.ClientSize.Height:0.##}");
        }

        // The analyser needs its decode to run ahead, and the marquee a step or two (CLASSIC_WAIT seconds, 3).
        var wait = double.TryParse(Environment.GetEnvironmentVariable("CLASSIC_WAIT"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 3;
        Pump(TimeSpan.FromSeconds(wait));
        if (Environment.GetEnvironmentVariable("CLASSIC_EARLIER") is { Length: > 0 } earlier)
        {
            // A frame a second before the kept one, to see what moved between them.
            classic.CaptureRenderedFrame()?.Save(Path.Combine(output, earlier), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Pump(TimeSpan.FromSeconds(1));
        }

        var shot = classic.CaptureRenderedFrame() ?? throw new InvalidOperationException("The compact player rendered nothing.");
        var name = "classic-" + (skinFile is { Length: > 0 } ? Path.GetFileNameWithoutExtension(skinFile) : "base") + (scale is { Length: > 0 } ? "-" + scale : string.Empty) + ".png";
        shot.Save(Path.Combine(output, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine($"{pose}: {Path.Combine(output, name)} ({shot.PixelSize.Width}x{shot.PixelSize.Height}, skin {classic.View.Skin.Name})");
        classic.Close();
        Pump(TimeSpan.FromMilliseconds(200));
        window.Close();
        return;
    }

    if (pose is "library" or "library-filter" or "collections" or "search" or "person" or "playlists")
    {
        var session = shell.Session ?? throw new InvalidOperationException("No server is open.");
        var sections = Result(session.Client.GetSectionsAsync(CancellationToken.None));
        var wanted = Environment.GetEnvironmentVariable("CAPTURE_SECTION");
        var movies = sections.FirstOrDefault(s => wanted is { Length: > 0 } && s.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                     ?? sections.First(s => s.Type == "movie");
        switch (pose)
        {
            case "library" or "library-filter" or "collections":
                shell.OpenSection(movies);
                Idle(shell);
                var library = (LibraryPageViewModel)shell.Router.Current!;
                if (pose == "library-filter")
                {
                    var genre = library.Filters.First(f => f.Filter == "genre");
                    Wait(genre.Values[Math.Min(1, genre.Values.Count - 1)].ChooseCommand.ExecuteAsync(null));
                    if (library.Filters.FirstOrDefault(f => f.Filter == "unwatched") is { } unwatched) Wait(unwatched.ToggleCommand.ExecuteAsync(null));
                }

                if (pose == "collections") Wait(library.ShowViewCommand.ExecuteAsync(LibraryView.Collections));
                break;
            case "search":
                shell.SearchText = Environment.GetEnvironmentVariable("CAPTURE_QUERY") ?? "the";
                shell.SearchNowCommand.Execute(null);
                break;
            case "person":
                var first = Result(session.Client.BrowseAsync(movies.Key, "sort=titleSort", 0, 12, CancellationToken.None)).Metadata!
                    .Select(m => Result(session.Client.GetMetadataAsync(m.RatingKey, CancellationToken.None)))
                    .First(m => m?.Role is { Count: > 0 })!;
                var person = first.Role![0];
                shell.OpenPerson(person.TagText, person.Thumb, person.Id!.Value);
                break;
            case "playlists":
                shell.ShowPlaylistsCommand.Execute(null);
                break;
        }

        Idle(shell);
    }

    if (pose == "player")
    {
        // The overlay over a black picture: there is no OpenGL on the headless platform.
        var item = shell.Router.Current is HomePageViewModel home
            ? home.Shelves.SelectMany(s => s.Tiles.OfType<MediaTileViewModel>()).First().Item
            : throw new InvalidOperationException("No home page to pick from.");
        shell.Play(item, resume: true);
        Settle(shell);
    }

    if (pose == "player-preview")
    {
        // A demo film (its previews and named chapters come from the demo server), the pointer resting on the seek bar.
        shell.Play(Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now).Movies[0], resume: false);
        Settle(shell);
        var seek = window.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.Name == "Seek")
                   ?? throw new InvalidOperationException("No seek bar.");
        var over = seek.TranslatePoint(new Point(seek.Bounds.Width * 0.42, seek.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException("The seek bar is not in the window.");
        window.MouseMove(over);
        Pump(TimeSpan.FromMilliseconds(300));
        window.MouseMove(over + new Point(1, 0));
        Pump(TimeSpan.FromMilliseconds(1500));
    }

    if (pose == "player-finder")
    {
        // Subtitles found online for a demo film, in the panel over the subtitle button.
        shell.Play(Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now).Movies[1], resume: false);
        Settle(shell);
        var page = window.GetVisualDescendants().OfType<Tuxflix.App.Views.Pages.PlayerPage>().FirstOrDefault()
                   ?? throw new InvalidOperationException("No player page.");
        page.OpenFinder();
        Pump(TimeSpan.FromMilliseconds(1500));
    }

    if (pose == "player-pip")
    {
        // Picture in picture: the small window, the pointer over it so its bar shows.
        shell.Play(Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now).Movies[0], resume: false);
        Settle(shell);
        var page = window.GetVisualDescendants().OfType<Tuxflix.App.Views.Pages.PlayerPage>().FirstOrDefault()
                   ?? throw new InvalidOperationException("No player page.");
        page.TogglePictureInPicture();
        Pump(TimeSpan.FromMilliseconds(400));
        window.MouseMove(new Point(window.ClientSize.Width / 2, window.ClientSize.Height / 3));
        Pump(TimeSpan.FromMilliseconds(600));
    }

    if (pose == "stream")
    {
        StreamCheck(shell);
        window.Close();
        return;
    }

    switch (real ? string.Empty : pose)
    {
        case "movie":
            shell.OpenItem(Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now).Movies[2]);
            break;
        case "series":
            shell.OpenItem(Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now).Shows[0]);
            break;
        case "discover":
            shell.ShowDiscoverCommand.Execute(null);
            break;
        case "servers":
            // Invented servers on an address that refuses at once, so every row settles quickly.
            shell.Router.Reset(new ServersPageViewModel(
                shell,
                [FakeServer("Den", true, null), FakeServer("Cabin", true, null), FakeServer("Robin's Library", false, "Robin")],
                "Den did not answer at any of its addresses. Is it running?"));
            break;
        case "status":
            shell.Router.Reset(new StatusPageViewModel("Connecting to Den", "Finding the fastest way to reach it…"));
            break;
    }

    Settle(shell);

    if (pose == "home-hover")
    {
        // Rest the pointer on the second poster of the second shelf.
        var tiles = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        var target = tiles.Where(b => b.DataContext is PosterTileViewModel).Skip(1).FirstOrDefault()
                     ?? throw new InvalidOperationException("No tile to hover.");
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("The tile is not in the window.");
        window.MouseMove(centre);
        Pump(TimeSpan.FromMilliseconds(600));
    }

    if (pose == "tooltip")
    {
        var downloads = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => Tuxflix.App.Controls.Tip.GetText(b) == "Back")
            ?? throw new InvalidOperationException("No Back button.");
        ToolTip.SetIsOpen(downloads, true);
        Pump(TimeSpan.FromMilliseconds(500));
    }

    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    var file = Path.Combine(output, pose + ".png");
    frame.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    Console.WriteLine($"{pose}: {file} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
    window.Close();
}

// The tooltip census: on each page of the demo, every button of our own views (not one inside a
// control's template) that shows only an icon must say what it does. Prints each control that does
// not, and how many were looked at, so a census that saw nothing cannot pass.
int Census()
{
    var paths = AppPaths.Resolve(Path.Combine(output, ".profile-census"), Environment.GetEnvironmentVariable);
    paths.EnsureCreated();
    var settings = SettingsStore.Load(paths.SettingsFile);
    var shell = new ShellViewModel(settings, paths) { Keyring = new ReadOnlySecretStore(new Keyring()), ReportsPlayback = false, Silent = true };
    var window = new MainWindow(shell, settings) { Width = width, Height = height };
    window.Show();
    shell.Start(demo: true);
    Settle(shell);

    var demo = Tuxflix.Core.Demo.DemoCatalog.Create(DateTimeOffset.Now);
    var session = shell.Session ?? throw new InvalidOperationException("The demo did not open.");
    var offenders = new List<string>();
    var looked = 0;
    var pages = 0;

    void Look(string page)
    {
        Settle(shell);
        pages++;
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            if (button.TemplatedParent is not null || !button.IsEffectivelyVisible) continue;
            var words = button.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text));
            if (words) continue;
            looked++;
            if (!string.IsNullOrWhiteSpace(Tuxflix.App.Controls.Tip.GetText(button)) || ToolTip.GetTip(button) is not null) continue;
            var within = button.GetVisualAncestors().OfType<StyledElement>().Select(a => a.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "?";
            var at = button.TranslatePoint(default, window) ?? default;
            offenders.Add($"{page}: {button.GetType().Name}{(button.Name is { Length: > 0 } name ? "#" + name : string.Empty)} in {within} at {at.X:0},{at.Y:0} ({button.DataContext?.GetType().Name ?? "no data"})");
        }
    }

    Look("home");
    var movies = Result(session.Client.GetSectionsAsync(CancellationToken.None)).First(s => s.Type == "movie");
    shell.OpenSection(movies);
    Idle(shell);
    Look("library");
    if (shell.Router.Current is LibraryPageViewModel library)
    {
        Wait(library.ShowViewCommand.ExecuteAsync(LibraryView.Collections));
        Idle(shell);
        Look("library collections");
    }

    shell.Router.Navigate(new CollectionPageViewModel(shell, session, demo.CollectionsOf(Tuxflix.Core.Demo.DemoCatalog.MoviesSectionKey)[0]));
    Look("collection");
    shell.OpenItem(demo.Movies[2]);
    Look("film");
    shell.OpenItem(demo.Shows[0]);
    Look("series");
    if (demo.ChildrenOf(demo.Shows[0].RatingKey).FirstOrDefault() is { } season)
    {
        shell.OpenItem(season);
        Look("season");
    }

    shell.SearchText = "the";
    shell.SearchNowCommand.Execute(null);
    Idle(shell);
    Look("search");
    var cast = demo.Movies.Select(m => Result(session.Client.GetMetadataAsync(m.RatingKey, CancellationToken.None))).First(m => m?.Role is { Count: > 0 })!.Role![0];
    shell.OpenPerson(cast.TagText, cast.Thumb, cast.Id!.Value);
    Idle(shell);
    Look("person");
    shell.ShowPlaylistsCommand.Execute(null);
    Idle(shell);
    Look("playlists");

    // The player with its controls up (the pointer over the picture), then small.
    shell.Play(demo.Movies[0], resume: false);
    Settle(shell);
    window.MouseMove(new Point(window.ClientSize.Width / 2, window.ClientSize.Height / 2));
    Pump(TimeSpan.FromMilliseconds(300));
    Look("player");
    if (window.GetVisualDescendants().OfType<Tuxflix.App.Views.Pages.PlayerPage>().FirstOrDefault() is { } player)
    {
        player.TogglePictureInPicture();
        Pump(TimeSpan.FromMilliseconds(300));
        window.MouseMove(new Point(window.ClientSize.Width / 2, window.ClientSize.Height / 3));
        Pump(TimeSpan.FromMilliseconds(300));
        Look("picture in picture");
        player.TogglePictureInPicture();
        Pump(TimeSpan.FromMilliseconds(300));
    }

    shell.Router.Reset(new ServersPageViewModel(shell, [FakeServer("Den", true, null), FakeServer("Robin's Library", false, "Robin")], "Den did not answer at any of its addresses."));
    Look("servers");
    shell.Router.Reset(new StatusPageViewModel("Connecting to Den", "Finding the fastest way to reach it…"));
    Look("status");
    window.Close();

    // Signed out: the welcome page.
    var outPaths = AppPaths.Resolve(Path.Combine(output, ".profile-census-welcome"), Environment.GetEnvironmentVariable);
    outPaths.EnsureCreated();
    var outSettings = SettingsStore.Load(outPaths.SettingsFile);
    var outShell = new ShellViewModel(outSettings, outPaths) { Keyring = new ReadOnlySecretStore(new Keyring()), ReportsPlayback = false };
    window = new MainWindow(outShell, outSettings) { Width = width, Height = height };
    window.Show();
    outShell.Start(demo: false);
    shell = outShell;
    Look("welcome");
    window.Close();

    Console.WriteLine($"census: {looked} icon-only controls looked at on {pages} pages; {offenders.Count} without a tooltip");
    foreach (var offender in offenders) Console.WriteLine("  " + offender);
    return offenders.Count == 0 && looked > 0 ? 0 : 1;
}

// Waits for the current page to finish loading (a library's later pages too), then for its artwork.
void Idle(ShellViewModel shell)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(60))
    {
        Pump(TimeSpan.FromMilliseconds(100));
        var page = shell.Router.Current;
        var busy = page?.IsLoading == true || page is LibraryPageViewModel { IsLoadingMore: true } || page is SearchPageViewModel { IsSearching: true };
        if (!busy) break;
    }

    Settle(shell);
}

// Runs the dispatcher and the render clock until the page and its artwork have landed.
void Settle(ShellViewModel shell)
{
    var clock = Stopwatch.StartNew();
    var quiet = 0;
    while (clock.Elapsed < TimeSpan.FromSeconds(20))
    {
        Pump(TimeSpan.FromMilliseconds(50));
        var busy = ImageLoader.Pending > 0 || shell.Router.Current?.IsLoading == true || shell.Rail.IsLoading;
        quiet = busy ? 0 : quiet + 1;
        if (quiet >= 8) break;
    }

    // Let the fade-ins finish.
    Pump(TimeSpan.FromMilliseconds(400));
}

// Runs the dispatcher until the task is done: the capture runs on the UI thread, so it cannot block on it.
T Result<T>(Task<T> task)
{
    Wait(task);
    return task.Result;
}

void Wait(Task task)
{
    var clock = Stopwatch.StartNew();
    while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(50));
    task.GetAwaiter().GetResult();
}

void Pump(TimeSpan duration)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < duration)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(8);
    }
}

static PlexResource FakeServer(string name, bool owned, string? owner) => new()
{
    Name = name,
    ClientIdentifier = name,
    Provides = "server",
    Owned = owned,
    SourceTitle = owner,
    ProductVersion = "1.42.1.10060-4e8b05daf",
    Platform = "Linux",
    Connections = [new PlexConnection { Uri = "http://127.0.0.1:9/", Local = true }],
};

// The real profile's client identifier and last server, read without writing anything back.
static (string Client, string? Server) ReadRealProfile()
{
    var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c
        ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    var file = Path.Combine(config, "tuxflix", "settings.json");
    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
    var root = json.RootElement;
    var client = root.GetProperty("clientIdentifier").GetString()
                 ?? throw new InvalidOperationException("The real profile has no client identifier yet; sign in with Tuxflix first.");
    var server = root.TryGetProperty("lastServerId", out var last) ? last.GetString() : null;
    return (client, server);
}

// Plays the first Continue Watching item silently (no picture, no sound, no progress reported) to
// prove the server hands the player its file: header token, direct play, demuxing, time moving.
static void StreamCheck(ShellViewModel shell)
{
    var session = shell.Session ?? throw new InvalidOperationException("No server is open.");
    var home = shell.Router.Current as HomePageViewModel ?? throw new InvalidOperationException("No home page.");
    var item = home.Shelves.SelectMany(s => s.Tiles.OfType<MediaTileViewModel>()).Select(t => t.Item).First(i => i.Type is "movie" or "episode");
    var full = session.Client.GetMetadataAsync(item.RatingKey, CancellationToken.None).GetAwaiter().GetResult() ?? item;
    var part = full.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Key ?? throw new InvalidOperationException("No part to play.");
    var headers = string.Join(",", session.Client.MediaHeaders(shell.Identity).Select(h => $"{h.Name}: {h.Value}"));

    using var player = new Tuxflix.Player.MpvPlayer(new Dictionary<string, string>
    {
        ["vo"] = "null",
        ["ao"] = "null",
        ["config"] = "no",
        ["terminal"] = "no",
        ["idle"] = "yes",
        ["http-header-fields"] = headers,
    });
    var loaded = new ManualResetEventSlim();
    var positions = new System.Collections.Concurrent.ConcurrentBag<double>();
    string? failure = null;
    player.FileLoaded += () => loaded.Set();
    player.Ended += (reason, error) => { if (reason == Tuxflix.Player.EndReason.Failed) { failure = error; loaded.Set(); } };
    player.Changed += change => { if (change is { Name: "time-pos", Number: { } s }) positions.Add(s); };

    var start = (full.ViewOffset ?? 0) / 1000.0;
    player.Load(session.Client.MediaUri(part).ToString(), start);
    if (!loaded.Wait(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("The file did not load within 20 seconds.");
    if (failure is not null) throw new InvalidOperationException($"The file did not open: {failure}");
    Thread.Sleep(TimeSpan.FromSeconds(4));

    Console.WriteLine($"stream: {full.Type} loaded; video {player.GetString("video-codec")} {player.GetString("width")}x{player.GetString("height")}, "
                      + $"audio {player.GetString("audio-codec-name")}; container {player.GetString("file-format")}; "
                      + $"asked to start at {start:0.0} s, reached {(positions.IsEmpty ? 0 : positions.Max()):0.0} s after 4 s.");
}
