namespace Tuxflix.Core.Plex;

/// <summary>How an image should come back from the server's photo transcoder.</summary>
public enum ImageFormat
{
    /// <summary>The transcoder's default: small, and right for opaque artwork.</summary>
    Jpeg,

    /// <summary>Keeps transparency, which a clear logo needs: as JPEG its clear parts turn black.</summary>
    Png,
}

/// <summary>
/// Which of an item's images to show for each purpose.
/// </summary>
/// <remarks>
/// Plex describes artwork two ways: the classic attributes (<c>thumb</c>, <c>art</c>, and the
/// parent and grandparent variants on seasons and episodes) and, on current servers, an
/// <c>Image</c> array typed coverPoster, background, backgroundSquare, clearLogo and snapshot. The
/// array is preferred where it has an entry, the attributes fill in where it does not, so older
/// servers and newer ones both get the best picture they have.
/// </remarks>
public static class Artwork
{
    /// <summary>A 2:3 poster. Episodes and seasons borrow their show's poster.</summary>
    public static string? Poster(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type switch
        {
            "episode" => item.GrandparentThumb ?? item.ParentThumb ?? item.Thumb,
            _ => Typed(item, "coverPoster") ?? item.Thumb ?? item.ParentThumb,
        };
    }

    /// <summary>A 16:9 background for a hero or a card.</summary>
    public static string? Backdrop(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Typed(item, "background") ?? item.Art ?? item.GrandparentArt;
    }

    /// <summary>A 16:9 picture of the item itself: an episode's still, else the backdrop.</summary>
    public static string? Still(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type == "episode"
            ? Typed(item, "snapshot") ?? item.Thumb ?? Backdrop(item)
            : Backdrop(item) ?? item.Thumb;
    }

    /// <summary>The title as artwork, transparent, when the server has one.</summary>
    public static string? Logo(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Typed(item, "clearLogo");
    }

    private static string? Typed(MetadataItem item, string type) =>
        item.Image?.FirstOrDefault(image => string.Equals(image.Type, type, StringComparison.OrdinalIgnoreCase))?.Url
        is { Length: > 0 } url ? url : null;
}
