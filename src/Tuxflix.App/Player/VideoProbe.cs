using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;

namespace Tuxflix.App.Player;

/// <summary>
/// <c>tuxflix --probe-video</c>: proves video reaches the window on this machine, without eyes.
/// </summary>
/// <remarks>
/// A bare window plays a silent pattern, red over blue, through the same video view the player
/// uses, and reads back pixels from what was drawn: frames must arrive, the top of the picture
/// must be red and the bottom blue (so the picture is the right way up), and the log says whether
/// hardware decoding took part. Exit code 0 is all of that; anything else is 1.
/// </remarks>
internal static class VideoProbe
{
    public const string Switch = "--probe-video";

    private const string Pattern = "av://lavfi:color=c=red:s=640x180:r=30[a];color=c=blue:s=640x180:r=30[b];[a][b]vstack";

    public static int ExitCode { get; private set; } = 1;

    public static Window Create()
    {
        if (!MpvPlayer.IsAvailable)
        {
            Log.Error("Probe: libmpv is not installed.");
            return new Window();
        }

        var view = new MpvVideoView();
        var window = new Window
        {
            Title = "Tuxflix video probe",
            Width = 640,
            Height = 360,
            Background = Brushes.Black,
            Content = view,
        };

        (uint Top, uint Middle, uint Bottom) last = default;
        view.Sampled = (top, middle, bottom) => last = (top, middle, bottom);

        // Starting mpv waits for its core, so a worker does it, as the player page does.
        SharedPlayer? player = null;
        window.Opened += async (_, _) =>
        {
            player = new SharedPlayer(await Task.Run(() => new MpvPlayer(new Dictionary<string, string>
            {
                ["vo"] = "libmpv",
                ["ao"] = "null",
                ["hwdec"] = "auto-safe",
                ["keep-open"] = "yes",
                ["loop-file"] = "inf",
                ["config"] = "no",
                ["terminal"] = "no",
            })));
            view.Player = player;
        };

        view.Ready += async () =>
        {
            if (player is null) return;
            player.Player.Load(Pattern);
            await Task.Delay(TimeSpan.FromSeconds(6));

            var hwdec = await Task.Run(() => player.Player.GetString("hwdec-current"));
            var frames = view.FramesDrawn;
            var topRed = IsMostly(last.Top, 0);
            var bottomBlue = IsMostly(last.Bottom, 16);
            Log.Info($"Probe: {frames} frames drawn; top {Describe(last.Top)}, middle {Describe(last.Middle)}, bottom {Describe(last.Bottom)}; hardware decoding {hwdec ?? "none"}.");

            ExitCode = frames > 10 && topRed && bottomBlue ? 0 : 1;
            Log.Info(ExitCode == 0
                ? "Probe: video reaches the window, the right way up."
                : $"Probe: FAILED (frames {frames > 10}, top red {topRed}, bottom blue {bottomBlue}).");

            view.Player = null;
            _ = Task.Run(player.Release);
            Dispatcher.UIThread.Post(window.Close);
        };

        return window;
    }

    private static bool IsMostly(uint rgba, int shift)
    {
        var channel = (rgba >> shift) & 0xFF;
        var others = new[] { rgba & 0xFF, (rgba >> 8) & 0xFF, (rgba >> 16) & 0xFF }.Sum(c => (long)c) - channel;
        return channel > 160 && others < 120;
    }

    private static string Describe(uint rgba) => $"#{rgba & 0xFF:x2}{(rgba >> 8) & 0xFF:x2}{(rgba >> 16) & 0xFF:x2}";
}
