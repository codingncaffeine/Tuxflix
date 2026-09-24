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
                .SelectMany(s => s.Tiles).Select(t => t.Item).FirstOrDefault(i => i.Type is "movie" or "episode");
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
            var audio = shared?.Player.GetString("aid");
            var output = shared?.Player.GetString("current-ao");
            var sounded = audio is null or "no" || !string.IsNullOrEmpty(output);
            Log.Info($"Probe: player page open, {frames} frames drawn, position {page?.Position:0.0} s of {page?.Duration:0.0} s, "
                     + $"audio track {audio ?? "none"} on {(string.IsNullOrEmpty(output) ? "no sound device" : output)}, "
                     + $"subtitle track {shared?.Player.GetString("sid") ?? "none"}, error {page?.ErrorMessage ?? "none"}.");

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

            ExitCode = frames > 10 && sounded && destroyed && kept && shell.Router.Current is not PlayerPageViewModel ? 0 : 1;
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
