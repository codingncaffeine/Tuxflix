using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// What the viewer has made of one item, watched, part-way or rated, shared by every tile, row and
/// page that shows it, so a change made in one place shows everywhere at once.
/// </summary>
/// <remarks>
/// Records arrive from many requests, some started before a change the viewer made here landed
/// on the server. A record is taken only if its request began after the last change was
/// confirmed, and after the record already held; while a change is on its way none is taken.
/// A record made up in the client (no request behind it) only fills a state nobody has filled.
/// </remarks>
public sealed partial class ItemState : ObservableObject
{
    private bool _filled;

    internal ItemState(MetadataItem item)
    {
        RatingKey = item.RatingKey;
        Type = item.Type;
        ParentKey = item.ParentRatingKey;
        GrandparentKey = item.GrandparentRatingKey;
    }

    public string RatingKey { get; }

    public string Type { get; }

    internal string? ParentKey { get; private set; }

    internal string? GrandparentKey { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatched), nameof(IsUnplayed))]
    public partial int ViewCount { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatched), nameof(Progress), nameof(HasProgress), nameof(IsUnplayed))]
    public partial long? ViewOffset { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(HasProgress))]
    public partial long? Duration { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatched), nameof(UnwatchedLeaves), nameof(IsUnplayed))]
    public partial int? LeafCount { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatched), nameof(UnwatchedLeaves), nameof(IsUnplayed))]
    public partial int? ViewedLeafCount { get; internal set; }

    /// <summary>The viewer's own rating, 0 to 10 (five stars in halves); null when unrated.</summary>
    [ObservableProperty]
    public partial double? UserRating { get; internal set; }

    [ObservableProperty]
    public partial long? LastViewedAt { get; internal set; }

    /// <summary>Changes sent and not yet answered.</summary>
    internal int Pending { get; set; }

    /// <summary>When the last change was confirmed, on the stopwatch clock.</summary>
    internal long ConfirmedAt { get; set; }

    /// <summary>When the request behind the record held began, on the stopwatch clock.</summary>
    internal long FetchedAt { get; private set; }

    /// <summary>A series or season counts its episodes; a film or an episode its own plays.</summary>
    public bool IsContainer => Type is "show" or "season" || LeafCount is > 0;

    public bool IsWatched => IsContainer
        ? LeafCount is > 0 && ViewedLeafCount >= LeafCount
        : ViewCount > 0 && ViewOffset is null or 0;

    public double? Progress => ViewOffset is > 0 && Duration is > 0 ? Math.Clamp((double)ViewOffset.Value / Duration.Value, 0, 1) : null;

    public bool HasProgress => Progress is > 0 and < 1;

    public int UnwatchedLeaves => LeafCount is { } leaves ? Math.Max(0, leaves - (ViewedLeafCount ?? 0)) : 0;

    /// <summary>Never started: no play, no place, no episode seen.</summary>
    public bool IsUnplayed => ViewCount == 0 && ViewOffset is null or 0 && ViewedLeafCount is null or 0;

    /// <summary>A detached state for an item with no server behind it (a test, a page without a session).</summary>
    public static ItemState Detached(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var state = new ItemState(item);
        state.Merge(item);
        return state;
    }

    /// <summary>Takes a record's values if it is the newest word on the item; true when it did.</summary>
    internal bool Merge(MetadataItem item)
    {
        if (item.FetchedAt == 0)
        {
            if (_filled) return false;
        }
        else if (Pending > 0 || item.FetchedAt < ConfirmedAt || item.FetchedAt < FetchedAt)
        {
            return false;
        }

        _filled = true;
        FetchedAt = Math.Max(FetchedAt, item.FetchedAt);
        ParentKey ??= item.ParentRatingKey;
        GrandparentKey ??= item.GrandparentRatingKey;
        ViewCount = item.ViewCount ?? 0;
        ViewOffset = item.ViewOffset is > 0 ? item.ViewOffset : null;
        if (item.Duration is > 0) Duration = item.Duration;
        LeafCount = item.LeafCount;
        ViewedLeafCount = item.ViewedLeafCount;
        UserRating = item.UserRating;
        LastViewedAt = item.LastViewedAt;
        return true;
    }

    internal (int, long?, int?, double?) Snapshot() => (ViewCount, ViewOffset, ViewedLeafCount, UserRating);

    internal void Restore((int ViewCount, long? ViewOffset, int? ViewedLeafCount, double? UserRating) snapshot)
    {
        ViewCount = snapshot.ViewCount;
        ViewOffset = snapshot.ViewOffset;
        ViewedLeafCount = snapshot.ViewedLeafCount;
        UserRating = snapshot.UserRating;
    }
}

