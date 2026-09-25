using Avalonia;

namespace Tuxflix.App.Tv;

/// <summary>A direction on the D-pad or the arrow keys.</summary>
public enum NavDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Picks where focus goes from one rectangle to the next in a direction: pure geometry, so it is
/// the same for tiles on a shelf, buttons in a bar or keys on a keyboard.
/// </summary>
/// <remarks>
/// Android's focus finder rules, which Android TV, Fire TV and most 10-foot interfaces inherit: a
/// candidate must lie in the direction pressed; one that overlaps the current rectangle across
/// the direction (in its "beam", as the tile straight below a tile is) beats one that does not,
/// unless the beam one is further away than the other one's far edge; otherwise the nearest
/// wins, distance along the direction weighing thirteen times as much as distance across it.
/// </remarks>
public static class SpatialNavigator
{
    /// <summary>The index in <paramref name="candidates"/> to move to, or -1 when nothing lies that way.</summary>
    public static int Pick(Rect from, NavDirection direction, IReadOnlyList<Rect> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var best = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (IsBetter(direction, from, candidates[i], best < 0 ? null : candidates[best])) best = i;
        }

        return best;
    }

    private static bool IsBetter(NavDirection direction, Rect source, Rect rect, Rect? best)
    {
        if (!IsCandidate(source, rect, direction)) return false;
        if (best is not { } current || !IsCandidate(source, current, direction)) return true;
        if (BeamBeats(direction, source, rect, current)) return true;
        if (BeamBeats(direction, source, current, rect)) return false;
        return Weighted(MajorDistance(direction, source, rect), MinorDistance(direction, source, rect))
               < Weighted(MajorDistance(direction, source, current), MinorDistance(direction, source, current));
    }

    /// <summary>Whether <paramref name="dest"/> lies in <paramref name="direction"/> from <paramref name="source"/>.</summary>
    private static bool IsCandidate(Rect source, Rect dest, NavDirection direction) => direction switch
    {
        NavDirection.Left => (source.Right > dest.Right || source.Left >= dest.Right) && source.Left > dest.Left,
        NavDirection.Right => (source.Left < dest.Left || source.Right <= dest.Left) && source.Right < dest.Right,
        NavDirection.Up => (source.Bottom > dest.Bottom || source.Top >= dest.Bottom) && source.Top > dest.Top,
        _ => (source.Top < dest.Top || source.Bottom <= dest.Top) && source.Bottom < dest.Bottom,
    };

    /// <summary>Whether one candidate, in the beam, beats another that is not.</summary>
    private static bool BeamBeats(NavDirection direction, Rect source, Rect first, Rect second)
    {
        var firstInBeam = BeamsOverlap(direction, source, first);
        if (BeamsOverlap(direction, source, second) || !firstInBeam) return false;
        if (!IsAhead(direction, source, second)) return true;

        // Across a row a tile in line always wins; up and down, only when it is nearer than the
        // other one's far edge, or a long row far away would lose to a short one in between.
        if (direction is NavDirection.Left or NavDirection.Right) return true;
        return MajorDistance(direction, source, first) < MajorDistanceToFarEdge(direction, source, second);
    }

    private static bool BeamsOverlap(NavDirection direction, Rect a, Rect b) => direction is NavDirection.Left or NavDirection.Right
        ? b.Bottom > a.Top && b.Top < a.Bottom
        : b.Right > a.Left && b.Left < a.Right;

    /// <summary>Wholly past the source's edge in the direction.</summary>
    private static bool IsAhead(NavDirection direction, Rect source, Rect dest) => direction switch
    {
        NavDirection.Left => source.Left >= dest.Right,
        NavDirection.Right => source.Right <= dest.Left,
        NavDirection.Up => source.Top >= dest.Bottom,
        _ => source.Bottom <= dest.Top,
    };

    private static double MajorDistance(NavDirection direction, Rect source, Rect dest) => Math.Max(0, direction switch
    {
        NavDirection.Left => source.Left - dest.Right,
        NavDirection.Right => dest.Left - source.Right,
        NavDirection.Up => source.Top - dest.Bottom,
        _ => dest.Top - source.Bottom,
    });

    private static double MajorDistanceToFarEdge(NavDirection direction, Rect source, Rect dest) => Math.Max(1, direction switch
    {
        NavDirection.Left => source.Left - dest.Left,
        NavDirection.Right => dest.Right - source.Right,
        NavDirection.Up => source.Top - dest.Top,
        _ => dest.Bottom - source.Bottom,
    });

    private static double MinorDistance(NavDirection direction, Rect source, Rect dest) => direction is NavDirection.Left or NavDirection.Right
        ? Math.Abs(source.Center.Y - dest.Center.Y)
        : Math.Abs(source.Center.X - dest.Center.X);

    private static double Weighted(double major, double minor) => (13 * major * major) + (minor * minor);
}
