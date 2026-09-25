using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tuxflix.Core.Plex;

public sealed partial class PlexServerClient
{
    /// <summary>
    /// A track's lyrics from its lyric stream: the server's own reading of it (JSON, asked for by
    /// every request this client makes), or the file itself (LRC or plain text) when that is what
    /// comes back. Null when the stream holds no words.
    /// </summary>
    public async Task<LyricSheet?> GetLyricsAsync(MediaStream stream, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Key is not { Length: > 0 } key) return null;
        var bytes = await GetBytesAsync(Resolve(key), cancellation).ConfigureAwait(false);
        return ReadLyrics(bytes);
    }

    /// <summary>Lyrics from what a lyric stream answered: the JSON envelope when it is one, else the text of the file.</summary>
    public static LyricSheet? ReadLyrics(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize(text, PlexJsonContext.Default.PlexEnvelope);
                if (envelope?.MediaContainer?.Lyrics is { Count: > 0 } lyrics) return lyrics.Select(LyricSheet.FromPlex).FirstOrDefault(s => s is not null);
                return null;
            }
            catch (JsonException)
            {
                // Not the server's envelope after all: read it as the file.
            }
        }

        return LyricSheet.Parse(text);
    }

    /// <summary>
    /// An audio stream's loudness over its length, dB, in <paramref name="readings"/> steps; empty
    /// when the server has not analysed it.
    /// </summary>
    public async Task<IReadOnlyList<double>> GetLoudnessLevelsAsync(long streamId, int readings, CancellationToken cancellation)
    {
        try
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"/library/streams/{streamId}/levels?subsample={readings}");
            var container = await GetAsync(path, cancellation).ConfigureAwait(false);
            return [.. (container.Level ?? []).Select(l => l.V)];
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }
    }

    /// <summary>
    /// The tracks that sound most like <paramref name="ratingKey"/>, nearest first, from the
    /// server's sonic analysis; empty when it has none for this track.
    /// </summary>
    public async Task<IReadOnlyList<MetadataItem>> GetSonicNeighboursAsync(string ratingKey, int limit, double maxDistance, CancellationToken cancellation)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/library/metadata/{Uri.EscapeDataString(ratingKey)}/nearest?excludeParentID=-1&limit={limit}&maxDistance={maxDistance:0.###}");
        return (await GetAsync(path, cancellation).ConfigureAwait(false)).Metadata ?? [];
    }

    /// <summary>
    /// A sonic adventure: a path of tracks from one to another, each a small step in sound from
    /// the one before, as the server computes it; empty when it cannot.
    /// </summary>
    public async Task<IReadOnlyList<MetadataItem>> GetSonicPathAsync(int sectionId, string startKey, string endKey, CancellationToken cancellation)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/library/sections/{sectionId}/computePath?startID={Uri.EscapeDataString(startKey)}&endID={Uri.EscapeDataString(endKey)}");
        return (await GetAsync(path, cancellation).ConfigureAwait(false)).Metadata ?? [];
    }

    /// <summary>A music library's stations (Library Radio, Deep Cuts, Time Travel...), as its hubs offer them.</summary>
    public async Task<IReadOnlyList<MetadataItem>> GetStationsAsync(int sectionId, CancellationToken cancellation)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/hubs/sections/{sectionId}?includeStations=1&count=12");
        var hubs = (await GetAsync(path, cancellation).ConfigureAwait(false)).Hub ?? [];
        return [.. hubs.Where(h => h.Type == "station" || h.HubIdentifier?.StartsWith("music.stations", StringComparison.Ordinal) == true)
            .SelectMany(h => h.Metadata ?? [])
            .Where(m => m.Key is { Length: > 0 })];
    }

    /// <summary>
    /// Starts a play queue on the server from a station, a playlist or any item, and answers with
    /// its first tracks: how a client plays a station, which is made as it is listened to.
    /// </summary>
    /// <param name="itemKey">The station's or item's own key (<c>/library/sections/24/stations/1</c>).</param>
    public async Task<MediaContainer> CreatePlayQueueAsync(string machineIdentifier, string itemKey, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        var uri = $"server://{machineIdentifier}/com.plexapp.plugins.library{itemKey}";
        var path = $"/playQueues?type=audio&uri={Uri.EscapeDataString(uri)}&shuffle=0&repeat=0&continuous=0&own=1&includeChapters=0";
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(path));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException($"{Name} refused the sign-in for /playQueues.");
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var envelope = await JsonSerializer.DeserializeAsync(stream, PlexJsonContext.Default.PlexEnvelope, cancellation).ConfigureAwait(false);
            return envelope?.MediaContainer ?? new MediaContainer();
        }
    }
}

public sealed partial class PlexServerClient
{
    /// <summary>An artist's radio stations (usually one, the artist's own), as the artist's record lists them when asked.</summary>
    public async Task<IReadOnlyList<MetadataItem>> GetArtistStationsAsync(string artistKey, CancellationToken cancellation) =>
        (await GetAsync($"/library/metadata/{Uri.EscapeDataString(artistKey)}?includeStations=1", cancellation).ConfigureAwait(false))
        .Metadata?.FirstOrDefault()?.Stations?.Metadata ?? [];
}
