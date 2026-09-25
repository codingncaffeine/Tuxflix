using System.Diagnostics;
using System.Globalization;
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

            // TUXFLIX_PROBE_ITEM plays a chosen title instead (a 4K HEVC film, a file with sidecar subtitles).
            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_ITEM") is { Length: > 0 } chosen && shell.Session is { } open)
            {
                item = await Task.Run(() => open.Client.GetMetadataAsync(chosen, CancellationToken.None));
            }

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

            // What the picture took: the decoder, its pixel format and transfer, and every frame lost on the way.
            var (hwdec, format, gamma, size, dropped, decoderDropped) = shared is null
                ? (null, null, null, null, null, null)
                : await Task.Run(() => (shared.Player.GetString("hwdec-current"), shared.Player.GetString("video-params/hw-pixelformat") ?? shared.Player.GetString("video-params/pixelformat"),
                    shared.Player.GetString("video-params/gamma"), $"{shared.Player.GetString("video-params/w")}x{shared.Player.GetString("video-params/h")}",
                    shared.Player.GetString("frame-drop-count"), shared.Player.GetString("decoder-frame-drop-count")));
            var noDrops = dropped is null or "0" && decoderDropped is null or "0";
            Log.Info($"Probe: {item.Title}: {size} {format ?? "?"} ({gamma ?? "?"}) decoded by {(string.IsNullOrEmpty(hwdec) || hwdec == "no" ? "SOFTWARE" : hwdec)}; "
                     + $"frames dropped by the output {dropped ?? "?"}, by the decoder {decoderDropped ?? "?"}.");
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

            var markersRight = true;
            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_MARKERS") == "1" && page is not null)
            {
                markersRight = await CheckMarkersAsync(shell, page, window);
            }

            var qualityRight = true;
            string? runningConversion = null;
            if (int.TryParse(Environment.GetEnvironmentVariable("TUXFLIX_PROBE_QUALITY"), CultureInfo.InvariantCulture, out var kbps) && page is not null && view is not null)
            {
                (qualityRight, runningConversion) = await CheckQualityAsync(session, page, view, StreamQuality.FromKbps(kbps));
            }

            var menuRight = true;
            if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_MENU") == "1" && page?.Player is { } menuPlayer)
            {
                menuRight = await CheckMenuAsync(page, menuPlayer.Player);
                if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_UI") is { } menuFolder
                    && window.GetVisualDescendants().OfType<Views.Pages.PlayerPage>().FirstOrDefault() is { } playerView)
                {
                    await PictureMenuAsync(playerView, menuFolder);
                }
            }

            var leaving = (shell.Router.Current as PlayerPageViewModel)?.Player;
            shell.GoBackCommand.Execute(null);
            var left = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(2));
            var destroyed = shared?.IsDisposed == true && leaving?.IsDisposed != false;
            Log.Info($"Probe: left the player; player destroyed {destroyed}; the UI thread answered after {left.Elapsed.TotalMilliseconds:0} ms.");

            if (runningConversion is not null) qualityRight &= await ConversionEndedAsync(session, runningConversion);

            var after = await ResumePointAsync(session, item);
            var kept = after == before;
            Log.Info($"Probe: the server's resume point is {after / 1000.0:0.0} s, {(kept ? "unchanged" : "MOVED")}.");

            ExitCode = frames > 10 && sounded && answered && destroyed && kept && noDrops && markersRight && menuRight && qualityRight && shell.Router.Current is not PlayerPageViewModel ? 0 : 1;
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

    /// <summary>
    /// The server's markers at work: in the intro the skip button offers itself and skipping lands
    /// at the intro's end; credits before the end offer a skip of their own, even after the intro
    /// was skipped; in the final credits (or the last half-minute) the next episode is offered. The
    /// page is kept as a picture at each step when <c>TUXFLIX_PROBE_UI</c> names a folder.
    /// </summary>
    private static async Task<bool> CheckMarkersAsync(ShellViewModel shell, PlayerPageViewModel page, Window window)
    {
        var markers = page.Item.Marker ?? [];
        var intro = markers.FirstOrDefault(m => m.Type == "intro");
        var final = markers.FirstOrDefault(m => m is { Type: "credits", Final: true });
        var folder = Environment.GetEnvironmentVariable("TUXFLIX_PROBE_UI");
        var right = true;
        Log.Info($"Probe: {markers.Count} markers, {page.Chapters.Count} chapters; the next episode is {(page.HasNext ? page.UpNextHeading : "none")}.");

        if (intro is not null)
        {
            page.SeekTo((intro.StartTimeOffset / 1000.0) + 1);
            var offered = await WithinAsync(() => page.ShowsSkip && page.SkipLabel == "SKIP INTRO", TimeSpan.FromSeconds(4));
            if (folder is not null) Snapshot(window, Path.Combine(folder, "skip-intro.png"));
            page.SkipCommand.Execute(null);
            var landed = await WithinAsync(() => page.Position >= (intro.EndTimeOffset / 1000.0) - 1.5, TimeSpan.FromSeconds(4));
            Log.Info($"Probe: in the intro the skip button {(offered ? "showed" : "DID NOT SHOW")}; skipping {(landed ? "landed at its end" : "DID NOT LAND")} ({page.Position:0.0} s, the intro ends at {intro.EndTimeOffset / 1000.0:0.0} s).");
            right &= offered && landed;
        }

        var credits = markers.FirstOrDefault(m => m is { Type: "credits", Final: false });
        if (credits is not null)
        {
            page.SeekTo((credits.StartTimeOffset / 1000.0) + 1);
            var offered = await WithinAsync(() => page.ShowsSkip && page.SkipLabel == "SKIP CREDITS", TimeSpan.FromSeconds(4));
            if (folder is not null) Snapshot(window, Path.Combine(folder, "skip-credits.png"));
            Log.Info($"Probe: in the credits the skip button {(offered ? "showed" : "DID NOT SHOW")}.");
            right &= offered;
        }

        // The next episode: from the final credits, or the last half-minute when the server marked none.
        if (page.HasNext)
        {
            page.SeekTo(final is not null ? (final.StartTimeOffset / 1000.0) + 1 : page.Duration - 25);
            var offered = await WithinAsync(() => page.ShowsUpNext, TimeSpan.FromSeconds(4));
            if (folder is not null) Snapshot(window, Path.Combine(folder, "up-next.png"));
            page.CancelUpNextCommand.Execute(null);
            var gone = await WithinAsync(() => !page.ShowsUpNext, TimeSpan.FromSeconds(2));
            Log.Info($"Probe: {(final is not null ? "in the final credits" : "in the last half-minute")} the next episode {(offered ? "was offered" : "WAS NOT OFFERED")} ({page.UpNextHeading}); cancelling {(gone ? "put it away" : "DID NOT")}.");
            right &= offered && gone;

            // Play now: the next episode takes the player's place and plays; back still leads out of the player.
            var heading = page.UpNextHeading;
            var old = page.Player;
            page.PlayNextCommand.Execute(null);
            var took = await WithinAsync(() => shell.Router.Current is PlayerPageViewModel { Position: > 1 } next && !ReferenceEquals(next, page), TimeSpan.FromSeconds(15));
            var playing = shell.Router.Current as PlayerPageViewModel;
            var released = await WithinAsync(() => old is { IsDisposed: true }, TimeSpan.FromSeconds(5));
            Log.Info($"Probe: playing the next episode {(took ? "took the player's place" : "DID NOT TAKE OVER")} ({heading}: {Format.EpisodeCode(playing?.Item ?? page.Item)} · {playing?.Item.Title} at {playing?.Position:0.0} s); "
                     + $"the last one's player {(released ? "was destroyed" : "WAS KEPT")}.");
            right &= took && released;
        }

        return right;
    }

    /// <summary>
    /// The playback menu's choices reach mpv: each is set as the menu sets it, read back from the
    /// player on a worker, and put back as it was.
    /// </summary>
    private static async Task<bool> CheckMenuAsync(PlayerPageViewModel page, MpvPlayer player)
    {
        var missed = new List<string>();
        static bool Near(double? value, double wanted) => value is { } v && Math.Abs(v - wanted) < 0.01;

        async Task Expect(string what, Action set, Func<MpvPlayer, bool> holds)
        {
            set();
            var clock = Stopwatch.StartNew();
            bool held;
            while (!(held = await Task.Run(() => holds(player))) && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50);
            if (!held) missed.Add(what);
        }

        var scale = page.SubtitleScale;
        var raised = page.SubtitlesRaised;
        var night = page.NightMode;
        await Expect("speed", () => page.Speed = 1.5, p => Near(p.GetNumber("speed"), 1.5));
        await Expect("speed back", () => page.Speed = 1, p => Near(p.GetNumber("speed"), 1));
        await Expect("fill", () => page.Fit = PictureFit.Fill, p => Near(p.GetNumber("panscan"), 1));
        await Expect("stretch", () => page.Fit = PictureFit.Stretch, p => Near(p.GetNumber("panscan"), 0) && p.GetString("keepaspect") == "no");
        await Expect("fit", () => page.Fit = PictureFit.Fit, p => Near(p.GetNumber("panscan"), 0) && p.GetString("keepaspect") == "yes");
        await Expect("4:3", () => page.Aspect = "4:3", p => Near(p.GetNumber("video-aspect-override"), 4.0 / 3));
        await Expect("own shape", () => page.Aspect = null, p => Near(p.GetNumber("video-aspect-override"), -1));
        await Expect("subtitle delay", () => page.SubtitleDelay = 0.3, p => Near(p.GetNumber("sub-delay"), 0.3));
        await Expect("subtitle delay back", () => page.SubtitleDelay = 0, p => Near(p.GetNumber("sub-delay"), 0));
        await Expect("audio delay", () => page.AudioDelay = -0.2, p => Near(p.GetNumber("audio-delay"), -0.2));
        await Expect("audio delay back", () => page.AudioDelay = 0, p => Near(p.GetNumber("audio-delay"), 0));
        await Expect("subtitle size", () => page.SubtitleScale = 1.6, p => Near(p.GetNumber("sub-scale"), 1.6));
        await Expect("subtitle size back", () => page.SubtitleScale = scale, p => Near(p.GetNumber("sub-scale"), scale));
        await Expect("raised", () => page.SubtitlesRaised = !raised, p => Near(p.GetNumber("sub-pos"), raised ? 100 : 88));
        await Expect("raised back", () => page.SubtitlesRaised = raised, p => Near(p.GetNumber("sub-pos"), raised ? 88 : 100));
        await Expect("night mode", () => page.NightMode = !night, p => (p.GetString("af") ?? string.Empty).Contains("dynaudnorm", StringComparison.Ordinal) != night);
        await Expect("night mode back", () => page.NightMode = night, p => (p.GetString("af") ?? string.Empty).Contains("dynaudnorm", StringComparison.Ordinal) == night);
        Log.Info(missed.Count == 0
            ? "Probe: every playback menu choice reached the player and was put back."
            : $"Probe: the playback menu DID NOT reach the player for {string.Join(", ", missed)}.");
        return missed.Count == 0;
    }

    /// <summary>
    /// A conversion from the real server: asked for at a lower quality, it arrives as HLS no taller
    /// than the quality allows, carries on from the same place (two minutes in, so a start from
    /// the beginning cannot pass), and shows in the server's list of conversions; back at the
    /// original, the file plays again and the conversion is gone from the list.
    /// </summary>
    private static async Task<(bool Right, string? Running)> CheckQualityAsync(ServerSession session, PlayerPageViewModel page, MpvVideoView view, StreamQuality quality)
    {
        page.SeekTo(120);
        await WithinAsync(() => page.Position is >= 120 and < 130, TimeSpan.FromSeconds(15));
        var from = page.Position;
        var framesBefore = view.FramesDrawn;
        page.SetQuality(quality);
        var converting = await WithinAsync(() => page.IsConverting, TimeSpan.FromSeconds(15));
        var carried = await WithinAsync(() => page.Position > from + 3, TimeSpan.FromSeconds(30)) && page.Position < from + 40;
        var at = page.Position;
        var shared = page.Player;
        var (path, format, height, picture) = shared is null
            ? (null, null, null, null)
            : await Task.Run(() => (shared.Player.GetString("path"), shared.Player.GetString("file-format"), shared.Player.GetNumber("video-params/h"),
                $"{shared.Player.GetString("video-codec")}, {shared.Player.GetString("video-params/pixelformat")} ({shared.Player.GetString("video-params/gamma")}), decoded by {shared.Player.GetString("hwdec-current")}"));
        if (Environment.GetEnvironmentVariable("TUXFLIX_PROBE_FRAME") is { Length: > 0 } frame)
        {
            await KeepFrameAsync(view, Path.ChangeExtension(frame, null) + "-converted.png");
        }

        var sessions = await Task.Run(() => session.Client.GetTranscodeSessionsAsync(CancellationToken.None));
        var key = page.TranscodeSession;
        var ours = key is null ? null : sessions.FirstOrDefault(s => s.Is(key));
        var drew = view.FramesDrawn > framesBefore + 30;
        var hls = path?.Contains("/video/:/transcode/universal/start.m3u8?", StringComparison.Ordinal) == true;
        var sized = height is { } lines && lines <= quality.Height;
        Log.Info($"Probe: at {quality.Label} the server {(converting ? "converts" : "DOES NOT CONVERT")} ({page.StreamSummary}); mpv reads {(hls ? "the HLS start" : "SOMETHING ELSE")} as {format ?? "?"}, "
                 + $"{height ?? 0:0} lines{(sized ? string.Empty : " (TOO TALL)")}, {picture}; playback {(carried ? "carried on" : "DID NOT CARRY ON")} from {from:0.0} s to {at:0.0} s, {view.FramesDrawn - framesBefore} frames drawn; "
                 + $"the server lists the conversion {(ours is null ? "NOT AT ALL" : $"({ours.VideoDecision} video, {ours.VideoCodec} {ours.Width}x{ours.Height}, {ours.Speed:0.0}x, hardware encoding {ours.HardwareEncoding ?? "none"})")}.");

        page.SetQuality(StreamQuality.Original);
        var direct = await WithinAsync(() => !page.IsConverting, TimeSpan.FromSeconds(15));
        var resumed = await WithinAsync(() => page.Position > at + 2, TimeSpan.FromSeconds(30)) && page.Position < at + 40;
        var clock = Stopwatch.StartNew();
        var stopped = false;
        while (!stopped && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var now = await Task.Run(() => session.Client.GetTranscodeSessionsAsync(CancellationToken.None));
            stopped = key is null || !now.Any(s => s.Is(key));
            if (!stopped) await Task.Delay(500);
        }

        var backPath = page.Player is { } again ? await Task.Run(() => again.Player.GetString("path")) : null;
        Log.Info($"Probe: back at the original the file {(direct && resumed ? "plays again" : "DOES NOT PLAY")} ({(backPath?.Contains("/library/parts/", StringComparison.Ordinal) == true ? "the part itself" : "SOMETHING ELSE")}, at {page.Position:0.0} s); "
                 + $"the server's conversion {(stopped ? $"was gone after {clock.Elapsed.TotalSeconds:0.0} s" : "IS STILL RUNNING")}.");

        // Converting once more, for the way out: leaving the player must end it on the server.
        page.SetQuality(quality);
        var again2 = await WithinAsync(() => page.IsConverting && page.Position > at + 4, TimeSpan.FromSeconds(30));
        var running = page.TranscodeSession;
        var listed = again2 && running is not null && (await Task.Run(() => session.Client.GetTranscodeSessionsAsync(CancellationToken.None))).Any(s => s.Is(running));
        Log.Info($"Probe: converting again {(listed ? "shows in the server's list" : "DOES NOT SHOW")}{(running != key ? " under a session of its own" : " UNDER THE SAME SESSION")}.");
        return (converting && hls && sized && carried && drew && ours is not null && direct && resumed && stopped && listed && running != key, running);
    }

    /// <summary>After the player was left while converting: the conversion is gone from the server's list.</summary>
    private static async Task<bool> ConversionEndedAsync(ServerSession session, string key)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var sessions = await Task.Run(() => session.Client.GetTranscodeSessionsAsync(CancellationToken.None));
            if (!sessions.Any(s => s.Is(key)))
            {
                Log.Info($"Probe: leaving the player ended its conversion on the server within {clock.Elapsed.TotalSeconds:0.0} s.");
                return true;
            }

            await Task.Delay(500);
        }

        Log.Info("Probe: leaving the player DID NOT END its conversion on the server.");
        return false;
    }

    private static async Task<bool> WithinAsync(Func<bool> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < limit) await Task.Delay(50);
        return condition();
    }

    /// <summary>
    /// The playback menu and its subtitle submenu as pictures, each from its own popup; after the
    /// idle delay the controls must still be up under the open menu.
    /// </summary>
    private static async Task PictureMenuAsync(Views.Pages.PlayerPage view, string folder)
    {
        if (view.OpenSettings() is not { } menu) return;
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        if (menu.Items[0] is Control first && TopLevel.GetTopLevel(first) is { } popup) Snapshot(popup, Path.Combine(folder, "menu.png"));
        foreach (var (name, file) in new[] { ("Subtitles", "menu-subtitles.png"), ("Quality", "menu-quality.png") })
        {
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header is string header && header.StartsWith(name, StringComparison.Ordinal)) is not { } submenu) continue;
            submenu.IsSubMenuOpen = true;
            await Task.Delay(400);
            if (submenu.Items[0] is Control inner && TopLevel.GetTopLevel(inner) is { } sub) Snapshot(sub, Path.Combine(folder, file));
            submenu.IsSubMenuOpen = false;
        }

        var up = view.FindControl<Panel>("Overlay") is { } overlay && !overlay.Classes.Contains("hidden");
        Log.Info($"Probe: with the menu open past the idle delay the controls {(up ? "stayed up" : "HID")}.");
        menu.Hide();

        if (view.FindControl<Button>("ChaptersButton") is { Flyout: { } chapters } button)
        {
            chapters.ShowAt(button);
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (TopLevel.GetTopLevel(view) is { } top) Snapshot(top, Path.Combine(folder, "chapters.png"));
            chapters.Hide();
        }
    }

    /// <summary>A window's controls as a picture (the video, drawn through its own surface, shows black).</summary>
    private static void Snapshot(TopLevel window, string file)
    {
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
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
