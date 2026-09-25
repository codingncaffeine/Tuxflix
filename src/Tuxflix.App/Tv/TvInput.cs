namespace Tuxflix.App.Tv;

/// <summary>
/// What a press asks the interface to do, whether it came from a controller, a remote or the
/// keyboard. The controller's map ties buttons to these; what each one does depends on where the
/// viewer is: the library, a menu or the player.
/// </summary>
public enum TvAction
{
    Up,
    Down,
    Left,
    Right,

    /// <summary>A: press what has focus; in the player, play or pause.</summary>
    Select,

    /// <summary>B: back a page, close a menu; in the player, hide the controls, then stop.</summary>
    Back,

    /// <summary>Start: the menu; in the player, the playback settings.</summary>
    Menu,

    /// <summary>Guide: library home; from the desktop interface, the TV interface.</summary>
    Home,

    /// <summary>Left shoulder: a screenful to the left; in the player, the previous chapter.</summary>
    PageLeft,

    /// <summary>Right shoulder: a screenful to the right; in the player, the next chapter.</summary>
    PageRight,

    /// <summary>Left trigger: in the player, seek back while it is held; elsewhere a screenful up.</summary>
    SeekBack,

    /// <summary>Right trigger: in the player, seek forward while it is held; elsewhere a screenful down.</summary>
    SeekForward,

    /// <summary>X: play what has focus at once; in the player, audio and subtitles; on the keyboard, delete.</summary>
    Play,

    /// <summary>Y: search; in the player, skip the intro or start the next episode; on the keyboard, a space.</summary>
    Search,
}

/// <summary>Where a press is: just down, held (repeating), or let go.</summary>
public enum TvPhase
{
    Press,
    Repeat,
    Release,
}

/// <summary>
/// A controller's buttons, by position rather than by label (A on an Xbox pad is Cross on a
/// PlayStation one: both are <see cref="South"/>). The first twenty-six are SDL's own numbers; the
/// triggers are axes to SDL and buttons here.
/// </summary>
public enum PadButton
{
    South,
    East,
    West,
    North,
    Back,
    Guide,
    Start,
    LeftStick,
    RightStick,
    LeftShoulder,
    RightShoulder,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    Misc1,
    RightPaddle1,
    LeftPaddle1,
    RightPaddle2,
    LeftPaddle2,
    Touchpad,
    Misc2,
    Misc3,
    Misc4,
    Misc5,
    Misc6,
    LeftTrigger = 100,
    RightTrigger = 101,
}

/// <summary>A controller's sticks and triggers, SDL's numbers.</summary>
public enum PadAxis
{
    LeftX,
    LeftY,
    RightX,
    RightY,
    LeftTrigger,
    RightTrigger,
}

public enum PadEventKind
{
    ButtonDown,
    ButtonUp,
    Axis,
    Added,
    Removed,
}

/// <summary>One thing a controller did, as the device layer reports it.</summary>
/// <param name="Value">An axis from -1 to 1 (triggers 0 to 1).</param>
/// <param name="Name">The controller's name, for <see cref="PadEventKind.Added"/>.</param>
public readonly record struct PadEvent(PadEventKind Kind, uint Pad, PadButton Button = default, PadAxis Axis = default, float Value = 0, string? Name = null)
{
    public static PadEvent Down(PadButton button, uint pad = 1) => new(PadEventKind.ButtonDown, pad, button);

    public static PadEvent Up(PadButton button, uint pad = 1) => new(PadEventKind.ButtonUp, pad, button);

    public static PadEvent Moved(PadAxis axis, float value, uint pad = 1) => new(PadEventKind.Axis, pad, Axis: axis, Value: value);
}

/// <summary>An action on its way to the interface: which, in what phase, and how hard (a trigger's depth, else 1).</summary>
public readonly record struct TvInput(TvAction Action, TvPhase Phase, float Strength = 1);

/// <summary>
/// Where controller events come from: SDL3 in the application, a hand-fed queue under test. Every
/// call is made on the input thread, never the UI thread.
/// </summary>
internal interface IPadSource : IDisposable
{
    /// <summary>Starts the device layer; false, with the reason, when there is none.</summary>
    bool Open(out string? unavailable);

    /// <summary>Waits up to <paramref name="timeout"/> for the next event.</summary>
    bool Next(TimeSpan timeout, out PadEvent padEvent);
}
