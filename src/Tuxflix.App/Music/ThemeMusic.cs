using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;

namespace Tuxflix.App.Music;

/// <summary>Plays a title's theme music quietly behind its page.</summary>
public interface IThemeMusic
{
    /// <summary>
    /// Fades <paramref name="source"/> in; a theme already playing fades out as it does. The same
    /// theme still playing carries on rather than starting again.
    /// </summary>
    /// <param name="headers">The headers mpv sends for it: the server's token travels here, never in the address.</param>
    /// <param name="muted">Plays with the sound off (a probe run), so the whole path runs and nothing is heard.</param>
    void Play(string source, IReadOnlyList<(string Name, string Value)> headers, bool muted);

    /// <summary>Fades the theme out and lets its player go.</summary>
    void Stop();
}

/// <summary>
/// Theme music through an audio-only mpv of its own, one per theme: faded in to a quiet level,
/// played once, faded out when its page goes.
/// </summary>
/// <remarks>
/// Starting mpv and letting it go both wait for its core, so each theme runs on a worker from
/// start to end; the interface thread only asks and returns. <c>TUXFLIX_AO</c> chooses the sound
/// output as for every player, so a test run opens no sound device.
/// </remarks>
public sealed class ThemeMusic(string? audioOutput = null) : IThemeMusic
{
    /// <summary>The level a theme plays at, of mpv's 100: under the room, over silence.</summary>
    public const double Level = 35;

    internal static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly string? _audioOutput = audioOutput ?? Tuxflix.App.Player.ProbeSwitches.AudioOutput;
    private Theme? _current;

    /// <summary>For tests: the player of the theme playing now, once mpv has started.</summary>
    internal MpvPlayer? CurrentPlayer
    {
        get
        {
            lock (_gate) return _current?.Player;
        }
    }

    /// <summary>For tests: the workers of the themes still playing or fading, and of the latest.</summary>
    internal List<Task> Runs { get; } = [];

    public void Play(string source, IReadOnlyList<(string Name, string Value)> headers, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(headers);
        if (!MpvPlayer.IsAvailable) return;

        lock (_gate)
        {
            if (_current is { } playing && playing.Source == source) return;
            _current?.End.TrySetResult();
            var theme = _current = new Theme(source);
            Runs.RemoveAll(run => run.IsCompleted);
            Runs.Add(Task.Run(() => RunAsync(theme, Options(headers, muted))));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _current?.End.TrySetResult();
            _current = null;
        }
    }

    private Dictionary<string, string> Options(IReadOnlyList<(string Name, string Value)> headers, bool muted)
    {
        var options = new Dictionary<string, string>
        {
            ["vid"] = "no",
            ["audio-display"] = "no",
            ["idle"] = "yes",
            ["config"] = "no",
            ["terminal"] = "no",
            ["input-default-bindings"] = "no",
            ["load-scripts"] = "no",
            ["ytdl"] = "no",
            ["volume"] = "0",
            ["audio-client-name"] = "Tuxflix",
            ["user-agent"] = $"Tuxflix/{BuildInfo.Version}",
        };
        if (headers.Count > 0) options["http-header-fields"] = string.Join(",", headers.Select(h => $"{h.Name}: {h.Value}"));
        if (muted) options["mute"] = "yes";
        if (_audioOutput is { } output) options["ao"] = output;
        return options;
    }

    private async Task RunAsync(Theme theme, Dictionary<string, string> options)
    {
        MpvPlayer? player = null;
        try
        {
            player = new MpvPlayer(options);
            lock (_gate) theme.Player = player;
            if (!theme.End.Task.IsCompleted)
            {
                player.Load(theme.Source);
                var level = await RampAsync(player, 0, Level, FadeIn, theme.End.Task).ConfigureAwait(false);
                await theme.End.Task.ConfigureAwait(false);
                await RampAsync(player, level, 0, FadeOut, until: null).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            Log.Warn("Theme music could not be played.", ex);
        }
        finally
        {
            lock (_gate) theme.Player = null;
            player?.Dispose();
        }
    }

    /// <summary>
    /// Moves the volume from one level to another in small steps; a fade in stops early when the
    /// theme is told to end, and the fade out starts from wherever it got to.
    /// </summary>
    /// <returns>The last level set.</returns>
    private static async Task<double> RampAsync(MpvPlayer player, double from, double to, TimeSpan duration, Task? until)
    {
        var steps = Math.Max(1, (int)(duration / Step));
        var level = from;
        for (var n = 1; n <= steps; n++)
        {
            if (until?.IsCompleted == true) break;
            await Task.Delay(Step).ConfigureAwait(false);
            level = from + ((to - from) * n / steps);
            player.PostNumber("volume", level);
        }

        return level;
    }

    private sealed class Theme(string source)
    {
        public string Source { get; } = source;

        /// <summary>Set when the theme is to fade out and end.</summary>
        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MpvPlayer? Player { get; set; }
    }
}
