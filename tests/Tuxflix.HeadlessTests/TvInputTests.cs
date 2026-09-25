using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Tuxflix.App.Tv;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The TV interface's input without a window: where the D-pad takes focus between rectangles,
/// what the controller's buttons do and how a new button swaps, and the controller thread's
/// presses, repeats, stick, triggers and the map editor's capture.
/// </summary>
public sealed class TvInputTests
{
    // Two shelves of four 100x150 tiles, 20 apart; the second shelf 200 lower and 40 to the right.
    private static readonly Rect[] Shelves =
    [
        new(0, 0, 100, 150), new(120, 0, 100, 150), new(240, 0, 100, 150), new(360, 0, 100, 150),
        new(40, 200, 100, 150), new(160, 200, 100, 150), new(280, 200, 100, 150), new(400, 200, 100, 150),
    ];

    [Fact]
    public void TheDPadGoesToTheNeighbourInTheRowAndToTheNearestInLineBelow()
    {
        Assert.Equal(1, From(0, NavDirection.Right));
        Assert.Equal(2, From(3, NavDirection.Left));

        // Down from the third tile, the two below both overlap it; the one whose centre is nearer wins.
        Assert.Equal(6, From(2, NavDirection.Down));
        Assert.Equal(4, From(0, NavDirection.Down));
        Assert.Equal(1, From(5, NavDirection.Up));

        // Nothing lies above the top row or left of the first tile.
        Assert.Equal(-1, From(1, NavDirection.Up));
        Assert.Equal(-1, From(0, NavDirection.Left));
    }

    [Fact]
    public void ATileInLineBeatsANearerOneOffToTheSide()
    {
        // Right from the source: one far along its row, one nearer but in the row below.
        var source = new Rect(0, 0, 100, 100);
        Rect[] candidates = [new Rect(400, 0, 100, 100), new Rect(130, 140, 100, 100)];

        // Whichever is looked at first.
        Assert.Equal(0, SpatialNavigator.Pick(source, NavDirection.Right, candidates));
        Assert.Equal(1, SpatialNavigator.Pick(source, NavDirection.Right, [candidates[1], candidates[0]]));
    }

    [Fact]
    public void TheStandardLayoutIsBigPictures()
    {
        var map = GamepadMap.Standard;

        Assert.Equal(TvAction.Select, map.ActionFor(PadButton.South));
        Assert.Equal(TvAction.Back, map.ActionFor(PadButton.East));
        Assert.Equal(TvAction.Play, map.ActionFor(PadButton.West));
        Assert.Equal(TvAction.Search, map.ActionFor(PadButton.North));
        Assert.Equal(TvAction.Menu, map.ActionFor(PadButton.Start));
        Assert.Equal(TvAction.Home, map.ActionFor(PadButton.Guide));
        Assert.Equal(TvAction.PageLeft, map.ActionFor(PadButton.LeftShoulder));
        Assert.Equal(TvAction.SeekForward, map.ActionFor(PadButton.RightTrigger));
        Assert.All(GamepadMap.Actions, action => Assert.NotEmpty(map.ButtonsFor(action)));
    }

    [Fact]
    public void GivingBackToASwapsAAndBAndTheSettingsKeepIt()
    {
        var map = GamepadMap.Standard.Assign(TvAction.Back, PadButton.South);

        // B was one of Back's two buttons: it keeps Back; A's old Select had no other button, so
        // Select takes Back's first button, B.
        Assert.Equal(TvAction.Back, map.ActionFor(PadButton.South));
        Assert.Equal(TvAction.Select, map.ActionFor(PadButton.East));
        Assert.All(GamepadMap.Actions, action => Assert.NotEmpty(map.ButtonsFor(action)));

        var saved = map.ToSettings();
        Assert.Equal("Back", saved["South"]);
        var loaded = GamepadMap.FromSettings(saved);
        Assert.All(Enum.GetValues<PadButton>(), button => Assert.Equal(map.ActionFor(button), loaded.ActionFor(button)));
    }

    [Fact]
    public void ASavedMapThatLosesAnActionIsNotUsed()
    {
        var saved = GamepadMap.Standard.ToSettings();
        saved.Remove("East");
        saved.Remove("Back");

        Assert.Same(GamepadMap.Standard, GamepadMap.FromSettings(saved));
        Assert.Same(GamepadMap.Standard, GamepadMap.FromSettings(new Dictionary<string, string> { ["Nonsense"] = "Select" }));
    }

