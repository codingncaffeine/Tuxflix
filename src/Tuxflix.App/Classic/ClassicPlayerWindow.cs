using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tuxflix.App.Music;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Classic;

/// <summary>
/// The compact player's window: borderless, exactly the size of the skin, moved by dragging the
/// skin itself. Closing it (or ejecting) goes back to the full window.
/// </summary>
public sealed class ClassicPlayerWindow : Window
{
    private const string MuseumUrl = "https://skins.webamp.org/";

    private readonly ShellViewModel _shell;
    private readonly MusicPlayer _music;
    private readonly SkinLibrary _skins;
    private readonly ClassicPlayerView _view;
    private readonly LayoutTransformControl _scaler;
    private readonly Panel _cached;
    private IReadOnlyList<SkinFile> _skinFiles = [];
    private int _loading;

    public ClassicPlayerWindow(ShellViewModel shell, MusicPlayer music, SkinLibrary skins, ClassicSkin skin, WindowIcon? icon)
    {
        _shell = shell;
        _music = music;
        _skins = skins;
        Title = "Tuxflix";
        Icon = icon;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Black;
        Topmost = shell.Settings.Classic.AlwaysOnTop;

        _view = new ClassicPlayerView(music, shell.Settings.Classic, skin);
        _view.CloseRequested += Close;
        _view.EjectRequested += Close;
        _view.MinimizeRequested += () => WindowState = WindowState.Minimized;
        _view.DragRequested += BeginMoveDrag;
        _view.MenuRequested += () => Show(MainMenu());
        _view.PresetsRequested += () => Show(PresetsMenu());
        _view.SettingsChanged += () =>
        {
            Topmost = _shell.Settings.Classic.AlwaysOnTop;
            ApplyScale();
            _shell.SaveSettings();
        };
        ScalingChanged += (_, _) =>
        {
            _view.ScreenScaleChanged();
            ApplyScale();
        };

        // Between two whole sizes the view draws at the larger one into a cached layer, which the
        // compositor scales down with bilinear filtering on its own thread. Three levels, each for
        // a reason: the scaling transform lands on the outer panel; the cache sits on the inner one,
        // which has no transform of its own (the layer would be drawn at the scaled size) and holds
        // the view as a child (a cache redraws for changes below it, not for its own drawing); and
        // the smoothing is set on the outer panel alone. The layer is drawn with a fresh context,
        // so there it smooths only the layer's blit; inherited by the view, it would win over the
        // view's own nearest-neighbour setting and blur the sprites inside the layer.
        _cached = new Panel { Children = { _view } };
        var scaled = new Panel { Children = { _cached } };
        RenderOptions.SetBitmapInterpolationMode(scaled, BitmapInterpolationMode.LowQuality);
        _scaler = new LayoutTransformControl { Child = scaled };
        Content = _scaler;
        ApplyScale();
    }

    /// <summary>Quit chosen from the menu: the whole application, not only this window.</summary>
    public event Action? QuitRequested;

    public ClassicPlayerView View => _view;

