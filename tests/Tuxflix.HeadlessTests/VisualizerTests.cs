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
/// visualizer's own thread, and a frame at the size of a large window is drawn well inside the
/// time a frame has at sixty a second.
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
    public void EveryModePaintsItsOwnShape()
    {
        var palette = VisualizerPalettes.Resolve("Classic", null);
        foreach (var mode in Enum.GetValues<VisualizerMode>())
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
    public async Task FramesAreDrawnOffTheCallersThreadWithinTheBand()
    {
        foreach (var mode in Enum.GetValues<VisualizerMode>())
        {
            var sequence = 0L;
            var shown = 0;
            var presentedOn = -1;
            var renderer = new VisualizerRenderer(() => Loud(Interlocked.Increment(ref sequence)), (r, _) =>
            {
                presentedOn = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref shown);
                r.Taken();
            })
            {
                Mode = mode,
                Palette = VisualizerPalettes.Resolve("Aurora", null),
            };
            renderer.Resize(new PixelSize(1600, 1000));
            var clock = Stopwatch.StartNew();
            renderer.Start();
            await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
            renderer.Stop();
            var elapsed = clock.Elapsed.TotalSeconds;
            var times = renderer.Times;
            Output.WriteLine($"{mode}: {times.Count} frames in {elapsed:0.00} s, 95 % under {times.Percentile(0.95):0.00} ms, slowest {times.Max:0.00} ms");
            Assert.NotEqual(Environment.CurrentManagedThreadId, renderer.DrawingThreadId);
            Assert.Equal(renderer.DrawingThreadId, presentedOn);
            Assert.InRange(times.Percentile(0.95), 0.01, 8.0);
            Assert.InRange(times.Max, 0.01, 1000.0 / 60);
            Assert.InRange(shown / elapsed, 50, 64);
        }
    }

    [Fact]
    public void AFullScreenFrameIsDrawnAtMostAtTheCapAndScaledUp()
    {
        Assert.Equal(new PixelSize(1600, 1000), VisualizerRenderer.DrawSize(new PixelSize(1600, 1000)));
        Assert.Equal(new PixelSize(1920, 1080), VisualizerRenderer.DrawSize(new PixelSize(3840, 2160)));
        Assert.Equal(new PixelSize(1080, 1920), VisualizerRenderer.DrawSize(new PixelSize(2160, 3840)));
    }

    private static int Bright(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue));
}
