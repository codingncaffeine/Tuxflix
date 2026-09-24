using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player;

namespace Tuxflix.App.Player;

/// <summary>
/// Shows a player's video inside the window, so the controls, tooltips and menus of the interface
/// can sit on top of the picture.
/// </summary>
/// <remarks>
/// libmpv draws every frame on a thread of its own (<see cref="VideoRenderer"/>) into a texture
/// the compositor shows through this control's visual; the UI thread only hands finished frames
/// over. Drawing on the UI thread (Avalonia's OpenGlControlBase does) made every key wait behind
/// libmpv, whose render call holds until each frame's display time: pause and volume answered
/// seconds late. The view follows its player: a new player, or leaving the window, ends the
/// renderer, which lets go of the player once its render context is gone.
/// </remarks>
public sealed class MpvVideoView : Control
{
    public static readonly StyledProperty<SharedPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, SharedPlayer?>(nameof(Player));

    private const int MaxRestarts = 3;

    private readonly VideoStats _stats = new();
    private Compositor? _compositor;
    private CompositionSurfaceVisual? _visual;
    private VideoRenderer? _renderer;
    private int _starts;
    private int _restarts;
    private Action<uint, uint, uint>? _sampled;

    public SharedPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>
    /// Raised on the UI thread once libmpv can draw here. A file loaded before this has no video
    /// output to open: mpv drops the video track and the file plays as nothing.
    /// </summary>
    public event Action? Ready;

    /// <summary>Whether libmpv can draw here now.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Frames drawn so far.</summary>
    public long FramesDrawn => _stats.FramesDrawn;

    /// <summary>Frame counts and timings, for the probes.</summary>
    public VideoStats Stats => _stats;

    /// <summary>For the video probe: three pixels of every drawn frame (top, middle, bottom), reported on the video thread.</summary>
    public Action<uint, uint, uint>? Sampled
    {
        get => _sampled;
        set
        {
            _sampled = value;
            if (_renderer is not null) _renderer.Sampled = value;
        }
    }

    /// <summary>
    /// For the probe: hands the next drawn frame to <paramref name="done"/> as width, height and
    /// RGBA bytes, in OpenGL's row order (the bottom row first), on the video thread.
    /// </summary>
    public void CaptureNextFrame(Action<int, int, byte[]> done) => _renderer?.CaptureNextFrame(done);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        _compositor = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            Stop();
            _restarts = 0;
            Start();
        }
        else if (change.Property == BoundsProperty)
        {
            if (_visual is not null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _renderer?.Resize(PixelSizeNow());
        }
    }

    private async void Start()
    {
        if (_renderer is not null || _compositor is not { } compositor || Player is not { IsDisposed: false } player) return;
        var start = ++_starts;
        try
        {
            var interop = await compositor.TryGetCompositionGpuInterop();
            var sharing = await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature))
                as IOpenGlTextureSharingRenderInterfaceContextFeature;

            // Superseded while waiting: another player, or the view left the window.
            if (start != _starts || _renderer is not null || !ReferenceEquals(player, Player) || !ReferenceEquals(compositor, _compositor)) return;
            if (interop is null || sharing is not { CanCreateSharedContext: true })
            {
                Log.Warn("Video cannot be shown: the window's renderer cannot share an OpenGL context with a video thread.");
                return;
            }

            var surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _visual.Surface = surface;
            ElementComposition.SetElementChildVisual(this, _visual);

            _renderer = new VideoRenderer(player, sharing, interop, surface, _stats, OnReady, OnFailed) { Sampled = _sampled };
            _renderer.Resize(PixelSizeNow());
            _renderer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("The video surface could not start.", ex);
        }
    }

    private void Stop()
    {
        _starts++;
        IsReady = false;
        if (_visual is not null)
        {
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }

        if (_renderer is { } renderer)
        {
            _renderer = null;
            renderer.Stop();
        }
    }

    private void OnReady(VideoRenderer renderer)
    {
        if (!ReferenceEquals(renderer, _renderer)) return;
        IsReady = true;
        Ready?.Invoke();
    }

    private void OnFailed(VideoRenderer renderer)
    {
        if (!ReferenceEquals(renderer, _renderer)) return;
        Stop();

        // A lost context (a GPU reset) comes back with a new one; a player that is gone does not.
        if (++_restarts <= MaxRestarts && Player is { IsDisposed: false })
        {
            Log.Info("Restarting the video surface.");
            Start();
        }
    }

    private PixelSize PixelSizeNow()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
    }
}