    /// <summary>Whether the size falls between two whole ones, so the drawing goes through the smoothing layer.</summary>
    public bool IsSmoothing => _cached.CacheMode is not null;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Platform.DesktopIdentity.Apply(this);
        _view.CanStayOnTop = !WindowingBackend.IsWayland(this);
        _view.Focus();
        _ = RefreshSkinsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // After the last frame that drew it has gone, the skin's own sheets go too.
        var skin = _view.Skin;
        DispatcherTimer.RunOnce(() => _skins.Release(skin), TimeSpan.FromSeconds(2));
    }

    /// <summary>Loads a skin from the library and puts it on, keeping the choice for next time.</summary>
    public async Task UseSkinAsync(string? fileName)
    {
        var ticket = ++_loading;
        var skin = await _skins.LoadAsync(fileName);
        if (ticket != _loading)
        {
            _skins.Release(skin);
            return;
        }

        var old = _view.Skin;
        _view.Skin = skin;
        _shell.Settings.Classic.Skin = fileName is null or SkinLibrary.BaseFileName ? null : fileName;
        _shell.SaveSettings();
        DispatcherTimer.RunOnce(() => _skins.Release(old), TimeSpan.FromSeconds(2));
    }

    private async Task RefreshSkinsAsync() => _skinFiles = await _skins.ListAsync();

    private void ApplyScale()
    {
        var shrink = _view.Shrink;
        var whole = shrink == 1;
        _scaler.LayoutTransform = whole ? null : new ScaleTransform(shrink, shrink);
        if (whole != _cached.CacheMode is null) _cached.CacheMode = whole ? null : new BitmapCache();
    }

    private void Show(ContextMenu menu)
    {
        menu.Placement = PlacementMode.Pointer;
        menu.Open(_view);
    }

    private ContextMenu MainMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Back to Tuxflix", Close));
        menu.Items.Add(new Separator());
        menu.Items.Add(Check("Equalizer", _view.ShowEqualizer, () => _view.ShowEqualizer = !_view.ShowEqualizer));
        menu.Items.Add(Check("Playlist", _view.ShowPlaylist, () => _view.ShowPlaylist = !_view.ShowPlaylist));
        menu.Items.Add(SkinsMenu());
        menu.Items.Add(Submenu("Size",
        [
            .. new (string Name, double Scale)[] { ("Original size", 1), ("One and a half", 1.5), ("Double size", 2) }
                .Select(size => Radio(size.Name, Math.Abs(_view.Scale - size.Scale) < 0.01, () => _view.Scale = size.Scale)),
        ]));
        menu.Items.Add(Submenu("Visualizer",
        [
            Radio("Spectrum analyser", _view.Visualizer == ClassicVis.Spectrum, () => _view.Visualizer = ClassicVis.Spectrum),
            Radio("Oscilloscope", _view.Visualizer == ClassicVis.Scope, () => _view.Visualizer = ClassicVis.Scope),
            Radio("Off", _view.Visualizer == ClassicVis.Off, () => _view.Visualizer = ClassicVis.Off),
        ]));
        menu.Items.Add(Check("Show time remaining", _view.ShowRemaining, () => _view.ShowRemaining = !_view.ShowRemaining));
        if (_view.CanStayOnTop) menu.Items.Add(Check("Always on top", _view.AlwaysOnTop, () => _view.AlwaysOnTop = !_view.AlwaysOnTop));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Tuxflix", () => QuitRequested?.Invoke()));
        return menu;
    }

    /// <summary>The museum in a window of its own; a skin added there joins the Skins menu at once.</summary>
    private void OpenMuseum()
    {
        var museum = new SkinMuseumWindow(new SkinMuseumViewModel(new SkinMuseum(), _skins, added => _ = RefreshSkinsAsync())) { Icon = Icon };
        museum.Show();
    }

    private MenuItem SkinsMenu()
    {
        var current = _shell.Settings.Classic.Skin ?? SkinLibrary.BaseFileName;
        var items = new List<Control>();
        items.AddRange(_skinFiles.Select(file => Radio(file.Label, file.FileName == current, () => _ = UseSkinAsync(file.FileName))));
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Item("Load skin file…", () => _ = LoadSkinFileAsync()));
        items.Add(Item("Browse the Winamp Skin Museum", OpenMuseum));
        items.Add(Item("Find more skins online…", () => _shell.OpenUrl(MuseumUrl)));
        return Submenu("Skins", items);
    }

    private ContextMenu PresetsMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Flat", () => Apply(EqPresets.Flat)));
        menu.Items.Add(new Separator());
        foreach (var preset in EqPresets.BuiltIn) menu.Items.Add(Item(preset.Name, () => Apply(preset)));
        return menu;

        void Apply(EqPreset preset)
        {
            _music.SetEqualizer(preset.Preamp, preset.Bands);
            if (!_music.IsEqualizerOn) _music.SetEqualizer(true);
            _view.Flash("EQ: " + preset.Name, 2);
        }
    }

    private async Task LoadSkinFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a classic skin",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Classic skins") { Patterns = ["*.wsz", "*.zip"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            var name = await _skins.ImportAsync(path);
            await UseSkinAsync(name);
            await RefreshSkinsAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Classic skin {Path.GetFileName(path)} was not added: {ex.Message}");
            _view.Flash("Not a classic skin", 3);
        }
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

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
}
