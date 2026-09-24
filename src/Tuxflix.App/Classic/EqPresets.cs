namespace Tuxflix.App.Classic;

/// <summary>An equalizer preset: the preamp and the ten bands, in dB.</summary>
public sealed record EqPreset(string Name, double Preamp, double[] Bands);

/// <summary>
/// Winamp's built-in equalizer presets, as Webamp keeps them (MIT, see NOTICES), turned from
/// Winamp's slider steps (1 to 64, with 33 flat) into decibels.
/// </summary>
public static class EqPresets
{
    public static EqPreset Flat { get; } = new("Flat", 0, new double[10]);

    public static IReadOnlyList<EqPreset> BuiltIn { get; } =
    [
        new("Classical", 0, [0, 0, 0, 0, 0, 0, -5, -5, -5, -6.6]),
        new("Club", 0, [0, 0, 1.9, 3.5, 3.5, 3.5, 1.9, 0, 0, 0]),
        new("Dance", 0, [5.8, 4.3, 1.2, -0.4, -0.4, -4.3, -5, -5, -0.4, -0.4]),
        new("Laptop speakers/headphones", 0, [2.7, 6.6, 3.1, -2.7, -1.9, 0.8, 2.7, 5.8, 7.7, 8.9]),
        new("Large hall", 0, [6.2, 6.2, 3.5, 3.5, 0, -3.5, -3.5, -3.5, 0, 0]),
        new("Party", 0, [4.3, 4.3, 0, 0, 0, 0, 0, 0, 4.3, 4.3]),
        new("Pop", 0, [-1.5, 2.7, 4.3, 4.6, 3.1, -1.2, -1.9, -1.9, -1.5, -1.5]),
        new("Reggae", 0, [0, 0, -0.8, -4.3, 0, 3.9, 3.9, 0, 0, 0]),
        new("Rock", 0, [4.6, 2.7, -3.9, -5.4, -2.7, 2.3, 5.4, 6.6, 6.6, 6.6]),
        new("Soft", 0, [2.7, 0.8, -1.2, -1.9, -1.2, 2.3, 5, 5.8, 6.6, 7.4]),
        new("Ska", 0, [-1.9, -3.5, -3.1, -0.8, 2.3, 3.5, 5.4, 5.8, 6.6, 5.8]),
        new("Full Bass", 0, [5.8, 5.8, 5.8, 3.5, 0.8, -3.1, -5.8, -7, -7.4, -7.4]),
        new("Soft Rock", 0, [2.3, 2.3, 1.2, -0.8, -3.1, -3.9, -2.7, -0.8, 1.5, 5.4]),
        new("Full Treble", 0, [-6.6, -6.6, -6.6, -3.1, 1.5, 6.6, 9.7, 9.7, 9.7, 10.5]),
        new("Full Bass & Treble", 0, [4.3, 3.5, 0, -5, -3.5, 0.8, 5, 6.6, 7.4, 7.4]),
        new("Live", 0, [-3.5, 0, 2.3, 3.1, 3.5, 3.5, 2.3, 1.5, 1.5, 1.2]),
        new("Techno", 0, [4.6, 3.5, 0, -3.9, -3.5, 0, 4.6, 5.8, 5.8, 5.4]),
    ];
}
