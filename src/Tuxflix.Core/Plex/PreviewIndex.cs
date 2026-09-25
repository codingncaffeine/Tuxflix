using System.Buffers.Binary;

namespace Tuxflix.Core.Plex;

/// <summary>
/// A part's seek previews: the small pictures a server makes every few seconds of a film, kept in
/// one BIF file (Roku's format, which Plex serves at <c>/library/parts/{id}/indexes/sd</c>).
/// </summary>
/// <remarks>
/// The file is an 8-byte signature, a version, the picture count and the time unit in
/// milliseconds (0 meaning 1000), then from byte 64 an index of (time, offset) pairs ending with
/// time 0xFFFFFFFF at the end of the last picture, then the JPEG pictures themselves. Every count
/// and offset is checked against the file's length: a damaged file reads as no previews.
/// </remarks>
public sealed class PreviewIndex
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x42, 0x49, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int IndexStart = 64;
    private const int MaxPictures = 100_000;

    private readonly byte[] _data;
    private readonly (long TimeMs, int Offset, int Length)[] _pictures;

    private PreviewIndex(byte[] data, (long, int, int)[] pictures)
    {
        _data = data;
        _pictures = pictures;
    }

    public int Count => _pictures.Length;

    /// <summary>Reads a BIF file; null when it is not one or does not add up.</summary>
    public static PreviewIndex? Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < IndexStart || !data.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var unit = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (unit == 0) unit = 1000;
        if (count == 0 || count > MaxPictures || IndexStart + ((count + 1) * 8L) > data.Length) return null;

        var pictures = new (long, int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(IndexStart + (i * 8));
            var time = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (offset >= next || next > data.Length) return null;
            pictures[i] = (time * (long)unit, (int)offset, (int)(next - offset));
        }

        return new PreviewIndex(data, pictures);
    }

    /// <summary>The picture shown at <paramref name="timeMs"/>: the last one taken at or before it.</summary>
    public int IndexAt(long timeMs)
    {
        var low = 0;
        var high = _pictures.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_pictures[middle].TimeMs <= timeMs) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    /// <summary>The JPEG bytes of picture <paramref name="index"/>.</summary>
    public ReadOnlyMemory<byte> Picture(int index)
    {
        var (_, offset, length) = _pictures[index];
        return _data.AsMemory(offset, length);
    }

    /// <summary>Builds a BIF file from pictures taken every <paramref name="intervalMs"/> (the demo library serves one).</summary>
    public static byte[] Build(IReadOnlyList<byte[]> pictures, uint intervalMs)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        var start = IndexStart + ((pictures.Count + 1) * 8);
        var data = new byte[start + pictures.Sum(p => p.Length)];
        Signature.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)pictures.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), intervalMs);
        var offset = start;
        for (var i = 0; i <= pictures.Count; i++)
        {
            var entry = data.AsSpan(IndexStart + (i * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, i < pictures.Count ? (uint)i : uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)offset);
            if (i < pictures.Count)
            {
                pictures[i].CopyTo(data, offset);
                offset += pictures[i].Length;
            }
        }

        return data;
    }
}
