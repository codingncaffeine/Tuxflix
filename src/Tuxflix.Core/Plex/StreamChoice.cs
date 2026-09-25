namespace Tuxflix.Core.Plex;

/// <summary>A track as the player lists it.</summary>
/// <param name="Type">"video", "audio" or "sub".</param>
/// <param name="Id">The player's id for the track.</param>
/// <param name="FileIndex">Its place in the media file, every kind counted from 0.</param>
/// <param name="Source">For a track loaded from a file of its own, the address it came from.</param>
public readonly record struct PlayerTrack(string Type, string Id, int? FileIndex, string? Source);

/// <summary>
/// Matches the streams the server lists for a part with the tracks the player found in the file.
/// </summary>
/// <remarks>
/// The server marks the audio and subtitle it has selected for this viewer: the account's language
/// settings, or a choice made for the item before. Every Plex player starts with that selection,
/// not with the file's own default flags, and tells the server when the viewer chooses another, in
/// the server's stream ids. Both sides number a file's streams in the same order, so a stream's
/// index finds its track. Where the numbers disagree but both sides list as many streams of that
/// kind, their order decides; otherwise the stream has no track and the player's choice stands.
/// </remarks>
public static class StreamChoice
{
    public const int Audio = 2;
    public const int Subtitle = 3;

    /// <summary>Whether the server described the part's streams; if not, the file's defaults stand.</summary>
    public static bool IsKnown(MediaPart part) => part.Stream is { Count: > 0 };

    /// <summary>The stream of a kind the server selected; for subtitles, none means off.</summary>
    public static MediaStream? Selected(MediaPart part, int streamType) =>
        part.Stream?.FirstOrDefault(s => s.StreamType == streamType && s.Selected);

    /// <summary>The subtitle files kept beside the media, which the player loads only when chosen.</summary>
    public static IEnumerable<MediaStream> ExternalSubtitles(MediaPart part) =>
        part.Stream?.Where(s => s.StreamType == Subtitle && s.IsExternal) ?? [];

    /// <summary>The player's track for a stream inside the file, or null if it has none.</summary>
    public static PlayerTrack? TrackFor(MediaStream stream, MediaPart part, IReadOnlyList<PlayerTrack> tracks)
    {
        if (TypeName(stream.StreamType) is not { } type || stream.Index is not { } index) return null;

        var inFile = tracks.Where(t => t.Type == type && t.Source is null).ToList();
        var kind = (part.Stream ?? []).Where(s => s.StreamType == stream.StreamType && s.Index is not null).OrderBy(s => s.Index).ToList();

        // The numbers agree only if every stream of the kind has exactly one track at its index;
        // one match on its own proves nothing, as a count shifted by one lands on the neighbour.
        if (kind.TrueForAll(s => inFile.Count(t => t.FileIndex == s.Index) == 1))
        {
            var match = inFile.FindIndex(t => t.FileIndex == index);
            return match >= 0 ? inFile[match] : null;
        }

        var place = kind.IndexOf(stream);
        return place >= 0 && kind.Count == inFile.Count ? inFile[place] : null;
    }

    /// <summary>The server's stream for a player track, or null if the server does not list it.</summary>
    /// <param name="sourceOf">The address the player loads a subtitle file from; null for one it is never given.</param>
    public static MediaStream? StreamFor(PlayerTrack track, MediaPart part, IReadOnlyList<PlayerTrack> tracks, Func<MediaStream, string?> sourceOf) =>
        track.Source is { } source
            ? ExternalSubtitles(part).FirstOrDefault(s => sourceOf(s) == source)
            : part.Stream?.FirstOrDefault(s => TypeName(s.StreamType) == track.Type && TrackFor(s, part, tracks) == track);

    private static string? TypeName(int streamType) => streamType switch
    {
        1 => "video",
        Audio => "audio",
        Subtitle => "sub",
        _ => null,
    };
}
