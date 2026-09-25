using System.Diagnostics;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Tv;

/// <summary>
/// The controller's own thread: reads the device layer, turns buttons into actions through the
/// map, repeats what is held, and hands every action to the interface.
/// </summary>
/// <remarks>
/// Nothing here runs on the UI thread. The thread waits on the device layer, so a press is read
/// within a poll (8 ms while a pad is connected) and handed on at once; the UI thread only
/// receives finished actions. A held direction repeats after 420 ms, then about eleven times a
/// second, as TV remotes and Steam Big Picture do; a held trigger repeats every 150 ms with its
/// depth, for seeking. The left stick moves focus like the D-pad whatever the map says. When
/// the map editor waits for a press, the next button goes to it instead of to the map, and its
/// release is swallowed.
/// </remarks>
internal sealed class GamepadInput : IDisposable
{
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(420);
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(90);
    public static readonly TimeSpan SeekRepeatEvery = TimeSpan.FromMilliseconds(150);

    private const float StickOn = 0.5f;
    private const float StickOff = 0.3f;
    private const float TriggerOn = 0.35f;
    private const float TriggerOff = 0.2f;

    /// <summary>The stick's four directions, as sources of held actions next to the buttons.</summary>
    private const int StickSource = 1000;

    private static int _saidUnavailable;

    private readonly Func<IPadSource> _open;
    private readonly Action<TvInput> _deliver;
    private readonly Thread _thread;
    private readonly Dictionary<int, Held> _held = [];
    private readonly HashSet<PadButton> _swallowed = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly bool[] _stick = new bool[4];
    private readonly bool[] _trigger = new bool[2];
    private volatile GamepadMap _map;
    private volatile Action<PadButton>? _capture;
    private volatile bool _stopping;
    private volatile string? _unavailable;
    private volatile IReadOnlyList<string> _pads = [];

    /// <param name="open">Makes the device layer; called on the input thread.</param>
    /// <param name="map">What the buttons do.</param>
    /// <param name="deliver">Takes each action, on the input thread: it posts them on.</param>
    public GamepadInput(Func<IPadSource> open, GamepadMap map, Action<TvInput> deliver)
    {
        _open = open;
        _map = map;
        _deliver = deliver;
        _thread = new Thread(Run) { IsBackground = true, Name = "Tuxflix controllers" };
    }

    /// <summary>The connected pads changed; raised on the input thread with their names.</summary>
    public event Action<IReadOnlyList<string>>? PadsChanged;

