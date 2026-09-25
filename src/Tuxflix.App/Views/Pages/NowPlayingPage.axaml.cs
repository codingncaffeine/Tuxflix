using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class NowPlayingPage : UserControl
{
    private static readonly TimeSpan ChromeLinger = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandsOffLyrics = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _chromeTimer;
    private TopLevel? _top;
    private NowPlayingPageViewModel? _model;
    private WindowState? _beforeFullScreen;
    private DateTime _lyricsTouched = DateTime.MinValue;

    public NowPlayingPage()
    {
        InitializeComponent();
        Seek.AddHandler(PointerPressedEvent, (_, _) => { if (Model?.Music is { } m) m.IsScrubbing = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(Seek.Value), RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(Seek.Value), RoutingStrategies.Bubble, handledEventsToo: true);
        Waveform.ScrubStarted += (_, _) => { if (Model?.Music is { } m) m.IsScrubbing = true; };
        Waveform.ScrubEnded += (_, _) => EndScrub(Waveform.Value);

        // Over the visualizer the controls show while the pointer moves, and go when it rests.
        _chromeTimer = new DispatcherTimer(ChromeLinger, DispatcherPriority.Background, (_, _) => HideChrome());
        VisualizerLayer.PointerMoved += (_, _) => ShowChrome();
        Visualizer.PointerPressed += OnVisualizerPressed;
        VisualizerTitleBand.PointerPressed += OnTitleBandPressed;
        FullScreenButton.Click += (_, _) => ToggleFullScreen();

        // A hand on the lyrics stops them following the music for a few seconds.
        LyricsScroll.AddHandler(PointerWheelChangedEvent, (_, _) => TouchLyrics(), RoutingStrategies.Tunnel, handledEventsToo: true);
        LyricsScroll.AddHandler(PointerPressedEvent, (_, _) => TouchLyrics(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private NowPlayingPageViewModel? Model => DataContext as NowPlayingPageViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = Model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _chromeTimer.Stop();
        LeaveFullScreen();
        _top?.RemoveHandler(KeyDownEvent, OnKey);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingPageViewModel.IsVisualizerOn) when Model is { } model:
                if (model.IsVisualizerOn)
                {
                    ShowChrome();
                }
                else
                {
                    _chromeTimer.Stop();
                    LeaveFullScreen();
                    Cursor = Cursor.Default;
                }

                break;
            case nameof(NowPlayingPageViewModel.CurrentLyric) or nameof(NowPlayingPageViewModel.IsLyricsPanel):
                Dispatcher.UIThread.Post(() => ScrollToLyric(animate: e.PropertyName == nameof(NowPlayingPageViewModel.CurrentLyric)), DispatcherPriority.Background);
                break;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Typing in a text box keeps its keys.
        if (Model is not { } model || e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
        var music = model.Music;
        switch (e.Key)
        {
            case Key.Space or Key.K:
                music.TogglePauseCommand.Execute(null);
                break;
            case Key.N:
                music.NextCommand.Execute(null);
                break;
            case Key.P:
                music.PreviousCommand.Execute(null);
                break;
            case Key.S:
                music.ToggleShuffleCommand.Execute(null);
                break;
            case Key.R:
                music.CycleRepeatCommand.Execute(null);
                break;
            case Key.M:
                music.ToggleMuteCommand.Execute(null);
                break;
            case Key.Left or Key.J:
                music.SeekTo(Math.Max(0, music.Position - 10));
                break;
            case Key.Right or Key.L:
                music.SeekTo(music.Position + 10);
                break;
            case Key.Up:
                music.ChangeVolume(5);
                break;
            case Key.Down:
                music.ChangeVolume(-5);
                break;
            case Key.V:
                model.ToggleVisualizerCommand.Execute(null);
                break;
            case Key.F when model.IsVisualizerOn:
                ToggleFullScreen();
                break;
            case Key.Escape when model.IsVisualizerOn:
                if (_top is Window { WindowState: WindowState.FullScreen }) LeaveFullScreen();
                else model.ToggleVisualizerCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void EndScrub(double value)
    {
        if (Model?.Music is not { IsScrubbing: true } music) return;
        music.IsScrubbing = false;
        music.SeekTo(value);
    }

    // The band along the top is the title bar the visualizer hides: it moves the window and a double
    // click maximises it. Full screen has no window to move, so there it is picture like the rest.
    private void OnTitleBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_top is not Window { WindowState: not WindowState.FullScreen } window)
        {
            OnVisualizerPressed(sender, e);
            return;
        }

        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
        WindowFrame.MoveOrMaximise(window, e);
        e.Handled = true;
    }

    // Clicking the picture moves on to the next mode, as clicking Winamp's analyser did.
    private void OnVisualizerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Visualizer).Properties.IsLeftButtonPressed) return;
        Model?.NextVisualizerModeCommand.Execute(null);
        ShowChrome();
        e.Handled = true;
    }

    private void ShowChrome()
    {
        VisualizerChrome.Opacity = 1;
        Cursor = Cursor.Default;
        _chromeTimer.Stop();
        if (Model?.IsVisualizerOn == true) _chromeTimer.Start();
    }

    private void HideChrome()
    {
        _chromeTimer.Stop();
        if (Model?.IsVisualizerOn != true) return;
        VisualizerChrome.Opacity = 0;
        Cursor = new Cursor(StandardCursorType.None);
    }

    private void ToggleFullScreen()
    {
        if (_top is not Window window) return;
        if (window.WindowState == WindowState.FullScreen)
        {
            LeaveFullScreen();
            return;
        }

        _beforeFullScreen = window.WindowState;
        window.WindowState = WindowState.FullScreen;
    }

    // Only a full screen this page asked for is undone by it.
    private void LeaveFullScreen()
    {
        if (_beforeFullScreen is not { } before) return;
        _beforeFullScreen = null;
        if (_top is Window { WindowState: WindowState.FullScreen } window) window.WindowState = before;
    }

    private void TouchLyrics()
    {
        _lyricsTouched = DateTime.UtcNow;
        LyricsScroll.Transitions = null;
    }

    /// <summary>Brings the line sung into the middle of the lyrics, gliding there unless the listener just scrolled.</summary>
    private void ScrollToLyric(bool animate)
    {
        if (Model is not { IsLyricsPanel: true, CurrentLyric: >= 0 } model) return;
        if (DateTime.UtcNow - _lyricsTouched < HandsOffLyrics) return;
        if (LyricsList.ContainerFromIndex(model.CurrentLyric) is not Control line) return;
        if (line.TranslatePoint(new Point(0, line.Bounds.Height / 2), LyricsList) is not { } middle) return;
        var extent = LyricsScroll.Extent.Height;
        var viewport = LyricsScroll.Viewport.Height;
        var target = Math.Clamp(middle.Y + 4 - (viewport * 0.4), 0, Math.Max(0, extent - viewport));
        LyricsScroll.Transitions = animate
            ? [new VectorTransition { Property = ScrollViewer.OffsetProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = new CubicEaseOut() }]
            : null;
        LyricsScroll.Offset = new Vector(0, target);
    }
}
