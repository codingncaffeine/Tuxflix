using System.Text.Json;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

public sealed class StreamChoiceTests
{
    // The shape of a real episode: video, English audio the server selected, and an English DVD
    // subtitle the file marks as its default but the server does not select.
    private const string EpisodeStreams = """
        [{"id":11,"streamType":1,"index":0,"codec":"h264","default":true},
         {"id":12,"streamType":2,"index":1,"codec":"aac","languageCode":"eng","selected":true,"default":true,"displayTitle":"English (AAC Stereo)"},
         {"id":13,"streamType":3,"index":2,"codec":"vobsub","languageCode":"eng","default":true,"displayTitle":"English"}]
        """;

    private static readonly PlayerTrack[] EpisodeTracks = [new("audio", "1", 1, null), new("sub", "1", 2, null)];

    private static MediaPart Part(string streams)
    {
        var json = """{"MediaContainer":{"Metadata":[{"ratingKey":"1","Media":[{"Part":[{"id":7,"key":"/library/parts/7/file.mkv","Stream":"""
                   + streams + "}]}]}]}}";
        return JsonSerializer.Deserialize(json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!.Metadata![0].Media![0].Part![0];
    }

    [Fact]
    public void TheServersSelectionWinsOverTheFilesDefaults()
    {
        var part = Part(EpisodeStreams);

        Assert.True(StreamChoice.IsKnown(part));
        Assert.Null(StreamChoice.Selected(part, StreamChoice.Subtitle));
        var audio = StreamChoice.Selected(part, StreamChoice.Audio);
        Assert.Equal(12, audio?.Id);
        Assert.Equal(new PlayerTrack("audio", "1", 1, null), StreamChoice.TrackFor(audio!, part, EpisodeTracks));
    }

    [Fact]
    public void AStreamFindsItsTrackByItsPlaceInTheFile()
    {
        var part = Part("""
            [{"id":21,"streamType":2,"index":1,"languageCode":"eng"},
             {"id":22,"streamType":2,"index":2,"languageCode":"jpn","selected":true},
             {"id":23,"streamType":3,"index":3,"languageCode":"eng"},
             {"id":24,"streamType":3,"index":4,"languageCode":"eng","forced":true,"selected":true}]
            """);
        PlayerTrack[] tracks = [new("audio", "1", 1, null), new("audio", "2", 2, null), new("sub", "1", 3, null), new("sub", "2", 4, null)];

        Assert.Equal("2", StreamChoice.TrackFor(StreamChoice.Selected(part, StreamChoice.Audio)!, part, tracks)?.Id);
        Assert.Equal("2", StreamChoice.TrackFor(StreamChoice.Selected(part, StreamChoice.Subtitle)!, part, tracks)?.Id);
        Assert.Equal(23, StreamChoice.StreamFor(tracks[2], part, tracks, s => s.Key!)?.Id);
    }

    [Fact]
    public void WhenTheNumbersDisagreeTheOrderAmongTheKindDecides()
    {
        var part = Part("""
            [{"id":31,"streamType":2,"index":1},
             {"id":32,"streamType":2,"index":2,"selected":true}]
            """);
        var selected = StreamChoice.Selected(part, StreamChoice.Audio)!;

        // The player counted one stream more before the audio: the second audio track is still the second.
        PlayerTrack[] shifted = [new("audio", "1", 2, null), new("audio", "2", 3, null)];
        Assert.Equal("2", StreamChoice.TrackFor(selected, part, shifted)?.Id);

        // A different number of audio tracks: no guess, the player's own choice stands.
        PlayerTrack[] more = [new("audio", "1", 5, null), new("audio", "2", 6, null), new("audio", "3", 7, null)];
        Assert.Null(StreamChoice.TrackFor(selected, part, more));
    }

    [Fact]
    public void ASubtitleFileBesideTheMediaIsMatchedByWhereItCameFrom()
    {
        var part = Part("""
            [{"id":41,"streamType":2,"index":0,"selected":true},
             {"id":42,"streamType":3,"key":"/library/streams/42","codec":"srt","languageCode":"eng","selected":true,"displayTitle":"English (SRT External)"}]
            """);
        static string SourceOf(MediaStream s) => "https://server.example:32400" + s.Key;

        var external = StreamChoice.Selected(part, StreamChoice.Subtitle)!;
        Assert.True(external.IsExternal);
        Assert.Equal(new[] { 42L }, StreamChoice.ExternalSubtitles(part).Select(s => s.Id));
        Assert.Null(StreamChoice.TrackFor(external, part, []));

        // Loaded, it comes back as a track with no place in the media file and its address as its source.
        PlayerTrack[] tracks = [new("audio", "1", 0, null), new("sub", "1", null, "https://server.example:32400/library/streams/42")];
        Assert.Equal(42, StreamChoice.StreamFor(tracks[1], part, tracks, SourceOf)?.Id);
        Assert.Equal(41, StreamChoice.StreamFor(tracks[0], part, tracks, SourceOf)?.Id);
    }

    [Fact]
    public void WithoutStreamsTheFileDecides()
    {
        var part = Part("[]");

        Assert.False(StreamChoice.IsKnown(part));
        Assert.Null(StreamChoice.Selected(part, StreamChoice.Audio));
    }
}
