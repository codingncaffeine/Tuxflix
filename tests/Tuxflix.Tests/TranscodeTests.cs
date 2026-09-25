using System.Net;
using System.Text.Json;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>Asking the server how to play: the parameters, the profile, and reading its answer.</summary>
public sealed class TranscodeTests
{
    // A device name with a space: the player's address must keep it escaped.
    private static readonly PlexClientIdentity Identity = new("test-client", "0.0.0", "test machine");

    // The shapes and words of a real server's answers (PMS 1.43), with invented media.
    // A decision's metadata carries its UltraBlur colours as a list, unlike the item's own metadata.
    private const string DirectPlayAnswer = """
        {"MediaContainer":{"size":1,"mdeDecisionCode":1000,"mdeDecisionText":"Direct play OK.","Metadata":[{"ratingKey":"7","type":"episode","title":"Pilot",
         "UltraBlurColors":[{"topLeft":"15236d","topRight":"8c491f","bottomLeft":"232a93","bottomRight":"2434a1"}],
         "Media":[{"id":1,"container":"mkv","videoCodec":"h264","audioCodec":"dca-ma","bitrate":26483,"selected":true,
          "Part":[{"id":2,"decision":"directplay","container":"mkv","Stream":[{"id":3,"streamType":1,"codec":"h264","width":1920,"height":1080},{"id":4,"streamType":2,"codec":"dca","selected":true}]}]}]}]}}
        """;

    private const string ConversionAnswer = """
        {"MediaContainer":{"size":1,"directPlayDecisionCode":3000,"directPlayDecisionText":"App cannot direct play this item. Direct play is disabled.",
         "generalDecisionCode":1001,"generalDecisionText":"Direct play not available; Conversion OK.","transcodeDecisionCode":1001,"transcodeDecisionText":"Direct play not available; Conversion OK.",
         "Metadata":[{"ratingKey":"7","type":"episode","title":"Pilot",
         "Media":[{"id":1,"container":"mp4","videoCodec":"h264","audioCodec":"aac","bitrate":3493,"protocol":"hls","selected":true,
          "Part":[{"id":2,"decision":"transcode","container":"mp4","Stream":[{"id":3,"streamType":1,"codec":"h264","decision":"transcode","width":1280,"height":720},{"id":4,"streamType":2,"codec":"aac","decision":"transcode","selected":true}]}]}]}]}}
        """;

