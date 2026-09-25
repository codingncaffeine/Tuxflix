using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv;

/// <summary>A page view that takes keys or actions itself before the TV interface's own handling.</summary>
internal interface ITvPage : ITvInputTarget
{
    /// <summary>True when the page used the key (typing into search, say).</summary>
    bool HandleKey(KeyEventArgs e) => false;

    /// <summary>What focus starts on when the page opens, before the page's default.</summary>
    Control? InitialFocus() => null;
}

/// <summary>
/// The TV interface: a full-screen frame with tabs along the top, the page under them, a legend of
/// buttons, and the menu and the keyboard in front when they are open. Keys and controller
/// actions arrive through <see cref="TvController"/>; this frame decides what is in front.
/// </summary>
public partial class TvShell : UserControl, ITvInputTarget
{
    /// <summary>The height the TV layout is drawn for; the whole frame scales from it to the screen.</summary>
    public const double DesignHeight = 1080;

    private readonly ConditionalWeakTable<PageViewModel, object> _focusedOn = new();
    private readonly TvController? _controller;
    private PageViewModel? _page;
    private Control? _beforeOverlay;
    private TextBox? _typingInto;
    private DateTime _placeUntil;

    /// <summary>For the XAML loader and the designer.</summary>
    public TvShell()
    {
        InitializeComponent();
        Pages.ContentTemplate = TvPages.Template;
    }

