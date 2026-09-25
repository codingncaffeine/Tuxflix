using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;
using Tuxflix.Player.Native;

namespace Tuxflix.App.Player;

/// <summary>
/// Video without OpenGL: libmpv's software renderer draws each frame on a thread of our own,
/// straight into one of three bitmaps, and the view draws the newest. For a machine whose window
/// renderer cannot share an OpenGL context (a virtual machine without 3D, a broken driver): more
/// work for the processor, the same picture, and the UI thread still only draws what is done.
/// </summary>
/// <remarks>
/// Three bitmaps, used in turn, so a frame is never drawn into while the compositor may still be
/// showing it: the one written is two frames behind the one on screen.
/// </remarks>
internal sealed class SoftwareVideoRenderer
{
    private const int Buffers = 3;
    private const ulong UpdateFrame = 1;

    private readonly SharedPlayer _player;
    private readonly VideoStats _stats;
    private readonly Action<SoftwareVideoRenderer> _ready;
    private readonly Action<SoftwareVideoRenderer> _failed;
    private readonly Action<WriteableBitmap> _show;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _lock = new();
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[Buffers];
    private PixelSize _size;
    private bool _resized;
    private int _next;
    private volatile bool _stopping;

    public SoftwareVideoRenderer(SharedPlayer player, VideoStats stats, Action<SoftwareVideoRenderer> ready, Action<SoftwareVideoRenderer> failed, Action<WriteableBitmap> show)
    {
        _player = player;
        _stats = stats;
        _ready = ready;
        _failed = failed;
        _show = show;
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Video (software)" }.Start();

    public void Resize(PixelSize size)
    {
        lock (_lock)
        {
            _resized |= size != _size;
            _size = size;
        }

        _wake.Set();
    }

    public void Stop()
    {
        _stopping = true;
        _wake.Set();
    }

    private unsafe void Run()
    {
        var self = GCHandle.Alloc(this);
        var render = IntPtr.Zero;
        var acquired = false;
        var format = Marshal.StringToCoTaskMemUTF8("bgr0");
        try
        {
            if (!(acquired = _player.Acquire())) throw new InvalidOperationException("the player was already gone");
            render = Create(GCHandle.ToIntPtr(self));
            Log.Info("Video surface: no OpenGL shared with the window here, so libmpv draws in software on its own thread.");
            Dispatcher.UIThread.Post(() => _ready(this));

            // Filled in for each frame; allocated once, not per frame.
            var pixels = stackalloc int[2];
            var stride = stackalloc nuint[1];
            var parameters = stackalloc MpvRenderParamEntry[5];

            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                var update = LibMpv.mpv_render_context_update(render);
                PixelSize size;
                bool resized;
                lock (_lock)
                {
                    size = _size;
                    resized = _resized;
                    _resized = false;
                }

                if ((update & UpdateFrame) == 0 && !resized) continue;
                if (size.Width < 2 || size.Height < 2) continue;

                var bitmap = Buffer(size);
                var watch = Stopwatch.StartNew();
                using (var target = bitmap.Lock())
                {
                    pixels[0] = size.Width;
                    pixels[1] = size.Height;
                    stride[0] = (nuint)target.RowBytes;
                    parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.SoftwareSize, Data = (IntPtr)pixels };
                    parameters[1] = new MpvRenderParamEntry { Type = MpvRenderParam.SoftwareFormat, Data = format };
                    parameters[2] = new MpvRenderParamEntry { Type = MpvRenderParam.SoftwareStride, Data = (IntPtr)stride };
                    parameters[3] = new MpvRenderParamEntry { Type = MpvRenderParam.SoftwarePointer, Data = target.Address };
                    parameters[4] = default;
                    var result = LibMpv.mpv_render_context_render(render, parameters);
                    if (result < 0) throw new InvalidOperationException("libmpv could not draw a frame in software: " + LibMpv.Describe(result));
                }

                _stats.Drawn(watch.Elapsed);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_stopping) return;
                    var handing = Stopwatch.StartNew();
                    _show(bitmap);
                    _stats.HandedOver(handing.Elapsed);
                    _stats.OnScreen();
                }, DispatcherPriority.Render);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Software video drawing stopped.", ex);
            if (!_stopping) Dispatcher.UIThread.Post(() => _failed(this));
        }
        finally
        {
            if (render != IntPtr.Zero)
            {
                LibMpv.mpv_render_context_set_update_callback(render, null, IntPtr.Zero);
                LibMpv.mpv_render_context_free(render);
            }

            Marshal.FreeCoTaskMem(format);
            self.Free();

            // The view lets go of the last frame on the UI thread; the bitmaps go after it.
            var bitmaps = _bitmaps.ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var bitmap in bitmaps) bitmap?.Dispose();
            }, DispatcherPriority.Background);

            // Only now may the player go: its render context is gone.
            if (acquired) _player.Release();
        }
    }

    /// <summary>The next of the three bitmaps, made again at a new size.</summary>
    private WriteableBitmap Buffer(PixelSize size)
    {
        var index = _next;
        _next = (_next + 1) % Buffers;
        if (_bitmaps[index] is { } existing && existing.PixelSize == size) return existing;
        var old = _bitmaps[index];
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _bitmaps[index] = bitmap;
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        return bitmap;
    }

    private unsafe IntPtr Create(IntPtr self)
    {
        var apiType = Marshal.StringToCoTaskMemUTF8("sw");
        try
        {
            var parameters = stackalloc MpvRenderParamEntry[2];
            parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.ApiType, Data = apiType };
            parameters[1] = default;
            IntPtr render;
            var result = LibMpv.mpv_render_context_create(&render, _player.Player.Handle, parameters);
            if (result < 0) throw new InvalidOperationException("libmpv could not draw in software: " + LibMpv.Describe(result));
            LibMpv.mpv_render_context_set_update_callback(render, &OnUpdate, self);
            return render;
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(IntPtr context)
    {
        if (GCHandle.FromIntPtr(context).Target is SoftwareVideoRenderer renderer) renderer._wake.Set();
    }
}
