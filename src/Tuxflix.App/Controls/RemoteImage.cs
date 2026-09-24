using Avalonia;
using Avalonia.Controls;
using Tuxflix.App.Imaging;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Controls;

/// <summary>
/// An image of a piece of Plex artwork: give it the item's image path and the size it is shown
/// at, and it fetches, decodes and fades the picture in.
/// </summary>
/// <remarks>
/// The request is sized in physical pixels (display size × the window's scale, rounded up to a
/// 16-pixel step so neighbouring sizes share a cache entry), which keeps HiDPI artwork sharp
/// without decoding a full-size poster for a 20-pixel rail thumbnail. Detaching from the tree
/// cancels the fetch and releases the bitmap, which is what lets a virtualised list recycle rows.
/// </remarks>
public class RemoteImage : Image
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Path));

    public static readonly StyledProperty<double> DecodeWidthProperty =
        AvaloniaProperty.Register<RemoteImage, double>(nameof(DecodeWidth), 300);

    public static readonly StyledProperty<double> DecodeHeightProperty =
        AvaloniaProperty.Register<RemoteImage, double>(nameof(DecodeHeight), 450);

    /// <summary>Png for artwork with transparency, such as a clear logo; Jpeg for everything else.</summary>
    public static readonly StyledProperty<ImageFormat> FormatProperty =
        AvaloniaProperty.Register<RemoteImage, ImageFormat>(nameof(Format), ImageFormat.Jpeg);

    private CancellationTokenSource? _loading;
    private ImageLoader.Lease? _lease;

    static RemoteImage()
    {
        PathProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.Reload());
    }

    public RemoteImage()
    {
        Opacity = 0;
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public ImageFormat Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public double DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public double DecodeHeight
    {
        get => GetValue(DecodeHeightProperty);
        set => SetValue(DecodeHeightProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Release();
    }

    private async void Reload()
    {
        Release();
        if (string.IsNullOrEmpty(Path) || ImageLoader.Current is not { } loader || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var scale = top.RenderScaling;
        var width = Step(DecodeWidth * scale);
        var height = Step(DecodeHeight * scale);
        var loading = _loading = new CancellationTokenSource();

        try
        {
            var lease = await loader.AcquireAsync(Path, width, height, Format, loading.Token);
            if (loading.IsCancellationRequested || !ReferenceEquals(loading, _loading))
            {
                lease?.Dispose();
                return;
            }

            _lease = lease;
            Source = lease?.Bitmap;
            Opacity = lease is null ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or replaced before it arrived.
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork {Path} could not be shown.", ex);
        }
    }

    private void Release()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        Source = null;
        Opacity = 0;
        _lease?.Dispose();
        _lease = null;
    }

    private static int Step(double pixels) => Math.Max(16, (int)Math.Ceiling(pixels / 16) * 16);
}
