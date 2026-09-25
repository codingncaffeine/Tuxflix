using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tuxflix.App.Classic;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel? _shell;
    private readonly SettingsStore? _settings;
    private SkinLibrary? _skins;
    private ClassicPlayerWindow? _classic;
    private bool _openingClassic;
    private bool _quitting;

    /// <summary>How the window was before it became the small picture-in-picture one; null when it is not.</summary>
    private (WindowState State, PixelPoint Position, double Width, double Height, double MinWidth, double MinHeight, bool Topmost)? _beforePip;
    private PlayerPageViewModel? _pipPage;

    /// <summary>For the XAML loader and the designer.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ShellViewModel shell, SettingsStore settings)
        : this()
    {
        _shell = shell;
        _settings = settings;
        DataContext = shell;

        if (settings.Current.UseSystemTitleBar)
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            CaptionButtons.IsVisible = false;
            Frame.CornerRadius = default;
            Ring.IsVisible = false;
        }
        else
        {
            WindowFrame.Apply(this);
            WindowFrame.Drags(this, TitleBar);
        }

        RestorePlacement(settings.Current.Window);
        Rail.Width = Math.Clamp(settings.Current.RailWidth, 220, 440);
        RailResizer.DragDelta += OnRailResize;

        AddHandler(PointerPressedEvent, OnMouseButtons, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
        SearchBox.KeyDown += OnSearchKey;
        Closing += (_, _) => RememberPlacement();
        Opened += (_, _) => Platform.DesktopIdentity.Apply(this);
        shell.CompactPlayerRequested += () => _ = ShowClassicAsync();

        // Whatever ends the playback (the file's end, back, the next page) ends the small window too.
        shell.Router.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Router.Current) && _pipPage is not null && !ReferenceEquals(shell.Router.Current, _pipPage))
            {
                ExitPictureInPicture();
            }
        };

        AttachTv(shell);
    }

    public bool IsPictureInPicture => _beforePip is not null;

    /// <summary>
    /// Makes the window a small borderless one showing only the picture, in the bottom right corner
    /// and above the other windows (under Plasma on Wayland, KWin is asked to do both).
    /// </summary>
    internal void EnterPictureInPicture(PlayerPageViewModel page, double aspect)
    {
        if (_beforePip is not null) return;
        _beforePip = (WindowState, Position, Width, Height, MinWidth, MinHeight, Topmost);
        _pipPage = page;
        page.IsPictureInPicture = true;
        WindowState = WindowState.Normal;
        MinWidth = 240;
        MinHeight = 120;
        Width = 480;
        Height = Math.Round(480 / Math.Clamp(double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9, 1.2, 2.4));
        Topmost = true;
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Position = new PixelPoint(area.Right - (int)((Width + 24) * scale), area.Bottom - (int)((Height + 24) * scale));
        }

        Log.Info("Picture in picture.");
        if (Platform.KeepAbove.ViaKWin) _ = AskKWinAsync(above: true);
    }

    internal void ExitPictureInPicture()
    {
        if (_beforePip is not { } before) return;
        _beforePip = null;
        if (_pipPage is { } page) page.IsPictureInPicture = false;
        _pipPage = null;
        Topmost = before.Topmost;
        MinWidth = before.MinWidth;
        MinHeight = before.MinHeight;
        Width = before.Width;
        Height = before.Height;
        Position = before.Position;
        WindowState = before.State;
        Log.Info("Back from picture in picture.");
        if (Platform.KeepAbove.ViaKWin && before.State == WindowState.Normal) _ = AskKWinAsync(above: false);
    }

    /// <summary>KWin places the window once its new size has reached the compositor.</summary>
    private async Task AskKWinAsync(bool above)
    {
        if (_shell is null) return;
        var folder = Path.Combine(_shell.Paths.Cache, "kwin");
        await Task.Delay(250);
        await Task.Run(() => Platform.KeepAbove.SetAsync(above, folder));
    }

    /// <summary>The compact classic player, while it stands in for this window.</summary>
    public ClassicPlayerWindow? Classic => _classic;

    /// <summary>Brings the window forward when another launch hands over to this one.</summary>
    public void BringForward()
    {
        // A launch while the compact player is up asks for Tuxflix itself: the full window.
        if (_classic is not null)
        {
            _classic.Close();
            return;
        }

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Opens the compact classic player and steps aside for it; closing the classic window brings
    /// this one back. The skin loads (and, the first time, downloads) on workers before either
    /// window changes, so a slow network never leaves the screen empty.
    /// </summary>
    public async Task ShowClassicAsync()
    {
        if (_classic is not null)
        {
            _classic.Activate();
            return;
        }

        if (_openingClassic || _shell?.Music is not { } music) return;
        _openingClassic = true;
        try
        {
            _skins ??= new SkinLibrary(Path.Combine(_shell.Paths.Data, "skins"));
            var skin = await _skins.LoadAsync(_shell.Settings.Classic.Skin);
            var window = _classic = new ClassicPlayerWindow(_shell, music, _skins, skin, Icon);
            window.Closed += (_, _) =>
            {
                _classic = null;
                if (_quitting) return;
                Show();
                Activate();
            };
            window.QuitRequested += () =>
            {
                _quitting = true;
                window.Close();
                Close();
            };
            window.Show();
            Hide();
            Log.Info($"Compact player opened with the {skin.Name} skin.");
        }
        finally
        {
            _openingClassic = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || _settings?.Current.UseSystemTitleBar == true) return;

        // A maximised or full-screen window has square corners and no ring: it meets the screen edge.
        var framed = WindowState == WindowState.Normal;
        Frame.CornerRadius = framed ? new CornerRadius(10) : default;
        Ring.IsVisible = framed;
        MaximizeIcon.Data = this.FindResource(framed ? "Icon.Square.Bold" : "Icon.Copy.Bold") as Geometry;
        Tip.SetText(MaximizeButton, framed ? "Maximize" : "Restore");
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _skins?.Dispose();
    }

    private void OnRailResize(object? sender, VectorEventArgs e)
    {
        Rail.Width = Math.Clamp(Rail.Width + e.Vector.X, 220, 440);
        if (_settings is not null) _settings.Current.RailWidth = Rail.Width;
    }

    // The mouse's side buttons go back and forward, as they do in Steam and every browser.
    private void OnMouseButtons(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsXButton1Pressed && _shell?.GoBackCommand.CanExecute(null) == true)
        {
            _shell.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (point.IsXButton2Pressed && _shell?.GoForwardCommand.CanExecute(null) == true)
        {
            _shell.GoForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Enter searches at once; Escape empties the box and leaves it.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (_shell is null) return;
        if (e.Key == Key.Enter)
        {
            _shell.SearchNowCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _shell.SearchText = string.Empty;
            Frame.Focus();
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (e.Key)
        {
            case Key.Left when alt && _shell.GoBackCommand.CanExecute(null):
                _shell.GoBackCommand.Execute(null);
                break;
            case Key.Right when alt && _shell.GoForwardCommand.CanExecute(null):
                _shell.GoForwardCommand.Execute(null);
                break;
            case Key.Home when alt:
            case Key.D1 when ctrl:
                _shell.GoHomeCommand.Execute(null);
                break;
            case Key.D2 when ctrl:
                _shell.ShowDiscoverCommand.Execute(null);
                break;
            case Key.D3 when ctrl:
                _shell.ShowActivityCommand.Execute(null);
                break;
            case Key.K when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.Q when ctrl:
                Close();
                break;
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                WindowState = WindowState.Normal;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight) return;
        Width = placement.Width;
        Height = placement.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(placement.X, placement.Y);
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void RememberPlacement()
    {
        // Full screen for the TV interface is not a size to come back to.
        if (_settings is null || IsTvShowing) return;

        // Closed while small: the next start opens as the window was before.
        if (_beforePip is { } before)
        {
            _settings.Current.Window = new WindowPlacement { X = before.Position.X, Y = before.Position.Y, Width = before.Width, Height = before.Height, Maximized = before.State == WindowState.Maximized };
            _settings.Save();
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        var previous = _settings.Current.Window;
        _settings.Current.Window = new WindowPlacement
        {
            // A maximised window keeps the size it will restore to.
            X = maximized && previous is not null ? previous.X : Position.X,
            Y = maximized && previous is not null ? previous.Y : Position.Y,
            Width = maximized && previous is not null ? previous.Width : Bounds.Width,
            Height = maximized && previous is not null ? previous.Height : Bounds.Height,
            Maximized = maximized,
        };
        _settings.Save();
    }
}