    [Fact]
    public async Task AHeldDirectionRepeatsAfterTheDelayAndStopsWhenLetGo()
    {
        using var pads = new Recorded();
        pads.Source.Push(PadEvent.Down(PadButton.DPadRight));
        var pressed = await pads.NextAsync();
        Assert.Equal(new TvInput(TvAction.Right, TvPhase.Press), pressed.Input);

        // The first repeat comes after the delay, within one repeat interval of it.
        var repeat = await pads.NextAsync();
        Assert.Equal(TvPhase.Repeat, repeat.Input.Phase);
        var after = repeat.At - pressed.At;
        Assert.InRange(after.TotalMilliseconds, GamepadInput.RepeatDelay.TotalMilliseconds - 5, GamepadInput.RepeatDelay.TotalMilliseconds + GamepadInput.RepeatEvery.TotalMilliseconds);

        pads.Source.Push(PadEvent.Up(PadButton.DPadRight));
        await pads.UntilAsync(i => i.Phase == TvPhase.Release);
        var quiet = pads.Count;
        await Task.Delay(GamepadInput.RepeatDelay, TestContext.Current.CancellationToken);
        Assert.Equal(quiet, pads.Count);
    }

    [Fact]
    public async Task TheStickMovesFocusWithAMarginBeforeItLetsGo()
    {
        using var pads = new Recorded();
        pads.Source.Push(PadEvent.Moved(PadAxis.LeftY, 0.4f));
        pads.Source.Push(PadEvent.Moved(PadAxis.LeftY, 0.8f));
        Assert.Equal(new TvInput(TvAction.Down, TvPhase.Press), (await pads.NextAsync()).Input);

        // Easing back to 0.4 still holds (it lets go below 0.3), so nothing is released yet.
        pads.Source.Push(PadEvent.Moved(PadAxis.LeftY, 0.4f));
        pads.Source.Push(PadEvent.Moved(PadAxis.LeftY, 0.1f));
        var released = await pads.UntilAsync(i => i.Phase == TvPhase.Release);
        Assert.Equal(TvAction.Down, released.Action);
        Assert.DoesNotContain(pads.All, i => i.Action != TvAction.Down);
    }

    [Fact]
    public async Task ATriggerIsASeekThatCarriesItsDepth()
    {
        using var pads = new Recorded();
        pads.Source.Push(PadEvent.Moved(PadAxis.RightTrigger, 0.2f));
        pads.Source.Push(PadEvent.Moved(PadAxis.RightTrigger, 0.9f));
        var pulled = (await pads.NextAsync()).Input;
        Assert.Equal((TvAction.SeekForward, TvPhase.Press), (pulled.Action, pulled.Phase));
        Assert.Equal(0.9f, pulled.Strength, 3);

        pads.Source.Push(PadEvent.Moved(PadAxis.RightTrigger, 0));
        Assert.Equal(TvAction.SeekForward, (await pads.UntilAsync(i => i.Phase == TvPhase.Release)).Action);
    }

    [Fact]
    public async Task TheMapEditorsPressGoesToItAndItsReleaseIsSwallowed()
    {
        using var pads = new Recorded();
        var captured = new TaskCompletionSource<PadButton>();
        pads.Input.CaptureNext(captured.SetResult);

        pads.Source.Push(PadEvent.Down(PadButton.LeftStick));
        pads.Source.Push(PadEvent.Up(PadButton.LeftStick));
        Assert.Equal(PadButton.LeftStick, await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The next press is the map's again.
        pads.Source.Push(PadEvent.Down(PadButton.South));
        Assert.Equal(new TvInput(TvAction.Select, TvPhase.Press), (await pads.NextAsync()).Input);
        Assert.Single(pads.All);
    }

    /// <summary>Where the D-pad goes from one of the shelves' tiles, as an index into the shelves; -1 for nowhere.</summary>
    private static int From(int tile, NavDirection direction)
    {
        var others = Shelves.Where((_, i) => i != tile).ToList();
        var picked = SpatialNavigator.Pick(Shelves[tile], direction, others);
        return picked < 0 ? -1 : Shelves.ToList().IndexOf(others[picked]);
    }

    /// <summary>A controller thread over a fake pad, every action it hands on kept with its time.</summary>
    private sealed class Recorded : IDisposable
    {
        private readonly ConcurrentQueue<(TvInput Input, TimeSpan At)> _got = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _read;

        public Recorded()
        {
            Input = new GamepadInput(() => Source, GamepadMap.Standard, input =>
            {
                _got.Enqueue((input, _clock.Elapsed));
                _arrived.Release();
            });
            Input.Start();
        }

        public FakePadSource Source { get; } = new();

        public GamepadInput Input { get; }

        public int Count => _got.Count;

        public IReadOnlyList<TvInput> All => [.. _got.Select(g => g.Input)];

        public async Task<(TvInput Input, TimeSpan At)> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "no action within 5 s");
            return _got.ElementAt(_read++);
        }

        public async Task<TvInput> UntilAsync(Func<TvInput, bool> wanted)
        {
            while (true)
            {
                var next = await NextAsync();
                if (wanted(next.Input)) return next.Input;
            }
        }

        public void Dispose()
        {
            Input.Dispose();
            _arrived.Dispose();
        }
    }
}
