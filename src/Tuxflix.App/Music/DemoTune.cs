using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Music;

/// <summary>
/// The demo library's songs: each track a little tune that libavfilter synthesizes as it plays (a
/// kick on the beat, a hat between, a bass line and a chord that moves every bar), so the demo's
/// music plays, levels, analyses and visualizes like real music with no file anywhere.
/// </summary>
internal static class DemoTune
{
    private static readonly double[] Roots = [110.0, 123.47, 130.81, 146.83, 164.81];

    public static string Address(MetadataItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var seed = 0u;
        foreach (var c in track.Title) seed = (seed ^ c) * 16777619u;
        var root = Roots[seed % (uint)Roots.Length];
        var beat = 60.0 / (92 + (seed % 44));
        var seconds = Math.Max(20, (track.Duration ?? 180_000) / 1000.0);
        string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

        // Each term, then the whole faded in over two seconds and out over three.
        var chord = $"(1+0.335*gt(mod(t,{F(8 * beat)}),{F(4 * beat)}))";
        var kick = $"0.5*sin(2*PI*52*t)*exp(-9*mod(t,{F(beat)}))";
        var bass = $"0.16*sin(2*PI*{F(root / 2)}*{chord}*t)*exp(-2.5*mod(t,{F(2 * beat)}))";
        var pad = $"(0.12*sin(2*PI*{F(root * 2)}*{chord}*t)+0.08*sin(2*PI*{F(root * 3)}*{chord}*t))*(0.7+0.3*sin(2*PI*t/{F(4 * beat)}))";
        var fade = $"min(1,t/2)*min(1,({F(seconds)}-t)/3)";
        string Channel(int n) => $"({kick}+0.05*(random({n})*2-1)*exp(-45*mod(t+{F(beat / 2)},{F(beat)}))+{bass}+{pad})*{fade}";
        return $"av://lavfi:aevalsrc=exprs='{Channel(0)}|{Channel(1)}':s=48000:d={F(seconds)}";
    }
}
