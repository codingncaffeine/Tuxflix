using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

public sealed partial class MediaContainer
{
    /// <summary>Streams listed on their own: the subtitles a search found online.</summary>
    [JsonPropertyName("Stream")]
    public List<MediaStream>? Stream { get; init; }
}

public sealed partial class MediaStream
{
    /// <summary>Where a subtitle found online comes from (<c>OpenSubtitles</c>).</summary>
    [JsonPropertyName("providerTitle")]
    public string? ProviderTitle { get; init; }

    /// <summary>The provider's own measure of how well a found subtitle fits; higher is better. The server sends it as text.</summary>
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    /// <summary>Subtitles for the deaf and hard of hearing: sounds described as well as speech.</summary>
    [JsonPropertyName("hearingImpaired")]
    public bool HearingImpaired { get; init; }

    /// <summary>The stream's file form: a subtitle's (<c>srt</c>, <c>ass</c>), or a lyric stream's, <c>lrc</c> (timed) or <c>txt</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }
}
