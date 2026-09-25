using System.Text;

namespace Tuxflix.Core.Demo;

/// <summary>
/// A short real video for the demo's downloads: uncompressed YUV4MPEG2, which mpv and every
/// ffmpeg-based player read, made byte by byte so that any range of it can be served without the
/// whole file existing anywhere. Bars drift across the picture and a bar along the foot fills as
/// it plays, in a colour of the title's own.
/// </summary>
public sealed class DemoVideo
{
    public const int Width = 320;
    public const int Height = 180;
    public const int FramesPerSecond = 10;

    private const int LumaBytes = Width * Height;
    private const int ChromaBytes = (Width / 2) * (Height / 2);
    private static readonly byte[] FrameMark = Encoding.ASCII.GetBytes("FRAME\n");
    private static readonly byte[] Header = Encoding.ASCII.GetBytes($"YUV4MPEG2 W{Width} H{Height} F{FramesPerSecond}:1 Ip A1:1 C420jpeg\n");

    private readonly int _seed;

    public DemoVideo(int seed, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        _seed = Math.Abs(seed);
        Frames = frames;
    }

    public int Frames { get; }

    /// <summary>Bytes in one frame, its mark included.</summary>
    public static int FrameBytes => FrameMark.Length + LumaBytes + (2 * ChromaBytes);

    public long Length => Header.Length + ((long)Frames * FrameBytes);

    /// <summary>The demo's file for an item: a few megabytes, a few seconds, the same every time.</summary>
    public static DemoVideo For(string ratingKey)
    {
        ArgumentNullException.ThrowIfNull(ratingKey);
        var seed = ratingKey.Aggregate(17, (hash, c) => unchecked((hash * 31) + c));
        return new DemoVideo(seed, 40 + (Math.Abs(seed) % 24));
    }

    /// <summary>Fills <paramref name="destination"/> with the file's bytes from <paramref name="offset"/> on.</summary>
    public void Read(long offset, Span<byte> destination)
    {
        var tintU = (byte)(98 + (_seed % 60));
        var tintV = (byte)(158 - ((_seed / 7) % 60));
        for (var i = 0; i < destination.Length; i++)
        {
            var position = offset + i;
            if (position < Header.Length)
            {
                destination[i] = Header[position];
                continue;
            }

            var inFrames = position - Header.Length;
            var frame = (int)(inFrames / FrameBytes);
            var within = (int)(inFrames % FrameBytes);
            if (within < FrameMark.Length)
            {
                destination[i] = FrameMark[within];
                continue;
            }

            var pixel = within - FrameMark.Length;
            destination[i] = pixel < LumaBytes ? Luma(frame, pixel % Width, pixel / Width)
                : pixel < LumaBytes + ChromaBytes ? tintU
                : tintV;
        }
    }

    /// <summary>The whole file at once, for checking a download against.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Length];
        Read(0, bytes);
        return bytes;
    }

    private byte Luma(int frame, int x, int y)
    {
        if (y >= Height - 10) return x * Frames < (frame + 1) * Width ? (byte)235 : (byte)24;
        return ((x + (frame * 6) + (_seed % 24)) / 24 % 2 == 0) ? (byte)(150 + (y / 6)) : (byte)(52 + (y / 9));
    }
}
