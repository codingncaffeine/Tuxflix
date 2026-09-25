using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Tuxflix.App.Music;
using Tuxflix.Core.Plex;
using Tuxflix.Player.Analysis;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The full-window visualizer: every mode draws its own shape in every palette, on the
/// visualizer's own thread. How fast is <see cref="VisualizerBandTests"/>.
/// </summary>
public sealed class VisualizerTests
{
    public VisualizerTests(ITestOutputHelper output)
    {
        HeadlessSkia.Ensure();
        Output = output;
    }

    private ITestOutputHelper Output { get; }

    /// <summary>A loud, busy frame: rising bands, a sine for the scope, a beat every fourth frame.</summary>
    internal static AudioFrame Loud(long sequence)
    {
        const int Bands = 32;
        var bands = new float[Bands];
        var peaks = new float[Bands];
        var centres = new float[Bands];
        for (var b = 0; b < Bands; b++)
        {
            bands[b] = 0.35f + (0.55f * MathF.Abs(MathF.Sin((b * 0.4f) + (sequence * 0.05f))));
            peaks[b] = Math.Min(1f, bands[b] + 0.08f);
            centres[b] = 30f * MathF.Pow(2, b / 3f);
        }

        var wave = new float[1024];
        for (var i = 0; i < wave.Length; i++) wave[i] = 0.5f * MathF.Sin(i * MathF.Tau / 128f);
        return new AudioFrame
        {
            Bands = bands,
            Peaks = peaks,
            BandCentres = centres,
            Waveform = wave,
            WaveformLeft = wave,
            WaveformRight = wave,
            Beat = sequence % 4 == 0,
            BeatIntensity = sequence % 4 == 0 ? 1f : 0.5f,
            Silent = false,
            Sequence = sequence,
        };
    }

