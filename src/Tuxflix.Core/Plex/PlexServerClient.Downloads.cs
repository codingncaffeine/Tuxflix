using System.Net.Http.Headers;

namespace Tuxflix.Core.Plex;

public sealed partial class PlexServerClient
{
    /// <summary>
    /// Asks for a part's file to keep (<c>download=1</c>), from byte <paramref name="from"/> on.
    /// The caller reads the answer and disposes it; the status is left for it to judge (a 403 is
    /// the owner withholding downloads from this account).
    /// </summary>
    /// <param name="validator">The ETag or Last-Modified the bytes so far came with: a server that sees the file changed sends all of it again.</param>
    public async Task<HttpResponseMessage> RequestPartAsync(string partKey, long from, string? validator, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partKey);
        // Not disposed here: the answer is read after this returns, and a request without content holds nothing.
        var request = new HttpRequestMessage(HttpMethod.Get, Resolve(partKey + (partKey.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "download=1"));
        if (Token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", Token);

        // Media is never compressed on the way, and a compressed answer would not add up to the file's size.
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.Range = new RangeHeaderValue(from, null);
        if (from > 0 && validator is not null) request.Headers.TryAddWithoutValidation("If-Range", validator);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
    }
}