    private static MediaContainer Read(string json) => JsonSerializer.Deserialize(json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!;

    private static Dictionary<string, string> Parameters(TranscodeRequest request) => request.Parameters(Identity).ToDictionary(p => p.Key, p => p.Value);

    private static TranscodeRequest Ask(StreamQuality quality) => new("7", quality, "session-1", "playback-1");

    [Fact]
    public void TheOriginalAsksForDirectPlayAndCapsNothing()
    {
        var request = Ask(StreamQuality.Original);
        var p = Parameters(request);

        Assert.Equal(("1", "1", "1", "none"), (p["hasMDE"], p["directPlay"], p["directStream"], p["subtitles"]));
        Assert.False(p.ContainsKey("maxVideoBitrate"));
        Assert.False(p.ContainsKey("videoResolution"));
        Assert.DoesNotContain("add-limitation", request.ProfileExtra, StringComparison.Ordinal);
        Assert.Equal("/library/metadata/7", p["path"]);
        Assert.Equal(("session-1", "playback-1", "lan"), (p["session"], p["X-Plex-Session-Identifier"], p["location"]));
    }

    [Fact]
    public void AConversionCapsBitrateSizeAndQuality()
    {
        var quality = StreamQuality.All.Single(q => q.Kbps == 4000);
        var request = Ask(quality) with { Remote = true, AudioStreamId = 44 };
        var p = Parameters(request);

        Assert.Equal(("0", "0"), (p["directPlay"], p["directStream"]));
        Assert.Equal(("4000", "1280x720", "100"), (p["maxVideoBitrate"], p["videoResolution"], p["videoQuality"]));
        Assert.Equal(("wan", "44"), (p["location"], p["audioStreamID"]));
        Assert.Contains("add-limitation(scope=videoCodec&scopeName=*&type=upperBound&name=video.bitrate&value=4000&replace=true)", request.ProfileExtra, StringComparison.Ordinal);
    }

    [Fact]
    public void BurningInASubtitleNeverAsksForDirectPlay()
    {
        var p = Parameters(Ask(StreamQuality.Original) with { BurnSubtitles = true });

        Assert.Equal(("0", "1", "burn"), (p["directPlay"], p["directStream"], p["subtitles"]));
    }

    [Fact]
    public void HevcIsOfferedInFragmentedMp4AndNeverInTransportStream()
    {
        var mp4 = Ask(StreamQuality.Original).ProfileExtra;
        var ts = (Ask(StreamQuality.Original) with { TransportStream = true }).ProfileExtra;

        Assert.Contains("protocol=hls&container=mp4&videoCodec=h264,hevc&", mp4, StringComparison.Ordinal);
        Assert.Contains("protocol=hls&container=mpegts&videoCodec=h264&", ts, StringComparison.Ordinal);
        Assert.DoesNotContain("hevc", ts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartAddressKeepsTheProfileEscaped()
    {
        var client = new PlexServerClient(new HttpClient(), new Uri("https://server.example:32400"), "token", "Test");
        var address = client.TranscodeAddress(Ask(StreamQuality.All[1]), Identity);

        Assert.StartsWith("https://server.example:32400/video/:/transcode/universal/start.m3u8?", address, StringComparison.Ordinal);
        Assert.Contains("X-Plex-Client-Profile-Extra=add-settings%28DirectPlayStreamSelection%3Dtrue%29%2Badd-limitation%28", address, StringComparison.Ordinal);
        Assert.Contains("scopeName%3D%2A", address, StringComparison.Ordinal);
        Assert.Contains("X-Plex-Device-Name=test%20machine", address, StringComparison.Ordinal);
        Assert.DoesNotContain("token", address, StringComparison.Ordinal);
        Assert.DoesNotContain(address.Split('?')[1], c => c is '(' or ')' or '*' or ' ' or '+');
    }

    [Fact]
    public void ADirectPlayAnswerPlaysTheFile()
    {
        var answer = Read(DirectPlayAnswer);
        var decision = PlaybackDecision.From(answer, "mp4");

        Assert.Equal(PlaybackRoute.DirectPlay, decision.Route);
        Assert.Equal("Direct play OK.", decision.Reason);
        Assert.Equal("15236d", answer.Metadata![0].UltraBlurColors?.TopLeft);
    }

    [Fact]
    public void UltraBlurColoursReadAsAnObjectOrAListOfOne()
    {
        var single = Read("""{"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"7","UltraBlurColors":{"topLeft":"aabbcc"}}]}}""");
        var none = Read("""{"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"7","UltraBlurColors":[]}]}}""");

        Assert.Equal("aabbcc", single.Metadata![0].UltraBlurColors?.TopLeft);
        Assert.Null(none.Metadata![0].UltraBlurColors);
    }

    [Fact]
    public void AConversionAnswerSaysWhatArrives()
    {
        var decision = PlaybackDecision.From(Read(ConversionAnswer), "mp4");

        Assert.Equal(PlaybackRoute.Convert, decision.Route);
        Assert.Equal("Direct play not available; Conversion OK.", decision.Reason);
        Assert.Equal("H.264 1280×720 · AAC · 3.5 Mbps", decision.Describe());
    }

    [Fact]
    public void AConversionInAContainerNobodyAskedForIsRefused()
    {
        var decision = PlaybackDecision.From(Read(ConversionAnswer), "mpegts");

        Assert.Equal(PlaybackRoute.Refused, decision.Route);
        Assert.True(decision.WrongContainer);
    }

    [Fact]
    public void ACodeOfTwoThousandOrMoreIsARefusalInTheServersWords()
    {
        // The code rules even over a part that says it converts.
        var refusal = PlaybackDecision.From(Read("""
            {"MediaContainer":{"size":1,"generalDecisionCode":2000,"generalDecisionText":"Neither direct play nor conversion is available.",
             "Metadata":[{"ratingKey":"7","Media":[{"id":1,"container":"mp4","selected":true,"Part":[{"id":2,"decision":"transcode","container":"mp4"}]}]}]}}
            """), "mp4");
        var silent = PlaybackDecision.From(Read("""{"MediaContainer":{"size":0}}"""), "mp4");

        Assert.Equal((PlaybackRoute.Refused, "Neither direct play nor conversion is available."), (refusal.Route, refusal.Reason));
        Assert.False(refusal.WrongContainer);
        Assert.Equal(PlaybackRoute.Refused, silent.Route);
    }

    [Fact]
    public async Task AServerThatIgnoresFragmentedMp4IsAskedForTransportStream()
    {
        var mpegts = ConversionAnswer.Replace("\"container\":\"mp4\"", "\"container\":\"mpegts\"", StringComparison.Ordinal);
        var network = new FakeNetwork(_ => Task.FromResult(FakeNetwork.Json(mpegts)));
        var client = new PlexServerClient(Identity.CreateHttpClient(network), new Uri("https://server.example:32400"), "token", "Test");

        var (decision, request) = await client.DecideAsync(Ask(StreamQuality.All[4]), Identity, CancellationToken.None);

        Assert.Equal(PlaybackRoute.Convert, decision.Route);
        Assert.True(request.TransportStream);
        Assert.Equal(2, network.Asked.Count);
        Assert.Contains("container%3Dmpegts", network.Asked[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionReadsInTheNamesPeopleKnow()
    {
        var media = Read(DirectPlayAnswer).Metadata![0].Media![0];

        Assert.Equal("H.264 · DTS-HD MA · 26.5 Mbps", CodecNames.Describe(media));
        Assert.Equal("H.264 1920×1080 · Dolby TrueHD · 40 Mbps", CodecNames.Describe(new Media { VideoCodec = "h264", Width = 1920, Height = 1080, AudioCodec = "truehd", Bitrate = 40000 }));
    }

    [Fact]
    public void QualitiesReadAsPlexLabelsThem()
    {
        Assert.Equal(
            ["Original", "20 Mbps · 1080p", "12 Mbps · 1080p", "10 Mbps · 1080p", "8 Mbps · 1080p", "4 Mbps · 720p", "3 Mbps · 720p", "2 Mbps · 720p", "1.5 Mbps · 480p", "720 kbps · 320p", "320 kbps · 240p"],
            StreamQuality.All.Select(q => q.Label));
        Assert.Same(StreamQuality.Original, StreamQuality.FromKbps(null));
        Assert.Same(StreamQuality.Original, StreamQuality.FromKbps(9999));
        Assert.Same(StreamQuality.All[5], StreamQuality.FromKbps(4000));
    }

    [Fact]
    public void AFileAlreadyUnderTheCapGainsNothingFromConverting()
    {
        var q = StreamQuality.FromKbps(8000);

        Assert.True(q.Covers(6000, 1080));
        Assert.False(q.Covers(9000, 1080));
        Assert.False(q.Covers(6000, 2160));
        Assert.False(q.Covers(null, 1080));
        Assert.False(StreamQuality.Original.Covers(6000, 1080));
    }
}