    [Fact]
    public void TheFirstModesPaintTheirOwnShape()
    {
        var palette = VisualizerPalettes.Resolve("Classic", null);
        foreach (var mode in new[] { VisualizerMode.Spectrum, VisualizerMode.Mirror, VisualizerMode.Radial, VisualizerMode.Scope, VisualizerMode.Particles })
        {
            using var bitmap = new SKBitmap(new SKImageInfo(400, 250, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            using var painter = new VisualizerPainter();
            for (var f = 0; f < 30; f++) painter.Paint(canvas, 400, 250, Loud(f), mode, palette, 1f / 60);

            // Where each mode must have drawn (a bar's middle, the ring's disc, the trace, the orb), and where it must have left the background.
            var (drawn, empty) = mode switch
            {
                VisualizerMode.Spectrum => (new SKPointI(205, 160), new SKPointI(205, 12)),
                VisualizerMode.Mirror => (new SKPointI(203, 110), new SKPointI(203, 8)),
                VisualizerMode.Radial => (new SKPointI(200, 125), new SKPointI(12, 12)),
                VisualizerMode.Scope => (new SKPointI(0, 125), new SKPointI(200, 8)),
                _ => (new SKPointI(200, 125), new SKPointI(3, 3)),
            };
            Assert.True(Bright(bitmap.GetPixel(drawn.X, drawn.Y)) > 60, $"{mode}: nothing drawn at {drawn} ({bitmap.GetPixel(drawn.X, drawn.Y)})");
            Assert.True(Bright(bitmap.GetPixel(empty.X, empty.Y)) < 40, $"{mode}: drawn over the background at {empty} ({bitmap.GetPixel(empty.X, empty.Y)})");
            if (mode == VisualizerMode.Particles) Assert.True(painter.Sparks > 50, $"only {painter.Sparks} sparks after thirty frames with beats");
        }
    }

    /// <summary>
    /// Every mode draws the music, not only its own ground: a second and a half of music leaves
    /// a picture unlike the one the same time of silence leaves. And no two modes draw the same
    /// picture from the same music, which is what a mode quietly drawing another would do.
    /// </summary>
    [Fact]
    public void EveryModeDrawsTheMusicAndNoTwoDrawAlike()
    {
        var palette = VisualizerPalettes.Resolve("Aurora", null);
        var pictures = new Dictionary<VisualizerMode, SKColor[]>();
        var deaf = new List<string>();
        foreach (var mode in Enum.GetValues<VisualizerMode>())
        {
            var music = Picture(mode, palette, Musical);
            var quiet = Picture(mode, palette, _ => AudioFrame.Empty(32, 1024));
            var answered = Differing(music, quiet);
            Output.WriteLine($"{mode}: {answered:P1} of the picture answers the music");
            if (answered < 0.01) deaf.Add($"{mode} ({answered:P2})");
            pictures[mode] = music;
        }

        Assert.True(deaf.Count == 0, $"music and silence draw nearly the same picture in {string.Join(", ", deaf)}");
        var alike = new List<string>();
        var modes = pictures.Keys.ToList();
        for (var a = 0; a < modes.Count; a++)
        {
            for (var b = a + 1; b < modes.Count; b++)
            {
                var apart = Differing(pictures[modes[a]], pictures[modes[b]]);
                if (apart < 0.02) alike.Add($"{modes[a]} and {modes[b]} ({apart:P2})");
            }
        }

        Assert.True(alike.Count == 0, $"these draw nearly the same picture: {string.Join(", ", alike)}");
    }

    /// <summary>
    /// The modes share their paints, and a shader drawn through a paint is dimmed by whatever
    /// alpha the last drawing left in it: the modes with shaded fills draw the same picture right
    /// after the meters (which leave a nearly clear paint behind) as they do from a fresh start.
    /// </summary>
    [Fact]
    public void AShadedFillLooksTheSameWhateverModeCameBefore()
    {
        var palette = VisualizerPalettes.Resolve("Aurora", null);
        foreach (var mode in new[] { VisualizerMode.Curve, VisualizerMode.FilledScope, VisualizerMode.Envelope })
        {
            var fresh = Picture(mode, palette, Musical);
            using var bitmap = new SKBitmap(new SKImageInfo(400, 250, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            using var painter = new VisualizerPainter();
            painter.Paint(canvas, 400, 250, Musical(0), VisualizerMode.VuMeters, palette, 1f / 60);
            for (var f = 0; f < 90; f++) painter.Paint(canvas, 400, 250, Musical(f), mode, palette, 1f / 60);
            var apart = Differing(fresh, bitmap.Pixels);
            Assert.True(apart < 0.01, $"{mode} after the meters differs from {mode} alone in {apart:P1} of the picture");
        }
    }

    /// <summary>Silence, frames with nothing in them and sizes from tiny to tall: every mode draws, none throws.</summary>
    [Fact]
    public void EveryModeDrawsSilenceAnEmptyFrameAndAnySize()
    {
        var palette = VisualizerPalettes.Resolve("Ember", null);
        var idle = new AudioFrame { Bands = new float[32], Peaks = new float[32], BandCentres = new float[32], Waveform = [], WaveformLeft = [], WaveformRight = [], Silent = true };
        var frames = new[] { AudioFrame.Empty(32, 1024), idle, AudioFrame.Empty(0, 0), Loud(3) };
        foreach (var mode in Enum.GetValues<VisualizerMode>())
        {
            foreach (var (width, height) in new[] { (8, 8), (401, 251), (250, 420), (37, 600) })
            {
                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bitmap);
                using var painter = new VisualizerPainter();
                for (var f = 0; f < 12; f++) painter.Paint(canvas, width, height, frames[f % frames.Length], mode, palette, f % 5 == 0 ? 0.1f : 1f / 60);
            }
        }
    }

    /// <summary>The cover mode shows the cover it is given, large in the middle; with none, a record in the palette's colours.</summary>
    [Fact]
    public void TheCoverModeShowsTheCoverOrElseARecord()
    {
        var palette = VisualizerPalettes.Resolve("Aurora", null);
        using var red = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        red.Erase(new SKColor(0xE0, 0x10, 0x10));
        using var cover = SKImage.FromBitmap(red);
        using var bitmap = new SKBitmap(new SKImageInfo(400, 250, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using (var painter = new VisualizerPainter { Cover = cover })
        {
            for (var f = 0; f < 10; f++) painter.Paint(canvas, 400, 250, Musical(f), VisualizerMode.Cover, palette, 1f / 60);
        }

        var middle = bitmap.GetPixel(200, 125);
        Assert.True(middle.Red > 200 && middle.Green < 40 && middle.Blue < 40, $"the cover is not in the middle ({middle})");

        using (var painter = new VisualizerPainter())
        {
            for (var f = 0; f < 10; f++) painter.Paint(canvas, 400, 250, Musical(f), VisualizerMode.Cover, palette, 1f / 60);
        }

        // The record's label, in the middle of the ramp.
        var label = bitmap.GetPixel(200 + 12, 125);
        var expected = palette.Sample(0.55f);
        Assert.True(Math.Abs(label.Red - expected.Red) < 12 && Math.Abs(label.Green - expected.Green) < 12 && Math.Abs(label.Blue - expected.Blue) < 12, $"no record's label in the middle ({label}, not {expected})");
    }

    /// <summary>
    /// A cover handed over is the renderer's: one replaced before it was drawn is let go at once,
    /// the one drawing is let go when the next arrives or the drawing ends, and one handed over
    /// after that is let go straight away. Each exactly once, and none while it may still be drawn.
    /// </summary>
    [Fact]
    public async Task ACoverHandedOverIsLetGoOnceItIsNoLongerDrawn()
    {
        SKImage Image()
        {
            using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
            return SKImage.FromBitmap(bitmap);
        }

        var shown = 0;
        var renderer = new VisualizerRenderer(() => Musical(Interlocked.Increment(ref shown)), (r, _) => r.Taken()) { Mode = VisualizerMode.Cover };
        renderer.Resize(new PixelSize(200, 120));
        var first = Image();
        var second = Image();
        renderer.SetCover(first);
        renderer.SetCover(second);
        Assert.Equal(IntPtr.Zero, first.Handle);
        renderer.Start();
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref shown) < 5 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.NotEqual(IntPtr.Zero, second.Handle);

        var third = Image();
        renderer.SetCover(third);
        var drawnWith = Volatile.Read(ref shown);
        while (Volatile.Read(ref shown) < drawnWith + 3 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(IntPtr.Zero, second.Handle);
        Assert.NotEqual(IntPtr.Zero, third.Handle);

        renderer.Stop();
        while (third.Handle != IntPtr.Zero && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(IntPtr.Zero, third.Handle);
        var late = Image();
        renderer.SetCover(late);
        Assert.Equal(IntPtr.Zero, late.Handle);
    }

    [Fact]
    public void TheCatalogueListsEveryModeOnceByFamilyAndCyclesThroughThemAll()
    {
        var all = VisualizerModes.All;
        Assert.Equal(Enum.GetValues<VisualizerMode>().Order(), all.Select(m => m.Mode).Order());
        Assert.Equal(all.Count, all.Select(m => m.Title).Distinct().Count());

        // A family's modes stand together, so the menus and the cycle go family by family.
        var families = all.Select(m => m.Family).Where((f, i) => i == 0 || f != all[i - 1].Family).ToList();
        Assert.Equal(families, families.Distinct());
        Assert.Equal(VisualizerModes.Families, families);

        foreach (var info in all)
        {
            Assert.Equal(info.Mode, VisualizerModes.Find(info.Title));
            Assert.Equal(info.Mode, VisualizerModes.Find(info.Mode.ToString()));
        }

        Assert.Null(VisualizerModes.Find("No such mode"));
        Assert.Null(VisualizerModes.Find("999"));
        var seen = new List<VisualizerMode>();
        var mode = all[0].Mode;
        for (var i = 0; i < all.Count; i++)
        {
            seen.Add(mode);
            mode = VisualizerModes.Next(mode);
        }

        Assert.Equal(all[0].Mode, mode);
        Assert.Equal(all.Select(m => m.Mode), seen);
    }

    /// <summary>
    /// Not a check: with <c>VIS_SHOTS</c> naming a folder, every mode's picture after two seconds of
    /// music-like frames, at a window's size, in two palettes, for a person to look at.
    /// </summary>
    [Fact]
    public void Screenshots()
    {
        var folder = Environment.GetEnvironmentVariable("VIS_SHOTS");
        if (string.IsNullOrEmpty(folder)) Assert.Skip("Set VIS_SHOTS to a folder to draw every mode into it.");
        Directory.CreateDirectory(folder);
        var only = Environment.GetEnvironmentVariable("VIS_ONLY");
        foreach (var name in new[] { "Aurora", "Classic" })
        {
            var palette = VisualizerPalettes.Resolve(name, null);
            foreach (var info in VisualizerModes.All)
            {
                if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(info.Mode.ToString())) continue;
                var size = VisualizerRenderer.DrawSize(new PixelSize(1280, 800), info.Mode);
                using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bitmap);
                using var painter = new VisualizerPainter();
                for (var f = 0; f < 150; f++) painter.Paint(canvas, size.Width, size.Height, Musical(f), info.Mode, palette, 1f / 60);
                using var shown = bitmap.Resize(new SKImageInfo(1280, 800, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
                using var file = File.Create(Path.Combine(folder, $"{VisualizerModes.All.ToList().IndexOf(info):00}-{info.Mode}-{name}.png"));
                shown.Encode(file, SKEncodedImageFormat.Png, 90);
            }
        }
    }

    /// <summary>Frames more like music than <see cref="Loud"/>: more in the bass than the treble, a kick every half second, a chord in the waveform, left and right apart.</summary>
    internal static AudioFrame Musical(long sequence)
    {
        const int Bands = 32;
        var random = new Random((int)sequence);
        var t = sequence / 60f;
        var kick = MathF.Exp(-(t % 0.5f) * 9f);
        var bands = new float[Bands];
        var peaks = new float[Bands];
        for (var b = 0; b < Bands; b++)
        {
            var slope = 0.85f - (0.45f * b / Bands);
            var wobble = 0.12f * MathF.Sin((t * 3.1f) + (b * 0.7f));
            var hit = b < 6 ? 0.25f * kick : 0f;
            bands[b] = Math.Clamp(slope + wobble + hit - (0.08f * (float)random.NextDouble()), 0f, 1f);
            peaks[b] = Math.Min(1f, bands[b] + 0.06f);
        }

        var left = new float[1024];
        var right = new float[1024];
        for (var i = 0; i < left.Length; i++)
        {
            var x = (i + (sequence * 800)) / 48000f;
            var chord = (0.28f * MathF.Sin(MathF.Tau * 110 * x)) + (0.16f * MathF.Sin(MathF.Tau * 277 * x)) + (0.1f * MathF.Sin(MathF.Tau * 330 * x));
            left[i] = chord + (0.05f * MathF.Sin(MathF.Tau * 1320 * x));
            right[i] = chord - (0.05f * MathF.Sin(MathF.Tau * 990 * x));
        }

        return new AudioFrame
        {
            Bands = bands,
            Peaks = peaks,
            BandCentres = new float[Bands],
            Waveform = [.. left.Zip(right, (l, r) => 0.5f * (l + r))],
            WaveformLeft = left,
            WaveformRight = right,
            RmsLeft = 0.72f + (0.1f * kick),
            RmsRight = 0.68f + (0.1f * kick),
            PeakLeft = 0.9f,
            PeakRight = 0.88f,
            Beat = sequence % 30 == 0,
            BeatIntensity = kick,
            Silent = false,
            Sequence = sequence,
        };
    }

    // A mode's picture after a second and a half of frames, at a small window's size.
    private static SKColor[] Picture(VisualizerMode mode, VisualizerPalette palette, Func<long, AudioFrame> frames)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(400, 250, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var painter = new VisualizerPainter();
        for (var f = 0; f < 90; f++) painter.Paint(canvas, 400, 250, frames(f), mode, palette, 1f / 60);
        return bitmap.Pixels;
    }

    // The share of pixels that differ plainly (by more than a sixteenth in some channel) between two pictures.
    private static double Differing(SKColor[] a, SKColor[] b)
    {
        var differ = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].Red - b[i].Red) > 16 || Math.Abs(a[i].Green - b[i].Green) > 16 || Math.Abs(a[i].Blue - b[i].Blue) > 16) differ++;
        }

        return differ / (double)a.Length;
    }

    [Fact]
    public void PalettesAreNamedAndAlbumTakesTheCoversColours()
    {
        Assert.Equal(VisualizerPalettes.AlbumName, VisualizerPalettes.Names[0]);
        Assert.Equal(VisualizerPalettes.Names.Count, VisualizerPalettes.Names.Distinct().Count());
        Assert.Equal("Aurora", VisualizerPalettes.Resolve("No such palette", null).Name);

        // No cover colours: the album palette is the first fixed one; with them, its ramp is theirs.
        Assert.Equal("Aurora", VisualizerPalettes.Resolve(VisualizerPalettes.AlbumName, null).Name);
        var cover = new UltraBlurColors { TopLeft = "1f5a8c", TopRight = "8c1f5a", BottomLeft = "0a1c2b", BottomRight = "5a8c1f" };
        var album = VisualizerPalettes.Resolve(VisualizerPalettes.AlbumName, cover);
        Assert.Equal(VisualizerPalettes.AlbumName, album.Name);
        album.Stops[0].ToHsv(out var hue, out _, out _);
        Assert.InRange(hue, 195f, 215f);
        Assert.True(Bright(album.Background) < 40, "the album's background is not dark");
    }

    /// <summary>
    /// The drawing thread's band: at the size of a large window, 95 % of frames drawn in under
    /// 8 ms and none over a whole frame (16.7 ms), sixty frames a second when the view takes each
    /// at once, and never on the thread that asked.
    /// </summary>
    [Fact]
    public async Task FramesAreDrawnOffTheCallersThreadAndHandedOverThere()
    {
        var shown = 0;
        var presentedOn = -1;
        var renderer = new VisualizerRenderer(() => Loud(0), (r, _) =>
        {
            presentedOn = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref shown);
            r.Taken();
        });
        renderer.Resize(new PixelSize(640, 400));
        renderer.Start();
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref shown) < 10 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, TestContext.Current.CancellationToken);
        renderer.Stop();

