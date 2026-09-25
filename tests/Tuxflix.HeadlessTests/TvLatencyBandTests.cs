using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tuxflix.App.Tv;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A controller's press reaches focus within three frames at sixty a second.</summary>
[Collection(nameof(AloneOnTheMachine))]
public sealed class TvLatencyBandTests
{
    [Fact]
    public Task AControllerPressMovesFocusWithinFiftyMilliseconds() => HeadlessApp.Run(async () =>
    {
        // The time is the machine's: a shared build machine's says nothing about a desktop's.
        if (Environment.GetEnvironmentVariable("CI") == "true") Assert.Skip("Press times are measured on a desktop, not on a shared build machine.");

        var pad = new FakePadSource();
        using var tv = await TvHarness.OpenHomeAsync(pad: pad);
        await tv.UntilAsync(() => pad.Opened, "the controller thread");

        // The suite before this leaves garbage behind: collected now, not in the middle of a press.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Timed from the press to the moment focus changes on the UI thread; the test's own
        // drawing between presses is outside the measurement. The first presses compile the focus
        // code: they move focus, uncounted.
        var clock = Stopwatch.StartNew();
        var focusedAt = new List<TimeSpan>();
        tv.Window.AddHandler(InputElement.GotFocusEvent, (_, _) => focusedAt.Add(clock.Elapsed), RoutingStrategies.Bubble, handledEventsToo: true);
        var moved = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var button = i % 2 == 0 ? PadButton.DPadRight : PadButton.DPadLeft;
            var before = focusedAt.Count;
            var pressedAt = clock.Elapsed;
            pad.Push(PadEvent.Down(button));
            pad.Push(PadEvent.Up(button));
            while (focusedAt.Count == before && clock.Elapsed - pressedAt < TimeSpan.FromSeconds(5)) await Task.Delay(1, TestContext.Current.CancellationToken);
            Assert.True(focusedAt.Count > before, $"press {i} moved no focus");
            if (i >= 2) moved.Add((focusedAt[before] - pressedAt).TotalMilliseconds);
            tv.Pump();
        }

        // The band: a press reaches focus within 50 ms, three frames at 60 Hz.
        Assert.All(moved, ms => Assert.InRange(ms, 0, 50));
    });
}
