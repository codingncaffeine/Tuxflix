namespace Tuxflix.Core.Plex;

/// <summary>
/// A track's shape for a seek bar, from the server's loudness readings: a bar per slice, as tall
/// as the slice is loud against the track's own loudest moment.
/// </summary>
/// <remarks>
/// Heights are relative to the track, over <see cref="RangeDb"/> below its loudest reading, as
/// Plexamp and SoundCloud draw them: a quiet track is not drawn flat and a loud one not solid, and
/// the verse, the chorus and a fade read at a glance. A slice keeps the loudest reading in it, so
/// a short loud moment is not averaged away when many readings share one bar.
/// </remarks>
public static class LoudnessWaveform
{
    /// <summary>How far below the loudest reading a bar reaches the floor.</summary>
    public const double RangeDb = 30;

    /// <summary>The lowest bar, so silence still shows where the track is.</summary>
    public const float Floor = 0.06f;

    /// <summary>Bar heights 0..1, <paramref name="bars"/> of them; empty when there are no readings.</summary>
    public static float[] FromLevels(IReadOnlyList<double> levels, int bars)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0 || bars <= 0) return [];
        var finite = levels.Where(double.IsFinite).ToList();
        if (finite.Count == 0) return [];
        var loudest = finite.Max();
        var heights = new float[bars];
        for (var b = 0; b < bars; b++)
        {
            // The readings this bar covers: a slice of them, or the nearest one when bars outnumber them.
            var from = (int)((long)b * levels.Count / bars);
            var to = Math.Max(from + 1, (int)((long)(b + 1) * levels.Count / bars));
            var slice = double.NegativeInfinity;
            for (var i = from; i < to && i < levels.Count; i++)
            {
                if (double.IsFinite(levels[i])) slice = Math.Max(slice, levels[i]);
            }

            var height = double.IsFinite(slice) ? 1 - ((loudest - slice) / RangeDb) : 0;
            heights[b] = (float)Math.Clamp(height, Floor, 1);
        }

        return heights;
    }
}
