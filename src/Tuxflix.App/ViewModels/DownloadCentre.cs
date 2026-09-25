using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Downloads;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The downloads as the window shows them: the queue with its progress, what is kept, the rules,
/// the free space, and a line for the status bar. Lives as long as the window.
/// </summary>
/// <remarks>
/// The manager works on workers and tells of every change with a copy. Changes are gathered and
/// applied here on the UI thread at most ten times a second, so progress never floods it. A row is
/// made once per download and updated in place, so a page holding one (an item page's Download
/// button) follows it without listening to anything else; the same goes for a series' or a
/// season's group. The disk is measured on a worker, every few seconds at most.
/// </remarks>
public sealed partial class DownloadCentre : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, DownloadRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DownloadGroupViewModel> _groups = new(StringComparer.Ordinal);
    private readonly object _incomingGate = new();
    private readonly Dictionary<string, long> _applied = new(StringComparer.Ordinal);
    private Dictionary<string, (DownloadRecord? Record, long Sequence)> _incoming = new(StringComparer.Ordinal);
    private bool _applying;
    private long _spaceMeasured;
    private bool _measuring;

    /// <param name="post">Runs an action on the UI thread; tests pass one they run by hand.</param>
    public DownloadCentre(ShellViewModel shell, DownloadManager manager, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(post);
        _shell = shell;
        _post = post;
        Manager = manager;
        manager.Changed += record => Arrive(record.Id, record, record.Sequence);
        manager.Removed += (id, sequence) => Arrive(id, null, sequence);
        manager.RulesChanged += () => post(ShowRules);

        // The index is read on a worker; the lists fill once it is in.
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Loaded = filled.Task;
        _ = Task.Run(() =>
        {
            manager.Load();
            post(() =>
            {
                foreach (var record in manager.Snapshot()) Apply(record.Id, record, record.Sequence);
                ShowRules();
                Summarise();
                filled.TrySetResult();
            });
        });
    }

    public DownloadManager Manager { get; }

    /// <summary>Done once the index has been read and the lists filled.</summary>
    public Task Loaded { get; }

    /// <summary>Waiting, running, paused and stopped downloads, in queue order.</summary>
    public ObservableCollection<DownloadRowViewModel> Queue { get; } = [];

    /// <summary>What is kept, newest first.</summary>
    public ObservableCollection<DownloadRowViewModel> Kept { get; } = [];

    public ObservableCollection<DownloadRuleViewModel> Rules { get; } = [];

    public bool HasQueue => Queue.Count > 0;

    public bool HasKept => Kept.Count > 0;

    public bool HasRules => Rules.Count > 0;

    public bool IsEmpty => Queue.Count == 0 && Kept.Count == 0;

    /// <summary>The status bar's line: "Nothing queued", "Downloading · 42%", "3 queued · paused".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "Nothing queued";

    /// <summary>How far the running downloads are together, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    /// <summary>"12.4 GB kept · 812 GB free on this drive".</summary>
    [ObservableProperty]
    public partial string SpaceText { get; private set; } = string.Empty;

    /// <summary>A download row, when the item is downloaded or queued.</summary>
    public DownloadRowViewModel? Row(string serverId, string ratingKey) =>
        _rows.GetValueOrDefault(DownloadRecord.Key(serverId, ratingKey)) is { IsGone: false } row ? row : null;

    /// <summary>
    /// The row for an item whether or not it is downloaded: one not downloaded is "gone" until it is,
    /// and the same row then comes to life, so a page can hold it from the start.
    /// </summary>
    public DownloadRowViewModel RowFor(string serverId, MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var id = DownloadRecord.Key(serverId, item.RatingKey);
        if (_rows.TryGetValue(id, out var row)) return row;
        row = new DownloadRowViewModel(this, new DownloadRecord { ServerId = serverId, RatingKey = item.RatingKey, Type = item.Type, Title = item.Title });
        row.Gone();
        _rows[id] = row;
        return row;
    }

    /// <summary>What is downloaded of a series or a season, kept up to date.</summary>
    public DownloadGroupViewModel Group(string serverId, MetadataItem showOrSeason)
    {
        ArgumentNullException.ThrowIfNull(showOrSeason);
        var key = DownloadRecord.Key(serverId, showOrSeason.RatingKey);
        if (!_groups.TryGetValue(key, out var group))
        {
            group = new DownloadGroupViewModel(this, serverId, showOrSeason);
            _groups[key] = group;
            group.Refresh(_rows.Values);
        }

        return group;
    }

    /// <summary>Queues a film or an episode from the open server; returns its row.</summary>
    public DownloadRowViewModel? Download(ServerSession session, MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.MachineIdentifier is not { } server) return null;
        var record = Manager.Enqueue(item, server, session.Name);
        return Apply(record.Id, record, record.Sequence);
    }

    /// <summary>Queues every episode of a season, in order, from a worker (the list comes from the server).</summary>
    public async Task DownloadAllAsync(ServerSession session, MetadataItem season)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(season);
        if (session.MachineIdentifier is not { } server) return;
        var episodes = await Task.Run(() => session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None));
        foreach (var episode in episodes.Where(e => e.Type == "episode")) Manager.Enqueue(episode, server, session.Name);
    }

    /// <summary>Keeps the next few unwatched episodes of a series or a season.</summary>
    public void KeepNext(ServerSession session, MetadataItem showOrSeason, int count)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.MachineIdentifier is { } server) Manager.SetRule(showOrSeason, server, session.Name, count);
    }

    /// <summary>Plays a kept copy, with the server if it is open and without it if not.</summary>
    internal void Play(DownloadRowViewModel row) => _ = _shell.PlayDownloadAsync(row.Record);

    /// <summary>
    /// Called from a worker for every change (a null record: taken off the list); applied on the UI
    /// thread in batches. Copies can arrive out of order from different threads: the newest wins.
    /// </summary>
    internal void Arrive(string id, DownloadRecord? record, long sequence)
    {
        lock (_incomingGate)
        {
            if (!_incoming.TryGetValue(id, out var waiting) || waiting.Sequence < sequence) _incoming[id] = (record, sequence);
            if (_applying) return;
            _applying = true;
        }

        _ = Task.Delay(100).ContinueWith(_ => _post(ApplyIncoming), TaskScheduler.Default);
    }

    private void ApplyIncoming()
    {
        Dictionary<string, (DownloadRecord? Record, long Sequence)> batch;
        lock (_incomingGate)
        {
            batch = _incoming;
            _incoming = new(StringComparer.Ordinal);
            _applying = false;
        }

        foreach (var (id, (record, sequence)) in batch) Apply(id, record, sequence);
        Summarise();
    }

    private DownloadRowViewModel? Apply(string id, DownloadRecord? record, long sequence)
    {
        _rows.TryGetValue(id, out var row);

        // Older than what the row already shows: a late copy of a download since moved on or removed.
        if (_applied.TryGetValue(id, out var shown) && shown >= sequence) return row;
        _applied[id] = sequence;
        if (record is null)
        {
            if (row is null) return null;

            // Kept, gone: a page holding it shows Download again, and a new download revives it.
            row.Gone();
            Queue.Remove(row);
            Kept.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new DownloadRowViewModel(this, record);
                _rows[id] = row;
            }
            else
            {
                row.Update(record);
            }

            Place(row);
        }

        foreach (var group in _groups.Values) group.Refresh(_rows.Values);
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(HasKept));
        OnPropertyChanged(nameof(IsEmpty));
        return row;
    }

    /// <summary>Kept rows go to the kept list, newest first; the rest stand in the manager's queue order.</summary>
    private void Place(DownloadRowViewModel row)
    {
        if (row.IsDone)
        {
            Queue.Remove(row);
            if (!Kept.Contains(row))
            {
                var at = 0;
                while (at < Kept.Count && Kept[at].Record.CompletedAt >= row.Record.CompletedAt) at++;
                Kept.Insert(at, row);
            }

            return;
        }

        Kept.Remove(row);
        if (!Queue.Contains(row)) Queue.Add(row);
    }

    /// <summary>After a move in the manager's queue, the list follows its order.</summary>
    internal void Reorder()
    {
        var order = Manager.Snapshot().Select(r => r.Id).ToList();
        var sorted = Queue.OrderBy(r => order.IndexOf(r.Record.Id)).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var from = Queue.IndexOf(sorted[i]);
            if (from != i) Queue.Move(from, i);
        }
    }

    private void Summarise()
    {
        var running = Queue.Where(r => r.Record.State == DownloadState.Downloading).ToList();
        var waiting = Queue.Count(r => r.Record.State == DownloadState.Queued);
        var paused = Queue.Count(r => r.Record.State == DownloadState.Paused);
        var failed = Queue.Count(r => r.Record.State == DownloadState.Failed);
        var total = running.Sum(r => r.Record.TotalBytes);
        IsDownloading = running.Count > 0;
        Progress = total > 0 ? running.Sum(r => r.Record.DoneBytes) / (double)total : 0;
        Summary = running.Count > 0 ? $"Downloading · {Progress:P0}".Replace(" %", "%", StringComparison.Ordinal) + (waiting > 0 ? $" · {waiting} queued" : string.Empty)
            : waiting > 0 ? $"{waiting} waiting for the server"
            : paused > 0 ? $"{paused} paused"
            : failed > 0 ? $"{failed} stopped"
            : Kept.Count > 0 ? (Kept.Count == 1 ? "1 kept" : $"{Kept.Count} kept")
            : "Nothing queued";
        MeasureSpace();
    }

    /// <summary>What the downloads take and what the drive has left, from a worker, every few seconds at most.</summary>
    public void MeasureSpace(bool now = false)
    {
        if (_measuring || (!now && Environment.TickCount64 - _spaceMeasured < 3000)) return;
        _measuring = true;
        _spaceMeasured = Environment.TickCount64;
        _ = Task.Run(Manager.Space).ContinueWith(
            measured => _post(() =>
            {
                _measuring = false;
                if (measured.IsCompletedSuccessfully)
                {
                    var (used, free) = measured.Result;
                    SpaceText = $"{DownloadRowViewModel.Bytes(used)} kept" + (free is { } left ? $" · {DownloadRowViewModel.Bytes(left)} free on this drive" : string.Empty);
                }
            }),
            TaskScheduler.Default);
    }

    private void ShowRules()
    {
        Rules.Clear();
        foreach (var rule in Manager.Rules()) Rules.Add(new DownloadRuleViewModel(this, rule));
        OnPropertyChanged(nameof(HasRules));
        foreach (var group in _groups.Values) group.Refresh(_rows.Values);
    }
}

