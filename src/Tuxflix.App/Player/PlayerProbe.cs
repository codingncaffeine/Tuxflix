using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Player;

namespace Tuxflix.App.Player;

/// <summary>
/// <c>tuxflix --probe-player</c>: the whole playback path in the real window, then out again.
/// </summary>
/// <remarks>
/// Opens the first film or episode on the home page in the player page, resuming where it was
/// left, lets it play, and counts the frames the video view drew; then goes back, and checks the
/// player was destroyed and the window still answers. Leaving is the dangerous half: libmpv blocks
/// for ever when its core is destroyed with a render context alive, which would freeze the window
/// with nothing logged. Exit code 0 is frames drawn, a sound device fed when there is audio,
/// player destroyed, UI thread free.
/// <para>
/// With <c>--demo</c> it plays the demo's pattern. Given a profile folder holding a copy of a
/// signed-in profile's settings, it plays from that account's server instead: the keyring is read
/// and never written, the sound device plays at volume zero, no progress is reported, and the
/// server's resume point must be where it was. <c>TUXFLIX_PROBE_SECONDS</c> sets how long it plays (6 s), and
/// <c>TUXFLIX_PROBE_FRAME</c> names a PNG file to keep one drawn frame in.
/// </para>
/// </remarks>
internal static class PlayerProbe
{
    public const string Switch = "--probe-player";

    public static int ExitCode { get; private set; } = 1;

    public static async void Run(ShellViewModel shell, Window window)
    {
        try
        {
            // Wait for the home page's shelves: a real server signs in and connects first.
            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(60) && (shell.Router.Current as HomePageViewModel)?.Shelves.Count is null or 0)
            {
                await Task.Delay(100);
            }

            var item = (shell.Router.Current as HomePageViewModel)?.Shelves
                .SelectMany(s => s.Tiles.OfType<MediaTileViewModel>()).Select(t => t.Item).FirstOrDefault(i => i.Type is "movie" or "episode");
            if (item is null || shell.Session is not { } session)
            {
                Log.Error($"Probe: no film or episode on the home page; the page says \"{shell.Router.Current?.Title}\".");
                return;
            }

            var before = await ResumePointAsync(session, item);
            Log.Info($"Probe: home filled after {startup.Elapsed.TotalSeconds:0.0} s; playing {(item.Type == "episode" ? "an episode" : "a film")} "
                     + $"left at {before / 1000.0:0.0} s.");
            shell.Play(item, resume: true);
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("TUXFLIX_PROBE_SECONDS"), out var s) && s > 0 ? s : 6;
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            var page = shell.Router.Current as PlayerPageViewModel;
            var shared = page?.Player;
            var view = window.GetVisualDescendants().OfType<MpvVideoView>().FirstOrDefault();
            var frames = view?.FramesDrawn ?? 0;

            // Reading mpv waits for its core: a worker reads.
            var (audio, output, subtitles) = shared is null
                ? (null, null, null)
                : await Task.Run(() => (shared.Player.GetString("aid"), shared.Player.GetString("current-ao"), shared.Player.GetString("sid")));
            var sounded = audio is null or "no" || !string.IsNullOrEmpty(output);
            Log.Info($"Probe: player page open, {frames} frames drawn, position {page?.Position:0.0} s of {page?.Duration:0.0} s, "
                     + $"audio track {audio ?? "none"} on {(string.IsNullOrEmpty(output) ? "no sound device" : output)}, "
                     + $"subtitle track {subtitles ?? "none"}, error {page?.ErrorMessage ?? "none"}.");

            var answered = false;
            if (page?.Player is { } playing && view is not null)
            {
                (var summary, answered) = await MeasureAnswersAsync(page, playing.Player, view);
                Log.Info(summary);
            }

            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_FRAME") is { Length: > 0 } file && view is not null)
            {
                await KeepFrameAsync(view, file);
            }

