using System.Text.Json;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The badges a file earns: a listing's record says only its resolution and its soundtrack's
/// kind; the full record says its picture's dynamic range too. Shapes as a real server sends them.
/// </summary>
public sealed class QualityBadgesTests
{
    private static MediaStream Picture(string? transfer = null, bool vision = false) => new() { StreamType = 1, ColorTrc = transfer, DolbyVision = vision };

    private static MediaStream Sound(string? profile) => new() { StreamType = 2, Profile = profile };

    private static Media File(string resolution, string? audioProfile, params MediaStream[] streams) =>
        new() { VideoResolution = resolution, AudioProfile = audioProfile, Part = [new MediaPart { Stream = [.. streams] }] };

    [Fact]
    public void AListingSaysTheResolutionAndTheSoundAndTheFullRecordThePictureToo()
    {
        Assert.Equal(["4K", "ATMOS"], QualityBadges.Listed(File("4k", "dolby truehd + dolby atmos")));
        Assert.Equal(["4K", "DTS:X"], QualityBadges.Listed(File("4k", "ma + dts:x")));
        Assert.Equal(["ATMOS"], QualityBadges.Listed(File("1080", "dolby truehd + dolby atmos")));
        Assert.Empty(QualityBadges.Listed(File("1080", "ma")));
        Assert.Empty(QualityBadges.Listed(null));

        // A listing has no streams: however HDR the file, the poster cannot say so.
        Assert.Equal(["4K"], QualityBadges.Listed(File("4k", null, Picture("smpte2084", vision: true))));

        // The full record: Dolby Vision with its HDR10 base, HDR10, HLG.
        Assert.Equal(["4K", "DOLBY VISION", "HDR10", "ATMOS"], QualityBadges.Full(File("4k", "dolby truehd + dolby atmos", Picture("smpte2084", vision: true), Sound("dolby truehd + dolby atmos"))));
        Assert.Equal(["4K", "HDR10"], QualityBadges.Full(File("4k", "lc", Picture("smpte2084"), Sound("lc"))));
        Assert.Equal(["HLG"], QualityBadges.Full(File("1080", null, Picture("arib-std-b67"))));
        Assert.Empty(QualityBadges.Full(File("1080", null, Picture("bt709"), Sound(null))));

        // Any soundtrack's Atmos counts, and Atmos is said before DTS:X.
        Assert.Equal(["ATMOS"], QualityBadges.Full(File("1080", "lc", Picture(), Sound("lc"), Sound("dolby digital plus + dolby atmos"))));
        Assert.Equal(["ATMOS"], QualityBadges.Full(File("1080", "ma + dts:x", Picture(), Sound("ma + dts:x"), Sound("dolby truehd + dolby atmos"))));
        Assert.Equal(["DTS:X"], QualityBadges.Full(File("1080", "ma + dts:x", Picture(), Sound("ma + dts:x"))));
    }

    [Fact]
    public void TheServersOwnFieldNamesAreRead()
    {
        const string Json = """
            {"MediaContainer":{"Metadata":[{"ratingKey":"1","type":"movie","title":"A film",
             "Media":[{"videoResolution":"4k","audioProfile":"dolby truehd + dolby atmos","Part":[{"id":7,"Stream":[
               {"id":1,"streamType":1,"codec":"hevc","profile":"main 10","colorTrc":"smpte2084","DOVIPresent":true,"DOVIProfile":8},
               {"id":2,"streamType":2,"codec":"truehd","profile":"dolby truehd + dolby atmos"}]}]}]}]}}
            """;
        var media = JsonSerializer.Deserialize(Json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!.Metadata![0].Media![0];
        var picture = media.Part![0].Stream![0];
        Assert.Equal("dolby truehd + dolby atmos", media.AudioProfile);
        Assert.Equal("smpte2084", picture.ColorTrc);
        Assert.True(picture.DolbyVision);
        Assert.Equal(8, picture.DolbyVisionProfile);
        Assert.Equal("main 10", picture.Profile);
        Assert.Equal(["4K", "DOLBY VISION", "HDR10", "ATMOS"], QualityBadges.Full(media));
    }
}