/// <summary>One download: a row on the Downloads page, and the state behind an item page's Download button.</summary>
public sealed partial class DownloadRowViewModel : ObservableObject
{
    private readonly DownloadCentre _centre;

    internal DownloadRowViewModel(DownloadCentre centre, DownloadRecord record)
    {
        _centre = centre;
        Record = record;
    }

    public DownloadRecord Record { get; private set; }

    /// <summary>Not downloaded (never, or taken off the list): an item page holding it offers Download.</summary>
    public bool IsGone { get; private set; }

    public bool IsPresent => !IsGone;

    public string Title => Record.Type == "episode" ? Record.ShowTitle ?? Record.Title : Record.Title;

    /// <summary>"S1 · E3 — The Crossing", or the year.</summary>
    public string Subtitle => Record.Type == "episode"
        ? $"S{Record.Season} · E{Record.Episode} — {Record.Title}"
        : Record.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// An episode's still or a film's backdrop: kept beside the file once it is downloaded, from the
    /// server while it waits (its own copy arrives when the transfer starts).
    /// </summary>
    public string? PicturePath => IsDone || Record.Picture is null || !_centre.Manager.IsAttached(Record.ServerId)
        ? new Uri(Record.ArtworkPath(Record.Type == "episode" ? "still.jpg" : "backdrop.jpg")).AbsoluteUri
        : Record.Picture;

