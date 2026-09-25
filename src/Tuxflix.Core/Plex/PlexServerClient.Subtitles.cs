using System.Globalization;

namespace Tuxflix.Core.Plex;

public sealed partial class PlexServerClient
{
    /// <summary>
    /// Subtitles for an item that the server's providers (OpenSubtitles) have online, best first.
    /// A search changes nothing; the results are descriptions until one is added.
    /// </summary>
    /// <param name="language">A two-letter language code (<c>en</c>, <c>fr</c>).</param>
    public async Task<IReadOnlyList<MediaStream>> SearchSubtitlesAsync(string ratingKey, string language, CancellationToken cancellation)
    {
        var container = await GetAsync(
            $"/library/metadata/{Uri.EscapeDataString(ratingKey)}/subtitles?language={Uri.EscapeDataString(language)}&hearingImpaired=0&forced=0",
            cancellation).ConfigureAwait(false);
        return [.. (container.Stream ?? []).Where(s => s.StreamType == 3).OrderByDescending(s => s.Score ?? 0)];
    }

    /// <summary>
    /// Has the server fetch a found subtitle and keep it with the item, for everyone who watches it.
    /// The server fetches in the background; the new stream shows in the item's parts shortly after.
    /// </summary>
    public async Task AddSubtitleAsync(string ratingKey, MediaStream found, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(found);
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/library/metadata/{Uri.EscapeDataString(ratingKey)}/subtitles?key={Uri.EscapeDataString(found.Key ?? string.Empty)}&codec={Uri.EscapeDataString(found.Codec ?? "srt")}&language={Uri.EscapeDataString(found.LanguageCode ?? found.Language ?? string.Empty)}&hearingImpaired={(found.HearingImpaired ? 1 : 0)}&forced={(found.Forced ? 1 : 0)}&providerTitle={Uri.EscapeDataString(found.ProviderTitle ?? string.Empty)}");
        await SendToDemoTooAsync(HttpMethod.Put, query, cancellation).ConfigureAwait(false);
    }

    /// <summary>A write that the demo library answers as well (the timeline and scrobble writes leave it alone).</summary>
    private async Task SendToDemoTooAsync(HttpMethod method, string pathAndQuery, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, Resolve(pathAndQuery));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new PlexUnauthorizedException($"{Name} refused the sign-in for {Describe(pathAndQuery)}.");
        }

        response.EnsureSuccessStatusCode();
    }
}
