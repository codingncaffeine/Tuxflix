using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>A page that keeps itself current when the server says its library changed.</summary>
public interface ILiveRefresh
{
    /// <summary>
    /// Libraries changed on the server: <paramref name="sections"/> names them (empty when the
    /// server did not say), <paramref name="items"/> the items it named.
    /// </summary>
    void OnLibraryChanged(IReadOnlySet<string> sections, IReadOnlySet<string> items);
}

/// <summary>
/// The open server's live news, from its notification socket: what plays on the viewer's other
/// devices, what the server is busy with, and library changes, which reach the page on screen.
/// </summary>
/// <remarks>
/// The socket is read on a thread of its own; each message is handed to the UI thread whole
/// (<see cref="Post"/>), where it only updates state. Reading a changed item again, or what plays,
/// goes to a worker. A library scan announces hundreds of changes: they are gathered, and the
/// page refreshes once the stream pauses, at most every ten seconds while a scan runs.
/// </remarks>
public sealed partial class LiveUpdates(ShellViewModel shell) : ObservableObject
{
    private readonly HashSet<string> _sections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private INotificationSource? _source;
    private ServerSession? _session;
    private bool _libraryDue;
    private bool _itemsDue;
    private bool _sessionsDue;
    private int _generation;

    /// <summary>Hands work to the UI thread; under test, whatever runs it.</summary>
    public Action<Action> Post { get; init; } = work => Avalonia.Threading.Dispatcher.UIThread.Post(work);