    public string PosterPath => new Uri(Record.ArtworkPath("poster.jpg")).AbsoluteUri;

    public bool IsDone => Record.State == DownloadState.Done;

    public bool IsActive => Record.State == DownloadState.Downloading;

    public bool IsFailed => Record.State == DownloadState.Failed;

    public bool CanPause => Record.State is DownloadState.Queued or DownloadState.Downloading;

    public bool CanResume => Record.State is DownloadState.Paused or DownloadState.Failed;

    public double Fraction => Record.Fraction;

    public bool ShowsProgress => !IsDone;

    public bool IsWatched => Record.Watched;

    /// <summary>Where a kept copy stopped, 0 to 1, for its bar.</summary>
    public double WatchProgress => Record.Duration > 0 && Record.ViewOffset > 0 ? Math.Clamp(Record.ViewOffset / (double)Record.Duration, 0, 1) : 0;

    public bool HasWatchProgress => IsDone && WatchProgress > 0;

    /// <summary>"Downloading · 42% of 1.4 GB", "Waiting for Den", "Stopped: …", "1.4 GB".</summary>
    public string Status => Record.State switch
    {
        DownloadState.Done => Bytes(Record.TotalBytes) + (Record.Watched ? " · watched" : string.Empty),
        DownloadState.Downloading => $"Downloading · {Math.Floor(Record.Fraction * 100).ToString(CultureInfo.InvariantCulture)}%" + (Record.TotalBytes > 0 ? $" of {Bytes(Record.TotalBytes)}" : string.Empty),
        DownloadState.Paused => $"Paused at {Math.Floor(Record.Fraction * 100).ToString(CultureInfo.InvariantCulture)}%",
        DownloadState.Failed => Record.Error ?? "Stopped",
        _ => _centre.Manager.IsAttached(Record.ServerId) ? "Queued" : $"Waiting for {Record.ServerName}",
    };

