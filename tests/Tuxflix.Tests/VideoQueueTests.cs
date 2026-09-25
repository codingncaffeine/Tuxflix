using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>What plays next from a playlist, a collection or a season.</summary>
public sealed class VideoQueueTests
{
    private static MetadataItem Film(int n) => new() { RatingKey = n.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = "movie", Title = $"Film {n}" };

    [Fact]
    public void TheQueueKeepsTheListsOrderAndEnds()
    {
        var queue = VideoQueue.Of([Film(1), Film(2), Film(3)], shuffle: false)!;

        Assert.Equal(["1", "2", "3"], new[] { queue.Current, queue.Advance().Current, queue.Advance().Advance().Current }.Select(i => i.RatingKey));
        Assert.Equal("2", queue.Next?.RatingKey);
        var last = queue.Advance().Advance();
        Assert.Null(last.Next);
        Assert.Same(last, last.Advance());
    }

    [Fact]
    public void OnlyFilmsAndEpisodesAreQueued()
    {
        var track = new MetadataItem { RatingKey = "9", Type = "track", Title = "A song" };
        var episode = new MetadataItem { RatingKey = "5", Type = "episode", Title = "An episode" };

        var queue = VideoQueue.Of([track, Film(1), episode], shuffle: false)!;

        Assert.Equal(2, queue.Count);
        Assert.Equal(["1", "5"], new[] { queue.Current.RatingKey, queue.Next!.RatingKey });
        Assert.Null(VideoQueue.Of([track], shuffle: false));
    }

    [Fact]
    public void ShufflingKeepsEveryItemOnceInAnotherOrder()
    {
        var films = Enumerable.Range(1, 20).Select(Film).ToList();

        var queue = VideoQueue.Of(films, shuffle: true, new Random(7))!;
        var order = new List<string>();
        for (var q = queue; ; q = q.Advance())
        {
            order.Add(q.Current.RatingKey);
            if (q.Next is null) break;
        }

        Assert.Equal(films.Select(f => f.RatingKey).Order(), order.Order());
        Assert.NotEqual(films.Select(f => f.RatingKey), order);
    }
}
