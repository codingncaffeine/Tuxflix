using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

/// <summary>
/// The photo viewer's view: the picture at the screen's size, zoomed with the wheel (about the
/// pointer), a double click or the keys, and dragged about while zoomed; its controls fade while
/// the pointer rests.
/// </summary>
public partial class PhotoViewerPage : UserControl
{
    private const double MaxZoom = 8;
    private static readonly TimeSpan ChromeLinger = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _chromeTimer;
    private TopLevel? _top;
    private PhotoViewerPageViewModel? _model;
    private WindowState? _beforeFullScreen;
    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragFrom;

    public PhotoViewerPage()
    {
        InitializeComponent();
        _chromeTimer = new DispatcherTimer(ChromeLinger, DispatcherPriority.Background, (_, _) => HideChrome());
        Stage.PointerMoved += OnStagePointerMoved;
        Stage.PointerPressed += OnStagePressed;
        Stage.PointerReleased += (_, _) => _dragFrom = null;
        Stage.PointerWheelChanged += OnWheel;
        Stage.DoubleTapped += OnDoubleTapped;
        Stage.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) ApplyZoom();
        };
        FullScreenButton.Click += (_, _) => ToggleFullScreen();
    }

    /// <summary>How far the photo is zoomed: 1 fits the window.</summary>
    public double Zoom => _zoom;

    private PhotoViewerPageViewModel? Model => DataContext as PhotoViewerPageViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = Model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The server scales each photo to the screen it is shown on, before the picture asks for it.
        _top = TopLevel.GetTopLevel(this);
        if (_top?.Screens?.ScreenFromTopLevel(_top) is { } screen)
        {
            Picture.DecodeWidth = Math.Min(3840, screen.Bounds.Width / screen.Scaling);
            Picture.DecodeHeight = Math.Min(2160, screen.Bounds.Height / screen.Scaling);
        }
        else
        {
            Picture.DecodeWidth = 1920;
            Picture.DecodeHeight = 1080;
        }

        base.OnAttachedToVisualTree(e);
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        ShowChrome();
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
        // A new photo starts fitted to the window.
        if (e.PropertyName == nameof(PhotoViewerPageViewModel.Index)) ZoomTo(1, default);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || e.Source is TextBox || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        var centre = new Point(Stage.Bounds.Width / 2, Stage.Bounds.Height / 2);
        switch (e.Key)
        {
            case Key.Right or Key.PageDown:
                model.NextCommand.Execute(null);
                break;
            case Key.Left or Key.PageUp:
                model.PreviousCommand.Execute(null);
                break;
            case Key.Space:
                model.ToggleSlideshowCommand.Execute(null);
                break;
            case Key.I:
                model.ToggleInfoCommand.Execute(null);
                break;
            case Key.F:
                ToggleFullScreen();
                break;
            case Key.OemPlus or Key.Add:
                ZoomTo(_zoom * 1.25, centre);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomTo(_zoom / 1.25, centre);
                break;
            case Key.D0 or Key.NumPad0:
                ZoomTo(1, centre);
                break;
            case Key.Escape:
                if (_top is Window { WindowState: WindowState.FullScreen } && _beforeFullScreen is not null) LeaveFullScreen();
                else model.CloseCommand.Execute(null);
                break;
            default:
                return;
        }

        ShowChrome();
        e.Handled = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomTo(_zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(Stage));
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => ZoomTo(_zoom > 1.01 ? 1 : 2.5, e.GetPosition(Stage));

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_zoom > 1.01 && e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed) _dragFrom = e.GetPosition(Stage);
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        ShowChrome();
        if (_dragFrom is not { } from) return;
        var at = e.GetPosition(Stage);
        _pan += at - from;
        _dragFrom = at;
        ApplyZoom();
    }

    /// <summary>Zooms to <paramref name="zoom"/>, keeping the point under <paramref name="about"/> where it is.</summary>
    private void ZoomTo(double zoom, Point about)
    {
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        var anchor = new Vector(about.X, about.Y);
        _pan = anchor - ((anchor - _pan) * (zoom / _zoom));
        _zoom = zoom;
        ApplyZoom();
    }

    // The picture never leaves a gap at an edge it could cover: panning stops at its sides.
    private void ApplyZoom()
    {
        var size = Stage.Bounds.Size;
        if (_zoom <= 1.001)
        {
            _zoom = 1;
            _pan = default;
        }
        else
        {
            _pan = new Vector(Math.Clamp(_pan.X, size.Width * (1 - _zoom), 0), Math.Clamp(_pan.Y, size.Height * (1 - _zoom), 0));
        }

        Picture.RenderTransform = _zoom == 1 ? null : new MatrixTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y));
        Stage.Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    private void ShowChrome()
    {
        Chrome.Opacity = 1;
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void HideChrome()
    {
        _chromeTimer.Stop();
        Chrome.Opacity = 0;
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

    private void LeaveFullScreen()
    {
        if (_beforeFullScreen is not { } before) return;
        _beforeFullScreen = null;
        if (_top is Window { WindowState: WindowState.FullScreen } window) window.WindowState = before;
    }
}