    /// <summary>What the item page's button says.</summary>
    public string ButtonLabel => Record.State switch
    {
        _ when IsGone => "DOWNLOAD",
        DownloadState.Done => "DOWNLOADED",
        DownloadState.Downloading => $"{Math.Floor(Record.Fraction * 100).ToString(CultureInfo.InvariantCulture)}%",
        DownloadState.Paused => "PAUSED",
        DownloadState.Failed => "STOPPED",
        _ => "QUEUED",
    };

    internal void Update(DownloadRecord record)
    {
        Record = record;
        IsGone = false;
        OnPropertyChanged(string.Empty);
    }

    internal void Gone()
    {
        IsGone = true;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Pause() => _centre.Manager.Pause(Record.Id);

    [RelayCommand]
    private void Resume() => _centre.Manager.Resume(Record.Id);

    /// <summary>Cancels a download, or deletes a kept one.</summary>
    [RelayCommand]
    private void Remove() => _centre.Manager.Remove(Record.Id);

    [RelayCommand]
    private void MoveToFront()
    {
        _centre.Manager.MoveToFront(Record.Id);
        _centre.Reorder();
    }

    [RelayCommand]
    private void Play()
    {
        if (IsDone) _centre.Play(this);
    }

    /// <summary>"812 MB", "4.2 GB".</summary>
    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };
}

/// <summary>What is downloaded of one series or season, and its rule: the state behind its Download button.</summary>
public sealed partial class DownloadGroupViewModel(DownloadCentre centre, string serverId, MetadataItem showOrSeason) : ObservableObject
{
    public MetadataItem Item { get; } = showOrSeason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(HasAny))]
    public partial int KeptCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(HasAny))]
    public partial int WaitingCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(HasRule), nameof(RuleText))]
    public partial DownloadRule? Rule { get; private set; }

    public bool HasAny => KeptCount + WaitingCount > 0;

    public bool HasRule => Rule is not null;

    public string RuleText => Rule is { } rule ? $"Keeping the next {rule.Keep} unwatched" : string.Empty;

    /// <summary>"3 downloaded", "Downloading 2 of 5 · 40%", or nothing when none are.</summary>
    public string Label => WaitingCount > 0
        ? $"Downloading {KeptCount + 1} of {KeptCount + WaitingCount} · {Math.Floor(Fraction * 100).ToString(CultureInfo.InvariantCulture)}%"
        : KeptCount > 0 ? (KeptCount == 1 ? "1 downloaded" : $"{KeptCount} downloaded")
        : Rule is not null ? "Waiting for episodes" : string.Empty;

    internal void Refresh(IEnumerable<DownloadRowViewModel> rows)
    {
        var mine = rows.Where(r => !r.IsGone && r.Record.ServerId == serverId && (r.Record.ShowKey == Item.RatingKey || r.Record.SeasonKey == Item.RatingKey)).ToList();
        KeptCount = mine.Count(r => r.IsDone);
        WaitingCount = mine.Count - KeptCount;
        var total = mine.Sum(r => r.Record.TotalBytes);
        Fraction = total > 0 ? mine.Sum(r => r.IsDone ? r.Record.TotalBytes : r.Record.DoneBytes) / (double)total : 0;
        Rule = centre.Manager.RuleFor(serverId, Item.RatingKey);
    }
}

/// <summary>A rule on the Downloads page.</summary>
public sealed partial class DownloadRuleViewModel(DownloadCentre centre, DownloadRule rule)
{
    public string Title => rule.Title;

    public string Description => $"Keeps the next {rule.Keep} unwatched episodes; watched ones go a day later.";

    [RelayCommand]
    private void Remove() => centre.Manager.RemoveRule(rule.Id);
}
