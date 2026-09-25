using System.Runtime.InteropServices;
using SkiaSharp;

namespace Tuxflix.App.Music;

/// <summary>
/// A picture an effect makes pixel by pixel: an array it writes, copied into one bitmap in one
/// call and shown through one image over the bitmap's own memory. Made again only when the size
/// changes, so a frame allocates nothing, native or managed.
/// </summary>
/// <remarks>
/// Pixels are Bgra8888 in memory order, as <c>B | G &lt;&lt; 8 | R &lt;&lt; 16 | A &lt;&lt; 24</c>, and opaque.
/// Setting a bitmap's pixels one call at a time would be one native call per pixel.
/// </remarks>
internal sealed class PixelField : IDisposable
{
    private SKBitmap? _bitmap;
    private SKImage? _image;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int[] Pixels { get; private set; } = [];

    /// <summary>Makes the picture this size; true when it was made anew, all black.</summary>
    public bool Ensure(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_bitmap is not null && width == Width && height == Height) return false;
        _image?.Dispose();
        _bitmap?.Dispose();
        Width = width;
        Height = height;
        Pixels = new int[width * height];
        Array.Fill(Pixels, unchecked((int)0xFF000000));
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        _bitmap = new SKBitmap(info);
        _image = SKImage.FromPixels(info, _bitmap.GetPixels(), info.RowBytes);
        return true;
    }

    /// <summary>Copies the array into the picture: after writing, before drawing.</summary>
    public void Upload() => Marshal.Copy(Pixels, 0, _bitmap!.GetPixels(), Pixels.Length);

    /// <summary>Draws the whole picture into <paramref name="dest"/>.</summary>
    public void Draw(SKCanvas canvas, SKRect dest, SKSamplingOptions sampling, SKPaint? paint = null) =>
        canvas.DrawImage(_image!, new SKRect(0, 0, Width, Height), dest, sampling, paint);

    /// <summary>Draws <paramref name="source"/> of the picture into <paramref name="dest"/>.</summary>
    public void Draw(SKCanvas canvas, SKRect source, SKRect dest, SKSamplingOptions sampling, SKPaint? paint = null) =>
        canvas.DrawImage(_image!, source, dest, sampling, paint);

    public void Dispose()
    {
        _image?.Dispose();
        _bitmap?.Dispose();
    }
}

/// <summary>
/// The spectrum's recent past, one row every <paramref name="every"/> seconds, the newest row
/// following the music live until the next is due, so the front of a landscape moves with the
/// music and the rest recedes evenly.
/// </summary>
internal sealed class SpectrumHistory(int rows, int columns, float every)
{
    private readonly float[][] _rows = [.. Enumerable.Range(0, rows).Select(_ => new float[columns])];
    private int _newest;
    private float _since;

    public int Rows => rows;

    public int Columns => columns;

    /// <summary>How far, 0 to 1, the newest row is towards being overtaken: what smooth scrolling offsets by.</summary>
    public float Fraction => Math.Clamp(_since / every, 0f, 1f);

    /// <summary>The row <paramref name="age"/> rows back, 0 the newest.</summary>
    public float[] Row(int age) => _rows[((_newest - age) % rows + rows) % rows];

    public void Add(float[] bands, float dt, Action<float[], float[]> regroup)
    {
        _since += dt;
        if (_since >= every)
        {
            _since %= every;
            _newest = (_newest + 1) % rows;
        }

        regroup(bands, _rows[_newest]);
    }
}
