using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Tuxflix.App.Tv;

/// <summary>Attached settings that shape how focus moves by direction.</summary>
public static class TvFocus
{
    /// <summary>A row (a shelf, a bar): focus coming back into it returns to where it was in it.</summary>
    public static readonly AttachedProperty<bool> IsGroupProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsGroup", typeof(TvFocus));

    /// <summary>
    /// A shelf of tiles: Left or Right at its end stays there, as Leanback's rows and Plex's and
    /// Netflix's shelves do, rather than jumping to a tile of another shelf further along. A bar
    /// (the tabs) lets focus go on to what stands beside it.
    /// </summary>
    public static readonly AttachedProperty<bool> HoldsEndsProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("HoldsEnds", typeof(TvFocus));

    /// <summary>Where focus starts on a page (PLAY on a film's page).</summary>
    public static readonly AttachedProperty<bool> IsDefaultProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsDefault", typeof(TvFocus));

    /// <summary>Never a stop for the D-pad, though the mouse may still use it.</summary>
    public static readonly AttachedProperty<bool> SkipProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Skip", typeof(TvFocus));

    /// <summary>A scroller that puts the focused row at its top, as a TV home keeps the row in use in one place.</summary>
    public static readonly AttachedProperty<bool> PinsRowsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("PinsRows", typeof(TvFocus));

    public static bool GetIsGroup(Control control) => control.GetValue(IsGroupProperty);

    public static void SetIsGroup(Control control, bool value) => control.SetValue(IsGroupProperty, value);

    public static bool GetHoldsEnds(Control control) => control.GetValue(HoldsEndsProperty);

    public static void SetHoldsEnds(Control control, bool value) => control.SetValue(HoldsEndsProperty, value);

    public static bool GetIsDefault(Control control) => control.GetValue(IsDefaultProperty);

    public static void SetIsDefault(Control control, bool value) => control.SetValue(IsDefaultProperty, value);

    public static bool GetSkip(Control control) => control.GetValue(SkipProperty);

    public static void SetSkip(Control control, bool value) => control.SetValue(SkipProperty, value);

    public static bool GetPinsRows(ScrollViewer control) => control.GetValue(PinsRowsProperty);

    public static void SetPinsRows(ScrollViewer control, bool value) => control.SetValue(PinsRowsProperty, value);
}

/// <summary>
/// Moves keyboard focus between the controls under a root by direction, the way a TV interface
/// does: the nearest control that way (<see cref="SpatialNavigator"/>), back to the place a row
/// was left when focus returns to it, scrolling whatever holds the new control until it shows.
/// </summary>
/// <remarks>
/// Runs on the UI thread and only when a key or a button asks: it walks the visible tree once
/// per press. A control is a stop when it can take focus and is something a viewer presses or
/// types into (buttons, text fields, list rows); inside such a control nothing else is a stop,
/// so a tile's own hover button never takes focus from the tile.
/// </remarks>
internal sealed class FocusEngine
{
    /// <summary>Room kept around a focused control when it is scrolled into view: its neighbour shows.</summary>
    private static readonly Thickness Margin = new(64, 48);

    private readonly ConditionalWeakTable<Control, WeakReference<Control>> _remembered = new();

    /// <summary>What has focus under <paramref name="root"/>, if anything there does.</summary>
    public static Control? Focused(Control root)
    {
        var focused = TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() as Control;
        return focused is not null && (ReferenceEquals(focused, root) || root.IsVisualAncestorOf(focused)) ? focused : null;
    }

    /// <summary>Whether a control is a stop for the D-pad.</summary>
    public static bool IsStop(Control control) =>
        control.Focusable
        && control.IsEffectivelyEnabled
        && !TvFocus.GetSkip(control)
        && control is Button or TextBox or Slider or ListBoxItem or ComboBox or TreeViewItem;

    /// <summary>Every stop under <paramref name="root"/> that is shown, in tree order.</summary>
    public static List<Control> Stops(Control root)
    {
        var stops = new List<Control>();
        var pending = new Stack<Visual>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var visual = pending.Pop();
            if (visual is Control control)
            {
                if (!control.IsVisible || !control.IsHitTestVisible || control.Opacity <= 0) continue;
                if (!ReferenceEquals(control, root) && IsStop(control))
                {
                    stops.Add(control);
                    continue;
                }
            }

            var children = visual.GetVisualChildren().ToList();
            for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }

