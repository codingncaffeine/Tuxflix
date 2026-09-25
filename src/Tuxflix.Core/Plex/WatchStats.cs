namespace Tuxflix.Core.Plex;

/// <summary>Hours watched in one week or one month, from its first day.</summary>
public readonly record struct WatchPeriod(DateOnly Start, double Hours);

/// <summary>A series among the most watched: how many episodes, for how long.</summary>
public readonly record struct WatchedShow(string Title, string? RatingKey, string? Thumb, int Episodes, double Hours);

/// <summary>
/// What the viewer watched, summed from their history: hours per week and per month, and the
/// series they watched most. Films and episodes count, music does not; each entry of the history
/// is one viewing of the whole item, as a server records one once it is nearly all watched.
/// </summary>
public sealed record WatchStats(IReadOnlyList<WatchPeriod> Weeks, IReadOnlyList<WatchPeriod> Months, IReadOnlyList<WatchedShow> TopShows)
{
    public double ThisWeek => Weeks.Count > 0 ? Weeks[^1].Hours : 0;

    public double ThisMonth => Months.Count > 0 ? Months[^1].Hours : 0;

    /// <summary>The first moment the stats need history from: the start of the earliest month shown.</summary>
    public static DateTimeOffset Since(DateTimeOffset now, TimeZoneInfo zone, int months)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var first = new DateTime(local.Year, local.Month, 1).AddMonths(-(months - 1));
        return new DateTimeOffset(first, zone.GetUtcOffset(first));
    }

    /// <summary>
    /// Sums <paramref name="history"/> into the last <paramref name="weeks"/> weeks (starting on
    /// <paramref name="firstDay"/>) and <paramref name="months"/> months up to <paramref name="now"/>,
    /// in the viewer's time zone. <paramref name="durations"/> gives each item's length in
    /// milliseconds; an entry without one (an item since deleted) is left out.
    /// </summary>
    public static WatchStats Compute(
        IEnumerable<MetadataItem> history,
        IReadOnlyDictionary<string, long> durations,
        DateTimeOffset now,
        TimeZoneInfo zone,
        DayOfWeek firstDay,
        int weeks = 8,
        int months = 6,
        int top = 5)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(durations);
        ArgumentNullException.ThrowIfNull(zone);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var thisWeek = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var weekHours = new double[weeks];
        var monthHours = new double[months];
        var shows = new Dictionary<string, (string Title, string? Key, string? Thumb, int Episodes, double Hours)>(StringComparer.Ordinal);

        foreach (var entry in history)
        {
            if (entry.Type is not ("movie" or "episode") || entry.ViewedAt is not { } viewedAt) continue;
            if (!durations.TryGetValue(entry.RatingKey, out var length) || length <= 0) continue;
            var hours = length / 3_600_000.0;
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(viewedAt), zone).DateTime);

            var week = (weeks - 1) - ((thisWeek.DayNumber - (day.DayNumber - (((int)day.DayOfWeek - (int)firstDay + 7) % 7))) / 7);
            if (week >= 0 && week < weeks && day <= today) weekHours[week] += hours;

            var month = (months - 1) - (((thisMonth.Year - day.Year) * 12) + thisMonth.Month - day.Month);
            if (month >= 0 && month < months && day <= today) monthHours[month] += hours;

            if (entry.Type == "episode" && month >= 0 && entry.GrandparentTitle is { } show)
            {
                var key = entry.GrandparentRatingKey ?? show;
                shows[key] = shows.TryGetValue(key, out var sum)
                    ? sum with { Episodes = sum.Episodes + 1, Hours = sum.Hours + hours }
                    : (show, entry.GrandparentRatingKey, entry.GrandparentThumb, 1, hours);
            }
        }

        return new WatchStats(
            [.. weekHours.Select((h, i) => new WatchPeriod(thisWeek.AddDays(-7 * (weeks - 1 - i)), Math.Round(h, 1)))],
            [.. monthHours.Select((h, i) => new WatchPeriod(thisMonth.AddMonths(-(months - 1 - i)), Math.Round(h, 1)))],
            [.. shows.Values.OrderByDescending(s => s.Hours).ThenByDescending(s => s.Episodes).ThenBy(s => s.Title, StringComparer.CurrentCulture).Take(top)
                .Select(s => new WatchedShow(s.Title, s.Key, s.Thumb, s.Episodes, Math.Round(s.Hours, 1)))]);
    }
}
