using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tuxflix.App.Music;

namespace Tuxflix.App.Views;

/// <summary>
/// Shows the full-window visualizer: a <see cref="VisualizerRenderer"/> draws on its own thread,
/// and this view only shows the newest frame it hands over, scaled to fill.
/// </summary>
/// <remarks>
/// It draws only while it is in a window and visible, and holds the music's analysis only as long
/// (<see cref="VisualizerFeed.Listen"/>): hidden, it costs nothing.
/// </remarks>
public sealed class VisualizerView : Control
{
    public static readonly StyledProperty<MusicPlayer?> MusicProperty =
        AvaloniaProperty.Register<VisualizerView, MusicPlayer?>(nameof(Music));

    public static readonly StyledProperty<VisualizerMode> ModeProperty =
        AvaloniaProperty.Register<VisualizerView, VisualizerMode>(nameof(Mode));

    public static readonly StyledProperty<VisualizerPalette?> PaletteProperty =
        AvaloniaProperty.Register<VisualizerView, VisualizerPalette?>(nameof(Palette));

    private VisualizerRenderer? _renderer;
    private IDisposable? _lease;
    private WriteableBitmap? _front;
    private bool _attached;

    public MusicPlayer? Music
    {
        get => GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    public VisualizerMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public VisualizerPalette? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    /// <summary>The renderer drawing now, if any: probes and tests read its frame times.</summary>
    public VisualizerRenderer? Renderer => _renderer;

    public override void Render(DrawingContext context)
    {
        if (_front is not { } frame) return;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
        {
            context.DrawImage(frame, new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height), new Rect(Bounds.Size));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Update();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty && _renderer is { } renderer) renderer.Mode = Mode;
        else if (change.Property == PaletteProperty && _renderer is { } painting) painting.Palette = Palette ?? VisualizerPalettes.Fixed[0];
        else if (change.Property == BoundsProperty) _renderer?.Resize(PixelSizeNow());
        else if (change.Property == IsVisibleProperty || change.Property == MusicProperty)
        {
            if (change.Property == MusicProperty) StopDrawing();
            Update();
        }
    }

    private void Update()
    {
        var wanted = _attached && IsVisible && Music is not null;
        if (wanted && _renderer is null) StartDrawing(Music!);
        else if (!wanted) StopDrawing();
    }

    private void StartDrawing(MusicPlayer music)
    {
        _lease = music.Visuals.Listen();
        var renderer = new VisualizerRenderer(() => music.Visuals.Full, Present) { Mode = Mode, Palette = Palette ?? VisualizerPalettes.Fixed[0] };
        _renderer = renderer;
        renderer.Resize(PixelSizeNow());
        renderer.Start();
    }

    private void StopDrawing()
    {
        _front = null;
        _renderer?.Stop();
        _renderer = null;
        _lease?.Dispose();
        _lease = null;
        InvalidateVisual();
    }

    // On the drawing thread: the frame is shown on the next pass of the UI thread, and only then may the next be drawn.
    private void Present(VisualizerRenderer renderer, WriteableBitmap frame) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(renderer, _renderer)) return;
            _front = frame;
            InvalidateVisual();
            renderer.Taken();
        }, DispatcherPriority.Render);

    private PixelSize PixelSizeNow()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new PixelSize((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale));
    }
}
