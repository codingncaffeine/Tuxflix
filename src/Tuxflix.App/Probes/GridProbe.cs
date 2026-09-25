using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Probes;

/// <summary>
/// <c>tuxflix --probe-grid</c>: scrolls the largest film library's grid from top to bottom in
/// the real window and measures every frame.
/// </summary>
/// <remarks>
/// The scroll moves at a steady 3,000 pixels a second (a hard fling, held) in sixtieth-of-a-second
/// steps by the wall clock, so it crosses every row. Each step's layout (the rows coming into view,
/// their tiles and images) is timed on the UI thread, and so is how late each step starts. A step
/// whose work or lateness passes a sixtieth of a second is a lost frame. The band: at most 1 %
/// lost, none over 50 ms, and a key posted during the scroll waits at most 50 ms. Given a profile holding a copy of a signed-in
/// profile's settings it scrolls that server's library; <c>TUXFLIX_PROBE_SECTION</c> picks another.
/// </remarks>
internal static class GridProbe
{
    public const string Switch = "--probe-grid";

    private const double Speed = 3000;

    public static int ExitCode { get; private set; } = 1;

    public static async void Run(ShellViewModel shell, MainWindow window)
    {
        try
        {
            var startup = Stopwatch.StartNew();
            while (shell.Session is null && startup.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
            if (shell.Session is not { } session)
            {
                Log.Error("Probe: no server opened.");
                return;
            }

            var section = await PickAsync(session);
            if (section is null)
            {
                Log.Error("Probe: the server has no film or series library.");
                return;
            }

            shell.OpenSection(section);
            var loading = Stopwatch.StartNew();
            while (loading.Elapsed < TimeSpan.FromSeconds(120) && shell.Router.Current is LibraryPageViewModel { IsLoading: true } or LibraryPageViewModel { IsLoadingMore: true })
            {
                await Task.Delay(100);
            }

            if (shell.Router.Current is not LibraryPageViewModel page || page.Grid.Count == 0)
            {
                Log.Error("Probe: the library page did not fill.");
                return;
            }

            await Task.Delay(1500);
            var grid = window.GetVisualDescendants().OfType<RowsGrid>().FirstOrDefault();
            if (grid is null)
            {
                Log.Error("Probe: no grid on the page.");
                return;
            }

            Log.Info(string.Create(CultureInfo.InvariantCulture, $"Probe: {section.Title} listed {page.Grid.Count} titles in {page.Grid.Rows.Count} rows of {page.Grid.Columns} after {loading.Elapsed.TotalSeconds:0.0} s; scrolling it at {Speed:0} px/s."));
            var scroller = grid.ScrollHost;
            var tilesBefore = Views.Tiles.PosterTile.Built;
            var rowsBefore = TileRow.Built;
            var (work, late, keys) = await ScrollAsync(window, grid);
            var tilesBuilt = Views.Tiles.PosterTile.Built - tilesBefore;
            Log.Info($"Probe: during the scroll {TileRow.Built - rowsBefore} rows and {tilesBuilt} poster tiles were built.");
            var reached = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 1;
            if (work.Count < 60 || !reached)
            {
                Log.Error($"Probe: the scroll did not reach the bottom ({work.Count} steps, at {scroller.Offset.Y:0} of {scroller.Extent.Height - scroller.Viewport.Height:0} px).");
                return;
            }

            // A step's work over a sixtieth of a second, or a step starting that late, is a frame the viewer loses.
            var sorted = work.Order().ToList();
            var p99 = sorted[(int)(sorted.Count * 0.99)];
            var longest = sorted[^1];
            var dropped = work.Count(w => w > 1000.0 / 60) + late.Count(l => l > 1000.0 / 60);
            var keyLongest = keys.Count > 0 ? keys.Max() : double.NaN;
            var within = dropped <= Math.Max(1, work.Count / 100) && longest <= 50 && keyLongest <= 50;
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Probe: {work.Count} steps over {scroller.Extent.Height:0} px; each step's UI work {sorted[sorted.Count / 2]:0.00} ms median, {p99:0.00} ms 99th percentile, {longest:0.00} ms longest; "
                + $"steps late by over a frame {late.Count(l => l > 1000.0 / 60)}; frames lost {dropped} ({100.0 * dropped / work.Count:0.0} %); a key waited {keyLongest:0} ms at most; {(within ? "within" : "OUTSIDE")} the band."));
            ExitCode = within ? 0 : 1;
            Log.Info(ExitCode == 0 ? "Probe: the grid scrolls without dropping frames." : "Probe: FAILED.");
        }
        catch (Exception ex)
        {
            Log.Error("Probe: failed with an exception.", ex);
        }
        finally
        {
            Dispatcher.UIThread.Post(window.Close);
        }
    }

    /// <summary>The library to scroll: the one named, else the film or series library with the most titles.</summary>
    private static async Task<LibraryDirectory?> PickAsync(ServerSession session)
    {
        var sections = await Task.Run(() => session.Client.GetSectionsAsync(CancellationToken.None));
        if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_SECTION") is { Length: > 0 } wanted)
        {
            return sections.FirstOrDefault(s => s.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        LibraryDirectory? best = null;
        var most = -1;
        foreach (var section in sections.Where(s => s.Type is "movie" or "show"))
        {
            var count = (await Task.Run(() => session.Client.BrowseAsync(section.Key, "sort=titleSort", 0, 0, CancellationToken.None))).TotalSize ?? 0;
            if (count <= most) continue;
            most = count;
            best = section;
        }

        return best;
    }

    /// <summary>
    /// Scrolls from top to bottom sixty times a second by the wall clock, keeping what each step
    /// cost the UI thread (the layout that realises the rows coming into view, their tiles and
    /// images) and how late each step started (a busy UI thread delays the next), and every
    /// key's wait.
    /// </summary>
    private static async Task<(List<double> Work, List<double> Late, List<double> Keys)> ScrollAsync(Avalonia.Controls.TopLevel window, RowsGrid grid)
    {
        var scroller = grid.ScrollHost;
        scroller.Offset = default;
        var work = new List<double>();
        var late = new List<double>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed;
            late.Add(Math.Max(0, (now - previous).TotalMilliseconds - (1000.0 / 60)));
            previous = now;
            var step = Stopwatch.StartNew();
            var end = scroller.Extent.Height - scroller.Viewport.Height;
            var offset = Math.Min(end, Speed * now.TotalSeconds);
            scroller.Offset = new Vector(0, offset);
            window.UpdateLayout();
            work.Add(step.Elapsed.TotalMilliseconds);
            if (offset >= end || now > TimeSpan.FromSeconds(60))
            {
                timer.Stop();
                done.TrySetResult();
            }
        };
        timer.Start();

        // While it scrolls, keys are posted the way input arrives, and their waits kept.
        var keys = new List<double>();
        while (!done.Task.IsCompleted)
        {
            keys.Add(await Task.Run(() => AnswerAsync(DispatcherPriority.Input)));
            await Task.Delay(100);
        }

        await done.Task;
        return (work, late, keys);
    }

    private static async Task<double> AnswerAsync(DispatcherPriority priority)
    {
        var clock = Stopwatch.StartNew();
        var ran = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => ran.TrySetResult(clock.Elapsed.TotalMilliseconds), priority);
        return await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(3))) == ran.Task ? await ran.Task : double.NaN;
    }
}
