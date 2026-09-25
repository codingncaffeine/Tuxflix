using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Classic;

/// <summary>
/// <c>tuxflix --probe-classic</c>: the compact player in the real window, then back to the full one.
/// </summary>
/// <remarks>
/// Plays the first artist on the rail that has tracks, opens the compact player the way its button
/// does, and checks in the real window: the main window steps aside; the classic window is the
/// skin's size in whole screen pixels; rolling it up resizes it; the equalizer and the balance build
/// the filter chain mpv plays through, and take it away again, while the music keeps playing; the UI
/// thread answers; closing it brings the main window back. Given a profile holding a copy of a
/// signed-in profile's settings, it plays from that account's server, in silence, reporting
/// nothing. <c>TUXFLIX_PROBE_SKIN</c> names a <c>.wsz</c> to wear instead of the base skin, and
/// <c>TUXFLIX_PROBE_FRAME</c> a PNG file to keep the drawn player in.
/// </remarks>
internal static class ClassicProbe
{
    public const string Switch = "--probe-classic";

    private static int _runs;

    public static int ExitCode { get; private set; } = 1;

    public static async void Run(ShellViewModel shell, MainWindow window)
    {
        // The window's start runs once; a second run means showing it again started it again.
        if (Interlocked.Increment(ref _runs) > 1)
        {
            ExitCode = 1;
            Log.Error("Probe: FAILED: started a second time; showing the main window again ran its start again.");
            return;
        }

        try
        {
            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(60) && (shell.Session is null || shell.Rail.IsLoading || shell.Rail.Rows.Count == 0))
            {
                await Task.Delay(100);
            }

            if (shell.Session is not { } session || shell.Music is not { } music)
            {
                Log.Error($"Probe: no server opened; the page says \"{shell.Router.Current?.Title}\".");
                return;
            }

            IReadOnlyList<MetadataItem> tracks = [];
            foreach (var artist in shell.Rail.Rows.OfType<RailItemRow>().Select(r => r.Item).Where(i => i.Type == "artist").Take(20))
            {
                tracks = await Task.Run(() => session.Client.GetAllLeavesAsync(artist.RatingKey, CancellationToken.None));
                if (tracks.Count > 0) break;
            }

            if (tracks.Count == 0)
            {
                Log.Error("Probe: no artist on the rail has tracks.");
                return;
            }

            await music.PlayAsync(tracks);
            var playing = Stopwatch.StartNew();
            while (music.Position < 1.5 && playing.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            Log.Info($"Probe: music playing after {startup.Elapsed.TotalSeconds:0.0} s, at {music.Position:0.0} s of \"{music.Title}\".");

            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_SKIN") is { Length: > 0 } skinFile)
            {
                using var library = new SkinLibrary(Path.Combine(shell.Paths.Data, "skins"));
                shell.Settings.Classic.Skin = await library.ImportAsync(skinFile);
            }

            // Open it the way the button does, and give the base skin its first download.
            var page = shell.Router.Current;
            var opening = Stopwatch.StartNew();
            shell.ShowCompactPlayerCommand.Execute(null);
            while (window.Classic is not { IsVisible: true } && opening.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(50);
            if (window.Classic is not { } classic)
            {
                Log.Error("Probe: the compact player did not open.");
                return;
            }

            await Task.Delay(1000);
            var view = classic.View;
            var stepped = !window.IsVisible;
            var scaling = classic.RenderScaling;
            var unit = view.LogicalUnit;
            var full = Size(view.ShowEqualizer, view.ShowPlaylist, shaded: false);
            var sized = Near(classic.ClientSize, new Size(ClassicSprites.MainWidth * unit, full * unit)) && !classic.IsSmoothing;
            Log.Info($"Probe: compact player open after {opening.Elapsed.TotalSeconds:0.0} s in the {view.Skin.Name} skin; main window "
                     + $"{(stepped ? "stepped aside" : "STILL SHOWN")}; window {classic.ClientSize.Width:0.##}x{classic.ClientSize.Height:0.##} at scale {scaling:0.##} "
                     + $"({classic.ClientSize.Width * scaling:0.##}x{classic.ClientSize.Height * scaling:0.##} pixels, {unit * scaling:0.##} a skin pixel), {(sized ? "as expected" : "WRONG SIZE")}.");

            // Between whole sizes: the window takes the size between, and the drawing goes through the smoothing layer.
            view.Scale = 1.5;
            await Task.Delay(700);
            var between = Near(classic.ClientSize, new Size(ClassicSprites.MainWidth * 1.5, full * 1.5)) && classic.IsSmoothing && view.DrawScale == (int)Math.Ceiling(1.5 * scaling);
            var betweenSize = classic.ClientSize;
            view.Scale = 2;
            await Task.Delay(700);
            var wholeAgain = Near(classic.ClientSize, new Size(ClassicSprites.MainWidth * unit, full * unit)) && !classic.IsSmoothing;
            Log.Info($"Probe: at 1.5 times the window is {betweenSize.Width:0.##}x{betweenSize.Height:0.##}, drawn at {view.DrawScale} and smoothed: "
                     + $"{(between ? "yes" : "NO")}; back at double size, sharp again: {(wholeAgain ? "yes" : "NO")}.");

            view.Shaded = true;
            await Task.Delay(700);
            var rolled = Near(classic.ClientSize, new Size(ClassicSprites.MainWidth * unit, Size(view.ShowEqualizer, view.ShowPlaylist, shaded: true) * unit));
            view.Shaded = false;
            await Task.Delay(700);
            var unrolled = Near(classic.ClientSize, new Size(ClassicSprites.MainWidth * unit, full * unit));
            Log.Info($"Probe: rolled up {(rolled ? "to its title bar" : "WRONGLY")}, and back {(unrolled ? "to full size" : "WRONGLY")}.");

            // The equalizer and the balance: one labelled filter while in use, none when both are neutral.
            var before = music.Position;
            var clock = Stopwatch.StartNew();
            music.SetEqualizer(true);
            music.SetEqualizerBand(0, 6);
            music.SetBalance(-0.5);
            await Task.Delay(800);
            var chain = await Task.Run(() => music.Player?.Player.GetString("af") ?? string.Empty);
            music.SetBalance(0);
            music.SetEqualizer(false);
            await Task.Delay(800);
            var cleared = await Task.Run(() => music.Player?.Player.GetString("af") ?? "unknown");
            await Task.Delay(700);
            var moved = music.Position - before;
            var built = chain.Contains("equalizer@b0", StringComparison.Ordinal) && chain.Contains("stereotools@bal", StringComparison.Ordinal);
            var kept = moved > clock.Elapsed.TotalSeconds * 0.8 && !music.IsPaused;
            Log.Info($"Probe: with the equalizer and balance on, mpv plays through \"{chain}\"; with both neutral, \"{cleared}\"; "
                     + $"the music moved {moved:0.0} s in {clock.Elapsed.TotalSeconds:0.0} s.");

            var keys = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                keys.Add(await Task.Run(() => AnswerAsync(DispatcherPriority.Input)));
                await Task.Delay(25);
            }

            keys.Sort();
            var answered = keys[^1] <= 50;
            Log.Info($"Probe: with the compact player drawing, a key waits {keys[keys.Count / 2]:0} ms (median) and {keys[^1]:0} ms (longest).");

            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_FRAME") is { Length: > 0 } file)
            {
                var pixels = new PixelSize((int)Math.Round(classic.ClientSize.Width * scaling), (int)Math.Round(classic.ClientSize.Height * scaling));
                using var frame = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));
                frame.Render(view);
                await Task.Run(() => frame.Save(file, PngBitmapEncoderOptions.Default));
            }

