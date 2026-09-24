using System.Globalization;

namespace Tuxflix.Core.Demo;

/// <summary>
/// The colours of a demo title, shared by the painter that draws its artwork and the catalogue
/// that reports its UltraBlur colours, so the two can never disagree.
/// </summary>
public readonly record struct DemoPalette(float GroundHue, float LightHue, float Saturation)
{
    // Pairs a film would actually be graded in (a ground hue and a light hue), so a shelf of them
    // reads as a varied library rather than one colour at ten brightnesses.
    private static readonly (float Ground, float Light, float Saturation)[] Grades =
    [
        (195, 28, 80),   // teal and orange
        (225, 44, 75),   // midnight and gold
        (352, 12, 78),   // crimson noir
        (150, 40, 70),   // forest and amber
        (268, 318, 82),  // violet and pink
        (205, 188, 70),  // ice and cyan
        (24, 38, 72),    // desert dusk
        (165, 88, 68),   // emerald and lime
        (212, 202, 76),  // deep ocean
        (330, 276, 72),  // rose and lavender
        (38, 205, 70),   // amber and sky
        (180, 350, 74),  // sea glass and coral
    ];

    public static DemoPalette From(int seed)
    {
        var grade = Grades[(seed / 7) % Grades.Length];
        var jitter = (seed / 3 % 17) - 8;
        return new DemoPalette((grade.Ground + jitter + 360) % 360, (grade.Light + jitter + 360) % 360, grade.Saturation);
    }

    /// <summary>The four corner colours Plex would compute for this title's backdrop.</summary>
    public (string TopLeft, string TopRight, string BottomRight, string BottomLeft) UltraBlur() =>
    (
        Hex(GroundHue, 45, 30),
        Hex(LightHue, Saturation * 0.8f, 40),
        Hex(GroundHue, 50, 18),
        Hex(GroundHue, 40, 12));

    /// <summary>HSL (degrees, percent, percent) to RRGGBB hex, the form Plex reports colours in.</summary>
    public static string Hex(float hue, float saturation, float lightness)
    {
        var s = saturation / 100f;
        var l = lightness / 100f;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var h = hue / 60f;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0f),
            < 2 => (x, c, 0f),
            < 3 => (0f, c, x),
            < 4 => (0f, x, c),
            < 5 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        var m = l - c / 2;
        static int Byte(float v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return string.Create(CultureInfo.InvariantCulture, $"{Byte(r + m):x2}{Byte(g + m):x2}{Byte(b + m):x2}");
    }
}
