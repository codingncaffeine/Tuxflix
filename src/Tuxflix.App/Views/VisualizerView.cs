using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Tuxflix.App.Imaging;
using Tuxflix.App.Music;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

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

    private const int CoverSize = 640;

    private VisualizerRenderer? _renderer;
    private IDisposable? _lease;
    private WriteableBitmap? _front;
    private bool _attached;
    private MusicPlayer? _drawing;
    private CancellationTokenSource? _coverLoad;
    private string? _coverPath;

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
        var renderer = new VisualizerRenderer(() => music.Visuals.Full, Present)
        {
            Mode = Mode,
            Palette = Palette ?? VisualizerPalettes.Fixed[0],
            Classic = () => music.Visuals.Classic,
        };
        _renderer = renderer;
        _drawing = music;
        music.PropertyChanged += OnMusicChanged;
        renderer.Resize(PixelSizeNow());
        renderer.Start();
        LoadCover(music.ArtPath);
    }

    private void StopDrawing()
    {
        if (_drawing is not null) _drawing.PropertyChanged -= OnMusicChanged;
        _drawing = null;
        _coverLoad?.Cancel();
        _coverLoad = null;
        _coverPath = null;
        _front = null;
        _renderer?.Stop();
        _renderer = null;
        _lease?.Dispose();
        _lease = null;
        InvalidateVisual();
    }

    private void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlayer.ArtPath) && sender is MusicPlayer music) LoadCover(music.ArtPath);
    }

    // The cover for the modes that show it, fetched and turned into a picture Skia can draw on a
    // worker; the renderer takes it on its own thread.
    private void LoadCover(string? path)
    {
        if (path == _coverPath || _renderer is not { } renderer) return;
        _coverPath = path;
        _coverLoad?.Cancel();
        if (path is null || ImageLoader.Current is not { } loader)
        {
            renderer.SetCover(null);
            return;
        }

        var cancel = _coverLoad = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var lease = await loader.AcquireAsync(path, CoverSize, CoverSize, ImageFormat.Jpeg, cancel.Token).ConfigureAwait(false);
                if (lease is null || cancel.IsCancellationRequested) return;
                var image = ToSkia(lease.Bitmap);
                if (cancel.IsCancellationRequested) image.Dispose();
                else renderer.SetCover(image);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn("The visualizer could not take the cover.", ex);
            }
        });
    }

    /// <summary>A copy of <paramref name="bitmap"/> as a Skia image, in the renderer's own pixel format.</summary>
    internal static SKImage ToSkia(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixels = new SKBitmap(info);
        using (var target = new SkiaFramebuffer(pixels))
        {
            bitmap.CopyPixels(target);
        }

        pixels.SetImmutable();
        return SKImage.FromBitmap(pixels);
    }

    // A Skia bitmap's memory, as the frame buffer a bitmap copies its pixels into (and converts them for).
    private sealed class SkiaFramebuffer(SKBitmap bitmap) : ILockedFramebuffer
    {
        public IntPtr Address => bitmap.GetPixels();

        public PixelSize Size => new(bitmap.Width, bitmap.Height);

        public int RowBytes => bitmap.RowBytes;

        public Vector Dpi => new(96, 96);

        public PixelFormat Format => PixelFormat.Bgra8888;

        public AlphaFormat AlphaFormat => AlphaFormat.Premul;

        public void Dispose()
        {
        }
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
