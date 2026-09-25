using System.Globalization;
using System.Text.RegularExpressions;

namespace Tuxflix.Core.Plex;

/// <summary>One line of lyrics: its words, and when it is sung when the lyrics are timed.</summary>
/// <param name="Start">From the start of the track; null in lyrics that are not timed.</param>
/// <param name="Text">The words; empty on a gap between verses.</param>
public sealed record LyricSheetLine(TimeSpan? Start, string Text);

/// <summary>
/// A track's lyrics as lines to show. Timed lyrics say which line is sung at any moment, so the
/// page can light it and keep it in view; untimed lyrics are simply read.
/// </summary>
/// <remarks>
/// Two sources: the server's own reading of a lyric stream (its JSON, lines and spans with
/// offsets in milliseconds), and the text of an LRC or plain file, for a stream served as the file
/// itself. LRC is read as players read it: several times on one line, the <c>[offset:]</c> tag,
/// word times (<c>&lt;mm:ss.xx&gt;</c>) dropped, metadata tags skipped, lines put in time order.
/// </remarks>
public sealed partial class LyricSheet
{
    private LyricSheet(IReadOnlyList<LyricSheetLine> lines, bool timed, string? credit)
    {
        Lines = lines;
        IsTimed = timed;
        Credit = credit;
    }

    public IReadOnlyList<LyricSheetLine> Lines { get; }

    /// <summary>Every line carries its time.</summary>
    public bool IsTimed { get; }

    /// <summary>Who wrote the words and whose rights they are, as the provider asks them to be shown.</summary>
    public string? Credit { get; }

    public bool IsEmpty => Lines.All(l => string.IsNullOrWhiteSpace(l.Text));

    /// <summary>The server's reading of a lyric stream; null when it holds no words.</summary>
    public static LyricSheet? FromPlex(LyricsInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var lines = new List<LyricSheetLine>();
        var timed = info.Timed && info.Line?.Any(l => l.StartOffset is not null) == true;
        foreach (var line in info.Line ?? [])
        {
            var text = string.Concat((line.Span ?? []).Select(s => s.Text)).Trim();
            lines.Add(new LyricSheetLine(timed && line.StartOffset is { } ms ? TimeSpan.FromMilliseconds(ms) : null, text));
        }

        if (timed)
        {
            // A line the server left without a time cannot be placed: it goes.
            lines = [.. lines.Where(l => l.Start is not null).OrderBy(l => l.Start)];
        }

        var credit = string.Join(" · ", new[] { info.Author, info.By }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var sheet = new LyricSheet(Trim(lines), timed, credit.Length > 0 ? credit : null);
        return sheet.IsEmpty ? null : sheet;
    }

    /// <summary>An LRC file, or plain text when no line carries a time; null when it holds no words.</summary>
    public static LyricSheet? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var offset = TimeSpan.Zero;
        var timed = new List<(TimeSpan Start, int Order, string Text)>();
        var plain = new List<LyricSheetLine>();
        var order = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            var times = new List<TimeSpan>();
            var rest = line;
            while (rest.StartsWith('['))
            {
                var close = rest.IndexOf(']', StringComparison.Ordinal);
                if (close < 0) break;
                var tag = rest[1..close];
                if (TryTime(tag, out var time))
                {
                    times.Add(time);
                }
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                {
                    // A positive offset brings the lyrics earlier.
                    offset = TimeSpan.FromMilliseconds(ms);
                }
                else if (MetadataTag().IsMatch(tag))
                {
                    // [ar:], [ti:], [al:], [by:], [length:]...: about the file, not words to show.
                }
                else
                {
                    break;
                }

                rest = rest[(close + 1)..];
            }

            var words = WordTime().Replace(rest, string.Empty).Trim();
            if (times.Count > 0)
            {
                foreach (var time in times) timed.Add((time, order++, words));
            }
            else if (rest.Length == line.Length || words.Length > 0)
            {
                plain.Add(new LyricSheetLine(null, words));
            }
        }

        LyricSheet sheet = timed.Count > 0
            ? new LyricSheet(
                Trim([.. timed.OrderBy(t => t.Start).ThenBy(t => t.Order).Select(t => new LyricSheetLine(t.Start - offset < TimeSpan.Zero ? TimeSpan.Zero : t.Start - offset, t.Text))]),
                timed: true,
                credit: null)
            : new LyricSheet(Trim(plain), timed: false, credit: null);
        return sheet.IsEmpty ? null : sheet;
    }

    /// <summary>
    /// The line sung at <paramref name="position"/>: the last whose time has come, or -1 before
    /// the first line and in lyrics that are not timed.
    /// </summary>
    public int LineAt(TimeSpan position)
    {
        if (!IsTimed || Lines.Count == 0) return -1;
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Start <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>[mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx], [mm:ss:xx], and an hour in front.</summary>
    private static bool TryTime(string tag, out TimeSpan time)
    {
        time = default;
        var match = TimeTag().Match(tag);
        if (!match.Success) return false;
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : "0";
        var ms = (int)Math.Round(double.Parse("0." + fraction, CultureInfo.InvariantCulture) * 1000);
        time = new TimeSpan(0, hours, minutes, seconds, ms);
        return true;
    }

    // Blank lines at either end say nothing; a blank inside is a gap in the singing and stays.
    private static List<LyricSheetLine> Trim(List<LyricSheetLine> lines)
    {
        var first = lines.FindIndex(l => l.Text.Length > 0);
        if (first < 0) return [];
        var last = lines.FindLastIndex(l => l.Text.Length > 0);
        return lines.GetRange(first, last - first + 1);
    }

    [GeneratedRegex(@"^(?:(?<h>\d{1,2}):)?(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^[a-zA-Z#]+:", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTime();
}
