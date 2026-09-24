using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class PlayerPage : UserControl
{
    private static readonly TimeSpan IdleBeforeHiding = TimeSpan.FromSeconds(2.8);

    private readonly DispatcherTimer _idle;
    private TopLevel? _top;

    public PlayerPage()
    {
        InitializeComponent();
        _idle = new DispatcherTimer { Interval = IdleBeforeHiding };
        _idle.Tick += (_, _) => HideControls();

        Video.Ready += () => Model?.SurfaceReady();
        Stage.PointerMoved += (_, _) => ShowControls();
        Stage.PointerPressed += OnStagePressed;

        // The seek bar follows playback except while the viewer holds it; letting go seeks.
        Seek.AddHandler(PointerPressedEvent, (_, _) => { if (Model is { } m) m.IsScrubbing = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private PlayerPageViewModel? Model => DataContext as PlayerPageViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Video.IsReady) Model?.SurfaceReady();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);

        // Keys are taken before any focused button sees them: Space must pause, not press the last
        // button clicked.
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        if (_top is Window window) WindowFrame.Drags(window, TopBar);
        ShowControls();
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnKey);
        _idle.Stop();
        if (_top is Window { WindowState: WindowState.FullScreen } window) window.WindowState = WindowState.Normal;
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        switch (e.Key)
        {
            case Key.Space or Key.K:
                model.TogglePauseCommand.Execute(null);
                break;
            case Key.Left or Key.J:
                model.SeekBy(-10);
                break;
            case Key.Right or Key.L:
                model.SeekBy(30);
                break;
            case Key.Up:
                model.ChangeVolume(5);
                break;
            case Key.Down:
                model.ChangeVolume(-5);
                break;
            case Key.M:
                model.ToggleMuteCommand.Execute(null);
                break;
            case Key.F or Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape when _top is Window { WindowState: WindowState.FullScreen } window:
                window.WindowState = WindowState.Normal;
                break;
            case Key.Escape:
                model.LeaveCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
        ShowControls();
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        // Only presses on the picture itself: the controls handle their own.
        if (!ReferenceEquals(e.Source, Video) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleFullScreen();
        Model?.TogglePauseCommand.Execute(null);
    }

    private void OnFullScreen(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (_top is not Window window) return;
        window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    private void EndScrub()
    {
        if (Model is not { IsScrubbing: true } model) return;
        model.IsScrubbing = false;
        model.SeekTo(Seek.Value);
    }

    private void ShowControls()
    {
        Overlay.Classes.Remove("hidden");
        Cursor = Cursor.Default;
        _idle.Stop();
        _idle.Start();
    }

    private void HideControls()
    {
        _idle.Stop();

        // Paused, dragging, or resting on a control: the controls stay.
        if (Model is null or { IsPaused: true } or { IsScrubbing: true } || BottomBar.IsPointerOver || TopBar.IsPointerOver) return;
        Overlay.Classes.Add("hidden");
        Cursor = new Cursor(StandardCursorType.None);
    }
}
