using SkiaSharp;

namespace Tuxflix.App.Imaging;

/// <summary>
/// A picture's size, read from its header before it is decoded. A few kilobytes of PNG, or of a
/// run-length bitmap, can claim a picture of gigabytes, and decoding allocates all of it: artwork
/// comes from servers and skins from anyone who uploaded one to the museum.
/// </summary>
internal static class ImageBounds
{
    /// <summary>Artwork from a server, which scales it to the size it is drawn at first.</summary>
    public const long ArtworkPixels = 64L * 1024 * 1024;

    /// <summary>A skin's sheet, a seek preview, a museum screenshot: small pictures, all of them.</summary>
    public const long SmallPixels = 4L * 1024 * 1024;

    /// <summary>
    /// Throws <see cref="ArgumentException"/>, as a picture that cannot be decoded does, when
    /// <paramref name="picture"/> is not one Skia can read or claims more than <paramref name="maxPixels"/>.
    /// </summary>
    public static void Check(byte[] picture, long maxPixels)
    {
        ArgumentNullException.ThrowIfNull(picture);
        using var codec = SKCodec.Create(new MemoryStream(picture, writable: false))
                          ?? throw new ArgumentException("Not a picture that can be read.", nameof(picture));
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if ((long)width * height > maxPixels)
        {
            throw new ArgumentException($"A picture of {width}x{height} is larger than any shown here; it is not decoded.", nameof(picture));
        }
    }
}
