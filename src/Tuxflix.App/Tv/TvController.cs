using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv;

/// <summary>A page or an overlay that takes some actions itself, before the general handling.</summary>
internal interface ITvInputTarget
{
    /// <summary>True when the action was used.</summary>
    bool Handle(TvInput input);
}

/// <summary>
/// Carries out actions from a controller, a remote or the arrow keys, on the UI thread: an open
/// menu gets the keys it understands, then the overlay or page in front gets its say, then focus
/// moves, presses, goes back, pages or plays.
/// </summary>
internal sealed class TvController(TopLevel top, ShellViewModel shell, Func<Control?> scope, Func<ITvInputTarget?> target)
{
    public FocusEngine Focus { get; } = new();

    /// <summary>After each action is carried out; tests and probes time the path with it.</summary>
    public event Action<TvInput>? Handled;

    public void Handle(TvInput input)
    {
        try
        {
            Carry(input);
        }
        finally
        {
            Handled?.Invoke(input);
        }
    }

    private void Carry(TvInput input)
    {
        if (OpenPopup() is { } popup)
        {
            if (input.Phase != TvPhase.Release) InPopup(popup, input.Action);
            return;
        }

        if (target() is { } front && front.Handle(input)) return;
        if (input.Phase == TvPhase.Release || scope() is not { } root) return;

        switch (input.Action)
        {
            case TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right:
                Focus.Move(root, Direction(input.Action));
                break;
            case TvAction.Select when input.Phase == TvPhase.Press:
                Press(FocusEngine.Focused(root));
                break;
            case TvAction.Back when input.Phase == TvPhase.Press:
                if (shell.GoBackCommand.CanExecute(null)) shell.GoBackCommand.Execute(null);
                break;
            case TvAction.Home when input.Phase == TvPhase.Press:
                if (shell.IsTv) shell.GoHome();
                else shell.IsTv = true;
                break;
            case TvAction.PageLeft or TvAction.PageRight:
                Page(root, input.Action == TvAction.PageLeft ? NavDirection.Left : NavDirection.Right);
                break;
            case TvAction.SeekBack or TvAction.SeekForward:
                Page(root, input.Action == TvAction.SeekBack ? NavDirection.Up : NavDirection.Down);
                break;
            case TvAction.Play when input.Phase == TvPhase.Press:
                PlayFocused(FocusEngine.Focused(root));
                break;
            case TvAction.Search when input.Phase == TvPhase.Press:
                shell.OpenSearch();
                break;
        }
    }

    public static NavDirection Direction(TvAction action) => action switch
    {
        TvAction.Up => NavDirection.Up,
        TvAction.Down => NavDirection.Down,
        TvAction.Left => NavDirection.Left,
        _ => NavDirection.Right,
    };

    /// <summary>Presses a control as the keyboard does: Enter on a button opens its menu or runs its command.</summary>
    public static void Press(Control? control)
    {
        if (control is null) return;
        SendKey(control, Key.Enter);
    }

    /// <summary>
    /// A key being sent by <see cref="SendKey"/>: the TV frame's own key handling lets it through
    /// to the control, or Enter would be read as Select again, and again.
    /// </summary>
    public static bool IsSending => t_sending;

    [ThreadStatic]
    private static bool t_sending;

    /// <summary>A key down and up on <paramref name="control"/>, as a keyboard would send them.</summary>
    public static void SendKey(Control control, Key key)
    {
        var outer = t_sending;
        t_sending = true;
        try
        {
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control });
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = control });
        }
        finally
        {
            t_sending = outer;
        }
    }

    /// <summary>A screenful that way: as many steps as the scroller around the focus shows controls.</summary>
    private void Page(Control root, NavDirection direction)
    {
        var current = FocusEngine.Focused(root);
        if (current is null)
        {
            Focus.FocusFirst(root);
            return;
        }

        var across = direction is NavDirection.Left or NavDirection.Right;
        var scroller = current.GetVisualAncestors().OfType<ScrollViewer>()
            .FirstOrDefault(s => across ? s.Extent.Width > s.Viewport.Width + 0.5 : s.Extent.Height > s.Viewport.Height + 0.5);
        var size = across ? current.Bounds.Width : current.Bounds.Height;
        var view = scroller is null ? 0 : across ? scroller.Viewport.Width : scroller.Viewport.Height;
        var steps = size > 0 && view > 0 ? Math.Max(1, (int)(view / size) - 1) : 3;
        for (var i = 0; i < steps; i++)
        {
            if (!Focus.Move(root, direction)) break;
        }
    }

    /// <summary>X on a film or an episode plays it from where it was left; on anything else it opens it.</summary>
    private void PlayFocused(Control? control)
    {
        switch (control?.DataContext)
        {
            case MediaTileViewModel { Item.Type: "movie" or "episode" } tile:
                tile.PlayCommand.Execute(null);
                break;
            case EpisodeRowViewModel episode:
                shell.Play(episode.Episode, resume: episode.HasProgress);
                break;
            default:
                Press(control);
                break;
        }
    }

    /// <summary>The popup in front (a menu, a flyout), if one is open; tooltips do not count.</summary>
    private Control? OpenPopup() =>
        OverlayLayer.GetOverlayLayer(top)?.Children.OfType<OverlayPopupHost>()
            .LastOrDefault(host => host.IsVisible && host.Content is Control content and not ToolTip && content.IsVisible);

    /// <summary>
    /// A menu understands the arrow keys, Enter and Escape itself; any other popup gets focus
    /// moved inside it, and Back closes it.
    /// </summary>
    private void InPopup(Control host, TvAction action)
    {
        var inside = FocusEngine.Focused(host);
        var content = (host as ContentControl)?.Content as Control ?? host;
        if (content is MenuBase or MenuFlyoutPresenter)
        {
            var key = action switch
            {
                TvAction.Up => Key.Up,
                TvAction.Down => Key.Down,
                TvAction.Left => Key.Left,
                TvAction.Right => Key.Right,
                TvAction.Select => Key.Enter,
                TvAction.Back or TvAction.Menu => Key.Escape,
                _ => Key.None,
            };
            if (key != Key.None) SendKey(inside ?? content, key);
            return;
        }

        switch (action)
        {
            case TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right:
                Focus.Move(host, Direction(action));
                break;
            case TvAction.Select:
                Press(inside);
                break;
            case TvAction.Back or TvAction.Menu:
                if (host.Parent is Popup popup) popup.IsOpen = false;
                break;
        }
    }
}
