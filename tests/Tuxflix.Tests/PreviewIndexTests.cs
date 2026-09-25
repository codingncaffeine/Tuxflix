using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>Seek previews from a BIF file: finding the picture for a time, and refusing a damaged file.</summary>
public sealed class PreviewIndexTests
{
    private static byte[] Picture(byte mark, int length = 5) => [.. Enumerable.Repeat(mark, length)];

    [Fact]
    public void ThePictureForATimeIsTheLastTakenAtOrBeforeIt()
    {
        var index = PreviewIndex.Parse(PreviewIndex.Build([Picture(1), Picture(2), Picture(3)], intervalMs: 2000))!;

        Assert.Equal(3, index.Count);
        Assert.Equal(0, index.IndexAt(0));
        Assert.Equal(0, index.IndexAt(1999));
        Assert.Equal(1, index.IndexAt(2000));
        Assert.Equal(2, index.IndexAt(4000));
        Assert.Equal(2, index.IndexAt(999_999));
        Assert.Equal([2, 2, 2, 2, 2], index.Picture(1).ToArray());
    }

    [Fact]
    public void ATimeUnitOfZeroMeansSeconds()
    {
        var index = PreviewIndex.Parse(PreviewIndex.Build([Picture(1), Picture(2)], intervalMs: 0))!;

        Assert.Equal(0, index.IndexAt(999));
        Assert.Equal(1, index.IndexAt(1000));
    }

    [Fact]
    public void ADamagedFileReadsAsNoPreviews()
    {
        var good = PreviewIndex.Build([Picture(1), Picture(2)], intervalMs: 1000);
        var truncated = good[..^3];
        var notBif = (byte[])good.Clone();
        notBif[1] = (byte)'X';
        var tooMany = (byte[])good.Clone();
        tooMany[12] = 0xFF;
        tooMany[13] = 0xFF;

        Assert.NotNull(PreviewIndex.Parse(good));
        Assert.Null(PreviewIndex.Parse(truncated));
        Assert.Null(PreviewIndex.Parse(notBif));
        Assert.Null(PreviewIndex.Parse(tooMany));
        Assert.Null(PreviewIndex.Parse([]));
    }
}