            shell.GoBackCommand.Execute(null);
            var left = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(2));
            var destroyed = shared?.IsDisposed == true;
            Log.Info($"Probe: left the player; player destroyed {destroyed}; the UI thread answered after {left.Elapsed.TotalMilliseconds:0} ms.");

            var after = await ResumePointAsync(session, item);
            var kept = after == before;
            Log.Info($"Probe: the server's resume point is {after / 1000.0:0.0} s, {(kept ? "unchanged" : "MOVED")}.");

            ExitCode = frames > 10 && sounded && answered && destroyed && kept && shell.Router.Current is not PlayerPageViewModel ? 0 : 1;
            Log.Info(ExitCode == 0 ? "Probe: playback opens, draws and closes cleanly." : "Probe: FAILED.");
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

    /// <summary>
    /// What a viewer feels while video plays: how long a key waits before the window acts on it,
    /// and how soon pause, play and a volume step take effect in mpv and show on the page.
    /// </summary>
    /// <remarks>
    /// A key press reaches the window as an input-priority job on the UI thread, so the actions are
    /// posted the same way, from another thread, and the effects polled from there. The bands: a
    /// key never waits longer than 50 ms (a little over one frame of 24 fps video), pause, play and
    /// volume take effect within 100 ms (the threshold at which a response stops feeling instant),
    /// and handing frames over takes the UI thread under 2 % of the time, with at most one in fifty
    /// hand-overs over a millisecond and none over 50 ms.
    /// Drawing on the UI thread measured 175 ms per key at the median, 2.6 s at worst, a pause not
    /// taken within 3 s, and 99 % of the UI thread.
    /// </remarks>
    private static async Task<(string Summary, bool Answered)> MeasureAnswersAsync(PlayerPageViewModel page, MpvPlayer player, MpvVideoView view)
    {
        var stats = view.Stats;
        var renderBefore = stats.RenderTime;
        var handOverBefore = stats.HandOverTime;
        var framesBefore = stats.FramesDrawn;
        var slowBefore = stats.SlowHandOvers;
        var gcBefore = GC.GetTotalPauseDuration();
        var collectionsBefore = GC.CollectionCount(0);
        var window = Stopwatch.StartNew();
        var (keyMedian, keyLongest, normalMedian, paused, pausedShown, resumed, volume) = await Task.Run(async () =>
        {
            var keys = new List<double>();
            var normal = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                keys.Add(await RunAtPriorityAsync(DispatcherPriority.Input));
                normal.Add(await RunAtPriorityAsync(DispatcherPriority.Default));
                await Task.Delay(25);
            }

            keys.Sort();
            normal.Sort();
            var pause = await RoundTripAsync(() => page.TogglePauseCommand.Execute(null), () => player.GetString("pause") == "yes", () => page.IsPaused);
            await Task.Delay(500);
            var play = await RoundTripAsync(() => page.TogglePauseCommand.Execute(null), () => player.GetString("pause") == "no", () => !page.IsPaused);
            await Task.Delay(500);
            var target = Math.Clamp(page.Volume - 5, 0, 150);
            var step = await RoundTripAsync(() => page.ChangeVolume(-5), () => player.GetNumber("volume") is { } v && Math.Abs(v - target) < 0.001, static () => true);
            return (keys[keys.Count / 2], keys[^1], normal[normal.Count / 2], pause.Effect, pause.Shown, play.Effect, step.Effect);
        });

        var seconds = window.Elapsed.TotalSeconds;
        var uiShare = (stats.HandOverTime - handOverBefore).TotalSeconds / seconds;
        var videoShare = (stats.RenderTime - renderBefore).TotalSeconds / seconds;

        // NaN (nothing within three seconds) fails every comparison, as it should. Work creeping onto
        // the UI thread shows as its share and as many slow hand-overs; one wall-clock outlier is the
        // system preempting the thread, and only fails when it is long enough to drop a frame.
        var frames = stats.FramesDrawn - framesBefore;
        var slow = stats.SlowHandOvers - slowBefore;
        var answered = keyLongest <= 50 && paused <= 100 && pausedShown <= 100 && resumed <= 100 && volume <= 100
                       && uiShare < 0.02 && slow <= Math.Max(1, frames / 50) && stats.LongestHandOver.TotalMilliseconds <= 50;
        var summary = $"Probe: a key waits {keyMedian:0} ms (median) and {keyLongest:0} ms (longest), a normal job {normalMedian:0} ms; "
               + $"pause takes effect after {paused:0} ms and shows after {pausedShown:0} ms, play again after {resumed:0} ms, "
               + $"a volume step after {volume:0} ms; meanwhile {(stats.FramesDrawn - framesBefore) / seconds:0} frames a second "
               + $"({stats.FramesShown} of {stats.FramesDrawn} so far confirmed on screen by the compositor), "
               + $"handing frames over took the UI thread {uiShare:P1} of the time ({stats.LongestHandOver.TotalMilliseconds:0.00} ms at most), "
               + $"drawing took the video thread {videoShare:P0} ({stats.LongestRender.TotalMilliseconds:0} ms at most) "
               + $"(NaN: not within three seconds); {stats.SlowHandOvers - slowBefore} hand-overs over 1 ms, "
               + $"garbage collection paused every thread {(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds:0.0} ms in {GC.CollectionCount(0) - collectionsBefore} collections; "
               + $"{(answered ? "within" : "OUTSIDE")} the bands.";
        return (summary, answered);
    }

    /// <summary>Milliseconds until a job posted now at this priority runs on the UI thread; NaN after three seconds.</summary>
    private static async Task<double> RunAtPriorityAsync(DispatcherPriority priority)
    {
        var clock = Stopwatch.StartNew();
        var ran = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => ran.TrySetResult(clock.Elapsed.TotalMilliseconds), priority);
        return await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(3))) == ran.Task ? await ran.Task : double.NaN;
    }

    /// <summary>
    /// Milliseconds from posting an action the way a key press arrives until mpv shows its effect,
    /// and until the page shows it; NaN for one that did not come within three seconds.
    /// </summary>
    private static async Task<(double Effect, double Shown)> RoundTripAsync(Action action, Func<bool> effect, Func<bool> shown)
    {
        var clock = Stopwatch.StartNew();
        Dispatcher.UIThread.Post(action, DispatcherPriority.Input);
        double effectAt = double.NaN, shownAt = double.NaN;
        while ((double.IsNaN(effectAt) || double.IsNaN(shownAt)) && clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (double.IsNaN(effectAt) && effect()) effectAt = clock.Elapsed.TotalMilliseconds;
            if (double.IsNaN(shownAt) && shown()) shownAt = clock.Elapsed.TotalMilliseconds;
            await Task.Delay(2);
        }

        return (effectAt, shownAt);
    }

    /// <summary>Where the server would resume the item, in milliseconds.</summary>
    private static async Task<long> ResumePointAsync(ServerSession session, MetadataItem item) =>
        (await session.Client.GetMetadataAsync(item.RatingKey, CancellationToken.None))?.ViewOffset ?? 0;

    /// <summary>Keeps the next drawn frame as a PNG, and logs how much of a picture it holds.</summary>
    private static async Task KeepFrameAsync(MpvVideoView view, string file)
    {
        var drawn = new TaskCompletionSource<(int Width, int Height, byte[] Pixels)>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.CaptureNextFrame((width, height, pixels) => drawn.TrySetResult((width, height, pixels)));
        if (await Task.WhenAny(drawn.Task, Task.Delay(TimeSpan.FromSeconds(3))) != drawn.Task)
        {
            Log.Warn("Probe: no frame was drawn to keep.");
            return;
        }

        var (w, h, rgba) = await drawn.Task;
        var (mean, spread) = await Task.Run(() =>
        {
            using var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using (var target = bitmap.Lock())
            {
                // OpenGL hands the bottom row first; an image starts at the top.
                for (var row = 0; row < h; row++)
                {
                    Marshal.Copy(rgba, (h - 1 - row) * w * 4, target.Address + (row * target.RowBytes), w * 4);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            bitmap.Save(file, PngBitmapEncoderOptions.Default);

            // Luma of every sixteenth pixel: the mean says how bright, the spread whether it is a picture at all.
            double sum = 0, squares = 0;
            var count = 0;
            for (var i = 0; i + 2 < rgba.Length; i += 4 * 16)
            {
                var luma = (0.2126 * rgba[i]) + (0.7152 * rgba[i + 1]) + (0.0722 * rgba[i + 2]);
                sum += luma;
                squares += luma * luma;
                count++;
            }

            var average = sum / Math.Max(1, count);
            return (average, Math.Sqrt(Math.Max(0, (squares / Math.Max(1, count)) - (average * average))));
        });
        Log.Info($"Probe: kept a {w}x{h} frame; luma mean {mean:0}, spread {spread:0}.");
    }
}
