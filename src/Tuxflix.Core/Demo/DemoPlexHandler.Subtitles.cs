using System.Globalization;
using System.Net;
using System.Text;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>
/// Subtitles found online, as the demo answers them: a search lists invented results, adding one
/// keeps it with the film as a subtitle file beside the media, and that file is served.
/// </summary>
public sealed partial class DemoPlexHandler
{
    private static readonly bool SubtitleRoutes = Add((handler, segments, query, request) => segments switch
    {
        ["library", "metadata", var key, "subtitles"] when request.Method == HttpMethod.Get => handler.FoundSubtitles(key, query["language"] ?? "en"),
        ["library", "metadata", var key, "subtitles"] when request.Method == HttpMethod.Put => handler.KeepSubtitle(key, query),
        _ => null,
    });

    private HttpResponseMessage FoundSubtitles(string key, string language)
    {
        if (Catalog.Find(key) is not { } item) return NotFound();
        var name = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.TwoLetterISOLanguageName == language)?.EnglishName ?? language;
        var found = Enumerable.Range(1, 3).Select(n => new MediaStream
        {
            Id = 800_000 + n,
            StreamType = 3,
            Key = $"/library/streams/{800_000 + n}",
            Codec = "srt",
            Language = name,
            LanguageCode = language,
            DisplayTitle = n == 3 ? name + " SDH" : name,
            HearingImpaired = n == 3,
            ProviderTitle = "OpenSubtitles",
            Score = 1000 - (n * 100),
            Title = $"{item.Title}.{item.Year}.release{n}",
            ExtendedDisplayTitle = $"{item.Title}.{item.Year}.release{n} ({name} SRT OpenSubtitles)",
        });
        return Json(new MediaContainer { Size = 3, Stream = [.. found] });
    }

    private HttpResponseMessage KeepSubtitle(string key, System.Collections.Specialized.NameValueCollection query)
    {
        if (Catalog.Find(key)?.Media?.FirstOrDefault()?.Part?.FirstOrDefault() is not { Stream: { } streams }) return NotFound();
        var id = 810_000 + streams.Count;
        var language = query["language"] ?? "en";
        streams.Add(new MediaStream
        {
            Id = id,
            StreamType = 3,
            Key = $"/library/streams/{id}",
            Codec = query["codec"] ?? "srt",
            LanguageCode = language,
            DisplayTitle = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.TwoLetterISOLanguageName == language)?.EnglishName ?? language,
            ExtendedDisplayTitle = $"Found online ({query["providerTitle"]})",
        });
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>A subtitle file the demo kept beside a film, or null when the stream is not one.</summary>
    private HttpResponseMessage? SubtitleFile(string id)
    {
        if (!id.StartsWith("81", StringComparison.Ordinal)) return null;
        var srt = "1\n00:00:01,000 --> 00:00:04,000\nA subtitle found online.\n\n2\n00:00:05,000 --> 00:00:09,000\nThe demo library kept it with the film.\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(srt, Encoding.UTF8, "text/srt") };
    }
}
