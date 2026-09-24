using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;
using Tuxflix.Player.Native;

namespace Tuxflix.App.Player;

/// <summary>
/// Draws a player's video into the window through libmpv's OpenGL render API, so the controls,
/// tooltips and menus of the interface can sit on top of the picture.
/// </summary>
/// <remarks>
/// libmpv renders into the framebuffer Avalonia hands this control each frame, flipped because
/// that framebuffer's rows run bottom-up. mpv announces a new frame from its own thread; the
/// announcement is posted to the UI thread, which asks for a render. The render context is made
/// and freed inside the OpenGL callbacks, where the context is current, and the view holds the
/// player until its render context is gone.
/// </remarks>
public sealed unsafe class MpvVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<SharedPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, SharedPlayer?>(nameof(Player));

    private GlInterface? _gl;
    private IntPtr _context;
    private SharedPlayer? _held;
    private GCHandle _self;
    private delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;
    private delegate* unmanaged<int, int, void> _bindFramebuffer;

    public SharedPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>
    /// Raised on the UI thread once libmpv has a render context here. A file loaded before this has
    /// no video output to open: mpv drops the video track and the file plays as nothing.
    /// </summary>
    public event Action? Ready;

    /// <summary>Whether the render context exists.</summary>
    public bool IsReady => _context != IntPtr.Zero;

    /// <summary>Frames drawn so far.</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>When set, every drawn frame reports three sampled pixels (for the probe): top, middle, bottom.</summary>
    public Action<uint, uint, uint>? Sampled { get; set; }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)gl.GetProcAddress("glReadPixels");
        _bindFramebuffer = (delegate* unmanaged<int, int, void>)gl.GetProcAddress("glBindFramebuffer");
        Log.Info($"Video surface: OpenGL context {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}.");
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        FreeContext();
        if (_self.IsAllocated) _self.Free();
    }

    protected override void OnOpenGlLost()
    {
        // The GL context is gone and the render context with it; start over on the next init.
        Log.Warn("The video surface lost its OpenGL context.");
        FreeContext();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty) RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!ReferenceEquals(_held, Player)) FreeContext();
        if (_context == IntPtr.Zero && Player is { } shared && !CreateContext(shared)) return;
        if (_context == IntPtr.Zero) return;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

        var diagnose = Sampled is not null && FramesDrawn < 3;
        if (diagnose) Diagnose(gl, fb, width, height, "before");

        var fbo = new MpvOpenGlFbo { Fbo = fb, Width = width, Height = height };
        var flip = 1;
        var parameters = stackalloc MpvRenderParamEntry[3];
        parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.OpenGlFbo, Data = (IntPtr)(&fbo) };
        parameters[1] = new MpvRenderParamEntry { Type = MpvRenderParam.FlipY, Data = (IntPtr)(&flip) };
        parameters[2] = default;
        var result = LibMpv.mpv_render_context_render(_context, parameters);
        FramesDrawn++;

        if (diagnose) Diagnose(gl, fb, width, height, $"after (render {result})");

        // mpv leaves framebuffer 0 bound when it is done; hand back the one Avalonia gave us, so
        // whatever it does next with this frame meets the state it set up.
        _bindFramebuffer(0x8D40 /* GL_FRAMEBUFFER */, fb);

        if (Sampled is { } sampled && _readPixels is not null)
        {
            // GL rows run bottom-up: the last row is the top of the picture as it is shown.
            sampled(Pixel(width / 2, height - 3), Pixel(width / 2, height / 2), Pixel(width / 2, 2));
        }
    }

    private uint Pixel(int x, int y)
    {
        uint rgba;
        _readPixels(x, y, 1, 1, 0x1908 /* GL_RGBA */, 0x1401 /* GL_UNSIGNED_BYTE */, &rgba);
        return rgba;
    }

    /// <summary>For the probe: the GL state the render call meets and leaves.</summary>
    private static void Diagnose(GlInterface gl, int fb, int width, int height, string when)
    {
        var getInteger = (delegate* unmanaged<int, int*, void>)gl.GetProcAddress("glGetIntegerv");
        var getError = (delegate* unmanaged<int>)gl.GetProcAddress("glGetError");
        var status = (delegate* unmanaged<int, int>)gl.GetProcAddress("glCheckFramebufferStatus");
        int bound;
        getInteger(0x8CA6 /* GL_FRAMEBUFFER_BINDING */, &bound);
        var viewport = stackalloc int[4];
        getInteger(0x0BA2 /* GL_VIEWPORT */, viewport);
        var complete = status(0x8D40 /* GL_FRAMEBUFFER */);
        var error = getError();
        Log.Info($"Video surface {when}: fb {fb}, bound {bound}, viewport {viewport[0]},{viewport[1]} {viewport[2]}x{viewport[3]}, "
                 + $"asked {width}x{height}, status 0x{complete:x}, error 0x{error:x}.");
    }

    private bool CreateContext(SharedPlayer shared)
    {
        if (_gl is null || !shared.Acquire()) return false;

        var apiType = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            var init = new MpvOpenGlInitParams { GetProcAddress = &GetProcAddress, GetProcAddressContext = GCHandle.ToIntPtr(_self) };
            var parameters = stackalloc MpvRenderParamEntry[3];
            parameters[0] = new MpvRenderParamEntry { Type = MpvRenderParam.ApiType, Data = apiType };
            parameters[1] = new MpvRenderParamEntry { Type = MpvRenderParam.OpenGlInitParams, Data = (IntPtr)(&init) };
            parameters[2] = default;

            IntPtr context;
            var result = LibMpv.mpv_render_context_create(&context, shared.Player.Handle, parameters);
            if (result < 0)
            {
                Log.Warn($"The video surface could not start: {LibMpv.Describe(result)}");
                shared.Release();
                return false;
            }

            _context = context;
            _held = shared;
            LibMpv.mpv_render_context_set_update_callback(_context, &OnUpdate, GCHandle.ToIntPtr(_self));
            Log.Info("Video surface: libmpv renders into the window.");
            Ready?.Invoke();
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }
    }

    private void FreeContext()
    {
        if (_context != IntPtr.Zero)
        {
            LibMpv.mpv_render_context_set_update_callback(_context, null, IntPtr.Zero);
            LibMpv.mpv_render_context_free(_context);
            _context = IntPtr.Zero;
        }

        // Only now may the player go: its render context is gone.
        _held?.Release();
        _held = null;
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetProcAddress(IntPtr context, IntPtr name)
    {
        var view = (MpvVideoView?)GCHandle.FromIntPtr(context).Target;
        var symbol = Marshal.PtrToStringUTF8(name);
        return view?._gl is { } gl && symbol is not null ? gl.GetProcAddress(symbol) : IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(IntPtr context)
    {
        // Called on mpv's own thread: nothing here may call back into mpv.
        if (GCHandle.FromIntPtr(context).Target is MpvVideoView view)
        {
            Dispatcher.UIThread.Post(view.RequestNextFrameRendering, DispatcherPriority.Render);
        }
    }
}