    /// <summary>How long library changes are gathered before the page refreshes: short for one change, long while a scan runs.</summary>
    public TimeSpan LibraryPause { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ScanPause { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long changed items and playbacks are gathered before they are read again.</summary>
    public TimeSpan ReadPause { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>What the server is busy with: a library scan, a refresh.</summary>
    public ObservableCollection<ServerActivityViewModel> Activities { get; } = [];

    /// <summary>What plays on the viewer's other devices.</summary>
    public ObservableCollection<NowPlayingViewModel> Playing { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsConnected { get; private set; }

    /// <summary>A few words on something the viewer just did ("Added to Weekend Marathon"), for a moment.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string? Notice { get; private set; }

    /// <summary>The status bar's line: a notice, else the first activity, else what plays, else nothing.</summary>
    public string Summary => Notice is { } notice ? notice : Activities.FirstOrDefault() is { } activity
        ? activity.Line
        : Playing.Count switch
        {
            0 => "Nothing playing",
            1 => $"Playing on {Playing[0].Device}",
            var n => string.Create(CultureInfo.CurrentCulture, $"{n} playing on your devices"),
        };

    public bool HasActivity => Activities.Count > 0;

    /// <summary>Listens to another server, or to none; everything heard from the last one is dropped.</summary>
    internal void Attach(ServerSession? session, INotificationSource? source)
    {
        _generation++;
        _source?.Dispose();
        _source = null;
        _session = session;
        Activities.Clear();
        Playing.Clear();
        _sections.Clear();
        _items.Clear();
        _stale.Clear();
        IsConnected = false;
        Changed();
        if (session is null || source is null) return;

        var generation = _generation;
        _source = source;
        source.Received += message => Post(() =>
        {
            if (generation == _generation) Apply(message);
        });
        source.ConnectionChanged += up => Post(() =>
        {
            if (generation != _generation) return;
            IsConnected = up;
            if (up) _ = RefreshPlayingAsync();
        });
        source.Start();
        _ = RefreshPlayingAsync();
    }

    /// <summary>Takes in one message. Runs on the UI thread and only updates state or schedules a read.</summary>
    internal void Apply(NotificationContainer message)
    {
        switch (message.Type)
        {
            case "playing":
                foreach (var state in message.Playing ?? []) OnPlaying(state);
                break;
            case "timeline":
                foreach (var entry in message.Timeline ?? []) OnTimeline(entry);
                break;
            case "activity":
                foreach (var notice in message.Activities ?? []) OnActivity(notice);
                break;
            case "status":
                foreach (var status in message.Status ?? []) Log.Debug($"Server: {status.Title}");
                break;

            // Progress lines, conversions, settings and the background queue belong to the
            // server's own dashboard; a viewer's client reads them and lets them go.
            default:
                break;
        }
    }

    private void OnPlaying(PlaySessionState state)
    {
        var shown = Playing.FirstOrDefault(p => p.SessionKey == state.SessionKey);
        if (shown is null || state.State == "stopped")
        {
            // A playback nobody lists yet, or one that ended: ask the server who and what it is.
            _sessionsDue = true;
            Later(ReadPause, ReadPlaying);
            if (state.State == "stopped" && state.RatingKey is { } finished) MarkStale(finished);
            return;
        }

        shown.Update(state);

        // The viewer's own playback elsewhere moves the item's progress here too.
        if (state.RatingKey is not { } key || state.ViewOffset is not > 0 || shell.Viewer is not { } viewer) return;
        viewer.Noted(key);
        if (viewer.Find(key) is { Pending: 0 } item) item.ViewOffset = state.ViewOffset;
    }

    private void OnTimeline(TimelineEntry entry)
    {
        if (!entry.IsLibraryChange) return;
        if (entry.SectionId is { } section) _sections.Add(section.ToString(CultureInfo.InvariantCulture));
        if (entry.ItemId is { } id)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            _items.Add(key);
            MarkStale(key);
        }

        if (_libraryDue) return;
        _libraryDue = true;
        Later(Activities.Any(a => a.IsScan) ? ScanPause : LibraryPause, FlushLibrary);
    }

    private void OnActivity(ActivityNotification notice)
    {
        if (notice.Activity is not { } activity || (notice.Uuid ?? activity.Uuid) is not { } uuid) return;
        var shown = Activities.FirstOrDefault(a => a.Uuid == uuid);
        if (notice.Event == "ended")
        {
            if (shown is not null) Activities.Remove(shown);

            // A scan that ended changed its library, whether or not every item said so.
            if (activity.Type?.StartsWith("library.", StringComparison.Ordinal) == true)
            {
                if (activity.Context?.LibrarySectionId is { } section) _sections.Add(section.ToString(CultureInfo.InvariantCulture));
                if (!_libraryDue)
                {
                    _libraryDue = true;
                    Later(LibraryPause, FlushLibrary);
                }
            }
        }
        else if (shown is null)
        {
            Activities.Add(new ServerActivityViewModel(uuid, activity));
        }
        else
        {
            shown.Update(activity);
        }

        Changed();
    }

    private void MarkStale(string ratingKey)
    {
        if (shell.Viewer?.Find(ratingKey) is null) return;
        _stale.Add(ratingKey);
        if (_itemsDue) return;
        _itemsDue = true;
        Later(ReadPause, ReadItems);
    }

    private void FlushLibrary()
    {
        _libraryDue = false;
        var sections = new HashSet<string>(_sections, StringComparer.Ordinal);
        var items = new HashSet<string>(_items, StringComparer.Ordinal);
        _sections.Clear();
        _items.Clear();
        switch (shell.Router.Current)
        {
            case ILiveRefresh page:
                page.OnLibraryChanged(sections, items);
                break;

            // Home has no part of its own here: its shelves are simply read again.
            case HomePageViewModel home:
                _ = home.ActivateAsync();
                break;
        }
    }

    private void ReadItems()
    {
        _itemsDue = false;
        var keys = _stale.ToList();
        _stale.Clear();
        if (shell.Viewer is { } viewer) _ = ReadAsync(() => viewer.RefreshAsync(keys));
    }

    private void ReadPlaying()
    {
        if (!_sessionsDue) return;
        _sessionsDue = false;
        _ = RefreshPlayingAsync();
    }

    /// <summary>
    /// Reads what plays now, keeping the viewer's own playbacks on devices other than this one.
    /// Reads take turns: a connection coming up, a stopped playback and a page can all ask at once,
    /// and two reads merging into the list together would list a playback twice.
    /// </summary>
    public async Task RefreshPlayingAsync()
    {
        if (_session is not { } session) return;
        await _reading.WaitAsync();
        try
        {
            var generation = _generation;
            var sessions = await ReadAsync(() => session.Client.GetSessionsAsync(CancellationToken.None));
            if (sessions is null || generation != _generation) return;

            var mine = sessions.Where(s => IsMine(session, s)).ToList();
            foreach (var gone in Playing.Where(p => mine.TrueForAll(m => m.SessionKey != p.SessionKey)).ToList()) Playing.Remove(gone);
            foreach (var playback in mine)
            {
                if (Playing.FirstOrDefault(p => p.SessionKey == playback.SessionKey) is { } shown) shown.Update(playback);
                else Playing.Add(new NowPlayingViewModel(shell, playback));
            }

            Changed();
        }
        finally
        {
            _reading.Release();
        }
    }

    private readonly SemaphoreSlim _reading = new(1, 1);

    /// <summary>
    /// The viewer's own playback on another device. A server shows its owner everyone's playbacks,
    /// the owner's under account 1; anybody else sees only their own.
    /// </summary>
    private bool IsMine(ServerSession session, MetadataItem playback) =>
        playback.Player?.MachineIdentifier != shell.Identity.ClientIdentifier
        && (!session.IsOwner || playback.User?.Id == "1");

    /// <summary>Says <paramref name="words"/> in the status bar for a few seconds.</summary>
    public void Say(string words)
    {
        Notice = words;
        Later(TimeSpan.FromSeconds(4), () =>
        {
            if (Notice == words) Notice = null;
        });
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasActivity));
    }

    private void Later(TimeSpan pause, Action work)
    {
        var generation = _generation;
        _ = Task.Delay(pause).ContinueWith(_ => Post(() =>
        {
            if (generation == _generation) work();
        }), TaskScheduler.Default);
    }

    private static async Task<T?> ReadAsync<T>(Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await Task.Run(read);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"Live news could not be read: {ex.Message}");
            return null;
        }
    }

    private static async Task ReadAsync(Func<Task> read)
    {
        try
        {
            await Task.Run(read);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"Live news could not be read: {ex.Message}");
        }
    }
}

