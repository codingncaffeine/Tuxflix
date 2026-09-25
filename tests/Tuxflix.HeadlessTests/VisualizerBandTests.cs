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
        foreach (var mode in Enum.GetValues<VisualizerMode>())
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
            Output.WriteLine($"{mode}: {times.Count} frames in {elapsed:0.00} s, 95 % under {times.Percentile(0.95):0.00} ms, slowest {times.Max:0.00} ms, full collections {GC.CollectionCount(2) - full}");
            Assert.InRange(times.Percentile(0.95), 0.01, 8.0);
            Assert.InRange(times.Max, 0.01, 1000.0 / 60);
            Assert.InRange(shown / elapsed, 50, 64);
        }
    }
}
