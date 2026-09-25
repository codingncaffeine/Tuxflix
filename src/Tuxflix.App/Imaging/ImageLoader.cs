using Avalonia.Media.Imaging;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Imaging;

/// <summary>
/// Fetches artwork through the server's photo transcoder, decodes it off the UI thread at the
/// size it will be shown, and keeps decoded bitmaps in memory by reference count.
/// </summary>
/// <remarks>
/// A bitmap is native memory the garbage collector cannot see, so it is released explicitly:
/// every <see cref="Lease"/> holds one reference, and only bitmaps nobody holds are disposed when
/// the cache is over budget. Disposing one an image is still drawing would blank it; leaving the
/// rest to the finalizer would let native memory grow with no pressure to collect it.
/// <para>
/// Concurrent requests for the same image share one fetch, and at most six fetches run at once
/// so a fast scroll cannot open hundreds of connections.
/// </para>
/// </remarks>
public sealed class ImageLoader
{
    private const long Budget = 320L * 1024 * 1024;

    private static int _pending;

    private readonly PlexServerClient _client;
    private readonly DiskCache? _disk;
    private readonly SemaphoreSlim _fetches = new(6);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();
    private readonly Dictionary<string, Task<Entry?>> _inflight = new(StringComparer.Ordinal);
    private long _bytes;

    /// <param name="client">The server the artwork comes from.</param>
    /// <param name="diskCache">Where fetched artwork is kept between runs, or null to keep it in memory only.</param>
    public ImageLoader(PlexServerClient client, string? diskCache)
    {
        _client = client;
        _disk = diskCache is null ? null : new DiskCache(diskCache);
    }

    /// <summary>The loader for the server the window is showing; images ask it for artwork.</summary>
    public static ImageLoader? Current { get; set; }

    /// <summary>Fetches still in flight, for anything that has to wait until artwork has landed.</summary>
    public static int Pending => Volatile.Read(ref _pending);

    /// <summary>A bitmap to draw, held until disposed.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly ImageLoader _owner;
        private readonly Entry _entry;
        private int _released;

        internal Lease(ImageLoader owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public Bitmap Bitmap => _entry.Bitmap;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(_entry);
        }
    }

    internal sealed class Entry(string key, Bitmap bitmap, long bytes)
    {
        public string Key { get; } = key;

        public Bitmap Bitmap { get; } = bitmap;

        public long Bytes { get; } = bytes;

        public int Users { get; set; }

        public LinkedListNode<Entry>? Node { get; set; }
    }

    /// <summary>The image at <paramref name="path"/>, decoded to <paramref name="width"/> pixels wide.</summary>
    public async Task<Lease?> AcquireAsync(string path, int width, int height, ImageFormat format, CancellationToken cancellation)
    {
        var key = $"{path}|{width}x{height}|{format}";
        Task<Entry?> load;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                return Take(cached);
            }

            if (!_inflight.TryGetValue(key, out load!))
            {
                // Started on the pool, never here: this runs under the lock and usually on the UI thread.
                load = Task.Run(() => LoadAsync(key, path, width, height, format));
                _inflight[key] = load;
            }
        }

        var entry = await load.WaitAsync(cancellation).ConfigureAwait(false);
        if (entry is null) return null;

        lock (_gate)
        {
            return _entries.ContainsKey(key) ? Take(entry) : null;
        }
    }

    private Lease Take(Entry entry)
    {
        entry.Users++;
        if (entry.Node is { } node)
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
        }

        return new Lease(this, entry);
    }

    private async Task<Entry?> LoadAsync(string key, string path, int width, int height, ImageFormat format)
    {
        Interlocked.Increment(ref _pending);
        try
        {
            await _fetches.WaitAsync().ConfigureAwait(false);
            try
            {
                // Artwork kept beside a download is read from its file, with or without a server;
                // nothing else on disk is, whatever address a server's answer names.
                byte[]? bytes;
                if (path.StartsWith("file://", StringComparison.Ordinal))
                {
                    bytes = Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(path);
                    if (bytes is null)
                    {
                        Log.Debug($"Artwork {path} is not artwork kept beside a download; not read.");
                        return null;
                    }
                }
                else if ((bytes = _disk?.Read(key)) is null)
                {
                    bytes = await _client.GetBytesAsync(_client.ImageUri(path, width, height, format), CancellationToken.None).ConfigureAwait(false);
                    _disk?.Write(key, bytes);
                }

                Bitmap bitmap;
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
                }

                var entry = new Entry(key, bitmap, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
                lock (_gate)
                {
                    entry.Node = _recency.AddFirst(entry);
                    _entries[key] = entry;
                    _bytes += entry.Bytes;
                    TrimLocked();
                }

                return entry;
            }
            finally
            {
                _fetches.Release();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            Log.Debug($"Artwork {path} did not load: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_gate) _inflight.Remove(key);
            Interlocked.Decrement(ref _pending);
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            TrimLocked();
        }
    }

    private void TrimLocked()
    {
        var node = _recency.Last;
        while (_bytes > Budget && node is not null)
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.Users <= 0)
            {
                _recency.Remove(node);
                _entries.Remove(entry.Key);
                _bytes -= entry.Bytes;
                entry.Bitmap.Dispose();
            }

            node = previous;
        }
    }
}
