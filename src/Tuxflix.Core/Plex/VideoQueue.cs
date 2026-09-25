namespace Tuxflix.Core.Plex;

/// <summary>
/// What plays after this one: the rest of a playlist, a collection or a season, in their order or
/// shuffled once for the whole run. Films and episodes only; anything else in a list is passed over.
/// </summary>
public sealed class VideoQueue
{
    private readonly IReadOnlyList<MetadataItem> _items;

    private VideoQueue(IReadOnlyList<MetadataItem> items, int index)
    {
        _items = items;
        Index = index;
    }

    public int Index { get; }

    public int Count => _items.Count;

    public MetadataItem Current => _items[Index];

    /// <summary>The one after this, or null at the end.</summary>
    public MetadataItem? Next => Index + 1 < _items.Count ? _items[Index + 1] : null;

    /// <summary>The queue moved on to the next item; the same queue at the end.</summary>
    public VideoQueue Advance() => Next is null ? this : new VideoQueue(_items, Index + 1);

    /// <summary>A queue of the films and episodes in <paramref name="items"/>, starting at the first; null when there are none.</summary>
    public static VideoQueue? Of(IEnumerable<MetadataItem> items, bool shuffle, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var playable = items.Where(i => i.Type is "movie" or "episode").ToArray();
        if (playable.Length == 0) return null;
        if (shuffle) (random ?? Random.Shared).Shuffle(playable);
        return new VideoQueue(playable, 0);
    }
}
