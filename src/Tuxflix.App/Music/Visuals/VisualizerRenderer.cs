using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

/// <summary>Draw times of the frames a renderer made, the latest few hundred: what a band is checked against.</summary>
public sealed class FrameTimes
{
    private readonly double[] _ring = new double[600];
    private readonly Lock _lock = new();
    private long _count;

    /// <summary>Frames drawn since the start.</summary>
    public long Count => Interlocked.Read(ref _count);

    public void Add(TimeSpan drawn)
    {
        lock (_lock)
        {
            _ring[_count % _ring.Length] = drawn.TotalMilliseconds;
            _count++;
        }
    }

    /// <summary>The draw time, ms, that <paramref name="fraction"/> of the recent frames came in under.</summary>
    public double Percentile(double fraction)
    {
        lock (_lock)
        {
            var n = (int)Math.Min(_count, _ring.Length);
            if (n == 0) return 0;
            var sorted = _ring.Take(n).Order().ToArray();
            return sorted[Math.Clamp((int)Math.Ceiling(fraction * n) - 1, 0, n - 1)];
        }
    }

    public double Max => Percentile(1);
}

/// <summary>
/// The full-window visualizer's drawing, on a thread of its own: sixty times a second it takes the
/// newest analysis frame, draws it with Skia straight into one of three bitmaps, and hands that
/// bitmap over. The UI thread only swaps which bitmap the view shows.
/// </summary>
/// <remarks>
/// <para>
/// One frame is handed over at a time: the next is drawn only once the view has taken the last,
/// so a busy UI thread slows the visualizer instead of queueing frames behind it. With three
/// bitmaps the one drawn into is never the one shown nor the one shown just before it.
/// </para>
/// <para>
/// Drawing is in software, so its cost grows with the area: a frame is drawn at most
/// <see cref="MaxLongSide"/> pixels on its long side and the view scales it up, which a full-screen
/// visualizer of bars, rings and sparks does not show.
/// </para>
/// </remarks>
public sealed class VisualizerRenderer
{
    public const int MaxLongSide = 1920;
    private const int Buffers = 3;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 60);

    private readonly Func<AudioFrame> _source;
    private readonly Action<VisualizerRenderer, WriteableBitmap> _present;
    private readonly AutoResetEvent _wake = new(false);
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[Buffers];
    private readonly Lock _lock = new();
    private PixelSize _size;
    private int _next;
    private volatile bool _taken = true;
    private volatile bool _stopping;
    private volatile VisualizerPalette _palette = VisualizerPalettes.Fixed[0];
    private volatile int _mode;

    /// <param name="source">The newest analysis frame; called on the drawing thread.</param>
    /// <param name="present">Called on the drawing thread with each finished frame; the view posts it to the UI thread and calls <see cref="Taken"/> once it shows it.</param>
    public VisualizerRenderer(Func<AudioFrame> source, Action<VisualizerRenderer, WriteableBitmap> present)
    {
        _source = source;
        _present = present;
    }

    public FrameTimes Times { get; } = new();

    /// <summary>The thread the frames are drawn on, once started: never the UI thread.</summary>
    public int? DrawingThreadId { get; private set; }

    public VisualizerMode Mode
    {
        get => (VisualizerMode)_mode;
        set => _mode = (int)value;
    }

    public VisualizerPalette Palette
    {
        get => _palette;
        set => _palette = value ?? VisualizerPalettes.Fixed[0];
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Tuxflix visualizer", Priority = ThreadPriority.AboveNormal }.Start();

    /// <summary>The size to draw at, in device pixels; any thread.</summary>
    public void Resize(PixelSize size)
    {
        lock (_lock) _size = size;
        _wake.Set();
    }

    /// <summary>The view took the last frame: the next may be drawn.</summary>
    public void Taken()
    {
        _taken = true;
        _wake.Set();
    }

    public void Stop()
    {
        _stopping = true;
        _wake.Set();
    }

    /// <summary>The size a frame is drawn at for a view of <paramref name="size"/>: the same, or scaled down to <see cref="MaxLongSide"/>.</summary>
    public static PixelSize DrawSize(PixelSize size)
    {
        var longest = Math.Max(size.Width, size.Height);
        if (longest <= MaxLongSide) return size;
        var scale = (double)MaxLongSide / longest;
        return new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
    }

    private void Run()
    {
        DrawingThreadId = Environment.CurrentManagedThreadId;
        using var painter = new VisualizerPainter();
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        try
        {
            while (!_stopping)
            {
                // The view has taken the last frame, and the next is due: a wake before then (the view
                // taking a frame, a new size) only waits again.
                while (!_taken && !_stopping) _wake.WaitOne(100);
                var due = last + Tick;
                for (var left = due - clock.Elapsed; left > TimeSpan.Zero && !_stopping; left = due - clock.Elapsed) _wake.WaitOne(left);
                if (_stopping) break;

                PixelSize size;
                lock (_lock) size = DrawSize(_size);
                if (size.Width < 4 || size.Height < 4)
                {
                    _wake.WaitOne(100);
                    continue;
                }

                var now = clock.Elapsed;
                var dt = (float)Math.Clamp((now - last).TotalSeconds, 0.001, 0.1);
                last = now;
                var bitmap = Buffer(size);
                var watch = Stopwatch.StartNew();
                using (var target = bitmap.Lock())
                {
                    var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info, target.Address, target.RowBytes)
                                        ?? throw new InvalidOperationException("Skia could not draw into the visualizer's bitmap.");
                    painter.Paint(surface.Canvas, size.Width, size.Height, _source(), Mode, Palette, dt);
                    surface.Canvas.Flush();
                }

                Times.Add(watch.Elapsed);
                _taken = false;
                _present(this, bitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The visualizer stopped drawing.", ex);
        }
        finally
        {
            // The view lets go of its frame on the UI thread first; the bitmaps go after it.
            var bitmaps = _bitmaps.ToArray();
            if (Application.Current is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var bitmap in bitmaps) bitmap?.Dispose();
                }, DispatcherPriority.Background);
            }

            _wake.Dispose();
        }
    }

    private WriteableBitmap Buffer(PixelSize size)
    {
        var index = _next;
        _next = (_next + 1) % Buffers;
        if (_bitmaps[index] is { } existing && existing.PixelSize == size) return existing;
        var old = _bitmaps[index];
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _bitmaps[index] = bitmap;
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        return bitmap;
    }
}