        return stops;
    }

    /// <summary>Where <paramref name="control"/> is, in <paramref name="root"/>'s coordinates.</summary>
    public static Rect? Place(Control control, Visual root) =>
        control.TransformToVisual(root) is { } matrix ? new Rect(control.Bounds.Size).TransformToAABB(matrix) : null;

    /// <summary>Notes what took focus, for every row it is in.</summary>
    public void Remember(Control focused)
    {
        foreach (var group in focused.GetVisualAncestors().OfType<Control>().Where(TvFocus.GetIsGroup))
        {
            _remembered.AddOrUpdate(group, new WeakReference<Control>(focused));
        }
    }

    /// <summary>Moves focus one step; false when nothing lies that way.</summary>
    public bool Move(Control root, NavDirection direction)
    {
        var current = Focused(root);
        if (current is null || ReferenceEquals(current, root)) return FocusFirst(root);
        if (Place(current, root) is not { } from) return FocusFirst(root);

        var target = Held(current, direction, Pick(root, current, from, direction));
        if (target is null && ScrollFurther(current, direction))
        {
            // A long grid only builds the rows it shows: bring the next one in, then look again.
            root.UpdateLayout();
            if (Place(current, root) is { } moved) target = Held(current, direction, Pick(root, current, moved, direction));
        }

        if (target is null) return false;
        FocusOn(Returning(current, target) ?? target);
        return true;
    }

    /// <summary>Focuses the page's default control, else its first stop in reading order.</summary>
    public bool FocusFirst(Control root)
    {
        var stops = Stops(root);
        var first = stops.FirstOrDefault(TvFocus.GetIsDefault)
                    ?? stops.Select(s => (Stop: s, At: Place(s, root)))
                        .Where(s => s.At is not null)
                        .OrderBy(s => Math.Round(s.At!.Value.Top / 16))
                        .ThenBy(s => s.At!.Value.Left)
                        .Select(s => s.Stop)
                        .FirstOrDefault();
        if (first is null) return false;
        FocusOn(first);
        return true;
    }

    /// <summary>Focuses <paramref name="control"/> as the D-pad does, and scrolls it into view.</summary>
    public void FocusOn(Control control)
    {
        control.Focus(NavigationMethod.Directional);
        Remember(control);
        ScrollIntoView(control);
    }

    /// <summary>
    /// Scrolls every scroller that holds <paramref name="control"/> just far enough to show it with
    /// a margin; a scroller that pins rows puts the control's row at its top instead.
    /// </summary>
    public static void ScrollIntoView(Control control)
    {
        foreach (var scroller in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (Place(control, scroller) is not { } at) continue;
            var viewport = scroller.Viewport;
            var extent = scroller.Extent;
            var offset = scroller.Offset;
            var x = offset.X;
            var y = offset.Y;

            if (extent.Width > viewport.Width + 0.5)
            {
                if (at.Left - Margin.Left < 0) x += at.Left - Margin.Left;
                else if (at.Right + Margin.Right > viewport.Width) x += Math.Min(at.Right + Margin.Right - viewport.Width, at.Left - Margin.Left);
            }

            if (extent.Height > viewport.Height + 0.5)
            {
                if (TvFocus.GetPinsRows(scroller))
                {
                    var row = control.GetVisualAncestors().OfType<Control>().TakeWhile(a => !ReferenceEquals(a, scroller)).LastOrDefault(TvFocus.GetIsGroup);
                    var top = (row is not null ? Place(row, scroller) : at)?.Top ?? at.Top;
                    y += top - Margin.Top;
                }
                else if (at.Top - Margin.Top < 0)
                {
                    y += at.Top - Margin.Top;
                }
                else if (at.Bottom + Margin.Bottom > viewport.Height)
                {
                    y += Math.Min(at.Bottom + Margin.Bottom - viewport.Height, at.Top - Margin.Top);
                }
            }

            var target = new Vector(
                Math.Clamp(x, 0, Math.Max(0, extent.Width - viewport.Width)),
                Math.Clamp(y, 0, Math.Max(0, extent.Height - viewport.Height)));
            if (target != offset) scroller.Offset = target;
        }
    }

    private static Control? Pick(Control root, Control current, Rect from, NavDirection direction)
    {
        var stops = Stops(root);
        stops.Remove(current);
        var places = new List<Rect>(stops.Count);
        var kept = new List<Control>(stops.Count);
        foreach (var stop in stops)
        {
            if (Place(stop, root) is not { Width: > 0, Height: > 0 } at) continue;
            places.Add(at);
            kept.Add(stop);
        }

        var index = SpatialNavigator.Pick(from, direction, places);
        return index < 0 ? null : kept[index];
    }

    /// <summary>The target, unless a move sideways would take focus out of a shelf that holds its ends.</summary>
    private static Control? Held(Control current, NavDirection direction, Control? target)
    {
        if (target is null || direction is NavDirection.Up or NavDirection.Down) return target;
        var shelf = current.GetVisualAncestors().OfType<Control>().FirstOrDefault(TvFocus.GetHoldsEnds);
        return shelf is null || shelf.IsVisualAncestorOf(target) ? target : null;
    }

    /// <summary>
    /// When the move enters a row focus has been in before, where it was in that row: a TV home
    /// keeps each shelf's place, as Plex's and Netflix's do.
    /// </summary>
    private Control? Returning(Control current, Control target)
    {
        foreach (var group in target.GetVisualAncestors().OfType<Control>().Where(TvFocus.GetIsGroup))
        {
            if (group.IsVisualAncestorOf(current)) return null;
            if (_remembered.TryGetValue(group, out var weak) && weak.TryGetTarget(out var kept)
                && group.IsVisualAncestorOf(kept) && kept.IsEffectivelyVisible && IsStop(kept))
            {
                return kept;
            }
        }

        return null;
    }

    /// <summary>Scrolls the nearest scroller that can go further that way by about one control.</summary>
    private static bool ScrollFurther(Control current, NavDirection direction)
    {
        foreach (var scroller in current.GetVisualAncestors().OfType<ScrollViewer>())
        {
            var room = direction switch
            {
                NavDirection.Down => scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y,
                NavDirection.Up => scroller.Offset.Y,
                NavDirection.Right => scroller.Extent.Width - scroller.Viewport.Width - scroller.Offset.X,
                _ => scroller.Offset.X,
            };
            if (room < 1) continue;
            var step = Math.Min(room, (direction is NavDirection.Up or NavDirection.Down ? current.Bounds.Height : current.Bounds.Width) + 24);
            scroller.Offset = direction switch
            {
                NavDirection.Down => scroller.Offset.WithY(scroller.Offset.Y + step),
                NavDirection.Up => scroller.Offset.WithY(scroller.Offset.Y - step),
                NavDirection.Right => scroller.Offset.WithX(scroller.Offset.X + step),
                _ => scroller.Offset.WithX(scroller.Offset.X - step),
            };
            return true;
        }

        return false;
    }
}
