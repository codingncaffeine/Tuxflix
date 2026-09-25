using Tuxflix.App.Tv;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A held direction first repeats within one repeat interval of the delay.</summary>
[Collection(nameof(AloneOnTheMachine))]
public sealed class TvRepeatBandTests
{
    [Fact]
    public async Task AHeldDirectionFirstRepeatsWithinOneIntervalOfTheDelay()
    {
        // The time is the machine's: a shared build machine can stall a thread past the band.
        if (Environment.GetEnvironmentVariable("CI") == "true") Assert.Skip("Repeat times are measured on a desktop, not on a shared build machine.");

        using var pads = new TvInputTests.Recorded();

        // The first press compiles the repeat code: it is let go, uncounted.
        pads.Source.Push(PadEvent.Down(PadButton.DPadLeft));
        await pads.NextAsync();
        pads.Source.Push(PadEvent.Up(PadButton.DPadLeft));
        await pads.UntilAsync(i => i.Phase == TvPhase.Release);

        // The suite before this leaves garbage behind: collected now, not in the middle of the hold.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        pads.Source.Push(PadEvent.Down(PadButton.DPadRight));
        var pressed = await pads.NextAsync();
        var repeat = await pads.NextAsync();
        pads.Source.Push(PadEvent.Up(PadButton.DPadRight));

        Assert.Equal(new TvInput(TvAction.Right, TvPhase.Repeat), repeat.Input);
        var after = repeat.At - pressed.At;
        Assert.InRange(after.TotalMilliseconds, GamepadInput.RepeatDelay.TotalMilliseconds - 5, GamepadInput.RepeatDelay.TotalMilliseconds + GamepadInput.RepeatEvery.TotalMilliseconds);
    }
}
