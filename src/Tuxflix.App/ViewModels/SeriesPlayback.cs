using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>What a series or a season plays: the episode that is next, or all of them.</summary>
/// <remarks>Every request runs on a worker.</remarks>
internal static class SeriesPlayback
{
    /// <summary>The episode to play: the first started or unwatched one, else the very first.</summary>
    /// <param name="seasons">The series' seasons when the caller has them already; read from the server if not.</param>
    public static async Task<MetadataItem?> NextAsync(ServerSession session, MetadataItem showOrSeason, IReadOnlyList<MetadataItem>? seasons = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(showOrSeason);
        seasons ??= showOrSeason.Type == "season"
            ? [showOrSeason]
            : [.. await Task.Run(() => session.Client.GetChildrenAsync(showOrSeason.RatingKey, CancellationToken.None))];

        // Specials (season 0) come last: a series starts at its first real season.
        MetadataItem? first = null;
        foreach (var season in seasons.Where(s => s.Index is not 0).Concat(seasons.Where(s => s.Index is 0)))
        {
            var episodes = await Task.Run(() => session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None));
            first ??= episodes.FirstOrDefault();
            if (episodes.FirstOrDefault(e => !e.IsWatched) is { } next) return next;
        }

        return first;
    }

    /// <summary>Every episode of a series or a season, in order.</summary>
    public static Task<IReadOnlyList<MetadataItem>> AllAsync(ServerSession session, MetadataItem showOrSeason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(showOrSeason);
        return Task.Run(() => showOrSeason.Type == "season"
            ? session.Client.GetChildrenAsync(showOrSeason.RatingKey, CancellationToken.None)
            : session.Client.GetAllLeavesAsync(showOrSeason.RatingKey, CancellationToken.None));
    }
}