/// <summary>Something the server is busy with, for the status bar and the Activity page.</summary>
public sealed partial class ServerActivityViewModel : ObservableObject
{
    internal ServerActivityViewModel(string uuid, ServerActivity activity)
    {
        Uuid = uuid;
        Update(activity);
    }

    public string Uuid { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Subtitle { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line), nameof(HasProgress), nameof(Fraction))]
    public partial int? Progress { get; private set; }

    public bool IsScan { get; private set; }

    public bool HasProgress => Progress is not null;

    public double Fraction => (Progress ?? 0) / 100.0;

    /// <summary>"Scanning Movies · 42%".</summary>
    public string Line => Progress is { } done ? string.Create(CultureInfo.CurrentCulture, $"{Title} · {done}%") : Title;

    internal void Update(ServerActivity activity)
    {
        Title = activity.Title ?? "Working";
        Subtitle = activity.Subtitle;
        Progress = activity.Progress is { } p and >= 0 ? Math.Min(100, p) : null;
        IsScan = activity.Type?.StartsWith("library.", StringComparison.Ordinal) == true;
    }
}

/// <summary>A playback on another of the viewer's devices.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    internal NowPlayingViewModel(ShellViewModel shell, MetadataItem playback)
    {
        _shell = shell;
        SessionKey = playback.SessionKey;
        Update(playback);
    }

    public string? SessionKey { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Detail), nameof(StillPath))]
    public partial MetadataItem Item { get; private set; } = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(TimeText))]
    public partial long ViewOffset { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(StateText))]
    public partial string State { get; private set; } = "playing";

    public string Device { get; private set; } = string.Empty;

    public string Product { get; private set; } = string.Empty;

    public string Heading => Item.Type == "episode" ? Item.GrandparentTitle ?? Item.Title : Item.Title;

    public string Detail => Item.Type == "episode"
        ? $"{Format.EpisodeCode(Item)}  ·  {Item.Title}"
        : Item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string? StillPath => Artwork.Still(Item);

    public double Progress => Item.Duration is > 0 ? Math.Clamp((double)ViewOffset / Item.Duration.Value, 0, 1) : 0;

    public string TimeText => $"{Clock(ViewOffset)} / {Clock(Item.Duration ?? 0)}";

    public bool IsPaused => State == "paused";

    public string StateText => State switch
    {
        "paused" => "PAUSED",
        "buffering" => "BUFFERING",
        _ => "PLAYING",
    };

    public string DeviceLine => string.IsNullOrEmpty(Product) ? Device : $"{Device}  ·  {Product}";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Open() => _shell.OpenItem(Item);

    internal void Update(MetadataItem playback)
    {
        Item = playback;
        ViewOffset = playback.ViewOffset ?? 0;
        State = playback.Player?.State ?? "playing";
        Device = playback.Player?.Title ?? "Another device";
        Product = playback.Player?.Product ?? string.Empty;
        OnPropertyChanged(nameof(DeviceLine));
    }

    internal void Update(PlaySessionState state)
    {
        if (state.ViewOffset is { } offset) ViewOffset = offset;
        if (state.State is { } now) State = now;
    }

    private static string Clock(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
