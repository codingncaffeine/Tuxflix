using System.Text.Json.Serialization;

namespace Tuxflix.Core.Plex;

// What the server keeps about a photo from its file: the camera and how it was set.

public sealed partial class Media
{
    /// <summary>A photo's aperture, as the file says it (<c>2.8</c>).</summary>
    [JsonPropertyName("aperture")]
    public string? Aperture { get; init; }

    /// <summary>A photo's exposure time, as the file says it (<c>1/250</c>).</summary>
    [JsonPropertyName("exposure")]
    public string? Exposure { get; init; }

    [JsonPropertyName("iso")]
    public int? Iso { get; init; }

    [JsonPropertyName("lens")]
    public string? Lens { get; init; }

    /// <summary>The camera's maker.</summary>
    [JsonPropertyName("make")]
    public string? Make { get; init; }

    /// <summary>The camera.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}