/// <summary>
/// The viewer's state on the open server: one <see cref="ItemState"/> per item on screen, and the
/// changes the viewer makes, shown at once and sent from a worker, taken back if the server says no.
/// </summary>
/// <remarks>
/// States are held weakly, by whatever shows them: a page that is gone lets its states go. A state
/// the viewer changed is held for the session, so an older record cannot undo the change.
/// Used from the UI thread; the requests run on workers.
/// </remarks>
public sealed class ViewerState(ServerSession session)
{
    private readonly Dictionary<string, WeakReference<ItemState>> _states = new(StringComparer.Ordinal);
    private readonly HashSet<ItemState> _changed = [];
    private readonly Dictionary<string, long> _changedAt = new(StringComparer.Ordinal);
    private int _added;

    public ServerSession Session { get; } = session;

    /// <summary>The viewer changed an item here, or the server's word on one arrived: pages that list it may redraw.</summary>
    public event Action<ItemState>? Changed;

    /// <summary>The shared state for an item, filled from its record when that is the newest word.</summary>
    public ItemState For(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_states)
        {
            if (!_states.TryGetValue(item.RatingKey, out var weak) || !weak.TryGetTarget(out var state))
            {
                state = new ItemState(item);
                _states[item.RatingKey] = new WeakReference<ItemState>(state);
                if (++_added % 512 == 0) Prune();

                // Made from a record read before the item last changed: the best there is now,
                // and the server's newer word follows.
                if (_changedAt.TryGetValue(item.RatingKey, out var changed) && item.FetchedAt < changed) _ = RefreshQuietlyAsync([item.RatingKey]);
            }

            state.Merge(item);
            return state;
        }
    }

    /// <summary>The state of an item somebody is showing, or null.</summary>
    public ItemState? Find(string ratingKey)
    {
        lock (_states)
        {
            return _states.TryGetValue(ratingKey, out var weak) && weak.TryGetTarget(out var state) ? state : null;
        }
    }

    /// <summary>The keys of every item somebody is showing now.</summary>
    public IReadOnlyList<string> Shown()
    {
        lock (_states)
        {
            return [.. _states.Where(p => p.Value.TryGetTarget(out _)).Select(p => p.Key)];
        }
    }

    /// <summary>The server's word on an item changed now (a playback elsewhere moved it): a record read before it is out of date.</summary>
    public void Noted(string ratingKey)
    {
        lock (_states) _changedAt[ratingKey] = Stopwatch.GetTimestamp();
    }

    /// <summary>Takes a newer record into the item's state, if anybody shows it.</summary>
    public void Take(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Find(item.RatingKey) is { } state && state.Merge(item)) Changed?.Invoke(state);
    }

    /// <summary>Reads items again from the server, for the states that show them.</summary>
    public async Task RefreshAsync(IEnumerable<string> ratingKeys, CancellationToken cancellation = default)
    {
        var keys = ratingKeys.Where(k => Find(k) is not null).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return;
        var items = await Task.Run(() => Session.Client.GetMetadataManyAsync(keys, cancellation), cancellation);
        foreach (var item in items) Take(item);
    }

    /// <summary>
    /// Marks an item watched or unwatched: at once here, in its season and series and in the
    /// episodes under it that are on screen, then on the server. False, and everything as it was,
    /// when the server would not take it.
    /// </summary>
    public async Task<bool> SetWatchedAsync(MetadataItem item, bool watched)
    {
        ArgumentNullException.ThrowIfNull(item);
        var state = For(item);
        var touched = new List<(ItemState State, (int, long?, int?, double?) Before)>();
        void Touch(ItemState s)
        {
            if (touched.TrueForAll(t => t.State != s)) touched.Add((s, s.Snapshot()));
        }

        Touch(state);
        if (state.IsContainer)
        {
            var delta = (watched ? state.LeafCount ?? 0 : 0) - (state.ViewedLeafCount ?? 0);
            state.ViewedLeafCount = watched ? state.LeafCount : 0;

            // The episodes (and a series' seasons) on screen, and a season's series.
            foreach (var below in Below(state))
            {
                Touch(below);
                if (below.IsContainer) below.ViewedLeafCount = watched ? below.LeafCount : 0;
                else (below.ViewCount, below.ViewOffset) = (watched ? Math.Max(1, below.ViewCount) : 0, null);
            }

            if (state.Type == "season" && state.ParentKey is { } show && Find(show) is { } series)
            {
                Touch(series);
                series.ViewedLeafCount = Math.Clamp((series.ViewedLeafCount ?? 0) + delta, 0, series.LeafCount ?? int.MaxValue);
            }
        }
        else
        {
            var was = state.IsWatched;
            (state.ViewCount, state.ViewOffset) = (watched ? Math.Max(1, state.ViewCount) : 0, null);
            if (was != watched)
            {
                foreach (var key in new[] { state.ParentKey, state.GrandparentKey })
                {
                    if (key is null || Find(key) is not { } above) continue;
                    Touch(above);
                    above.ViewedLeafCount = Math.Clamp((above.ViewedLeafCount ?? 0) + (watched ? 1 : -1), 0, above.LeafCount ?? int.MaxValue);
                }
            }
        }

        var ok = await SendAsync(touched, () => watched
            ? Session.Client.MarkWatchedAsync(item.RatingKey, CancellationToken.None)
            : Session.Client.MarkUnwatchedAsync(item.RatingKey, CancellationToken.None));
        if (ok) await RefreshQuietlyAsync(touched.Select(t => t.State.RatingKey));
        return ok;
    }

    /// <summary>Rates an item, 0 to 10 in whole points (half stars), or takes the rating away with null.</summary>
    public async Task<bool> RateAsync(MetadataItem item, double? rating)
    {
        ArgumentNullException.ThrowIfNull(item);
        var state = For(item);
        var before = state.Snapshot();
        state.UserRating = rating is { } given ? Math.Clamp(Math.Round(given, MidpointRounding.AwayFromZero), 0, 10) : null;
        return await SendAsync([(state, before)], () => Session.Client.RateAsync(item.RatingKey, state.UserRating, CancellationToken.None));
    }

    /// <summary>The viewer's playlists as last read, for the menus that add to them.</summary>
    public IReadOnlyList<MetadataItem> Playlists { get; private set; } = [];

    /// <summary>The playlists were read again or changed.</summary>
    public event Action? PlaylistsChanged;

    /// <summary>The kind of playlist an item can go in: <c>video</c>, <c>audio</c> or <c>photo</c>; null for none.</summary>
    public static string? PlaylistTypeOf(MetadataItem item) => item?.Type switch
    {
        "movie" or "episode" or "show" or "season" or "clip" => "video",
        "track" or "album" or "artist" => "audio",
        "photo" => "photo",
        _ => null,
    };

    /// <summary>Reads the viewer's playlists again.</summary>
    public async Task LoadPlaylistsAsync()
    {
        try
        {
            Playlists = await Task.Run(() => Session.Client.GetPlaylistsAsync(CancellationToken.None));
            PlaylistsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The playlists could not be read.", ex);
        }
    }

    /// <summary>Makes a playlist of <paramref name="items"/> under <paramref name="title"/>; null when the server would not.</summary>
    public async Task<MetadataItem?> CreatePlaylistAsync(string title, IReadOnlyList<MetadataItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(title) || items.Count == 0 || PlaylistTypeOf(items[0]) is not { } type || Session.MachineIdentifier is not { } machine) return null;
        try
        {
            var made = await Task.Run(() => Session.Client.CreatePlaylistAsync(title.Trim(), type, machine, [.. items.Select(i => i.RatingKey)], CancellationToken.None));
            await LoadPlaylistsAsync();
            return made;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The playlist could not be made.", ex);
            return null;
        }
    }

    /// <summary>Adds items to the end of a playlist; false when the server would not.</summary>
    public async Task<bool> AddToPlaylistAsync(MetadataItem playlist, IReadOnlyList<MetadataItem> items)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || Session.MachineIdentifier is not { } machine) return false;
        try
        {
            await Task.Run(() => Session.Client.AddToPlaylistAsync(playlist.RatingKey, machine, [.. items.Select(i => i.RatingKey)], CancellationToken.None));
            await LoadPlaylistsAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The playlist could not take the items.", ex);
            return false;
        }
    }

    /// <summary>A playlist was renamed, emptied or deleted elsewhere in the window: the menus read the list again.</summary>
    public void PlaylistsEdited() => _ = LoadPlaylistsAsync();

    private IEnumerable<ItemState> Below(ItemState container)
    {
        List<ItemState> all;
        lock (_states)
        {
            all = [.. _states.Values.Select(w => w.TryGetTarget(out var s) ? s : null).OfType<ItemState>()];
        }

        return all.Where(s => s.ParentKey == container.RatingKey || s.GrandparentKey == container.RatingKey);
    }

    private async Task<bool> SendAsync(List<(ItemState State, (int, long?, int?, double?) Before)> touched, Func<Task> send)
    {
        foreach (var (state, _) in touched)
        {
            state.Pending++;
            _changed.Add(state);
            Changed?.Invoke(state);
        }

        var ok = true;
        try
        {
            await Task.Run(send);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The server did not take a change.", ex);
            ok = false;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var (state, before) in touched)
        {
            Noted(state.RatingKey);
            state.Pending--;
            state.ConfirmedAt = now;
            if (!ok) state.Restore(before);
            Changed?.Invoke(state);
        }

        return ok;
    }

    private async Task RefreshQuietlyAsync(IEnumerable<string> keys)
    {
        try
        {
            await RefreshAsync(keys);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"The server's word after a change could not be read: {ex.Message}");
        }
    }

    private void Prune()
    {
        foreach (var dead in _states.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToList()) _states.Remove(dead);
    }
}