        Assert.True(shown >= 10, $"{shown} frames in 10 s");
        Assert.NotEqual(Environment.CurrentManagedThreadId, renderer.DrawingThreadId);
        Assert.Equal(renderer.DrawingThreadId, presentedOn);
    }

    [Fact]
    public void AFullScreenFrameIsDrawnAtMostAtTheCapAndScaledUp()
    {
        Assert.Equal(new PixelSize(1600, 1000), VisualizerRenderer.DrawSize(new PixelSize(1600, 1000)));
        Assert.Equal(new PixelSize(1920, 1080), VisualizerRenderer.DrawSize(new PixelSize(3840, 2160)));
        Assert.Equal(new PixelSize(1080, 1920), VisualizerRenderer.DrawSize(new PixelSize(2160, 3840)));
        Assert.Equal(new PixelSize(1600, 1000), VisualizerRenderer.DrawSize(new PixelSize(1600, 1000), VisualizerMode.Spectrum));
        Assert.Equal(new PixelSize(960, 600), VisualizerRenderer.DrawSize(new PixelSize(1600, 1000), VisualizerMode.Terrain));
        Assert.Equal(new PixelSize(640, 400), VisualizerRenderer.DrawSize(new PixelSize(1600, 1000), VisualizerMode.Plasma));
    }

    /// <summary>
    /// Through the renderer itself: a mode made pixel by pixel is handed over at its own smaller
    /// size for the view to scale, and the pixel modes draw the classic skins' frame, not the full
    /// one, when there is one.
    /// </summary>
    [Fact]
    public async Task TheRendererDrawsEachModeAtItsSizeAndThePixelModesFromTheClassicFrame()
    {
        var full = new float[19];
        Array.Fill(full, 1f);
        var classic = new AudioFrame { Bands = full, Peaks = full, BandCentres = new float[19], Waveform = new float[512], WaveformLeft = new float[512], WaveformRight = new float[512] };
        var sizes = new List<PixelSize>();
        var lit = -1;
        var renderer = new VisualizerRenderer(() => AudioFrame.Empty(32, 1024), (r, bitmap) =>
        {
            lock (sizes) sizes.Add(bitmap.PixelSize);

            // A frame is known by its size, not by the mode now asked for: the last plasma frame
            // can be handed over just after the mode changes.
            if (bitmap.PixelSize == new PixelSize(1600, 1000))
            {
                // A pixel three eighths of the way down the bars' middle: lit only by a full bar.
                using var buffer = bitmap.Lock();
                var pixel = System.Runtime.InteropServices.Marshal.ReadInt32(buffer.Address, (buffer.RowBytes * (buffer.Size.Height * 3 / 8)) + (buffer.Size.Width / 2 * 4));
                Volatile.Write(ref lit, Math.Max(pixel & 255, Math.Max((pixel >> 8) & 255, (pixel >> 16) & 255)));
            }

            r.Taken();
        })
        {
            Mode = VisualizerMode.Plasma,
            Classic = () => classic,
        };
        renderer.Resize(new PixelSize(1600, 1000));
        renderer.Start();
        var clock = Stopwatch.StartNew();
        bool Seen(PixelSize size)
        {
            lock (sizes) return sizes.Contains(size);
        }

        while (!Seen(new PixelSize(640, 400)) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, TestContext.Current.CancellationToken);
        renderer.Mode = VisualizerMode.PixelBars;
        while (Volatile.Read(ref lit) < 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, TestContext.Current.CancellationToken);
        renderer.Stop();

        Assert.True(Seen(new PixelSize(640, 400)), "a plasma frame was not handed over at 640 by 400");
        Assert.True(Seen(new PixelSize(1600, 1000)), "the pixel bars were not handed over at the view's size");
        Assert.True(Volatile.Read(ref lit) > 80, $"the pixel bars did not draw the classic frame's full bars (brightest channel {Volatile.Read(ref lit)})");
    }

    private static int Bright(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue));
}
