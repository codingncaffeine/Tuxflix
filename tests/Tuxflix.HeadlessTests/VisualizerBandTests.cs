using System.Diagnostics;
using Avalonia;
using Tuxflix.App.Music;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A frame at the size of a large window is drawn well inside the time a frame has at sixty a second.</summary>
[Collection(nameof(AloneOnTheMachine))]
public sealed class VisualizerBandTests
{
    public VisualizerBandTests(ITestOutputHelper output)
    {
        HeadlessSkia.Ensure();
        Output = output;
    }

    private ITestOutputHelper Output { get; }

    [Fact]
    public async Task EveryModeDrawsWellInsideASixtiethOfASecond()
    {
        // Drawing is in software, so its time is the processor's: a shared build machine's says
        // nothing about a desktop's.
        if (Environment.GetEnvironmentVariable("CI") == "true") Assert.Skip("Frame times are measured on a desktop, not on a shared build machine.");

        // A process's first frames compile the drawing code and start Skia: the band is for the
        // drawing, so every mode draws for a moment first, uncounted.
        var warm = new VisualizerRenderer(() => VisualizerTests.Loud(0), (r, _) => r.Taken()) { Palette = VisualizerPalettes.Resolve("Aurora", null) };
        warm.Resize(new PixelSize(1600, 1000));
        warm.Start();
        foreach (var mode in Enum.GetValues<VisualizerMode>())
        {
            warm.Mode = mode;
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        warm.Stop();

        // Every mode is judged, and all that miss are named. A mode that misses is measured once
        // more and judged on the better of the two: a mode that is slow misses both, while a
        // hiccup of the machine's own (another program, the desktop) rarely lands in the same mode
        // twice, and with 38 modes a single frame of it anywhere would otherwise fail the lot.
        var missed = new List<string>();
        foreach (var mode in Enum.GetValues<VisualizerMode>())
        {
            var miss = await MeasureAsync(mode);
            if (miss is not null)
            {
                Output.WriteLine($"{mode}: missed ({miss}), measured again");
                miss = await MeasureAsync(mode);
            }

            if (miss is not null) missed.Add($"{mode}: {miss}");
        }

        Assert.True(missed.Count == 0, $"outside the band twice running: {string.Join("; ", missed)}");
    }

    // One mode drawn for a second and a half at a large window's size; what it missed, or null.
    private async Task<string?> MeasureAsync(VisualizerMode mode)
    {
        var sequence = 0L;
        var shown = 0;
        var renderer = new VisualizerRenderer(() => VisualizerTests.Loud(Interlocked.Increment(ref sequence)), (r, _) =>
        {
            Interlocked.Increment(ref shown);
            r.Taken();
        })
        {
            Mode = mode,
            Palette = VisualizerPalettes.Resolve("Aurora", null),
        };
        renderer.Resize(new PixelSize(1600, 1000));

        // The suite before this leaves garbage behind; collected in the middle of a mode, it
        // would time the heap, not the drawing.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var full = GC.CollectionCount(2);
        var clock = Stopwatch.StartNew();
        renderer.Start();
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        renderer.Stop();
        var elapsed = clock.Elapsed.TotalSeconds;
        var times = renderer.Times;
        var (p95, slowest, rate) = (times.Percentile(0.95), times.Max, shown / elapsed);
        Output.WriteLine($"{mode}: {times.Count} frames in {elapsed:0.00} s, 95 % under {p95:0.00} ms, slowest {slowest:0.00} ms, full collections {GC.CollectionCount(2) - full}");
        return p95 is < 0.01 or > 8.0 ? $"95 % under {p95:0.00} ms, not 8"
            : slowest is < 0.01 or > 1000.0 / 60 ? $"a frame of {slowest:0.00} ms, over a sixtieth of a second"
            : rate is < 50 or > 64 ? $"{rate:0.0} frames a second, not 50 to 64"
            : null;
    }
}
