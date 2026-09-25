using System.Text;
using System.Text.Json;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>
/// Music beyond playing: lyrics in the server's shape and as files, the seek bar's shape from
/// loudness readings, and the demo library answering every music request as a server does.
/// </summary>
public sealed class MusicTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // The shape a server answers a timed lyric stream with (asked for JSON), words invented.
    private const string TimedJson = """
        {"MediaContainer":{"size":1,"Lyrics":[{"provider":"com.plexapp.agents.lyricfind","minLines":3,"timed":true,
          "author":"A. Writer","by":"Lyrics (c) Some Publisher","Line":[
          {"startOffset":12110,"endOffset":14870,"Span":[{"text":"First line of the song","startOffset":12110,"endOffset":14870}]},
          {"startOffset":14870,"endOffset":17710,"Span":[{"text":"Second ","startOffset":14870,"endOffset":16000},{"text":"line","startOffset":16000,"endOffset":17710}]},
          {"startOffset":17710,"endOffset":19350},
          {"startOffset":19350,"endOffset":23350,"Span":[{"text":"After the gap","startOffset":19350,"endOffset":23350}]}]}]}}
        """;

    // And an untimed one: lines, no offsets.
    private const string PlainJson = """
        {"MediaContainer":{"size":1,"Lyrics":[{"provider":"com.plexapp.agents.lyricfind","minLines":0,"timed":false,
          "Line":[{"Span":[{"text":"Only words"}]},{"Span":[{"text":"no times"}]}]}]}}
        """;

    [Fact]
    public void TheServersTimedLyricsBecomeTimedLinesWithTheirGaps()
    {
        var sheet = PlexServerClient.ReadLyrics(Encoding.UTF8.GetBytes(TimedJson));
        Assert.NotNull(sheet);
        Assert.True(sheet.IsTimed);
        Assert.Equal(["First line of the song", "Second line", string.Empty, "After the gap"], sheet.Lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(14870), sheet.Lines[1].Start);
        Assert.Equal("A. Writer · Lyrics (c) Some Publisher", sheet.Credit);

        // Before the first line nothing is lit; each line from its own start to the next's.
        Assert.Equal(-1, sheet.LineAt(TimeSpan.FromSeconds(12.0)));
        Assert.Equal(0, sheet.LineAt(TimeSpan.FromMilliseconds(12110)));
        Assert.Equal(0, sheet.LineAt(TimeSpan.FromMilliseconds(14869)));
        Assert.Equal(1, sheet.LineAt(TimeSpan.FromMilliseconds(14870)));
        Assert.Equal(2, sheet.LineAt(TimeSpan.FromSeconds(18)));
        Assert.Equal(3, sheet.LineAt(TimeSpan.FromMinutes(9)));
    }

    [Fact]
    public void TheServersUntimedLyricsAreReadNotFollowed()
    {
        var sheet = PlexServerClient.ReadLyrics(Encoding.UTF8.GetBytes(PlainJson));
        Assert.NotNull(sheet);
        Assert.False(sheet.IsTimed);
        Assert.Equal(["Only words", "no times"], sheet.Lines.Select(l => l.Text));
        Assert.Equal(-1, sheet.LineAt(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AnLrcFileIsReadAsPlayersReadIt()
    {
        const string Lrc = "[ar:Somebody]\n[ti:A Song]\n[offset:+500]\n[00:12.00]First\n[00:15.5][01:02.250]Chorus <00:15.80>with <00:16.10>words\n[00:20.00]\n[00:24.00]Last\n";
        var sheet = PlexServerClient.ReadLyrics(Encoding.UTF8.GetBytes(Lrc));
        Assert.NotNull(sheet);
        Assert.True(sheet.IsTimed);

        // Metadata tags are not lines; one line with two times is sung twice; word times go; the offset brings every line 0.5 s earlier.
        Assert.Equal(["First", "Chorus with words", string.Empty, "Last", "Chorus with words"], sheet.Lines.Select(l => l.Text));
        Assert.Equal(
            [TimeSpan.FromSeconds(11.5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(19.5), TimeSpan.FromSeconds(23.5), TimeSpan.FromSeconds(61.75)],
            sheet.Lines.Select(l => l.Start!.Value));
        Assert.Equal(4, sheet.LineAt(TimeSpan.FromSeconds(70)));
    }

    [Fact]
    public void TextWithoutTimesIsPlainLyricsAndBlankEndsAreDropped()
    {
        var sheet = PlexServerClient.ReadLyrics(Encoding.UTF8.GetBytes("\n\n[Verse]\nHello there\n\nSecond verse\n\n"));
        Assert.NotNull(sheet);
        Assert.False(sheet.IsTimed);
        Assert.Equal(["[Verse]", "Hello there", string.Empty, "Second verse"], sheet.Lines.Select(l => l.Text));
        Assert.Null(PlexServerClient.ReadLyrics(Encoding.UTF8.GetBytes("[ar:Nobody]\n\n")));
    }

    [Fact]
    public void AStreamSaysItIsLyricsAndTimedInTheServersOwnSpelling()
    {
        const string Json = """
            {"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"1","type":"track","title":"T","musicAnalysisVersion":"1","Media":[{"id":1,"Part":[{"id":2,"Stream":[
              {"id":3,"streamType":2,"codec":"mp3","gain":"-3.60","albumGain":"-3.60","peak":"0.905334"},
              {"id":4,"key":"/library/streams/4","streamType":4,"codec":"lrc","format":"lrc","minLines":"3","provider":"com.plexapp.agents.lyricfind","timed":"1"},
              {"id":5,"key":"/library/streams/5","streamType":4,"codec":"txt","format":"txt","provider":"com.plexapp.agents.lyricfind"}]}]}]}]}}
            """;
        var track = JsonSerializer.Deserialize(Json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!.Metadata![0];
        var streams = track.Media![0].Part![0].Stream!;
        Assert.Equal(1, track.MusicAnalysisVersion);
        Assert.False(streams[0].IsLyrics);
        Assert.True(streams[1].IsLyrics);
        Assert.True(streams[1].Timed);
        Assert.True(streams[2].IsLyrics);
        Assert.False(streams[2].Timed);
    }

    [Fact]
    public void LoudnessReadingsAreReadInTheServersShape()
    {
        const string Json = """{"MediaContainer":{"size":3,"totalSamples":"1773","Level":[{"v":-20.0},{"v":-13.9},{"v":-39.9}]}}""";
        var container = JsonSerializer.Deserialize(Json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!;
        Assert.Equal(1773, container.TotalSamples);
        Assert.Equal([-20.0, -13.9, -39.9], container.Level!.Select(l => l.V));
    }

    [Fact]
    public void TheWaveformIsLoudAgainstTheTracksOwnLoudest()
    {
        // The loudest reading is a full bar, 30 dB under it the floor, half way half a bar.
        var bars = LoudnessWaveform.FromLevels([-10, -25, -40, -70], 4);
        Assert.Equal([1f, 0.5f, LoudnessWaveform.Floor, LoudnessWaveform.Floor], bars);

        // A short loud moment among quiet readings keeps its bar tall when readings share bars.
        var quiet = Enumerable.Repeat(-30.0, 100).ToArray();
        quiet[37] = -10;
        var shared = LoudnessWaveform.FromLevels(quiet, 10);
        Assert.Equal(1f, shared[3]);
        Assert.All(shared.Where((_, i) => i != 3), h => Assert.True(h < 0.4f));

        // Fewer readings than bars: each reading spans its share of them.
        Assert.Equal([1f, 1f, 1f, 0.5f, 0.5f, 0.5f], LoudnessWaveform.FromLevels([-10, -25], 6));
        Assert.Empty(LoudnessWaveform.FromLevels([], 6));
    }

    [Fact]
    public async Task TheDemoServerHasAMusicLibraryWithEverythingAPlayerAsksFor()
    {
        var (client, catalog) = Create();
        var cancel = TestContext.Current.CancellationToken;

        var sections = await client.GetSectionsAsync(cancel);
        var music = Assert.Single(sections, s => s.Type == "artist");
        Assert.Equal(DemoCatalog.MusicSectionKey, music.Key);
        Assert.Equal(3, (await client.GetSectionItemsAsync(music.Key, "titleSort", cancel)).Metadata!.Count);
        Assert.Equal(catalog.Tracks.Count, (await client.BrowseAsync(music.Key, "type=10&sort=titleSort", 0, 100, cancel)).TotalSize);

        // Many records in one request, as the queue reads them.
        var tracks = catalog.Tracks;
        var many = await client.GetMetadataManyAsync([tracks[4].RatingKey, tracks[0].RatingKey], cancel);
        Assert.Equal([tracks[4].RatingKey, tracks[0].RatingKey], many.Select(t => t.RatingKey));

        // Timed lyrics, plain lyrics, and loudness for the seek bar.
        var timed = tracks.First(t => Streams(t).Any(s => s.IsLyrics && s.Timed));
        var sheet = await client.GetLyricsAsync(Streams(timed).First(s => s.IsLyrics), cancel);
        Assert.True(sheet is { IsTimed: true, Lines.Count: > 4 });
        var plain = tracks.First(t => Streams(t).Any(s => s.IsLyrics && !s.Timed));
        Assert.True((await client.GetLyricsAsync(Streams(plain).First(s => s.IsLyrics), cancel)) is { IsTimed: false });
        var audio = Streams(timed).First(s => s.StreamType == StreamChoice.Audio);
        Assert.Equal(64, (await client.GetLoudnessLevelsAsync(audio.Id, 64, cancel)).Count);

        // The sonic neighbours and a path between two tracks, both ends included.
        var near = await client.GetSonicNeighboursAsync(tracks[0].RatingKey, 5, 0.3, cancel);
        Assert.Equal(5, near.Count);
        Assert.DoesNotContain(near, t => t.RatingKey == tracks[0].RatingKey);
        var path = await client.GetSonicPathAsync(int.Parse(music.Key, System.Globalization.CultureInfo.InvariantCulture), tracks[0].RatingKey, tracks[^1].RatingKey, cancel);
        Assert.True(path.Count >= 2);
        Assert.Equal(tracks[0].RatingKey, path[0].RatingKey);
        Assert.Equal(tracks[^1].RatingKey, path[^1].RatingKey);

        // The library's stations and an artist's, each played through a play queue.
        var stations = await client.GetStationsAsync(int.Parse(music.Key, System.Globalization.CultureInfo.InvariantCulture), cancel);
        Assert.Equal(["Library Radio", "Deep Cuts Radio", "Time Travel Radio"], stations.Select(s => s.Title));
        Assert.All(stations, s => Assert.True(s.Radio));
        var artistStation = Assert.Single(await client.GetArtistStationsAsync(catalog.Artists[0].RatingKey, cancel));
        var queue = await client.CreatePlayQueueAsync(DemoCatalog.MachineIdentifier, artistStation.Key!, cancel);
        Assert.True(queue.PlayQueueId > 0);
        Assert.NotEmpty(queue.Metadata!);
        Assert.Contains(queue.Metadata!, t => t.GrandparentRatingKey == catalog.Artists[0].RatingKey);
        Assert.NotEmpty((await client.CreatePlayQueueAsync(DemoCatalog.MachineIdentifier, stations[0].Key!, cancel)).Metadata!);
    }

    [Fact]
    public async Task TheFilmsFiltersStillAnswerBesideTheMusicRoutes()
    {
        var (client, _) = Create();
        var genres = await client.GetFilterValuesAsync(DemoCatalog.MoviesSectionKey, "genre", TestContext.Current.CancellationToken);
        Assert.NotEmpty(genres);
    }

    private static List<MediaStream> Streams(MetadataItem track) => track.Media![0].Part![0].Stream!;

    private static (PlexServerClient Client, DemoCatalog Catalog) Create()
    {
        var catalog = DemoCatalog.Create(Now);
        var identity = new PlexClientIdentity("test-client", "0.0.0", "tests");
        var client = new PlexServerClient(identity.CreateHttpClient(new DemoPlexHandler(catalog, art: null)), DemoPlexHandler.BaseUri, null, "Demo", isDemo: true);
        return (client, catalog);
    }
}
