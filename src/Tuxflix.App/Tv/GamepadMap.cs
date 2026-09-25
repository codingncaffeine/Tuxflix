using System.Collections.Frozen;

namespace Tuxflix.App.Tv;

/// <summary>
/// Which controller button does what. Immutable: a change makes a new map, which the input
/// thread picks up whole, so it never reads one half-changed.
/// </summary>
/// <remarks>
/// The standard layout is the one Steam Big Picture, Kodi and Plex's TV apps share: A selects, B
/// goes back, the shoulders page, the triggers seek, Start opens the menu and Guide goes home.
/// Giving a button a new action never leaves an action without a button: when the button was
/// the only one for its old action, the old action takes the new one's previous button, so
/// assigning Back to A swaps A and B rather than losing Select. The left stick always moves focus
/// as the D-pad does and cannot be reassigned, so a map can never lock the viewer out.
/// </remarks>
public sealed class GamepadMap
{
    private readonly FrozenDictionary<PadButton, TvAction> _actions;

    private GamepadMap(IEnumerable<KeyValuePair<PadButton, TvAction>> actions) => _actions = actions.ToFrozenDictionary();

    /// <summary>The standard layout.</summary>
    public static GamepadMap Standard { get; } = new(new Dictionary<PadButton, TvAction>
    {
        [PadButton.South] = TvAction.Select,
        [PadButton.East] = TvAction.Back,
        [PadButton.West] = TvAction.Play,
        [PadButton.North] = TvAction.Search,
        [PadButton.Back] = TvAction.Back,
        [PadButton.Guide] = TvAction.Home,
        [PadButton.Start] = TvAction.Menu,
        [PadButton.LeftShoulder] = TvAction.PageLeft,
        [PadButton.RightShoulder] = TvAction.PageRight,
        [PadButton.LeftTrigger] = TvAction.SeekBack,
        [PadButton.RightTrigger] = TvAction.SeekForward,
        [PadButton.DPadUp] = TvAction.Up,
        [PadButton.DPadDown] = TvAction.Down,
        [PadButton.DPadLeft] = TvAction.Left,
        [PadButton.DPadRight] = TvAction.Right,
    });

    /// <summary>Every action, in the order the map editor lists them.</summary>
    public static IReadOnlyList<TvAction> Actions { get; } = Enum.GetValues<TvAction>();

    /// <summary>What <paramref name="button"/> does; null when it does nothing.</summary>
    public TvAction? ActionFor(PadButton button) => _actions.TryGetValue(button, out var action) ? action : null;

    /// <summary>The buttons that do <paramref name="action"/>, in button order.</summary>
    public IReadOnlyList<PadButton> ButtonsFor(TvAction action) =>
        [.. _actions.Where(pair => pair.Value == action).Select(pair => pair.Key).Order()];

    /// <summary>A map in which <paramref name="button"/> does <paramref name="action"/>; see the remarks for swaps.</summary>
    public GamepadMap Assign(TvAction action, PadButton button)
    {
        var map = new Dictionary<PadButton, TvAction>(_actions);
        var previous = ButtonsFor(action);
        var displaced = ActionFor(button);
        map[button] = action;
        if (displaced is { } old && old != action && !map.ContainsValue(old) && previous.Count > 0)
        {
            map[previous[0]] = old;
        }

        return new GamepadMap(map);
    }

    /// <summary>The map as settings keep it: button name to action name.</summary>
    public Dictionary<string, string> ToSettings() =>
        _actions.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString());

    /// <summary>
    /// The map the settings hold, or the standard one when they hold none. Names that are not a
    /// button or an action are skipped; a saved map that leaves an action without a button is
    /// not used at all.
    /// </summary>
    public static GamepadMap FromSettings(IReadOnlyDictionary<string, string>? saved)
    {
        if (saved is not { Count: > 0 }) return Standard;
        var map = new Dictionary<PadButton, TvAction>();
        foreach (var (buttonName, actionName) in saved)
        {
            if (Enum.TryParse<PadButton>(buttonName, out var button) && Enum.IsDefined(button)
                && Enum.TryParse<TvAction>(actionName, out var action) && Enum.IsDefined(action))
            {
                map[button] = action;
            }
        }

        return Actions.All(map.ContainsValue) ? new GamepadMap(map) : Standard;
    }

    /// <summary>What the button is called on screen, as an Xbox pad labels it (the layout most pads copy).</summary>
    public static string Name(PadButton button) => button switch
    {
        PadButton.South => "A",
        PadButton.East => "B",
        PadButton.West => "X",
        PadButton.North => "Y",
        PadButton.Back => "View",
        PadButton.Guide => "Guide",
        PadButton.Start => "Menu",
        PadButton.LeftStick => "Left stick press",
        PadButton.RightStick => "Right stick press",
        PadButton.LeftShoulder => "LB",
        PadButton.RightShoulder => "RB",
        PadButton.LeftTrigger => "LT",
        PadButton.RightTrigger => "RT",
        PadButton.DPadUp => "D-pad up",
        PadButton.DPadDown => "D-pad down",
        PadButton.DPadLeft => "D-pad left",
        PadButton.DPadRight => "D-pad right",
        PadButton.Misc1 => "Share",
        PadButton.Touchpad => "Touchpad",
        PadButton.RightPaddle1 => "Paddle P1",
        PadButton.LeftPaddle1 => "Paddle P3",
        PadButton.RightPaddle2 => "Paddle P2",
        PadButton.LeftPaddle2 => "Paddle P4",
        _ => button.ToString(),
    };

    /// <summary>The action's name in the map editor.</summary>
    public static string Title(TvAction action) => action switch
    {
        TvAction.Up => "Up",
        TvAction.Down => "Down",
        TvAction.Left => "Left",
        TvAction.Right => "Right",
        TvAction.Select => "Select",
        TvAction.Back => "Back",
        TvAction.Menu => "Menu",
        TvAction.Home => "Home",
        TvAction.PageLeft => "Page left",
        TvAction.PageRight => "Page right",
        TvAction.SeekBack => "Seek back",
        TvAction.SeekForward => "Seek forward",
        TvAction.Play => "Play now",
        TvAction.Search => "Search",
        _ => action.ToString(),
    };

    /// <summary>What the action does, in the library and in the player.</summary>
    public static string Describe(TvAction action) => action switch
    {
        TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right => "Moves the focus; in the player, seeks or shows the controls",
        TvAction.Select => "Presses what has focus; in the player, play or pause",
        TvAction.Back => "Goes back, closes a menu; in the player, stops",
        TvAction.Menu => "Opens the menu; in the player, the playback settings",
        TvAction.Home => "Library home; from the desktop, the TV interface",
        TvAction.PageLeft => "A screenful left; in the player, the previous chapter",
        TvAction.PageRight => "A screenful right; in the player, the next chapter",
        TvAction.SeekBack => "A screenful up; in the player, seeks back while held",
        TvAction.SeekForward => "A screenful down; in the player, seeks forward while held",
        TvAction.Play => "Plays what has focus; in the player, audio and subtitles",
        TvAction.Search => "Search; in the player, skips the intro or starts the next episode",
        _ => string.Empty,
    };
}