    internal TvShell(TvShellViewModel model, TvController controller)
        : this()
    {
        Model = model;
        _controller = controller;
        DataContext = model;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, e) => { if (e.Source is Control focused) controller.Focus.Remember(focused); }, RoutingStrategies.Bubble, handledEventsToo: true);
        model.Shell.Router.PropertyChanged += OnRouterChanged;
        Pages.LayoutUpdated += (_, _) => PlaceFocus();
        AddHandler(GotFocusEvent, (_, _) => PlaceFrame(), RoutingStrategies.Bubble, handledEventsToo: true);
        FocusLayer.LayoutUpdated += (_, _) => PlaceFrame();
        Keyboard.Done += CloseOverlay;
        Keyboard.PropertyChanged += (_, e) =>
        {
            if (e.Property == OnScreenKeyboard.TextProperty && _typingInto is { } box)
            {
                box.Text = Keyboard.Text;
                KeyboardPreview.Text = Keyboard.Text;
            }
        };
        _page = model.Shell.Router.Current;
        _placeUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    }

    internal TvShellViewModel? Model { get; }

    /// <summary>Where focus moves now: the menu or the keyboard when one is open, else the whole frame.</summary>
    internal Control ActiveScope => !Overlay.IsVisible ? Canvas : MenuPanel.IsVisible ? MenuPanel : KeyboardPanel;

    /// <summary>The menu is open.</summary>
    internal bool IsMenuOpen => Overlay.IsVisible && MenuPanel.IsVisible;

    /// <summary>The on-screen keyboard is open over a text field.</summary>
    internal bool IsKeyboardOpen => Overlay.IsVisible && KeyboardPanel.IsVisible;

    /// <summary>The view of the page showing, when it takes keys or actions itself.</summary>
    private ITvPage? PageView => Pages.GetVisualDescendants().OfType<ITvPage>()
        .FirstOrDefault(view => view is Control control && ReferenceEquals(control.DataContext, Model?.Shell.Router.Current));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Bounds.Height > 0)
        {
            var scale = Math.Max(0.4, Bounds.Height / DesignHeight);
            Scale.LayoutTransform = new ScaleTransform(scale, scale);
        }
    }

    public bool Handle(TvInput input)
    {
        if (Overlay.IsVisible) return InOverlay(input);
        if (input.Phase == TvPhase.Release) return PageView?.Handle(input) == true;

        if (input.Action == TvAction.Menu && Model?.Shell.IsImmersive != true)
        {
            OpenMenu();
            return true;
        }

        if (input is { Action: TvAction.Select, Phase: TvPhase.Press } && FocusEngine.Focused(Canvas) is TextBox box)
        {
            OpenKeyboard(box);
            return true;
        }

        if (PageView?.Handle(input) == true) return true;

        // Back at the first page goes up to the tabs, as a TV app's menu is where back leads.
        if (input is { Action: TvAction.Back, Phase: TvPhase.Press } && Model?.Shell.GoBackCommand.CanExecute(null) != true && _controller is { } controller)
        {
            if (FocusEngine.Focused(Tabs) is null) controller.Focus.FocusFirst(Tabs);
            return true;
        }

        return false;
    }

    /// <summary>Opens the menu over the page, focus on its first row.</summary>
    internal void OpenMenu()
    {
        _beforeOverlay = FocusEngine.Focused(Canvas);
        KeyboardPanel.IsVisible = false;
        MenuPanel.IsVisible = true;
        Overlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => _controller?.Focus.FocusFirst(MenuPanel), DispatcherPriority.Loaded);
    }

    /// <summary>Opens the on-screen keyboard to type into <paramref name="box"/>.</summary>
    internal void OpenKeyboard(TextBox box)
    {
        _beforeOverlay = box;
        _typingInto = null;
        Keyboard.Text = box.Text ?? string.Empty;
        _typingInto = box;
        KeyboardLabel.Text = (Controls.Tip.GetText(box) ?? box.PlaceholderText ?? "TYPE").ToUpperInvariant();
        KeyboardPreview.Text = Keyboard.Text;
        MenuPanel.IsVisible = false;
        KeyboardPanel.IsVisible = true;
        Overlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => _controller?.Focus.FocusFirst(KeyboardPanel), DispatcherPriority.Loaded);
    }

    internal void CloseOverlay()
    {
        Overlay.IsVisible = false;
        MenuPanel.IsVisible = false;
        KeyboardPanel.IsVisible = false;
        _typingInto = null;
        if (_beforeOverlay is { } back && TopLevel.GetTopLevel(back) is not null) back.Focus(NavigationMethod.Directional);
        _beforeOverlay = null;
    }

    private bool InOverlay(TvInput input)
    {
        if (input.Phase == TvPhase.Release) return true;
        switch (input.Action)
        {
            case TvAction.Back or TvAction.Menu when input.Phase == TvPhase.Press:
                CloseOverlay();
                return true;
            case TvAction.Play when IsKeyboardOpen:
                Keyboard.Delete();
                return true;
            case TvAction.Search when IsKeyboardOpen:
                Keyboard.Type(" ");
                return true;
            case TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right or TvAction.Select:
                return false;
            default:
                return true;
        }
    }

    private void OnMenu(object? sender, RoutedEventArgs e) => OpenMenu();

    private void OnMenuChoice(object? sender, RoutedEventArgs e) => CloseOverlay();

    private void OnQuit(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as Window)?.Close();

    /// <summary>Keys for a room: arrows move, Enter presses, Escape goes back; a text field keeps what it types.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _controller is null || TvController.IsSending) return;
        var focused = FocusEngine.Focused(this);
        if (!Overlay.IsVisible && PageView?.HandleKey(e) == true)
        {
            e.Handled = true;
            return;
        }

        if (focused is TextBox && e.Key is not (Key.Up or Key.Down or Key.Escape)) return;
        TvAction? action = e.Key switch
        {
            Key.Up => TvAction.Up,
            Key.Down => TvAction.Down,
            Key.Left => TvAction.Left,
            Key.Right => TvAction.Right,
            Key.Enter => TvAction.Select,
            Key.Escape or Key.Back or Key.BrowserBack => TvAction.Back,
            Key.Apps or Key.F10 => TvAction.Menu,
            Key.BrowserHome => TvAction.Home,
            Key.PageUp => TvAction.SeekBack,
            Key.PageDown => TvAction.SeekForward,
            Key.BrowserSearch or Key.F3 => TvAction.Search,
            _ => null,
        };
        if (action is not { } chosen) return;
        e.Handled = true;
        _controller.Handle(new TvInput(chosen, TvPhase.Press));
    }

    private void OnRouterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Router.Current) || Model is null) return;
        CloseOverlay();

        // The page left keeps what had focus on it, so coming back puts focus there again.
        if (_page is { } left && FocusEngine.Focused(Pages)?.DataContext is { } data && !ReferenceEquals(data, left))
        {
            _focusedOn.AddOrUpdate(left, data);
        }

        _page = Model.Shell.Router.Current;
        _placeUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// A page that has just opened gets focus once it has loaded: where it was when the viewer
    /// left it, else the page's own choice, else its first control. Pages load after they open
    /// (and load again when they are come back to, with new tiles for the same titles), so this
    /// is tried as they lay out, for a few seconds, and a title is known again by its key.
    /// </summary>
    private void PlaceFocus()
    {
        if (_controller is null || DateTime.UtcNow > _placeUntil || Overlay.IsVisible) return;
        var page = Model?.Shell.Router.Current;
        if (page is { IsLoading: true }) return;
        if (Pages.GetVisualDescendants().OfType<UserControl>().FirstOrDefault(c => ReferenceEquals(c.DataContext, page)) is not { } view) return;

        // The viewer got there first.
        if (FocusEngine.Focused(view) is not null)
        {
            _placeUntil = default;
            return;
        }

        var stops = FocusEngine.Stops(view);
        if (stops.Count == 0) return;

        var target = page is not null && _focusedOn.TryGetValue(page, out var data) ? stops.FirstOrDefault(s => Equals(Identity(s.DataContext), Identity(data))) : null;
        target ??= (view as ITvPage)?.InitialFocus();
        if (target is not null) _controller.Focus.FocusOn(target);
        else if (!_controller.Focus.FocusFirst(view)) return;
        _placeUntil = default;
    }

    /// <summary>What a control shows, as something that stays the same when its page loads again.</summary>
    private static object? Identity(object? data) => data switch
    {
        MediaTileViewModel tile => (tile.GetType(), tile.Item.RatingKey),
        EpisodeRowViewModel episode => episode.Episode.RatingKey,
        CastMemberViewModel person => person.Name,
        _ => data,
    };

    /// <summary>
    /// Puts the focus frame over what has focus, following it as rows scroll: tiles, PLAY and the
    /// player's own buttons; the TV interface's pills and keys fill with the accent instead. One
    /// frame above everything, rather than a ring in each control, is never clipped by a row and
    /// reads the same on every kind of tile.
    /// </summary>
    private void PlaceFrame()
    {
        var focused = FocusEngine.Focused(Canvas);
        if (focused is not Button button || !WantsFrame(button) || !Shown(button) || focused.TransformToVisual(FocusLayer) is not { } matrix)
        {
            FocusFrame.IsVisible = false;
            return;
        }

        var around = new Rect(focused.Bounds.Size).TransformToAABB(matrix).Inflate(button.Classes.Contains("tile") ? 6 : 4);
        Avalonia.Controls.Canvas.SetLeft(FocusFrame, around.X);
        Avalonia.Controls.Canvas.SetTop(FocusFrame, around.Y);
        FocusFrame.Width = around.Width;
        FocusFrame.Height = around.Height;
        FocusFrame.IsVisible = true;
    }

    /// <summary>Whether a button shows focus with the frame rather than a fill of its own.</summary>
    private static bool WantsFrame(Button button)
    {
        if (button.Classes.Contains("tile") || button.Classes.Contains("play")) return true;
        var theme = button.Theme;
        return theme is null || !new[] { "TvTab", "TvButton", "TvKey", "TvRow" }.Any(key => ReferenceEquals(theme, Application.Current?.FindResource(key)));
    }

    /// <summary>On screen: no part of the way up is hidden, see-through or out of reach (the player's resting controls).</summary>
    private static bool Shown(Control control) =>
        control.IsEffectivelyVisible && control.GetSelfAndVisualAncestors().OfType<InputElement>().All(v => v.Opacity > 0 && v.IsHitTestVisible);
}
