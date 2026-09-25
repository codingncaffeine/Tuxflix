using Avalonia.Controls;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Activity: what plays on the viewer's other devices and what the server is busy with (both kept
/// current by the server's notifications), the viewer's watch history, and how much they watched.
/// Read only: nothing here changes anything on the server.
/// </summary>
public sealed partial class ActivityPageViewModel(ShellViewModel shell, ServerSession session) : PageViewModel
{
    private const int HistoryPage = 50;
    private const int StatsMonths = 6;
    private const int StatsWeeks = 8;
    private int _historyStart;
    private bool _historyEnded;

    public override string Title => "Activity";

    public override TopTab Tab => TopTab.Activity;

    public override bool ShowsRail => false;

    public LiveUpdates Live => shell.Live;

    public ObservableCollection<HistoryRowViewModel> History { get; } = [];

    public ObservableCollection<StatBarViewModel> Weeks { get; } = [];

    public ObservableCollection<StatBarViewModel> Months { get; } = [];

    public ObservableCollection<TopShowViewModel> TopShows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    public partial WatchStats? Stats { get; private set; }

    public bool HasStats => Stats is not null;

    public string ThisWeekText => Hours(Stats?.ThisWeek ?? 0);

    public string ThisMonthText => Hours(Stats?.ThisMonth ?? 0);

    public string SixMonthsText => Hours(Stats?.Months.Sum(m => m.Hours) ?? 0);

    public bool HasTopShows => TopShows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowMore))]
    public partial bool IsLoadingHistory { get; private set; }

    public bool CanShowMore => !_historyEnded && !IsLoadingHistory && History.Count > 0;

    public bool HistoryIsEmpty => !IsLoading && !IsLoadingHistory && History.Count == 0 && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(HistoryIsEmpty)];

    /// <summary>The viewer's own history: the owner's is account 1, anybody else sees only theirs anyway.</summary>
    private long? Account => session.IsOwned ? 1 : null;

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        History.Clear();
        _historyStart = 0;
        _historyEnded = false;
        var playing = shell.Live.RefreshPlayingAsync();
        var stats = LoadStatsAsync(cancellation);
        await LoadHistoryAsync(cancellation);
        await stats;
        await playing;
    }

    [RelayCommand]
    private Task ShowMoreAsync() => LoadHistoryAsync(CancellationToken.None);

    /// <summary>The next films and episodes of the history; music the viewer played is passed over.</summary>
    private async Task LoadHistoryAsync(CancellationToken cancellation)
    {
        if (_historyEnded || IsLoadingHistory) return;
        IsLoadingHistory = true;
        try
        {
            var found = 0;
            for (var page = 0; page < 10 && found < HistoryPage && !_historyEnded; page++)
            {
                var start = _historyStart;
                var container = await Task.Run(() => session.Client.GetHistoryAsync(Account, null, start, 100, cancellation), cancellation);
                var entries = container.Metadata ?? [];
                _historyStart += entries.Count;
                _historyEnded = entries.Count == 0 || _historyStart >= (container.TotalSize ?? int.MaxValue);
                foreach (var entry in entries.Where(e => e.Type is "movie" or "episode"))
                {
                    History.Add(new HistoryRowViewModel(shell, entry));
                    found++;
                }
            }
        }
        finally
        {
            IsLoadingHistory = false;
            OnPropertyChanged(nameof(CanShowMore));
            OnPropertyChanged(nameof(HistoryIsEmpty));
        }
    }

    /// <summary>Six months of history, and how long each thing watched runs, summed into hours.</summary>
    private async Task LoadStatsAsync(CancellationToken cancellation)
    {
        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.Local;
        var since = WatchStats.Since(now, zone, StatsMonths);
        var account = Account;
        var stats = await Task.Run(
            async () =>
            {
                var entries = new List<MetadataItem>();
                for (var start = 0; start < 4000;)
                {
                    var page = await session.Client.GetHistoryAsync(account, since, start, 200, cancellation).ConfigureAwait(false);
                    var items = page.Metadata ?? [];
                    entries.AddRange(items.Where(e => e.Type is "movie" or "episode"));
                    start += items.Count;
                    if (items.Count == 0 || start >= (page.TotalSize ?? int.MaxValue)) break;
                }

                var records = await session.Client.GetLightAsync(entries.Select(e => e.RatingKey), cancellation).ConfigureAwait(false);
                var durations = records.Where(r => r.Duration is > 0).GroupBy(r => r.RatingKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Duration!.Value, StringComparer.Ordinal);
                return WatchStats.Compute(entries, durations, now, zone, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, StatsWeeks, StatsMonths);
            },
            cancellation);

        Stats = stats;
        // A week names its month only where a month starts, so eight labels fit under eight bars.
        Fill(Weeks, stats.Weeks, w => w == stats.Weeks[0] || w.Start.Day <= 7 ? w.Start.ToString("d MMM", CultureInfo.CurrentCulture) : w.Start.Day.ToString(CultureInfo.CurrentCulture), w => $"The week of {w.Start.ToString("d MMMM", CultureInfo.CurrentCulture)}");
        Fill(Months, stats.Months, m => m.Start.ToString("MMM", CultureInfo.CurrentCulture), m => m.Start.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        TopShows.Clear();
        foreach (var show in stats.TopShows) TopShows.Add(new TopShowViewModel(shell, show));
        OnPropertyChanged(nameof(ThisWeekText));
        OnPropertyChanged(nameof(ThisMonthText));
        OnPropertyChanged(nameof(SixMonthsText));
        OnPropertyChanged(nameof(HasTopShows));
    }

    private static void Fill(ObservableCollection<StatBarViewModel> bars, IReadOnlyList<WatchPeriod> periods, Func<WatchPeriod, string> label, Func<WatchPeriod, string> name)
    {
        bars.Clear();
        var most = Math.Max(1, periods.Count == 0 ? 1 : periods.Max(p => p.Hours));
        for (var n = 0; n < periods.Count; n++)
        {
            var period = periods[n];
            bars.Add(new StatBarViewModel(label(period), $"{name(period)}: {Hours(period.Hours)}", period.Hours / most, n == periods.Count - 1, Hours(period.Hours)));
        }
    }

    /// <summary>"5.2 h", "12 h", "0 h".</summary>
    public static string Hours(double hours) => hours switch
    {
        0 => "0 h",
        < 10 => hours.ToString("0.#", CultureInfo.CurrentCulture) + " h",
        _ => hours.ToString("N0", CultureInfo.CurrentCulture) + " h",
    };
}