    /// <summary>What the buttons do; a new map takes effect with the next press.</summary>
    public GamepadMap Map
    {
        get => _map;
        set => _map = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Why there are no controllers (no SDL3); null while they work.</summary>
    public string? Unavailable => _unavailable;

    /// <summary>The names of the pads connected now.</summary>
    public IReadOnlyList<string> Pads => _pads;

    /// <summary>Whether the thread has read the device layer and found it (or found it missing).</summary>
    public bool IsStarted { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>The next button pressed goes to <paramref name="onButton"/> (on the input thread), not to the map.</summary>
    public void CaptureNext(Action<PadButton> onButton) => _capture = onButton;

    public void CancelCapture() => _capture = null;

    public void Dispose()
    {
        _stopping = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        IPadSource? source = null;
        try
        {
            source = _open();
            if (!source.Open(out var why))
            {
                _unavailable = why ?? "no controller support";
                if (Interlocked.Exchange(ref _saidUnavailable, 1) == 0) Log.Info($"No controller support: {_unavailable}. The keyboard works as ever.");
                return;
            }

            IsStarted = true;
            while (!_stopping)
            {
                var wait = _held.Count == 0 ? 250 : Math.Clamp(_held.Values.Min(h => h.Due) - Now(), 0, 250);
                if (source.Next(TimeSpan.FromMilliseconds(wait), out var padEvent)) Handle(padEvent);
                Repeat();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The controller thread stopped.", ex);
        }
        finally
        {
            IsStarted = true;
            source?.Dispose();
        }
    }

    private void Handle(PadEvent padEvent)
    {
        switch (padEvent.Kind)
        {
            case PadEventKind.ButtonDown:
                Down(padEvent.Button, 1);
                break;
            case PadEventKind.ButtonUp:
                Up(padEvent.Button);
                break;
            case PadEventKind.Axis:
                Axis(padEvent.Axis, padEvent.Value);
                break;
            case PadEventKind.Added:
                _names[padEvent.Pad] = padEvent.Name ?? "Controller";
                Log.Info($"Controller connected: {_names[padEvent.Pad]}.");
                Announce();
                break;
            case PadEventKind.Removed:
                if (_names.Remove(padEvent.Pad, out var name)) Log.Info($"Controller disconnected: {name}.");
                Announce();
                break;
        }
    }

    private void Announce()
    {
        _pads = [.. _names.Values];
        PadsChanged?.Invoke(_pads);
    }

    private void Down(PadButton button, float strength)
    {
        if (Interlocked.Exchange(ref _capture, null) is { } capture)
        {
            _swallowed.Add(button);
            capture(button);
            return;
        }

        if (_map.ActionFor(button) is { } action) Press((int)button, action, strength);
    }

    private void Up(PadButton button)
    {
        if (_swallowed.Remove(button)) return;
        if (_held.Remove((int)button, out var held))
        {
            _deliver(new TvInput(held.Action, TvPhase.Release, 0));
        }
        else if (_map.ActionFor(button) is { } action)
        {
            _deliver(new TvInput(action, TvPhase.Release, 0));
        }
    }

    private void Axis(PadAxis axis, float value)
    {
        switch (axis)
        {
            case PadAxis.LeftX:
                Stick(2, value <= -StickOn, value > -StickOff, TvAction.Left);
                Stick(3, value >= StickOn, value < StickOff, TvAction.Right);
                break;
            case PadAxis.LeftY:
                // Down is positive, as on the screen.
                Stick(0, value <= -StickOn, value > -StickOff, TvAction.Up);
                Stick(1, value >= StickOn, value < StickOff, TvAction.Down);
                break;
            case PadAxis.LeftTrigger or PadAxis.RightTrigger:
                var side = axis == PadAxis.LeftTrigger ? 0 : 1;
                var button = side == 0 ? PadButton.LeftTrigger : PadButton.RightTrigger;
                if (!_trigger[side] && value >= TriggerOn)
                {
                    _trigger[side] = true;
                    Down(button, value);
                }
                else if (_trigger[side] && value <= TriggerOff)
                {
                    _trigger[side] = false;
                    Up(button);
                }
                else if (_trigger[side] && _held.TryGetValue((int)button, out var held))
                {
                    _held[(int)button] = held with { Strength = value };
                }

                break;
        }
    }

    private void Stick(int direction, bool on, bool off, TvAction action)
    {
        if (!_stick[direction] && on)
        {
            _stick[direction] = true;
            Press(StickSource + direction, action, 1);
        }
        else if (_stick[direction] && off)
        {
            _stick[direction] = false;
            if (_held.Remove(StickSource + direction)) _deliver(new TvInput(action, TvPhase.Release, 0));
        }
    }

    private void Press(int source, TvAction action, float strength)
    {
        _deliver(new TvInput(action, TvPhase.Press, strength));
        if (action is TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right or TvAction.PageLeft or TvAction.PageRight or TvAction.SeekBack or TvAction.SeekForward)
        {
            var every = action is TvAction.SeekBack or TvAction.SeekForward ? SeekRepeatEvery : RepeatEvery;
            _held[source] = new Held(action, Now() + (long)RepeatDelay.TotalMilliseconds, (long)every.TotalMilliseconds, strength);
        }
    }

    private void Repeat()
    {
        if (_held.Count == 0) return;
        var now = Now();
        foreach (var (source, held) in _held.ToList())
        {
            if (held.Due > now) continue;
            _deliver(new TvInput(held.Action, TvPhase.Repeat, held.Strength));
            _held[source] = held with { Due = now + held.Every };
        }
    }

    private static long Now() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private readonly record struct Held(TvAction Action, long Due, long Every, float Strength);
}
