using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;

namespace Tuxflix.App.Player;

/// <summary>
/// <c>tuxflix --demo --probe-player</c>: the whole playback path in the real window, then out again.
/// </summary>
/// <remarks>
/// Opens the first Continue Watching item of the demo in the player page, lets it play, and
/// counts the frames the video view drew; then goes back, and checks the player was destroyed
/// and the window still answers. Leaving is the dangerous half: libmpv blocks for ever when its
/// core is destroyed with a render context alive, which would freeze the window with nothing
/// logged. Exit code 0 is frames drawn, player destroyed, UI thread free.
/// </remarks>
internal static class PlayerProbe
{
    public const string Switch = "--probe-player";

    public static int ExitCode { get; private set; } = 1;

    public static async void Run(ShellViewModel shell, Window window)
    {
        try
        {
            // Wait for the home page's shelves.
            for (var i = 0; i < 100 && (shell.Router.Current as HomePageViewModel)?.Shelves.Count is null or 0; i++) await Task.Delay(100);
            var item = (shell.Router.Current as HomePageViewModel)?.Shelves.SelectMany(s => s.Tiles).FirstOrDefault()?.Item;
            if (item is null)
            {
                Log.Error("Probe: the demo home page never filled.");
                return;
            }

            shell.Play(item, resume: true);
            await Task.Delay(TimeSpan.FromSeconds(6));

            var page = shell.Router.Current as PlayerPageViewModel;
            var shared = page?.Player;
            var view = window.GetVisualDescendants().OfType<MpvVideoView>().FirstOrDefault();
            var frames = view?.FramesDrawn ?? 0;
            Log.Info($"Probe: player page open, {frames} frames drawn, position {page?.Position:0.0} s, error {page?.ErrorMessage ?? "none"}.");

            shell.GoBackCommand.Execute(null);
            var left = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(2));
            var destroyed = shared?.IsDisposed == true;
            Log.Info($"Probe: left the player; player destroyed {destroyed}; the UI thread answered after {left.Elapsed.TotalMilliseconds:0} ms.");

            ExitCode = frames > 10 && destroyed && shell.Router.Current is not PlayerPageViewModel ? 0 : 1;
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
}
