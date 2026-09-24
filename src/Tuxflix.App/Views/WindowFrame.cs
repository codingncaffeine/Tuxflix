using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Tuxflix.App.Views;

/// <summary>
/// What a window has to do for itself once the system frame is off: resize from its edges, move
/// from its title bar, and maximise on a double-click there. Adapted from Mailbox.
/// </summary>
internal static class WindowFrame
{
    private const double ResizeMargin = 6;

    private static readonly (WindowEdge Edge, StandardCursorType Cursor)[] EdgeCursors =
    [
        (WindowEdge.NorthWest, StandardCursorType.TopLeftCorner),
        (WindowEdge.NorthEast, StandardCursorType.TopRightCorner),
        (WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner),
        (WindowEdge.SouthEast, StandardCursorType.BottomRightCorner),
        (WindowEdge.North, StandardCursorType.TopSide),
        (WindowEdge.South, StandardCursorType.BottomSide),
        (WindowEdge.West, StandardCursorType.LeftSide),
        (WindowEdge.East, StandardCursorType.RightSide),
    ];

    public static void Apply(Window window)
    {
        window.PointerMoved += (_, e) =>
        {
            window.Cursor = EdgeAt(window, e.GetPosition(window)) is { } edge
                ? new Cursor(EdgeCursors.First(c => c.Edge == edge).Cursor)
                : Cursor.Default;
        };

        // Tunnel, so an edge press starts a resize before whatever sits under the edge sees it.
        window.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(window, e.GetPosition(window)) is not { } edge) return;
            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Makes <paramref name="bar"/> move the window; a double-click toggles maximise.</summary>
    public static void Drags(Window window, Control bar)
    {
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed || e.Handled) return;
            if (e.Source is Visual source && IsInteractive(source, bar)) return;

            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            window.BeginMoveDrag(e);
        };
    }

    private static bool IsInteractive(Visual source, Control bar)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, bar); visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or Thumb) return true;
        }

        return false;
    }

    private static WindowEdge? EdgeAt(Window window, Point p)
    {
        if (window.WindowState != WindowState.Normal) return null;
        var west = p.X <= ResizeMargin;
        var east = p.X >= window.Bounds.Width - ResizeMargin;
        var north = p.Y <= ResizeMargin;
        var south = p.Y >= window.Bounds.Height - ResizeMargin;
        return (north, south, west, east) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }
}
