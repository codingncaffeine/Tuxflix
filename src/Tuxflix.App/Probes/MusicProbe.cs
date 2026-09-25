using System.Diagnostics;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Probes;

/// <summary>
/// <c>tuxflix --probe-music</c>: plays the first artist on the rail that has tracks, muted, with the
/// desktop's media controls on, for <c>TUXFLIX_PROBE_SECONDS</c> (20), logging each change of
/// state; a script drives it from outside meanwhile, as the media keys would.
/// </summary>
internal static class MusicProbe
{
    public const string Switch = "--probe-music";

    public static int ExitCode { get; private set; } = 1;

    public static async void Run(ShellViewModel shell, MainWindow window)
    {
        try
        {
            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(60) && (shell.Session is null || shell.Rail.IsLoading || shell.Rail.Rows.Count == 0)) await Task.Delay(100);
            if (shell.Session is not { } session || shell.Music is not { } music)
            {
                Log.Error("Probe: no server opened.");
                return;
            }

            IReadOnlyList<MetadataItem> tracks = [];
            foreach (var artist in shell.Rail.Rows.OfType<RailItemRow>().Select(r => r.Item).Where(i => i.Type == "artist").Take(20))
            {
                tracks = await Task.Run(() => session.Client.GetAllLeavesAsync(artist.RatingKey, CancellationToken.None));
                if (tracks.Count > 1) break;
            }

            if (tracks.Count == 0)
            {
                Log.Error("Probe: no artist on the rail has tracks.");
                return;
            }

            music.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(music.IsPaused) or nameof(music.Current) or nameof(music.Volume) or nameof(music.Repeat) or nameof(music.IsShuffled))
                {
                    Log.Info($"Probe: music now {(music.IsPaused ? "paused" : "playing")} \"{music.Title}\" at volume {music.Volume:0}, repeat {music.Repeat}, shuffle {music.IsShuffled}.");
                }
            };
            await music.PlayAsync(tracks);
            Log.Info($"Probe: playing {tracks.Count} tracks, muted, with the media controls on.");
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("TUXFLIX_PROBE_SECONDS"), out var s) && s > 0 ? s : 20;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            ExitCode = 0;
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