            var beforeClosing = music.Position;
            classic.Close();
            await Task.Delay(1000);
            var back = window.IsVisible && window.Classic is null;
            var same = ReferenceEquals(shell.Router.Current, page) && ReferenceEquals(shell.Music, music) && music.Position > beforeClosing + 0.5 && !music.IsPaused;
            Log.Info($"Probe: back on the same page with the same music, still playing: {(same ? "yes" : "NO")} ({music.Position:0.0} s of \"{music.Title}\").");
            var remembered = !shell.Settings.Equalizer.On && shell.Settings.Equalizer.Bands[0] == 6 && !shell.Settings.Classic.Shaded;
            Log.Info($"Probe: closed; the main window is {(back ? "back" : "NOT BACK")}; settings {(remembered ? "kept" : "NOT KEPT")}.");

            ExitCode = stepped && sized && between && wholeAgain && rolled && unrolled && built && cleared.Length == 0 && kept && answered && back && same && remembered && _runs == 1 ? 0 : 1;
            Log.Info(ExitCode == 0 ? "Probe: the compact player opens, plays, rolls up and gives the window back." : "Probe: FAILED.");
        }
        catch (Exception ex)
        {
            Log.Error("Probe: failed with an exception.", ex);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                window.Classic?.Close();
                window.Close();
            });
        }
    }

    private static int Size(bool equalizer, bool playlist, bool shaded) =>
        (shaded ? ClassicSprites.ShadeHeight : ClassicSprites.MainHeight) + (equalizer ? ClassicSprites.EqHeight : 0) + (playlist ? ClassicSprites.PlaylistHeight : 0);

    /// <summary>Within the one pixel a window rounds its size to.</summary>
    private static bool Near(Size actual, Size expected) =>
        Math.Abs(actual.Width - expected.Width) <= 1 && Math.Abs(actual.Height - expected.Height) <= 1;

    /// <summary>Milliseconds until a job posted now at this priority runs on the UI thread; NaN after three seconds.</summary>
    private static async Task<double> AnswerAsync(DispatcherPriority priority)
    {
        var clock = Stopwatch.StartNew();
        var ran = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => ran.TrySetResult(clock.Elapsed.TotalMilliseconds), priority);
        return await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(3))) == ran.Task ? await ran.Task : double.NaN;
    }
}
