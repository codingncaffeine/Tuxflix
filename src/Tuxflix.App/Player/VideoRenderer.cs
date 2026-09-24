using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;
using Tuxflix.Player.Native;

namespace Tuxflix.App.Player;

/// <summary>Frame counts and timings of a video view, written by its threads and read by the probes.</summary>
public sealed class VideoStats
{
    private long _frames;
    private long _renderTicks;
    private long _longestRenderTicks;
    private long _handOverTicks;
    private long _longestHandOverTicks;
    private long _shown;
    private long _slowHandOvers;

    /// <summary>Hand-overs that took the UI thread longer than a millisecond.</summary>
    public long SlowHandOvers => Interlocked.Read(ref _slowHandOvers);

    /// <summary>Frames drawn so far.</summary>
    public long FramesDrawn => Interlocked.Read(ref _frames);

    /// <summary>Frames the compositor confirmed it put on the surface.</summary>
    public long FramesShown => Interlocked.Read(ref _shown);

    /// <summary>Time libmpv's render call took, on the video thread.</summary>
    public TimeSpan RenderTime => TimeSpan.FromTicks(Interlocked.Read(ref _renderTicks));

    public TimeSpan LongestRender => TimeSpan.FromTicks(Interlocked.Read(ref _longestRenderTicks));

    /// <summary>Time the UI thread spent handing finished frames to the compositor.</summary>
    public TimeSpan HandOverTime => TimeSpan.FromTicks(Interlocked.Read(ref _handOverTicks));

    public TimeSpan LongestHandOver => TimeSpan.FromTicks(Interlocked.Read(ref _longestHandOverTicks));

    internal void Drawn(TimeSpan took)
    {
        Interlocked.Increment(ref _frames);
        Add(ref _renderTicks, ref _longestRenderTicks, took);
    }

    internal void HandedOver(TimeSpan took)
    {
        Add(ref _handOverTicks, ref _longestHandOverTicks, took);
        if (took > TimeSpan.FromMilliseconds(1)) Interlocked.Increment(ref _slowHandOvers);
    }

    internal void OnScreen() => Interlocked.Increment(ref _shown);

    private static void Add(ref long total, ref long longest, TimeSpan took)
    {
        Interlocked.Add(ref total, took.Ticks);
        var seen = Interlocked.Read(ref longest);
        while (took.Ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref longest, took.Ticks, seen);
            if (previous == seen) break;
            seen = previous;
        }
    }
}

/// <summary>
/// Draws one player's video on a thread of its own and hands each finished frame to Avalonia's
/// compositor, so no part of the drawing runs on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The thread owns an OpenGL context in the compositor's share group and keeps it current for its
/// whole life (Avalonia holds a context's lock while it is current, so nothing else may ever make
/// this one current: textures are created and deleted here and only here). libmpv's render context
/// lives on this thread too. mpv signals a new frame from its own threads; the frame is drawn into
/// a free texture of a small pool, finished, and posted to the UI thread, whose only work is to
/// give the texture to the drawing surface, a queued job that takes microseconds.
/// </para>
/// <para>
/// A texture is free again once a frame handed over after it is on screen: the surface no longer
/// shows it. Before a texture is deleted (a new size, or the end), the compositor lets go of its
/// imported copy on the UI thread; only then does this thread delete it. libmpv's render call waits
/// for each frame's display time, which paces the frames, and waiting is harmless here.
/// </para>
/// </remarks>
internal sealed class VideoRenderer
{
    private const int MaxFrames = 6;
    private const ulong UpdateFrame = 1;

    private readonly SharedPlayer _player;
    private readonly IOpenGlTextureSharingRenderInterfaceContextFeature _sharing;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly VideoStats _stats;
    private readonly Action<VideoRenderer> _ready;
    private readonly Action<VideoRenderer> _failed;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly List<Frame> _frames = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _frameFreed = new(false);
    private readonly ManualResetEventSlim _released = new(false);
    private PixelSize _size;
    private bool _resized;
    private long _sequence;
    private long _shownSequence;
    private volatile bool _stopping;
    private Action<int, int, byte[]>? _capture;
    private GlInterface? _gl;
    private unsafe delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;

