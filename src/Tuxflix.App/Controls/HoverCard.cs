using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;

namespace Tuxflix.App.Controls;

/// <summary>
/// The card a title shows beside its tile when the pointer rests on it, as Steam's library does:
/// <c>ui:HoverCard.For="{Binding}"</c> on a tile. One card serves every tile; it takes neither
/// focus nor clicks, and goes as the pointer leaves, presses or scrolls, or a key is pressed.
/// </summary>
/// <remarks>
/// It shows after the pointer has rested a moment, and sooner while moving from one tile to the
/// next with a card already up, as tooltips do. Never in the TV interface, which has no pointer.
/// </remarks>
public static class HoverCard
{
    /// <summary>How long the pointer rests on a tile before its card shows.</summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(650);

    /// <summary>How long it rests on the next tile when a card showed a moment ago.</summary>
    public static readonly TimeSpan BetweenDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How soon after a card went a new one counts as moving between tiles.</summary>
    private static readonly TimeSpan Warm = TimeSpan.FromMilliseconds(400);

    public static readonly AttachedProperty<MediaTileViewModel?> ForProperty =
        AvaloniaProperty.RegisterAttached<Control, MediaTileViewModel?>("For", typeof(HoverCard));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(HoverCard));

    private static Flyout? _card;
    private static DispatcherTimer? _timer;
    private static Control? _resting;
    private static Control? _showingAt;
    private static Control? _pressed;
    private static long _goneAt;

    static HoverCard()
    {
        // A press anywhere closes the card by itself (it dismisses lightly), and a pressed tile
        // shows no card again until the pointer is off it: the end of a press's capture can report
        // the pointer leaving and coming back while it never moved. A scroll over the tile and a
        // key anywhere in the window do not close it by themselves, so they are listened for.
        ForProperty.Changed.AddClassHandler<Control>((tile, _) =>
        {
            if (tile.GetValue(HookedProperty)) return;
            tile.SetValue(HookedProperty, true);
            tile.PointerEntered += (_, _) => Rest(tile);
            tile.PointerExited += (_, e) =>
            {
                if (ReferenceEquals(_pressed, tile) && !new Rect(tile.Bounds.Size).Contains(e.GetPosition(tile))) _pressed = null;
                Leave(tile);
            };
            tile.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
            {
                _pressed = tile;
                Leave(tile);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            tile.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => Leave(tile), handledEventsToo: true);
            tile.DetachedFromVisualTree += (_, _) => Leave(tile);
        });
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) =>
        {
            if (_resting is { } tile) Leave(tile);
            if (_showingAt is { } shown) Leave(shown);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static MediaTileViewModel? GetFor(Control control) => control?.GetValue(ForProperty);

    public static void SetFor(Control control, MediaTileViewModel? value) => control?.SetValue(ForProperty, value);

    /// <summary>The card showing now, for probes and tests; null when none is up.</summary>
    internal static HoverCardViewModel? Showing => _card is { IsOpen: true, Content: HoverCardView { DataContext: HoverCardViewModel card } } ? card : null;

    /// <summary>The tile the card showing now belongs to.</summary>
    internal static Control? ShowingAt => _card?.IsOpen == true ? _showingAt : null;

    /// <summary>How many times a card has shown: a test can tell a card that came and went from none.</summary>
    internal static int ShownCount { get; private set; }

    private static void Rest(Control tile)
    {
        if (GetFor(tile) is not { Shell.IsTv: false }) return;

        // A pressed tile shows no card until the pointer has been off it; any other tile clears that.
        if (ReferenceEquals(_pressed, tile)) return;
        _pressed = null;
        _resting = tile;
        var warm = _card?.IsOpen == true || Stopwatch.GetElapsedTime(_goneAt) < Warm;
        _timer ??= new DispatcherTimer(ShowDelay, DispatcherPriority.Normal, (_, _) => Due());
        _timer.Stop();
        _timer.Interval = warm ? BetweenDelay : ShowDelay;
        _timer.Start();
    }

    private static void Due()
    {
        _timer?.Stop();
        if (_resting is not { IsPointerOver: true } tile || GetFor(tile) is not { } model || TopLevel.GetTopLevel(tile) is null) return;
        _card ??= NewCard();
        ((HoverCardView)_card.Content!).DataContext = new HoverCardViewModel(model);
        if (_card.IsOpen) _card.Hide();
        _showingAt = tile;
        _card.ShowAt(tile);
        ShownCount++;
    }

    private static void Leave(Control tile)
    {
        if (ReferenceEquals(_resting, tile))
        {
            _resting = null;
            _timer?.Stop();
        }

        if (_card?.IsOpen == true && ReferenceEquals(_showingAt, tile))
        {
            _card.Hide();
            _showingAt = null;
            _goneAt = Stopwatch.GetTimestamp();
        }
    }

    private static Flyout NewCard()
    {
        var card = new Flyout
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            ShowMode = FlyoutShowMode.Transient,
            OverlayDismissEventPassThrough = true,
            HorizontalOffset = 10,
            Content = new HoverCardView(),
        };
        card.FlyoutPresenterClasses.Add("hover-card");
        return card;
    }
}