/// <summary>One bar of a chart of hours: its label under it, its height as a share of the tallest.</summary>
public sealed class StatBarViewModel(string label, string tip, double share, bool isCurrent, string value)
{
    /// <summary>The tallest bar's height in the chart.</summary>
    public const double Tallest = 92;

    public string Label { get; } = label;

    public string Tip { get; } = tip;

    public string Value { get; } = value;

    /// <summary>The bar's height; a period with nothing watched keeps a sliver, so the row reads as a row.</summary>
    public double Height { get; } = Math.Max(3, share * Tallest);

    /// <summary>This week or this month: drawn in the accent and labelled.</summary>
    public bool IsCurrent { get; } = isCurrent;

    /// <summary>This period in the accent; the others recede in the palette's muted ink.</summary>
    public Avalonia.Media.IBrush? Fill => Avalonia.Application.Current?.FindResource(IsCurrent ? "Brush.Accent" : "Brush.Text.Disabled") as Avalonia.Media.IBrush;
}

/// <summary>A series among the most watched.</summary>
public sealed partial class TopShowViewModel(ShellViewModel shell, WatchedShow show)
{
    public string Title { get; } = show.Title;

    public string? ThumbPath { get; } = show.Thumb;

    public string Detail { get; } = (show.Episodes == 1 ? "1 episode" : $"{show.Episodes} episodes") + "  ·  " + ActivityPageViewModel.Hours(show.Hours);

    public bool CanOpen => show.RatingKey is not null;

    public string OpenTip => $"Open {show.Title}";

    [RelayCommand]
    private void Open()
    {
        if (show.RatingKey is { } key) shell.OpenItem(new MetadataItem { RatingKey = key, Type = "show", Title = show.Title, Thumb = show.Thumb });
    }
}

/// <summary>One viewing in the history.</summary>
public sealed partial class HistoryRowViewModel(ShellViewModel shell, MetadataItem entry)
{
    public MetadataItem Entry { get; } = entry;

    public string Heading => Entry.Type == "episode" ? Entry.GrandparentTitle ?? Entry.Title : Entry.Title;

    public string Detail => Entry.Type == "episode"
        ? string.Join("  ·  ", new[] { Format.EpisodeCode(Entry), Entry.Title }.Where(s => !string.IsNullOrEmpty(s)))
        : "Film";

    public string? ThumbPath => Entry.Thumb ?? Entry.GrandparentThumb;

    /// <summary>"Today, 21:04", "Yesterday, 19:30", "Tue 23 Sep, 20:15".</summary>
    public string When => Describe(Entry.ViewedAt, DateTimeOffset.Now);

    public static string Describe(long? viewedAt, DateTimeOffset now)
    {
        if (viewedAt is not { } seconds) return string.Empty;
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        var time = at.ToString("t", CultureInfo.CurrentCulture);
        var days = (now.ToLocalTime().Date - at.Date).Days;
        return days switch
        {
            0 => $"Today, {time}",
            1 => $"Yesterday, {time}",
            < 7 => $"{at.ToString("dddd", CultureInfo.CurrentCulture)}, {time}",
            _ when at.Year == now.Year => at.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            _ => at.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    [RelayCommand]
    private void Open() => shell.OpenItem(new MetadataItem { RatingKey = Entry.RatingKey, Type = Entry.Type, Title = Entry.Title, ParentRatingKey = Entry.ParentRatingKey, GrandparentRatingKey = Entry.GrandparentRatingKey, GrandparentTitle = Entry.GrandparentTitle });
}
