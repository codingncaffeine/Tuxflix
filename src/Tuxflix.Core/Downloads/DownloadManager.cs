using System.Text.Json;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;

namespace Tuxflix.Core.Downloads;

/// <summary>
/// Films and episodes kept for watching without the server: a queue that survives restarts, one
/// or two transfers at a time under one speed limit, and the files kept.
/// </summary>
/// <remarks>
/// All the work happens on workers. Callers, the UI thread among them, only queue a change and
/// return; they get copies of the records back through <see cref="Changed"/>, raised on a worker.
/// A transfer resumes where it stopped with an HTTP range, after a pause, a cut connection or a
/// restart, and a file is kept only once its size matches the server's own figure. A server that is
/// not open leaves its downloads waiting: its sign-in lives in the keyring, never in these files.
/// The index (the records, the rules and the watch changes still to send) is one JSON file, written
/// beside itself and renamed over the old one, as the settings are.
/// </remarks>
public sealed partial class DownloadManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _indexPath;
    private readonly string _defaultFolder;
    private readonly Func<DownloadSettings> _settings;
    private readonly List<DownloadRecord> _records = [];
    private readonly List<DownloadRule> _rules = [];
    private readonly List<PendingWrite> _pending = [];
    private readonly Dictionary<string, PlexServerClient> _servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transfer> _active = new(StringComparer.Ordinal);
    private readonly SpeedLimiter _limiter;
    private readonly object _saveGate = new();
    private bool _unsaved;
    private bool _saving;
    private Task _writer = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Counts the copies handed out, so a late one never undoes a newer one.</summary>
    private long _sequence;

    /// <param name="indexPath">The file the queue, the rules and the unsent watch changes are kept in.</param>
    /// <param name="defaultFolder">Where downloads go when the settings name no folder.</param>
    /// <param name="settings">The download settings, read whenever they matter, so a change applies at once.</param>
    /// <param name="time">The clock; tests pass one they can move.</param>
    public DownloadManager(string indexPath, string defaultFolder, Func<DownloadSettings> settings, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFolder);
        ArgumentNullException.ThrowIfNull(settings);
        _indexPath = indexPath;
        _defaultFolder = defaultFolder;
        _settings = settings;
        Time = time ?? TimeProvider.System;
        _limiter = new SpeedLimiter(() => Math.Max(0, _settings().SpeedLimit));
    }

    /// <summary>A record changed; the argument is a copy. Raised on a worker.</summary>
    public event Action<DownloadRecord>? Changed;

    /// <summary>
    /// A record was taken off the list (by its <see cref="DownloadRecord.Id"/>) and its files are
    /// being deleted; the number orders it among the copies <see cref="Changed"/> hands out.
    /// </summary>
    public event Action<string, long>? Removed;

    /// <summary>A rule was added, changed or dropped.</summary>
    public event Action? RulesChanged;

    public TimeProvider Time { get; }

    /// <summary>How long an episode a rule fetched stays once watched, for a second look or a mistaken mark.</summary>
    public TimeSpan RemoveWatchedAfter { get; set; } = TimeSpan.FromDays(1);

    /// <summary>The wait after a first failed attempt; each further one doubles it.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts in a row that bring no bytes before a download stops with a reason.</summary>
    internal int MaxFailures { get; set; } = 5;

    /// <summary>Where new downloads go.</summary>
    public string Folder => _settings().Folder is { Length: > 0 } chosen ? chosen : _defaultFolder;

    /// <summary>
    /// Reads the index. Reads the disk: a worker's job, once, before the manager is used. A file
    /// that cannot be read is set aside, and the log says so.
    /// </summary>
    public void Load()
    {
        DownloadIndex? index = null;
        if (File.Exists(_indexPath))
        {
            try
            {
                index = JsonSerializer.Deserialize(File.ReadAllText(_indexPath), DownloadJsonContext.Default.DownloadIndex);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"The download list at {_indexPath} could not be read; starting with none.", ex);
                try
                {
                    File.Move(_indexPath, _indexPath + ".unreadable", overwrite: true);
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("The unreadable download list could not be set aside.", moveError);
                }
            }
        }

        if (index is null) return;

        var kept = new List<DownloadRecord>();
        foreach (var record in index.Items)
        {
            if (record.State == DownloadState.Done)
            {
                if (!File.Exists(record.MediaPath))
                {
                    Log.Info($"{record.Title} is no longer on disk; it is taken off the download list.");
                    continue;
                }
            }
            else
            {
                // Stopped by the last exit mid-transfer: it carries on. The file on disk says how far it got.
                if (record.State == DownloadState.Downloading) record.State = DownloadState.Queued;
                record.DoneBytes = File.Exists(record.PartialPath) ? new FileInfo(record.PartialPath).Length : 0;
            }

            kept.Add(record);
        }

        lock (_gate)
        {
            _records.AddRange(kept);
            _rules.AddRange(index.Rules);
            _pending.AddRange(index.Pending);
        }

        Log.Info($"Downloads: {kept.Count(r => r.State == DownloadState.Done)} kept, {kept.Count(r => r.State != DownloadState.Done)} waiting, {index.Pending.Count} watch change(s) to send.");

        // A server may have opened while the list was being read.
        string[] open;
        lock (_gate) open = [.. _servers.Keys];
        foreach (var server in open) Wake(server);
    }

    /// <summary>The settings changed (how many at once, say): starts what may now start.</summary>
    public void SettingsChanged() => Pump();

    /// <summary>Every record, in queue order, as copies.</summary>
    public IReadOnlyList<DownloadRecord> Snapshot()
    {
        lock (_gate) return [.. _records.Select(Snap)];
    }

    /// <summary>A copy of the record for an item, or null when it is not downloaded or queued.</summary>
    public DownloadRecord? Find(string serverId, string ratingKey)
    {
        var id = DownloadRecord.Key(serverId, ratingKey);
        lock (_gate) return Get(id) is { } found ? Snap(found) : null;
    }

    /// <summary>Whether a server is open, so its downloads can run and its watch changes be sent.</summary>
    public bool IsAttached(string serverId)
    {
        lock (_gate) return _servers.ContainsKey(serverId);
    }

    /// <summary>
    /// A server is open: its downloads run through <paramref name="client"/>, the watch changes made
    /// without it are sent, and its rules are brought up to date. Returns at once.
    /// </summary>
    public void Attach(string serverId, PlexServerClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (_disposed) return;
            _servers[serverId] = client;
        }

        Wake(serverId);
    }

    /// <summary>
    /// A server is open and the list is read: downloads that stopped for a reason that may have
    /// passed wait their turn again, the queue moves, and the watch changes and rules follow.
    /// </summary>
    private void Wake(string serverId)
    {
        var requeued = new List<DownloadRecord>();
        lock (_gate)
        {
            if (_disposed || !_servers.ContainsKey(serverId)) return;
            foreach (var record in _records.Where(r => r.ServerId == serverId && r.State == DownloadState.Failed && r.Retryable))
            {
                record.State = DownloadState.Queued;
                record.Error = null;
                requeued.Add(Snap(record));
            }
        }

        foreach (var record in requeued) Changed?.Invoke(record);
        Pump();
        _ = Task.Run(async () =>
        {
            await SyncAsync(serverId, CancellationToken.None).ConfigureAwait(false);
            await ApplyRulesAsync(serverId, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>The server is closed: its transfers stop where they are and wait for it to be open again.</summary>
    public void Detach(string serverId)
    {
        var stopping = new List<Transfer>();
        lock (_gate)
        {
            _servers.Remove(serverId);
            foreach (var transfer in _active.Values.Where(t => t.Record.ServerId == serverId))
            {
                transfer.Mark(StopReason.Detached);
                stopping.Add(transfer);
            }
        }

        foreach (var transfer in stopping) transfer.Cancel();
    }

    /// <summary>
    /// Puts a film or an episode on the queue, at the end. One already there carries on if it was
    /// paused or stopped, and one already kept stays as it is. Returns a copy of its record.
    /// </summary>
    /// <param name="ruleId">The rule asking for it, so it goes again once watched.</param>
    public DownloadRecord Enqueue(MetadataItem item, string serverId, string serverName, string? ruleId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var part = item.Media?.FirstOrDefault()?.Part?.FirstOrDefault();
        DownloadRecord copy;
        lock (_gate)
        {
            if (Get(DownloadRecord.Key(serverId, item.RatingKey)) is { } existing)
            {
                if (existing.State is DownloadState.Paused or DownloadState.Failed)
                {
                    existing.State = DownloadState.Queued;
                    existing.Error = null;
                }

                copy = Snap(existing);
            }
            else
            {
                var record = new DownloadRecord
                {
                    ServerId = serverId,
                    ServerName = serverName,
                    RatingKey = item.RatingKey,
                    Type = item.Type,
                    Title = item.Title,
                    ShowTitle = item.GrandparentTitle,
                    ShowKey = item.GrandparentRatingKey,
                    SeasonKey = item.ParentRatingKey,
                    Season = item.ParentIndex,
                    Episode = item.Index,
                    Year = item.Year,
                    Duration = item.Duration ?? 0,
                    PartKey = part?.Key ?? string.Empty,
                    TotalBytes = part?.Size ?? 0,
                    Folder = Path.Combine(Folder, SafeName(serverId), SafeName(item.RatingKey)),
                    FileName = MediaName(item, part),
                    AddedAt = Time.GetUtcNow().ToUnixTimeSeconds(),
                    ViewOffset = item.ViewOffset ?? 0,
                    Picture = item.Type == "episode" ? item.Thumb : item.Art ?? item.Thumb,
                    RuleId = ruleId,
                };
                _records.Add(record);
                copy = Snap(record);
                Log.Info($"Queued for download: {Describe(record)}.");
            }
        }

        Changed?.Invoke(copy);
        SaveSoon();
        Pump();
        return copy;
    }

    /// <summary>Holds a download; what has arrived is kept for later.</summary>
    public void Pause(string id)
    {
        Transfer? active = null;
        DownloadRecord? copy = null;
        lock (_gate)
        {
            if (Get(id) is not { State: DownloadState.Queued or DownloadState.Downloading } record) return;
            record.State = DownloadState.Paused;
            if (_active.TryGetValue(id, out active)) active.Mark(StopReason.Paused);
            copy = Snap(record);
        }

        active?.Cancel();
        Changed?.Invoke(copy);
        SaveSoon();
    }

    /// <summary>Carries on with a paused download, or tries a stopped one again.</summary>
    public void Resume(string id)
    {
        DownloadRecord? copy;
        lock (_gate)
        {
            if (Get(id) is not { State: DownloadState.Paused or DownloadState.Failed } record) return;
            record.State = DownloadState.Queued;
            record.Error = null;
            copy = Snap(record);
        }

        Changed?.Invoke(copy);
        SaveSoon();
        Pump();
    }

    public void PauseAll()
    {
        foreach (var record in Snapshot().Where(r => r.State is DownloadState.Queued or DownloadState.Downloading)) Pause(record.Id);
    }

    public void ResumeAll()
    {
        foreach (var record in Snapshot().Where(r => r.State == DownloadState.Paused)) Resume(record.Id);
    }

    /// <summary>Moves a waiting download to the head of the queue: it is the next to start.</summary>
    public void MoveToFront(string id)
    {
        lock (_gate)
        {
            if (Get(id) is not { } record) return;
            _records.Remove(record);
            _records.Insert(0, record);
        }

        SaveSoon();
        Pump();
    }

    /// <summary>Cancels a download or deletes a kept one: off the list, and its files off the disk.</summary>
    public void Remove(string id)
    {
        Transfer? active = null;
        DownloadRecord? record;
        long sequence;
        lock (_gate)
        {
            record = Get(id);
            if (record is null) return;
            _records.Remove(record);
            if (_active.TryGetValue(id, out active)) active.Mark(StopReason.Removed);
            sequence = ++_sequence;
        }

        Log.Info($"Removed from downloads: {Describe(record)}.");
        Removed?.Invoke(id, sequence);

        // A running transfer deletes its own files once it has let go of them.
        if (active is not null)
        {
            active.Cancel();
        }
        else
        {
            var gone = record;
            _ = Task.Run(() => DeleteFiles(gone));
        }

        SaveSoon();
        Pump();
    }

    /// <summary>
    /// What the kept files take, and what is free on the drive of the download folder (null when
    /// that cannot be told). Reads the disk: a worker's job.
    /// </summary>
    public (long Used, long? Free) Space()
    {
        long used;
        lock (_gate) used = _records.Sum(r => r.State == DownloadState.Done ? r.TotalBytes : r.DoneBytes);
        return (used, FreeSpace(Folder));
    }

    /// <summary>Stops the transfers where they are (they carry on at the next start) and writes the index.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Transfer> running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = [.. _active.Values];
            foreach (var transfer in running) transfer.Mark(StopReason.Shutdown);
        }

        foreach (var transfer in running) transfer.Cancel();
        await Task.WhenAll(running.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        SaveSoon();
        await Flush(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    /// <summary>Waits until every save so far is on disk, or <paramref name="limit"/> passes.</summary>
    public async Task<bool> Flush(TimeSpan limit)
    {
        Task writer;
        lock (_saveGate) writer = _writer;
        try
        {
            await writer.WaitAsync(limit).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Starts what may start: the first waiting downloads whose server is open, up to the number allowed at once.</summary>
    private void Pump()
    {
        var starting = new List<(Transfer Transfer, PlexServerClient Client)>();
        lock (_gate)
        {
            if (_disposed) return;
            var limit = Math.Clamp(_settings().Simultaneous, 1, 2);
            foreach (var record in _records)
            {
                if (_active.Count >= limit) break;
                if (record.State != DownloadState.Queued || _active.ContainsKey(record.Id)) continue;
                if (!_servers.TryGetValue(record.ServerId, out var client)) continue;
                var transfer = new Transfer(record);
                _active[record.Id] = transfer;
                record.State = DownloadState.Downloading;
                record.Error = null;
                starting.Add((transfer, client));
            }
        }

        foreach (var (transfer, client) in starting)
        {
            Raise(transfer.Record);
            transfer.Task = Task.Run(() => RunAsync(transfer, client));
        }
    }

    private DownloadRecord? Get(string id) => _records.FirstOrDefault(r => r.Id == id);

    /// <summary>A copy for the outside, numbered in the order copies are taken; under the lock.</summary>
    private DownloadRecord Snap(DownloadRecord record)
    {
        var copy = record.Copy();
        copy.Sequence = ++_sequence;
        return copy;
    }

    /// <summary>Raises <see cref="Changed"/> with a copy taken under the lock.</summary>
    private void Raise(DownloadRecord record)
    {
        DownloadRecord copy;
        lock (_gate) copy = Snap(record);
        Changed?.Invoke(copy);
    }

    /// <summary>
    /// Has a worker write the index; saves in quick succession collapse into one. The worker takes the
    /// snapshot, so the caller (often the UI thread) never waits for it to be made or written.
    /// </summary>
    private void SaveSoon()
    {
        lock (_saveGate)
        {
            _unsaved = true;
            if (_saving) return;
            _saving = true;
            _writer = Task.Run(WriteUnsaved);
        }
    }

    private void WriteUnsaved()
    {
        while (true)
        {
            lock (_saveGate)
            {
                if (!_unsaved)
                {
                    _saving = false;
                    return;
                }

                _unsaved = false;
            }

            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(
                    new DownloadIndex { Items = [.. _records], Rules = [.. _rules], Pending = [.. _pending] },
                    DownloadJsonContext.Default.DownloadIndex);
            }

            try
            {
                WriteAtomically(_indexPath, System.Text.Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"The download list could not be saved to {_indexPath}.", ex);
            }
        }
    }

    /// <summary>Written beside the target and renamed over it: a crash leaves the old file or the new, never half.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes what a download wrote, file by file by name, then its folder if that leaves it empty.</summary>
    private static void DeleteFiles(DownloadRecord record)
    {
        try
        {
            foreach (var file in new[] { record.MediaPath, record.PartialPath, record.MetadataPath, record.MetadataPath + ".new" }.Concat(ArtworkNames.Select(record.ArtworkPath)))
            {
                if (File.Exists(file)) File.Delete(file);
            }

            DeleteIfEmpty(record.Folder);
            DeleteIfEmpty(Path.GetDirectoryName(record.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The files of {record.Title} could not all be deleted from {record.Folder}.", ex);
        }
    }

    private static void DeleteIfEmpty(string? folder)
    {
        if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            // The nearest folder that exists stands for the drive.
            var probe = folder;
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { } parent) probe = parent;
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"Free space at {folder} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>A name that is safe as a file or folder on any drive the viewer might choose, removable ones included.</summary>
    internal static string SafeName(string name)
    {
        var safe = new string([.. name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c)]).Trim().TrimEnd('.');
        if (safe.Length > 120) safe = safe[..120].TrimEnd();
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>"The Glass Meridian (2023).mkv", "Lanternfall - S01E03 - The Crossing.mkv".</summary>
    internal static string MediaName(MetadataItem item, MediaPart? part)
    {
        var extension = part?.Container is { Length: > 0 } container ? container
            : Path.GetExtension(part?.Key ?? string.Empty).TrimStart('.') is { Length: > 0 } fromKey ? fromKey
            : "mkv";
        var name = item.Type == "episode"
            ? $"{item.GrandparentTitle} - S{item.ParentIndex ?? 0:00}E{item.Index ?? 0:00} - {item.Title}"
            : item.Year is { } year ? $"{item.Title} ({year})" : item.Title;
        return SafeName(name) + "." + SafeName(extension);
    }

    private static string Describe(DownloadRecord record) =>
        record.Type == "episode" ? $"{record.ShowTitle} S{record.Season ?? 0:00}E{record.Episode ?? 0:00}" : record.Title;

    private static readonly string[] ArtworkNames = ["poster.jpg", "backdrop.jpg", "still.jpg"];

    private enum StopReason
    {
        None,
        Paused,
        Removed,
        Detached,
        Shutdown,
    }

    /// <summary>One running download: its record, what stops it, and why.</summary>
    private sealed class Transfer(DownloadRecord record)
    {
        private readonly CancellationTokenSource _cancel = new();

        public DownloadRecord Record { get; } = record;

        public CancellationToken Token => _cancel.Token;

        public StopReason Reason { get; private set; }

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>When progress was last told, from <see cref="Environment.TickCount64"/>.</summary>
        public long LastTold { get; set; }

        /// <summary>When the index was last saved during this transfer.</summary>
        public long LastSaved { get; set; }

        /// <summary>Says why it will stop; under the manager's lock.</summary>
        public void Mark(StopReason reason)
        {
            if (Reason == StopReason.None) Reason = reason;
        }

        /// <summary>Stops it; outside the manager's lock, as what wakes up may take the lock itself.</summary>
        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It had already finished.
            }
        }

        public void Dispose() => _cancel.Dispose();
    }
}
