using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class PlayerPage : UserControl
{
    private static readonly TimeSpan IdleBeforeHiding = TimeSpan.FromSeconds(2.8);

    private readonly DispatcherTimer _idle;
    private TopLevel? _top;
    private int _menusOpen;

    public PlayerPage()
    {
        InitializeComponent();
        _idle = new DispatcherTimer { Interval = IdleBeforeHiding };
        _idle.Tick += (_, _) => HideControls();
        foreach (var button in BottomBar.GetLogicalDescendants().OfType<Button>())
        {
            if (button.Flyout is { } flyout) KeepControlsWhileOpen(flyout);
        }

        Video.Ready += () => Model?.SurfaceReady();
        Stage.PointerMoved += (_, _) => ShowControls();
        Stage.PointerPressed += OnStagePressed;

        // The seek bar follows playback except while the viewer holds it; letting go seeks.
        Seek.AddHandler(PointerPressedEvent, (_, _) => { if (Model is { } m) m.IsScrubbing = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);

        // Over the bar, and while dragging it: what is at that place.
        Seek.AddHandler(PointerMovedEvent, OnSeekPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerExitedEvent, (_, _) => { if (Model is { IsScrubbing: false } m) m.Previews?.Hide(); }, RoutingStrategies.Bubble, handledEventsToo: true);
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
        UseTvLayout();
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
        if (!_tvLayout && _top is Window { WindowState: WindowState.FullScreen } window) window.WindowState = WindowState.Normal;
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
            case Key.I:
                TogglePictureInPicture();
                break;
            case Key.Escape when _top is MainWindow { IsPictureInPicture: true } small:
                small.ExitPictureInPicture();
                break;
            case Key.Enter when model.ShowsUpNext:
                model.PlayNextCommand.Execute(null);
                break;
            case Key.Enter when model.ShowsSkip:
                model.SkipCommand.Execute(null);
                break;
            case Key.Escape when !_tvLayout && _top is Window { WindowState: WindowState.FullScreen } window:
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
        if (!ReferenceEquals(e.Source, Video) && !ReferenceEquals(e.Source, PipBar)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // The small window moves with the picture; a double press brings it back to full size.
        if (_top is MainWindow { IsPictureInPicture: true } small)
        {
            if (e.ClickCount == 2) small.ExitPictureInPicture();
            else small.BeginMoveDrag(e);
            return;
        }

        if (!ReferenceEquals(e.Source, Video)) return;
        if (e.ClickCount == 2) ToggleFullScreen();
        Model?.TogglePauseCommand.Execute(null);
    }

    private void OnFullScreen(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnPictureInPicture(object? sender, RoutedEventArgs e) => TogglePictureInPicture();

    /// <summary>The window becomes a small one showing only the picture, or comes back.</summary>
    public void TogglePictureInPicture()
    {
        if (_top is not MainWindow window || Model is not { } model) return;
        if (window.IsPictureInPicture)
        {
            window.ExitPictureInPicture();
            return;
        }

        var media = model.Item.Media?.FirstOrDefault();
        var aspect = media?.AspectRatio ?? (media is { Width: > 0, Height: > 0 } ? (double)media.Width.Value / media.Height.Value : 16.0 / 9);
        window.EnterPictureInPicture(model, aspect);
    }

    /// <summary>The playback menu, built from the player's state each time it opens.</summary>
    private void OnSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Opens the playback settings menu over its button; returns it (the player probe pictures it).</summary>
    internal MenuFlyout? OpenSettings()
    {
        if (Model is not { } model) return null;
        var menu = SettingsMenu(model);
        KeepControlsWhileOpen(menu);
        ShowControls();
        menu.ShowAt(SettingsButton);
        return menu;
    }

    /// <summary>The controls stay up while one of their menus is open, as every player keeps them.</summary>
    private void KeepControlsWhileOpen(FlyoutBase flyout)
    {
        flyout.Opened += (_, _) => _menusOpen++;
        flyout.Closed += (_, _) =>
        {
            _menusOpen = Math.Max(0, _menusOpen - 1);
            ShowControls();
        };
    }

    private static MenuFlyout SettingsMenu(PlayerPageViewModel model)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };

        menu.Items.Add(Submenu($"Quality ({model.Quality.Label})",
        [
            Label(model.StreamSummary),
            .. model.StreamDetail.Length > 0 ? [Label(model.StreamDetail)] : Array.Empty<Control>(),
            new Separator(),
            .. model.Qualities.Select(q => Radio(q.Label, q == model.Quality, () => model.SetQuality(q))),
        ]));

        menu.Items.Add(Submenu("Speed", [.. new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 }.Select(v =>
            Radio(v == 1 ? "Normal" : string.Create(CultureInfo.InvariantCulture, $"{v:0.##}×"), Math.Abs(model.Speed - v) < 0.01, () => model.Speed = v))]));

        menu.Items.Add(Submenu("Picture",
        [
            Radio("Fit the window", model.Fit == PictureFit.Fit, () => model.Fit = PictureFit.Fit),
            Radio("Fill the window", model.Fit == PictureFit.Fill, () => model.Fit = PictureFit.Fill),
            Radio("Stretch to the window", model.Fit == PictureFit.Stretch, () => model.Fit = PictureFit.Stretch),
            new Separator(),
            Radio("The file's own shape", model.Aspect is null, () => model.Aspect = null),
            .. new[] { "16:9", "4:3", "2.39:1", "1.85:1" }.Select(a => Radio(a, model.Aspect == a, () => model.Aspect = a)),
        ]));

        menu.Items.Add(Submenu("Subtitles",
        [
            .. new (string Name, double Scale)[] { ("Small", 0.8), ("Normal", 1), ("Large", 1.3), ("Larger", 1.6) }
                .Select(size => Radio(size.Name, Math.Abs(model.SubtitleScale - size.Scale) < 0.01, () => model.SubtitleScale = size.Scale)),
            new Separator(),
            Check("Raised above the bottom", model.SubtitlesRaised, () => model.SubtitlesRaised = !model.SubtitlesRaised),
            new Separator(),
            Label(PlayerPageViewModel.DelayText(model.SubtitleDelay)),
            Item("Show 0.1 s earlier", () => model.SubtitleDelay = Math.Round(model.SubtitleDelay - 0.1, 2)),
            Item("Show 0.1 s later", () => model.SubtitleDelay = Math.Round(model.SubtitleDelay + 0.1, 2)),
            Item("Back in step", () => model.SubtitleDelay = 0, enabled: model.SubtitleDelay != 0),
        ]));

        menu.Items.Add(Submenu("Sound",
        [
            Check("Night mode: quiet dialogue up, loud moments down", model.NightMode, () => model.NightMode = !model.NightMode),
            Check("Stereo only: surround mixed down to two speakers", model.Stereo, () => model.Stereo = !model.Stereo),
            Check("Passthrough: surround sent to an AV receiver as it is", model.Passthrough, () => model.Passthrough = !model.Passthrough),
            .. model.Passthrough ? [Label("The receiver decodes it: no volume or night mode here")] : Array.Empty<Control>(),
            new Separator(),
            Label(PlayerPageViewModel.DelayText(model.AudioDelay)),
            Item("Play 0.1 s earlier", () => model.AudioDelay = Math.Round(model.AudioDelay - 0.1, 2)),
            Item("Play 0.1 s later", () => model.AudioDelay = Math.Round(model.AudioDelay + 0.1, 2)),
            Item("Back in step", () => model.AudioDelay = 0, enabled: model.AudioDelay != 0),
        ]));

        menu.Items.Add(Submenu($"Sleep timer ({model.SleepLabel})",
        [
            Radio("Off", model.SleepMinutes == 0, () => model.SetSleep(0)),
            .. new[] { 15, 30, 45, 60, 90 }.Select(m => Radio($"In {m} minutes", model.SleepMinutes == m, () => model.SetSleep(m))),
            Radio("At the end of this", model.SleepMinutes == -1, () => model.SetSleep(-1)),
        ]));

        menu.Items.Add(new Separator());
        menu.Items.Add(Check("Skip intros by themselves", model.AutoSkipIntro, () => model.AutoSkipIntro = !model.AutoSkipIntro));
        menu.Items.Add(Check("Skip credits by themselves", model.AutoSkipCredits, () => model.AutoSkipCredits = !model.AutoSkipCredits));
        menu.Items.Add(Check("Play the next episode", model.AutoPlayNext, () => model.AutoPlayNext = !model.AutoPlayNext));
        return menu;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A line that says how things stand; nothing to press.</summary>
    private static MenuItem Label(string text) => new() { Header = text, IsEnabled = false };

    private static MenuItem Check(string header, bool on, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        return item;
    }

    private static MenuItem Radio(string header, bool on, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = on;
        return item;
    }

    private static MenuItem Submenu(string header, IEnumerable<Control> items)
    {
        var item = new MenuItem { Header = header };
        foreach (var child in items) item.Items.Add(child);
        return item;
    }

    private void ToggleFullScreen()
    {
        if (_top is MainWindow { IsPictureInPicture: true } small) small.ExitPictureInPicture();
        if (_top is not Window window) return;
        window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    /// <summary>Shows the preview for the place under the pointer, or the thumb's while it is dragged.</summary>
    private void OnSeekPointer(object? sender, PointerEventArgs e)
    {
        if (Model is not { Previews: { } previews } model || Seek.GetVisualDescendants().OfType<Track>().FirstOrDefault() is not { } track) return;
        var point = e.GetPosition(track);
        var seconds = model.IsScrubbing ? model.SeekValue : Math.Clamp(track.ValueFromPoint(point), 0, model.SeekMaximum);
        previews.Show(seconds);
        PlacePreview(track, seconds / Math.Max(1, model.SeekMaximum));
    }

    /// <summary>Centres the preview over <paramref name="fraction"/> of the bar, kept inside the window, just above the bar.</summary>
    internal void PlacePreview(Track track, double fraction)
    {
        var origin = track.TranslatePoint(default, this) ?? default;
        var width = PreviewBox.Bounds.Width > 0 ? PreviewBox.Bounds.Width : 250;
        var x = origin.X + (Math.Clamp(fraction, 0, 1) * track.Bounds.Width) - (width / 2);
        PreviewBox.Margin = new Thickness(Math.Clamp(x, 12, Math.Max(12, Bounds.Width - width - 12)), 0, 0, Math.Max(0, Bounds.Height - origin.Y + 10));
    }

    private void EndScrub()
    {
        if (Model is not { IsScrubbing: true } model) return;
        model.IsScrubbing = false;
        if (!Seek.IsPointerOver) model.Previews?.Hide();
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

        // Paused, dragging, a menu open, or resting on a control: the controls stay.
        if (Model is null or { IsPaused: true } or { IsScrubbing: true } || _menusOpen > 0 || BottomBar.IsPointerOver || TopBar.IsPointerOver) return;
        Overlay.Classes.Add("hidden");
        Cursor = new Cursor(StandardCursorType.None);
    }
}
