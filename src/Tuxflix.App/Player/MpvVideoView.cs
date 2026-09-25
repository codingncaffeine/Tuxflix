using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    private SoftwareVideoRenderer? _software;
    private WriteableBitmap? _front;
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
    /// <summary><c>TUXFLIX_VIDEO=software</c>: draw in software even where OpenGL is shared, to test the fallback.</summary>
    internal static bool ForceSoftware => Environment.GetEnvironmentVariable("TUXFLIX_VIDEO") == "software";

    /// <summary>Whether the picture is drawn in software, without OpenGL.</summary>
    public bool IsSoftware => _software is not null;

    /// <summary>Hands the next frame over as RGBA, bottom row first (as OpenGL reads it), whichever renderer drew it.</summary>
    public void CaptureNextFrame(Action<int, int, byte[]> done)
    {
        if (_renderer is not null)
        {
            _renderer.CaptureNextFrame(done);
            return;
        }

        if (_front is not { } front) return;
        _ = Task.Run(() =>
        {
            using var source = front.Lock();
            var (w, h) = (source.Size.Width, source.Size.Height);
            var rgba = new byte[w * h * 4];
            var row = new byte[source.RowBytes];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(source.Address + (y * source.RowBytes), row, 0, source.RowBytes);
                var at = (h - 1 - y) * w * 4;
                for (var x = 0; x < w; x++)
                {
                    rgba[at + (x * 4)] = row[(x * 4) + 2];
                    rgba[at + (x * 4) + 1] = row[(x * 4) + 1];
                    rgba[at + (x * 4) + 2] = row[x * 4];
                    rgba[at + (x * 4) + 3] = 255;
                }
            }

            done(w, h, rgba);
        });
    }

    /// <summary>The software renderer's newest frame; the OpenGL renderer draws through its own visual instead.</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_front is { } front) context.DrawImage(front, new Rect(Bounds.Size));
    }

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
            _software?.Resize(PixelSizeNow());
        }
    }

    private async void Start()
    {
        if (_renderer is not null || _software is not null || _compositor is not { } compositor || Player is not { IsDisposed: false } player) return;
        var start = ++_starts;
        try
        {
            var interop = await compositor.TryGetCompositionGpuInterop();
            var sharing = await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature))
                as IOpenGlTextureSharingRenderInterfaceContextFeature;

            // Superseded while waiting: another player, or the view left the window.
            if (start != _starts || _renderer is not null || !ReferenceEquals(player, Player) || !ReferenceEquals(compositor, _compositor)) return;
            if (ForceSoftware || interop is null || sharing is not { CanCreateSharedContext: true })
            {
                if (!ForceSoftware) Log.Warn("The window's renderer cannot share an OpenGL context with a video thread; drawing video in software.");
                StartSoftware(player);
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

    private void StartSoftware(SharedPlayer player)
    {
        _software = new SoftwareVideoRenderer(player, _stats, OnSoftwareReady, OnSoftwareFailed, bitmap =>
        {
            _front = bitmap;
            InvalidateVisual();
        });
        _software.Resize(PixelSizeNow());
        _software.Start();
    }

    private void OnSoftwareReady(SoftwareVideoRenderer renderer)
    {
        if (!ReferenceEquals(renderer, _software)) return;
        IsReady = true;
        Ready?.Invoke();
    }

    private void OnSoftwareFailed(SoftwareVideoRenderer renderer)
    {
        if (!ReferenceEquals(renderer, _software)) return;
        Stop();
    }

    private void Stop()
    {
        _starts++;
        IsReady = false;
        if (_software is { } software)
        {
            _software = null;
            software.Stop();
            _front = null;
            InvalidateVisual();
        }

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
        // OpenGL that keeps failing gives way to drawing in software.
        if (Player is not { IsDisposed: false } player) return;
        if (++_restarts <= MaxRestarts)
        {
            Log.Info("Restarting the video surface.");
            Start();
        }
        else
        {
            Log.Warn("OpenGL video kept failing; drawing video in software.");
            StartSoftware(player);
        }
    }

    private PixelSize PixelSizeNow()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
    }
}
