namespace Tuxflix.Core.Plex;

/// <summary>
/// What a file's markers mean at a moment of playback: which one the viewer may skip, and when the
/// next episode is offered.
/// </summary>
/// <remarks>
/// Markers are told apart by kind and start, never by <c>id</c>: the server's id is not unique (an
/// episode's intro and credits can carry the same one), and keying on it hides the credits once the
/// intro was skipped. The final credits belong to the next episode's card; with nothing to follow
/// they can be skipped like any others, which ends the file.
/// </remarks>
public sealed class MarkerTimeline(IReadOnlyList<Marker> markers)
{
    /// <summary>The skip goes away this long before its marker ends: a skip there would land where the viewer already is.</summary>
    public const long SkipCutoffMs = 1500;

    /// <summary>With no final credits marked, the next episode is offered this long before the end.</summary>
    public const long NextLeadMs = 30_000;

    private readonly HashSet<(string, long)> _skipped = [];

    /// <summary>The intro or credits under the playhead that can still be skipped, or null.</summary>
    /// <param name="ms">The playhead, in milliseconds.</param>
    /// <param name="hasNext">Whether an episode follows: then the final credits are its card's, not a skip.</param>
    public Marker? SkippableAt(long ms, bool hasNext)
    {
        var marker = markers.FirstOrDefault(m => m.Type is "intro" or "credits" && ms >= m.StartTimeOffset && ms < m.EndTimeOffset - SkipCutoffMs);
        if (marker is null || (marker.Final && hasNext)) return null;
        return _skipped.Contains((marker.Type, marker.StartTimeOffset)) ? null : marker;
    }

    /// <summary>The marker was skipped: it does not offer itself again in this playback.</summary>
    public void Skipped(Marker marker) => _skipped.Add((marker.Type, marker.StartTimeOffset));

    /// <summary>Whether the playhead is where the next episode is offered: in the final credits, or the last half-minute when none are marked.</summary>
    public bool OffersNext(long ms, long durationMs)
    {
        var final = markers.FirstOrDefault(m => m is { Type: "credits", Final: true });
        return final is not null ? ms >= final.StartTimeOffset : durationMs > 0 && ms >= durationMs - NextLeadMs;
    }
}