    public VideoRenderer(
        SharedPlayer player,
        IOpenGlTextureSharingRenderInterfaceContextFeature sharing,
        ICompositionGpuInterop interop,
        CompositionDrawingSurface surface,
        VideoStats stats,
        Action<VideoRenderer> ready,
        Action<VideoRenderer> failed)
    {
        _player = player;
        _sharing = sharing;
        _interop = interop;
        _surface = surface;
        _stats = stats;
        _ready = ready;
        _failed = failed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Tuxflix video" };
    }

    /// <summary>For the video probe: three pixels of every drawn frame (top, middle, bottom), on the video thread.</summary>
    public Action<uint, uint, uint>? Sampled { get; set; }

    public void Start() => _thread.Start();

    /// <summary>The size to draw at, in pixels. Any thread.</summary>
    public void Resize(PixelSize size)
    {
        lock (_lock)
        {
            if (size == _size) return;
            _size = size;
            _resized = true;
        }

        _wake.Set();
    }

    /// <summary>Hands the next drawn frame to <paramref name="done"/>, as RGBA rows bottom first, on the video thread.</summary>
    public void CaptureNextFrame(Action<int, int, byte[]> done)
    {
        Volatile.Write(ref _capture, done);
        _wake.Set();
    }

    /// <summary>
    /// Ends drawing. On the UI thread: the surface and the imported textures are let go first, then
    /// the video thread frees the render context, deletes the textures and lets go of the player.
    /// </summary>
    public async void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        try
        {
            _surface.Dispose();
            Frame[] frames;
            lock (_lock) frames = [.. _frames];
            foreach (var frame in frames) await LetGoAsync(frame);
        }
        catch (Exception ex)
        {
            Log.Warn("The video surface did not let go cleanly.", ex);
        }
        finally
        {
            _released.Set();
        }
    }

    private unsafe void Run()
    {
        IGlContext? context = null;
        IDisposable? current = null;
        var acquired = false;
        var render = IntPtr.Zero;
        var fbo = 0;
        var self = GCHandle.Alloc(this);
        try
        {
            context = _sharing.CreateSharedContext(null) ?? throw new InvalidOperationException("the compositor gave no shared OpenGL context");
            current = context.MakeCurrent();
            _gl = context.GlInterface;
            _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)_gl.GetProcAddress("glReadPixels");
            fbo = _gl.GenFramebuffer();
            if (!(acquired = _player.Acquire())) throw new InvalidOperationException("the player was already gone");
            render = CreateRenderContext(GCHandle.ToIntPtr(self));
            Log.Info($"Video surface: libmpv draws on its own thread, OpenGL {context.Version.Type} {context.Version.Major}.{context.Version.Minor}.");
            Dispatcher.UIThread.Post(() => _ready(this));

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

                var capture = Volatile.Read(ref _capture);
                if ((update & UpdateFrame) == 0 && !resized && capture is null) continue;
                if (size.Width < 1 || size.Height < 1) continue;
                if (TakeFrame(context, size) is not { } frame) continue;

                Draw(render, fbo, frame, capture);
                HandOver(frame);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Video drawing stopped.", ex);
            if (!_stopping) Dispatcher.UIThread.Post(() => _failed(this));
        }
        finally
        {
            // The compositor must let go of the textures before they are deleted.
            if (!_released.Wait(TimeSpan.FromSeconds(5))) Log.Warn("The video surface was not let go in time; freeing it anyway.");
            try
            {
                if (render != IntPtr.Zero)
                {
                    LibMpv.mpv_render_context_set_update_callback(render, null, IntPtr.Zero);
                    LibMpv.mpv_render_context_free(render);
                }

                lock (_lock)
                {
                    foreach (var frame in _frames) frame.Texture.Dispose();
                    _frames.Clear();
                }

                if (fbo != 0) _gl?.DeleteFramebuffer(fbo);
            }
            catch (Exception ex)
            {
                Log.Warn("The video surface did not clean up.", ex);
            }

            current?.Dispose();
            context?.Dispose();
            self.Free();

            // Only now may the player go: its render context is gone. Destroying it waits for mpv,
            // which is fine here and nowhere near the UI thread.
            if (acquired) _player.Release();
        }
    }

    private unsafe IntPtr CreateRenderContext(IntPtr self)
    {
        var apiType = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            var init = new MpvOpenGlInitParams { GetProcAddress = &GetProcAddress, GetProcAddressContext = self };
            var parameters = stackalloc MpvRenderParamEntry[3];
            parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.ApiType, Data = apiType };
            parameters[1] = new MpvRenderParamEntry { Type = MpvRenderParam.OpenGlInitParams, Data = (IntPtr)(&init) };
            parameters[2] = default;

            IntPtr render;
            var result = LibMpv.mpv_render_context_create(&render, _player.Player.Handle, parameters);
            if (result < 0) throw new InvalidOperationException("libmpv could not draw here: " + LibMpv.Describe(result));
            LibMpv.mpv_render_context_set_update_callback(render, &OnUpdate, self);
            return render;
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }
    }

    /// <summary>A texture of this size that the compositor is not showing, or null when none frees up in time.</summary>
    private Frame? TakeFrame(IGlContext context, PixelSize size)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            lock (_lock)
            {
                for (var i = _frames.Count - 1; i >= 0; i--)
                {
                    var frame = _frames[i];
                    if (frame.State == FrameState.Retired || (frame.State == FrameState.Free && frame.Size != size && !frame.WasImported))
                    {
                        frame.Texture.Dispose();
                        _frames.RemoveAt(i);
                    }
                    else if (frame.State == FrameState.Free && frame.Size != size)
                    {
                        // Imported textures are let go by the compositor first, on the UI thread.
                        frame.State = FrameState.Retiring;
                        Dispatcher.UIThread.Post(() => Retire(frame));
                    }
                }

                if (_frames.Find(f => f.State == FrameState.Free && f.Size == size) is { } free)
                {
                    free.State = FrameState.Drawing;
                    return free;
                }

                if (_frames.Count < MaxFrames)
                {
                    var created = new Frame(_sharing.CreateSharedTextureForComposition(context, size), size) { State = FrameState.Drawing };
                    _frames.Add(created);
                    return created;
                }
            }

            // Every texture is on its way to the screen: the UI thread is behind. Wait a little, then drop this frame.
            _frameFreed.WaitOne(TimeSpan.FromMilliseconds(250));
        }

        return null;
    }

    private unsafe void Draw(IntPtr render, int fbo, Frame frame, Action<int, int, byte[]>? capture)
    {
        var gl = _gl!;
        gl.BindFramebuffer(0x8D40 /* GL_FRAMEBUFFER */, fbo);
        gl.FramebufferTexture2D(0x8D40, 0x8CE0 /* GL_COLOR_ATTACHMENT0 */, 0x0DE1 /* GL_TEXTURE_2D */, frame.Texture.TextureId, 0);
        if (!frame.Checked)
        {
            var status = gl.CheckFramebufferStatus(0x8D40);
            if (status != 0x8CD5 /* GL_FRAMEBUFFER_COMPLETE */) throw new InvalidOperationException($"the video framebuffer is incomplete (0x{status:x})");
            frame.Checked = true;
        }

        var target = new MpvOpenGlFbo { Fbo = fbo, Width = frame.Size.Width, Height = frame.Size.Height, InternalFormat = frame.Texture.InternalFormat };
        var flip = 1;
        var parameters = stackalloc MpvRenderParamEntry[3];
        parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.OpenGlFbo, Data = (IntPtr)(&target) };
        parameters[1] = new MpvRenderParamEntry { Type = MpvRenderParam.FlipY, Data = (IntPtr)(&flip) };
        parameters[2] = default;
        var started = Stopwatch.GetTimestamp();
        LibMpv.mpv_render_context_render(render, parameters);
        _stats.Drawn(Stopwatch.GetElapsedTime(started));

        // mpv leaves framebuffer 0 bound; the readbacks below read ours.
        gl.BindFramebuffer(0x8D40, fbo);
        if (Sampled is { } sampled && _readPixels is not null)
        {
            // GL rows run bottom-up: the last row is the top of the picture as it is shown.
            sampled(Pixel(frame.Size.Width / 2, frame.Size.Height - 3), Pixel(frame.Size.Width / 2, frame.Size.Height / 2), Pixel(frame.Size.Width / 2, 2));
        }

        if (capture is not null && Interlocked.CompareExchange(ref _capture, null, capture) == capture && _readPixels is not null)
        {
            var pixels = new byte[frame.Size.Width * frame.Size.Height * 4];
            fixed (byte* data = pixels) _readPixels(0, 0, frame.Size.Width, frame.Size.Height, 0x1908 /* GL_RGBA */, 0x1401 /* GL_UNSIGNED_BYTE */, data);
            capture(frame.Size.Width, frame.Size.Height, pixels);
        }

        // The compositor samples this texture from its own context: every command must be done first.
        gl.Finish();
    }

    private unsafe uint Pixel(int x, int y)
    {
        uint rgba;
        _readPixels(x, y, 1, 1, 0x1908, 0x1401, &rgba);
        return rgba;
    }

    private void HandOver(Frame frame)
    {
        lock (_lock)
        {
            frame.State = FrameState.Queued;
            frame.Sequence = ++_sequence;
        }

        Dispatcher.UIThread.Post(() => Show(frame), DispatcherPriority.Render);
    }

    /// <summary>On the UI thread: gives the texture to the surface. Microseconds; no drawing.</summary>
    private void Show(Frame frame)
    {
        var started = Stopwatch.GetTimestamp();
        if (_stopping)
        {
            lock (_lock) frame.State = FrameState.Free;
            return;
        }

        try
        {
            if (frame.Imported is null)
            {
                frame.Imported = _interop.ImportImage(frame.Texture);
                lock (_lock) frame.WasImported = true;
            }

            _surface.UpdateAsync(frame.Imported).ContinueWith(
                shown => Shown(frame, shown), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.Warn("A video frame could not be shown.", ex);
            lock (_lock) frame.State = FrameState.Free;
        }

        _stats.HandedOver(Stopwatch.GetElapsedTime(started));
    }

    /// <summary>The surface shows <paramref name="frame"/> now: every frame shown before it is free.</summary>
    private void Shown(Frame frame, Task shown)
    {
        lock (_lock)
        {
            if (shown.IsFaulted || shown.IsCanceled || frame.Sequence < _shownSequence)
            {
                frame.State = FrameState.Free;
            }
            else
            {
                foreach (var other in _frames)
                {
                    if (!ReferenceEquals(other, frame) && other.State == FrameState.Shown) other.State = FrameState.Free;
                }

                frame.State = FrameState.Shown;
                _shownSequence = frame.Sequence;
                _stats.OnScreen();
            }
        }

        _frameFreed.Set();
    }

    /// <summary>On the UI thread: the compositor lets go of a texture of an old size, then the video thread deletes it.</summary>
    private async void Retire(Frame frame)
    {
        await LetGoAsync(frame);
        lock (_lock)
        {
            if (frame.State == FrameState.Retiring) frame.State = FrameState.Retired;
        }

        _frameFreed.Set();
    }

    /// <summary>On the UI thread: the compositor's imported copy of a texture goes (once, whoever asks first).</summary>
    private static async Task LetGoAsync(Frame frame)
    {
        var imported = frame.Imported;
        frame.Imported = null;
        if (imported is null) return;
        try
        {
            await imported.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("The compositor did not let go of a video texture.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetProcAddress(IntPtr context, IntPtr name)
    {
        var renderer = (VideoRenderer?)GCHandle.FromIntPtr(context).Target;
        var symbol = Marshal.PtrToStringUTF8(name);
        return renderer?._gl is { } gl && symbol is not null ? gl.GetProcAddress(symbol) : IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(IntPtr context)
    {
        // On one of mpv's threads: nothing here may call back into mpv.
        if (GCHandle.FromIntPtr(context).Target is VideoRenderer renderer) renderer._wake.Set();
    }

    private enum FrameState
    {
        Free,
        Drawing,
        Queued,
        Shown,
        Retiring,
        Retired,
    }

    /// <summary>A texture of the pool. Its state is guarded by the renderer's lock; its import belongs to the UI thread.</summary>
    private sealed class Frame(ICompositionImportableOpenGlSharedTexture texture, PixelSize size)
    {
        public ICompositionImportableOpenGlSharedTexture Texture { get; } = texture;

        public PixelSize Size { get; } = size;

        public FrameState State { get; set; }

        public long Sequence { get; set; }

        public bool Checked { get; set; }

        public bool WasImported { get; set; }

        public ICompositionImportedGpuImage? Imported { get; set; }
    }
}
