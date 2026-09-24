// Renders poses of the demo library to PNG files, headlessly, with the application's own styles.
//
//   dotnet run --project tools/Tuxflix.Capture -c Release -- <output-dir> [WIDTHxHEIGHT] [pose ...]
//
// Poses: home, home-hover, movie, series, welcome, discover, tooltip. With none given, all of them.
// Every run uses a throwaway profile under the output directory; nothing touches the real one.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App;
using Tuxflix.App.Imaging;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core;
using Tuxflix.Core.Settings;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Tuxflix.Capture <output-dir> [WIDTHxHEIGHT] [pose ...]");
    return 2;
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var size = args.Length > 1 && args[1].Contains('x', StringComparison.Ordinal) ? args[1] : "1600x1000";
var width = double.Parse(size.Split('x')[0], System.Globalization.CultureInfo.InvariantCulture);
var height = double.Parse(size.Split('x')[1], System.Globalization.CultureInfo.InvariantCulture);
var poses = args.Skip(size == (args.Length > 1 ? args[1] : null) ? 2 : 1).ToList();
if (poses.Count == 0) poses = ["home", "home-hover", "movie", "series", "welcome", "discover", "tooltip"];

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
    .SetupWithoutStarting();

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
    var shell = new ShellViewModel(settings, paths);
    var window = new MainWindow(shell, settings) { Width = width, Height = height };
    window.Show();

    if (pose == "welcome")
    {
        shell.Start(demo: false);
    }
    else
    {
        shell.Start(demo: true);
        Settle(shell);
    }

    switch (pose)
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
