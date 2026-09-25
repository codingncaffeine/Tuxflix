using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Tuxflix.App.Music;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Settings;
using Tuxflix.Player.Analysis;
using S = Tuxflix.App.Classic.ClassicSprites;

namespace Tuxflix.App.Classic;

/// <summary>
/// The compact classic player: the main window, the equalizer and the playlist of a Winamp 2
/// skin, drawn from the skin's own sheets at two or three times their size, pixel for pixel.
/// </summary>
/// <remarks>
/// Every part is a sprite cut from a sheet and drawn with nearest-neighbour scaling, so any of
/// the classic skins looks as its author drew it. The small analyser reads the shared
/// <see cref="VisualizerFeed"/>; everything else follows the <see cref="MusicPlayer"/>. Drawing
/// records a few dozen image and rectangle operations thirty times a second; the heavy work (the
/// decode and the FFT) happens on the feed's own thread.
/// </remarks>
public sealed class ClassicPlayerView : Control
{
    private const int PlaylistRow = 13;
    private const int MarqueeChars = 31;
    private const string Separator = "  ***  ";

    private readonly MusicPlayer _music;
    private readonly ClassicPlayerSettings _settings;
    private readonly Dictionary<Color, IBrush> _brushes = [];
    private ClassicSkin _skin;
    private DispatcherTimer? _timer;
    private IDisposable? _feed;
    private WriteableBitmap? _visBackground;
    private WriteableBitmap? _visGradient;
    private Region _pressed;
    private Region _hover;
    private double? _dragVolume;
    private double? _dragBalance;
    private double? _dragPosition;
    private double? _dragEq;
    private int _eqBand;
    private int _miniButton;
    private Vector _gripOffset;
    private Cursor? _gripCursor;
    private Part _activePart = Part.Main;
    private string? _flash;
    private DateTime _flashUntil;
    private int _marqueeStep;
    private DateTime _lastStep = DateTime.UtcNow;
    private int _scroll;
    private int _selected = -1;

    public ClassicPlayerView(MusicPlayer music, ClassicPlayerSettings settings, ClassicSkin skin)
    {
        _music = music;
        _settings = settings;
        _skin = skin;
        Focusable = true;
        ClipToBounds = true;
        BuildVisBitmaps();
        _music.EqualizerChanged += InvalidateVisual;
    }

    public ClassicSkin Skin
    {
        get => _skin;
        set
        {
            _skin = value;
            BuildVisBitmaps();
            InvalidateVisual();
        }
    }

    /// <summary>The close button: back to the full window.</summary>
    public event Action? CloseRequested;

    public event Action? MinimizeRequested;

    /// <summary>Eject: back to the library to choose something.</summary>
    public event Action? EjectRequested;

    /// <summary>The options button, the "I" of the clutter bar or a right click: the menu.</summary>
    public event Action? MenuRequested;

    /// <summary>The equalizer's presets button.</summary>
    public event Action? PresetsRequested;

    /// <summary>The window drag should start: the title bar or any part that is not a control.</summary>
    public event Action<PointerPressedEventArgs>? DragRequested;

    /// <summary>A setting changed here (size, a window shown, the analyser, the time): the host saves it.</summary>
    public event Action? SettingsChanged;

    public bool AlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            _settings.AlwaysOnTop = value;
            Changed();
        }
    }

    public const double MinScale = 1;

    public const double MaxScale = 2;

    /// <summary>The size, in screen pixels per skin pixel: 1 is the original size, 2 double size, and anything between.</summary>
    public double Scale
    {
        get => Math.Clamp(_settings.Scale, MinScale, MaxScale);
        set
        {
            _settings.Scale = Math.Clamp(value, MinScale, MaxScale);
            Changed(relayout: true);
        }
    }

    /// <summary>
    /// Screen pixels per skin pixel the view draws at: always a whole number, so every skin pixel
    /// is the same size. A size between two whole ones draws at the next one up, and the host
    /// scales that drawing down smoothly by <see cref="Shrink"/> (the "sharp bilinear" of
    /// emulators): even pixels, softened by at most one screen pixel at their edges.
    /// </summary>
    public int DrawScale
    {
        get
        {
            var device = Scale * ScreenScale;
            var whole = Math.Round(device);
            return (int)Math.Max(1, Math.Abs(device - whole) < 0.01 ? whole : Math.Ceiling(device));
        }
    }

    /// <summary>What the host scales the drawing by: exactly 1 at a whole size, below 1 between two.</summary>
    public double Shrink
    {
        get
        {
            var shrink = Scale * ScreenScale / DrawScale;
            return Math.Abs(shrink - 1) < 0.01 ? 1 : shrink;
        }
    }

    /// <summary>Logical units per skin pixel on screen, after the host's scaling.</summary>
    public double LogicalUnit => DrawScale * Shrink / ScreenScale;

    private double ScreenScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    public bool ShowEqualizer
    {
        get => _settings.ShowEqualizer;
        set
        {
            _settings.ShowEqualizer = value;
            Changed(relayout: true);
        }
    }

    public bool ShowPlaylist
    {
        get => _settings.ShowPlaylist;
        set
        {
            _settings.ShowPlaylist = value;
            Changed(relayout: true);
        }
    }

    /// <summary>The main window rolled up to its title bar.</summary>
    public bool Shaded
    {
        get => _settings.Shaded;
        set
        {
            _settings.Shaded = value;
            Changed(relayout: true);
        }
    }

    public bool ShowRemaining
    {
        get => _settings.ShowRemaining;
        set
        {
            _settings.ShowRemaining = value;
            Changed();
        }
    }

    public ClassicVis Visualizer
    {
        get => Enum.TryParse<ClassicVis>(_settings.Visualizer, out var v) ? v : ClassicVis.Spectrum;
        set
        {
            _settings.Visualizer = value.ToString();
            UpdateFeed();
            Changed();
        }
    }

    private ClassicVis Vis => Visualizer;

    /// <summary>Logical units per skin pixel in the view's own drawing: a whole number of screen pixels.</summary>
    private double Unit => DrawScale / ScreenScale;

    private int MainHeight => Shaded ? S.ShadeHeight : S.MainHeight;

    private int LogicalHeight => MainHeight + (ShowEqualizer ? S.EqHeight : 0) + (ShowPlaylist ? S.PlaylistHeight : 0);

    private int EqTop => MainHeight;

    private int PlaylistTop => MainHeight + (ShowEqualizer ? S.EqHeight : 0);

    protected override Size MeasureOverride(Size availableSize) => new(S.MainWidth * Unit, LogicalHeight * Unit);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
        UpdateFeed();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _feed?.Dispose();
        _feed = null;
        _music.EqualizerChanged -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    private void Tick()
    {
        if (DateTime.UtcNow - _lastStep >= TimeSpan.FromMilliseconds(220))
        {
            _lastStep = DateTime.UtcNow;
            _marqueeStep++;
        }

        InvalidateVisual();
    }

    private void UpdateFeed()
    {
        var wanted = Vis != ClassicVis.Off;
        if (wanted && _feed is null) _feed = _music.Visuals.Listen();
        else if (!wanted && _feed is not null)
        {
            _feed.Dispose();
            _feed = null;
        }
    }

    private void Changed(bool relayout = false)
    {
        if (relayout) InvalidateMeasure();
        InvalidateVisual();
        SettingsChanged?.Invoke();
    }

    /// <summary>The window moved to a screen of another scale: whole screen pixels again.</summary>
    public void ScreenScaleChanged() => InvalidateMeasure();

    // ================================================================ drawing ==

    public override void Render(DrawingContext context)
    {
        using var options = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None, EdgeMode = EdgeMode.Aliased });
        using var scale = context.PushTransform(Matrix.CreateScale(Unit, Unit));
        if (Shaded) DrawShade(context);
        else DrawMain(context);
        if (ShowEqualizer) DrawEqualizer(context, EqTop);
        if (ShowPlaylist) DrawPlaylist(context, PlaylistTop);
    }

    private void DrawMain(DrawingContext c)
    {
        if (_skin.Sheet(S.Main) is null)
        {
            // No skin at all (offline, before the base skin arrived): a plain frame that still works.
            c.FillRectangle(Brush(Color.FromRgb(0x24, 0x24, 0x33)), new Rect(0, 0, S.MainWidth, S.MainHeight));
            c.FillRectangle(Brush(Colors.Black), S.VisArea);
        }

        Draw(c, S.MainBackground, 0, 0);
        Draw(c, IsActive(Part.Main) ? S.TitleBarActive : S.TitleBar, 0, 0);
        Draw(c, Pressed(Region.Options) ? S.OptionsButtonDown : S.OptionsButton, S.OptionsArea);
        Draw(c, Pressed(Region.Minimize) ? S.MinimizeButtonDown : S.MinimizeButton, S.MinimizeArea);
        Draw(c, Pressed(Region.Shade) ? S.ShadeButtonDown : S.ShadeButton, S.ShadeArea);
        Draw(c, Pressed(Region.Close) ? S.CloseButtonDown : S.CloseButton, S.CloseArea);

        Draw(c, S.ClutterBar, S.ClutterArea);
        if (AlwaysOnTop) Draw(c, S.ClutterA, S.ClutterAArea);
        if (Scale >= MaxScale - 0.01) Draw(c, S.ClutterD, S.ClutterDArea);

        var playing = _music.HasCurrent && !_music.IsPaused;
        Draw(c, playing ? S.PlayingIndicator : _music.IsHalted || !_music.HasCurrent ? S.StoppedIndicator : S.PausedIndicator, S.StatusArea);
        Draw(c, playing ? S.WorkingIndicator : S.NotWorkingIndicator, S.WorkArea);

        if (_music.HasCurrent && !_music.IsHalted) DrawTime(c);
        DrawVisualizer(c);
        DrawMarquee(c);
        DrawMediaInfo(c);

        // Volume: a strip of 28 frames, one per level, and the thumb over it.
        var volume = _dragVolume ?? Math.Clamp(_music.Volume / 100, 0, 1);
        var frame = Math.Clamp((int)Math.Round(volume * 28) - 1, 0, 27);
        DrawPart(c, S.Volume, new Rect(0, frame * 15, 68, 13), S.VolumeArea.X, S.VolumeArea.Y);
        Draw(c, Pressed(Region.Volume) ? S.VolumeThumbDown : S.VolumeThumb, S.VolumeArea.X + (volume * 51), S.VolumeArea.Y + 1);

        var balance = _dragBalance ?? _music.Balance;
        var balanceFrame = Math.Clamp((int)Math.Floor(Math.Abs(balance) * 27), 0, 27);
        DrawPart(c, S.Balance, new Rect(9, balanceFrame * 15, 38, 13), S.BalanceArea.X, S.BalanceArea.Y);
        Draw(c, Pressed(Region.Balance) ? S.BalanceThumbDown : S.BalanceThumb, S.BalanceArea.X + ((balance + 1) / 2 * 24), S.BalanceArea.Y + 1);

        Draw(c, ShowEqualizer ? (Pressed(Region.EqButton) ? S.EqOnDown : S.EqOn) : Pressed(Region.EqButton) ? S.EqOffDown : S.EqOff, S.EqButtonArea);
        Draw(c, ShowPlaylist ? (Pressed(Region.PlButton) ? S.PlOnDown : S.PlOn) : Pressed(Region.PlButton) ? S.PlOffDown : S.PlOff, S.PlButtonArea);

        Draw(c, S.PositionBackground, S.PositionArea);
        if (_music.HasCurrent && _music.Duration > 0 && !_music.IsHalted)
        {
            var at = _dragPosition ?? Math.Clamp(_music.Position / _music.Duration, 0, 1);
            Draw(c, Pressed(Region.Position) ? S.PositionThumbDown : S.PositionThumb, S.PositionArea.X + (at * (248 - 29)), S.PositionArea.Y);
        }

        Draw(c, Pressed(Region.Previous) ? S.PreviousDown : S.Previous, S.PreviousArea);
        Draw(c, Pressed(Region.Play) ? S.PlayDown : S.Play, S.PlayArea);
        Draw(c, Pressed(Region.Pause) ? S.PauseDown : S.Pause, S.PauseArea);
        Draw(c, Pressed(Region.Stop) ? S.StopDown : S.Stop, S.StopArea);
        Draw(c, Pressed(Region.Next) ? S.NextDown : S.Next, S.NextArea);
        Draw(c, Pressed(Region.Eject) ? S.EjectDown : S.Eject, S.EjectArea);
        Draw(c, _music.IsShuffled ? (Pressed(Region.Shuffle) ? S.ShuffleOnDown : S.ShuffleOn) : Pressed(Region.Shuffle) ? S.ShuffleOffDown : S.ShuffleOff, S.ShuffleArea);
        Draw(c, _music.IsRepeating ? (Pressed(Region.Repeat) ? S.RepeatOnDown : S.RepeatOn) : Pressed(Region.Repeat) ? S.RepeatOffDown : S.RepeatOff, S.RepeatArea);
    }

    /// <summary>The main window rolled up: title bar buttons, a small analyser, the time and a small position bar.</summary>
    private void DrawShade(DrawingContext c)
    {
        Draw(c, IsActive(Part.Main) ? S.ShadeBackgroundActive : S.ShadeBackground, 0, 0);
        Draw(c, Pressed(Region.Options) ? S.OptionsButtonDown : S.OptionsButton, S.OptionsArea);
        Draw(c, Pressed(Region.Minimize) ? S.MinimizeButtonDown : S.MinimizeButton, S.MinimizeArea);
        Draw(c, Pressed(Region.Shade) ? S.UnshadeButtonDown : S.UnshadeButton, S.ShadeArea);
        Draw(c, Pressed(Region.Close) ? S.CloseButtonDown : S.CloseButton, S.CloseArea);

        var frame = _music.Visuals.Classic;
        if (Vis == ClassicVis.Spectrum && _feed is not null && !frame.Silent)
        {
            // Nineteen bars a pixel wide, five tall, in the analyser's colours.
            var area = S.ShadeVisArea;
            for (var i = 0; i < 19 && i < frame.Bands.Length; i++)
            {
                var height = (int)Math.Round(Math.Clamp(frame.Bands[i], 0, 1) * 5);
                for (var y = 0; y < height; y++)
                {
                    c.FillRectangle(Brush(_skin.VisColors[17 - (y * 3)]), new Rect(area.X + (i * 2), area.Bottom - 1 - y, 1, 1));
                }
            }
        }

        if (_music.HasCurrent && !_music.IsHalted)
        {
            var remaining = _settings.ShowRemaining && _music.Duration > 0;
            var seconds = (int)Math.Max(0, remaining ? _music.Duration - _music.Position : _music.Position);
            var clock = string.Create(CultureInfo.InvariantCulture, $"{(remaining ? '-' : ' ')}{Math.Min(99, seconds / 60):00}:{seconds % 60:00}");
            DrawText(c, clock, S.ShadeTimeArea.X, S.ShadeTimeArea.Y, 6);

            Draw(c, S.ShadePositionBackground, S.ShadePositionArea);
            if (_music.Duration > 0)
            {
                var at = _dragPosition ?? Math.Clamp(_music.Position / _music.Duration, 0, 1);
                var thumb = at < 1 / 3.0 ? S.ShadePositionThumbLeft : at < 2 / 3.0 ? S.ShadePositionThumb : S.ShadePositionThumbRight;
                Draw(c, thumb, S.ShadePositionArea.X + Math.Round(at * (17 - 3)), S.ShadePositionArea.Y);
            }
        }
    }

    private void DrawTime(DrawingContext c)
    {
        var remaining = _settings.ShowRemaining && _music.Duration > 0;
        var seconds = (int)Math.Max(0, remaining ? _music.Duration - _music.Position : _music.Position);
        var minutes = Math.Min(99, seconds / 60);
        seconds %= 60;
        var ex = _skin.HasExtendedDigits;
        if (remaining)
        {
            if (ex) Draw(c, S.MinusSignEx, S.TimeArea.X, S.TimeArea.Y);
            else Draw(c, S.MinusSign, S.TimeArea.X - 1, S.TimeArea.Y + 6);
        }

        Draw(c, S.Digit(minutes / 10, ex), S.TimeArea.X + 9, S.TimeArea.Y);
        Draw(c, S.Digit(minutes % 10, ex), S.TimeArea.X + 21, S.TimeArea.Y);
        Draw(c, S.Digit(seconds / 10, ex), S.TimeArea.X + 39, S.TimeArea.Y);
        Draw(c, S.Digit(seconds % 10, ex), S.TimeArea.X + 51, S.TimeArea.Y);
    }

    private void DrawVisualizer(DrawingContext c)
    {
        var area = S.VisArea;
        if (_visBackground is not null) c.DrawImage(_visBackground, new Rect(0, 0, 76, 16), area);
        if (Vis == ClassicVis.Off || _feed is null) return;

        var frame = _music.Visuals.Classic;
        if (frame.Silent && frame.Bands.All(b => b < 0.02f)) return;

        if (Vis == ClassicVis.Spectrum)
        {
            // Nineteen bars three pixels wide, each row in its own colour, and a peak dot over each.
            for (var i = 0; i < 19 && i < frame.Bands.Length; i++)
            {
                var height = (int)Math.Round(Math.Clamp(frame.Bands[i], 0, 1) * 16);
                var x = area.X + (i * 4);
                if (height > 0 && _visGradient is not null)
                {
                    c.DrawImage(_visGradient, new Rect(0, 16 - height, 1, height), new Rect(x, area.Y + 16 - height, 3, height));
                }

                var peak = (int)Math.Round(Math.Clamp(frame.Peaks[i], 0, 1) * 16);
                if (peak > 0) c.FillRectangle(Brush(_skin.VisColors[23]), new Rect(x, area.Y + 16 - peak, 3, 1));
            }
        }
        else
        {
            // The scope: 76 columns of the waveform, joined column to column, brighter away from the centre line.
            var wave = frame.Waveform;
            if (wave.Length == 0) return;
            var previous = -1;
            for (var x = 0; x < 76; x++)
            {
                var sample = wave[Math.Min(wave.Length - 1, x * wave.Length / 76)];
                var y = Math.Clamp((int)Math.Round(8 - (sample * 16)), 0, 15);
                var from = previous < 0 ? y : Math.Min(previous, y);
                var to = previous < 0 ? y : Math.Max(previous, y);
                var colour = _skin.VisColors[22 - Math.Min(4, Math.Abs(y - 8) / 2)];
                c.FillRectangle(Brush(colour), new Rect(area.X + x, area.Y + from, 1, to - from + 1));
                previous = y;
            }
        }
    }

    /// <summary>Shows a short message in the marquee for a moment, as Winamp did while a slider moved.</summary>
    public void Flash(string text, double seconds = 1)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        InvalidateVisual();
    }

    private void DrawMarquee(DrawingContext c)
    {
        // The text sits three pixels into its box.
        var y = S.MarqueeArea.Y + 3;
        if (_flash is not null && DateTime.UtcNow < _flashUntil)
        {
            DrawText(c, _flash, S.MarqueeArea.X, y, MarqueeChars);
            return;
        }

        var text = MarqueeText();
        if (text.Length <= MarqueeChars)
        {
            DrawText(c, text, S.MarqueeArea.X, y, MarqueeChars);
            return;
        }

        var loop = text + Separator;
        var start = _marqueeStep % loop.Length;
        var shown = string.Concat(Enumerable.Range(0, MarqueeChars).Select(i => loop[(start + i) % loop.Length]));
        DrawText(c, shown, S.MarqueeArea.X, y, MarqueeChars);
    }

    private string MarqueeText()
    {
        if (_music.Current is not { } entry) return "Tuxflix";
        var index = _music.Queue.IndexOf(entry) + 1;
        var artist = string.IsNullOrEmpty(entry.Artist) ? string.Empty : entry.Artist + " - ";
        return string.Create(CultureInfo.InvariantCulture, $"{index}. {artist}{entry.Title} ({entry.DurationText})");
    }

    private void DrawMediaInfo(DrawingContext c)
    {
        if (_music.Current?.Track is not { } track) return;
        var media = track.Media?.FirstOrDefault();
        if (media?.Bitrate is { } bitrate) DrawText(c, Math.Min(999, bitrate).ToString(CultureInfo.InvariantCulture).PadLeft(3), S.KbpsArea.X, S.KbpsArea.Y, 3);
        var rate = media?.Part?.FirstOrDefault()?.Stream?.FirstOrDefault(s => s.StreamType == 2)?.SamplingRate ?? 44100;
        DrawText(c, (rate / 1000).ToString(CultureInfo.InvariantCulture).PadLeft(2), S.KhzArea.X, S.KhzArea.Y, 2);
        var stereo = (media?.AudioChannels ?? 2) >= 2;
        Draw(c, stereo ? S.Mono : S.MonoLit, S.MonoArea);
        Draw(c, stereo ? S.StereoLit : S.Stereo, S.StereoArea);
    }

    private void DrawEqualizer(DrawingContext c, int top)
    {
        Draw(c, S.EqBackground, 0, top);
        Draw(c, IsActive(Part.Equalizer) ? S.EqTitleBarActive : S.EqTitleBar, 0, top);
        if (Pressed(Region.EqClose)) Draw(c, S.EqCloseDown, S.EqCloseArea.X, top + S.EqCloseArea.Y);

        var on = _music.IsEqualizerOn;
        var onSprite = on ? (Pressed(Region.EqOn) ? S.EqOnButtonLitDown : S.EqOnButtonLit) : Pressed(Region.EqOn) ? S.EqOnButtonDown : S.EqOnButton;
        Draw(c, onSprite, S.EqOnArea.X, top + S.EqOnArea.Y);
        Draw(c, Pressed(Region.EqAuto) ? S.EqAutoButtonDown : S.EqAutoButton, S.EqAutoArea.X, top + S.EqAutoArea.Y);
        Draw(c, Pressed(Region.EqPresets) ? S.EqPresetsButtonDown : S.EqPresetsButton, S.EqPresetsArea.X, top + S.EqPresetsArea.Y);

        DrawEqGraph(c, top);
        DrawEqSlider(c, S.EqPreampArea.X, top + S.EqPreampArea.Y, _eqBand == -1 && _dragEq is { } p ? p : Fraction(_music.EqualizerPreamp), Pressed(Region.EqPreamp));
        for (var band = 0; band < 10; band++)
        {
            var area = S.EqBandArea(band);
            var value = _eqBand == band && _dragEq is { } d ? d : Fraction(_music.EqualizerBands[band]);
            DrawEqSlider(c, area.X, top + area.Y, value, Pressed(Region.EqBand) && _eqBand == band);
        }
    }

    /// <summary>A band slider: one of 28 background frames for its level, and the thumb on it.</summary>
    private void DrawEqSlider(DrawingContext c, double x, double y, double value, bool down)
    {
        var frame = Math.Clamp((int)Math.Round(value * 27), 0, 27);
        DrawPart(c, S.Eqmain, new Rect(13 + (frame % 14 * 15), 164 + (frame / 14 * 65), 14, 63), x, y);
        Draw(c, down ? S.EqThumbDown : S.EqThumb, x + 1, y + ((1 - value) * (62 - 11)));
    }

    /// <summary>The curve through the ten bands, each pixel coloured from the skin's own graph colours by height.</summary>
    private void DrawEqGraph(DrawingContext c, int top)
    {
        var x0 = S.EqGraphArea.X;
        var y0 = top + S.EqGraphArea.Y;
        Draw(c, S.EqGraphBackground, x0, y0);
        var preampY = 9 - (_music.EqualizerPreamp / 12 * 9);
        Draw(c, S.EqPreampLine, x0, y0 + Math.Round(preampY));
        if (_skin.Sheet(S.Eqmain) is not { } sheet) return;

        var points = Enumerable.Range(0, 10).Select(i => _music.EqualizerBands[i]).ToArray();
        for (var x = 0; x < 113; x++)
        {
            var t = x / 112.0 * 9;
            var i = Math.Min(8, (int)t);
            var f = t - i;
            // A cosine blend between neighbours: smooth enough for 19 pixels of height.
            var blend = (1 - Math.Cos(f * Math.PI)) / 2;
            var db = (points[i] * (1 - blend)) + (points[i + 1] * blend);
            var y = Math.Clamp((int)Math.Round(9 - (db / 12 * 9)), 0, 18);
            c.DrawImage(sheet, new Rect(115, 294 + y, 1, 1), new Rect(x0 + x, y0 + y, 1, 1));
        }
    }

    private void DrawPlaylist(DrawingContext c, int top)
    {
        const int width = S.MainWidth;
        const int height = S.PlaylistHeight;
        var style = _skin.Playlist;

        // The frame: corners, tiles along the top and sides, the two bottom pieces.
        var active = IsActive(Part.Playlist);
        for (var x = 25; x < width - 25; x += 25) Draw(c, active ? S.PlTopTileActive : S.PlTopTile, x, top);
        Draw(c, active ? S.PlTopLeftActive : S.PlTopLeft, 0, top);
        Draw(c, active ? S.PlTitleActive : S.PlTitle, (width - 100) / 2, top);
        Draw(c, active ? S.PlTopRightActive : S.PlTopRight, width - 25, top);
        for (var y = top + 20; y < top + height - 38; y += 29)
        {
            Draw(c, S.PlLeftTile, 0, y);
            Draw(c, S.PlRightTile, width - 20, y);
        }

        Draw(c, S.PlBottomLeft, 0, top + height - 38);
        Draw(c, S.PlBottomRight, width - 150, top + height - 38);

        // The list: the skin's colours and font.
        var list = new Rect(12, top + 20, width - 12 - 20, height - 20 - 38);
        c.FillRectangle(Brush(style.NormalBackground), list);
        var rows = (int)(list.Height / PlaylistRow);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _music.Queue.Count - rows));
        var typeface = new Typeface(new FontFamily(style.Font + ", Arial, sans-serif"));
        using (c.PushClip(list))
        {
            for (var row = 0; row < rows && _scroll + row < _music.Queue.Count; row++)
            {
                var index = _scroll + row;
                var entry = _music.Queue[index];
                var y = list.Y + (row * PlaylistRow);
                if (index == _selected) c.FillRectangle(Brush(style.SelectedBackground), new Rect(list.X, y, list.Width, PlaylistRow));
                var colour = Brush(entry.IsCurrent ? style.Current : style.Normal);
                var label = string.Create(CultureInfo.InvariantCulture, $"{index + 1}. {(string.IsNullOrEmpty(entry.Artist) ? string.Empty : entry.Artist + " - ")}{entry.Title}");
                var time = new FormattedText(entry.DurationText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, colour);
                var name = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, colour)
                {
                    MaxTextWidth = list.Width - time.Width - 8,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                c.DrawText(name, new Point(list.X + 2, y + 1));
                c.DrawText(time, new Point(list.Right - time.Width - 3, y + 1));
            }
        }

        // The scroll handle rides the right edge.
        var span = list.Height - 18;
        var fraction = _music.Queue.Count > rows ? _scroll / (double)(_music.Queue.Count - rows) : 0;
        Draw(c, S.PlScrollHandle, width - 15, list.Y + (fraction * span));

        // The bottom right: the selected track's length over the queue's, and the time now.
        var bottom = top + height - 38;
        var right = width - 150;
        var total = _music.Queue.Sum(e => (e.Track.Duration ?? 0) / 1000.0);
        var selected = _selected >= 0 && _selected < _music.Queue.Count ? (_music.Queue[_selected].Track.Duration ?? 0) / 1000.0 : 0;
        DrawText(c, $"{Format.Clock(selected)}/{Format.Clock(total)}", right + 7, bottom + 10, 22);
        if (_music.HasCurrent && !_music.IsHalted)
        {
            var remaining = _settings.ShowRemaining && _music.Duration > 0;
            var seconds = (int)Math.Max(0, remaining ? _music.Duration - _music.Position : _music.Position);
            var clock = string.Create(CultureInfo.InvariantCulture, $"{(remaining ? '-' : ' ')}{Math.Min(99, seconds / 60):00}:{seconds % 60:00}");
            DrawText(c, clock, right + 66, bottom + 23, 6);
        }
    }

    // ================================================================ input ==

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = Logical(e.GetPosition(this));
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            MenuRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;
        _activePart = point.Y < MainHeight ? Part.Main : ShowEqualizer && point.Y < EqTop + S.EqHeight ? Part.Equalizer : Part.Playlist;
        _pressed = HitTest(point, out _eqBand);
        _miniButton = _eqBand;
        _hover = _pressed;
        switch (_pressed)
        {
            case Region.Volume or Region.Balance or Region.Position or Region.EqBand or Region.EqPreamp:
                Slide(point);
                break;
            case Region.PlaylistRow:
                var row = PlaylistRowAt(point);
                if (e.ClickCount >= 2 && row >= 0 && row < _music.Queue.Count) _music.PlayEntry(_music.Queue[row]);
                _selected = row;
                break;
            case Region.Grip when TopLevel.GetTopLevel(this) is { } top:
                // The corner keeps its distance from the pointer while the window follows it.
                var at = e.GetPosition(top);
                _gripOffset = new Vector(top.ClientSize.Width - at.X, top.ClientSize.Height - at.Y);
                break;
            case Region.Drag when e.ClickCount == 2 && point.Y < S.ShadeHeight:
                _pressed = Region.None;
                Shaded = !Shaded;
                break;
            case Region.Drag:
                _pressed = Region.None;
                DragRequested?.Invoke(e);
                break;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = Logical(e.GetPosition(this));
        if (_pressed == Region.None)
        {
            Cursor = Grip.Contains(point) ? _gripCursor ??= new Cursor(StandardCursorType.BottomRightCorner) : null;
            return;
        }

        switch (_pressed)
        {
            case Region.Grip:
                Resize(e);
                return;
            case Region.Volume or Region.Balance or Region.Position or Region.EqBand or Region.EqPreamp:
                Slide(point);
                break;
            default:
                _hover = HitTest(point, out var band) == _pressed && (_pressed != Region.EqBand || band == _eqBand) ? _pressed : Region.None;
                break;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var released = _pressed;
        var point = Logical(e.GetPosition(this));
        var inside = HitTest(point, out _) == released;
        _pressed = Region.None;
        e.Pointer.Capture(null);

        switch (released)
        {
            case Region.Volume or Region.Balance or Region.EqBand or Region.EqPreamp:
                break;
            case Region.Position when _dragPosition is { } at:
                _music.SeekTo(at * _music.Duration);
                break;
            default:
                if (inside) Activate(released);
                break;
        }

        _dragVolume = _dragBalance = _dragPosition = _dragEq = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = Logical(e.GetPosition(this));
        if (ShowPlaylist && point.Y >= PlaylistTop)
        {
            _scroll -= Math.Sign(e.Delta.Y) * 3;
        }
        else
        {
            _music.ChangeVolume(Math.Sign(e.Delta.Y) * 4);
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            // Winamp's own keys: the bottom row of the keyboard is the transport.
            case Key.Z: _music.PreviousCommand.Execute(null); break;
            case Key.X: _music.Resume(); break;
            case Key.C or Key.Space: _music.TogglePauseCommand.Execute(null); break;
            case Key.V: _music.Halt(); break;
            case Key.B: _music.NextCommand.Execute(null); break;
            case Key.L: EjectRequested?.Invoke(); break;
            case Key.S: _music.ToggleShuffleCommand.Execute(null); break;
            case Key.R: ToggleRepeat(); break;
            case Key.Left: _music.SeekTo(Math.Max(0, _music.Position - 5)); break;
            case Key.Right: _music.SeekTo(_music.Position + 5); break;
            case Key.Up: _music.ChangeVolume(2); break;
            case Key.Down: _music.ChangeVolume(-2); break;
            default: return;
        }

        e.Handled = true;
        InvalidateVisual();
    }

    private void Activate(Region region)
    {
        switch (region)
        {
            case Region.Close: CloseRequested?.Invoke(); break;
            case Region.Minimize: MinimizeRequested?.Invoke(); break;
            case Region.Options or Region.ClutterO or Region.ClutterI: MenuRequested?.Invoke(); break;
            case Region.Shade: Shaded = !Shaded; break;
            case Region.ClutterA: AlwaysOnTop = !AlwaysOnTop; break;
            case Region.ClutterD: Scale = Scale >= MaxScale - 0.01 ? MinScale : MaxScale; break;
            case Region.ClutterV or Region.Visualizer: CycleVisualizer(); break;
            case Region.Time: ShowRemaining = !ShowRemaining; break;
            case Region.Previous: _music.PreviousCommand.Execute(null); break;
            case Region.Play: _music.Resume(); break;
            case Region.Pause: _music.TogglePauseCommand.Execute(null); break;
            case Region.Stop: _music.Halt(); break;
            case Region.Next: _music.NextCommand.Execute(null); break;
            case Region.Eject: EjectRequested?.Invoke(); break;
            case Region.Shuffle: _music.ToggleShuffleCommand.Execute(null); break;
            case Region.Repeat: ToggleRepeat(); break;
            case Region.EqButton: ShowEqualizer = !ShowEqualizer; break;
            case Region.PlButton: ShowPlaylist = !ShowPlaylist; break;
            case Region.EqClose: ShowEqualizer = false; break;
            case Region.PlaylistClose: ShowPlaylist = false; break;
            case Region.PlaylistTransport: MiniTransport(_miniButton); break;
            case Region.EqOn: _music.SetEqualizer(!_music.IsEqualizerOn); break;
            case Region.EqPresets: PresetsRequested?.Invoke(); break;
        }
    }

    /// <summary>The playlist's small transport: previous, play, pause, stop, next, eject.</summary>
    private void MiniTransport(int button)
    {
        switch (button)
        {
            case 0: _music.PreviousCommand.Execute(null); break;
            case 1: _music.Resume(); break;
            case 2: _music.TogglePauseCommand.Execute(null); break;
            case 3: _music.Halt(); break;
            case 4: _music.NextCommand.Execute(null); break;
            case 5: EjectRequested?.Invoke(); break;
        }
    }

    /// <summary>The classic repeat is on or off; on repeats the whole queue.</summary>
    private void ToggleRepeat()
    {
        // The player cycles off, all, one; the classic button only knows off and all.
        do
        {
            _music.CycleRepeatCommand.Execute(null);
        }
        while (_music.Repeat == RepeatMode.One);
    }

    private void CycleVisualizer() => Visualizer = Visualizer switch
    {
        ClassicVis.Spectrum => ClassicVis.Scope,
        ClassicVis.Scope => ClassicVis.Off,
        _ => ClassicVis.Spectrum,
    };

    /// <summary>
    /// A slider follows the pointer: the sound changes as it moves (a seek waits for the release),
    /// and the marquee says the value, as Winamp's did.
    /// </summary>
    private void Slide(Point point)
    {
        switch (_pressed)
        {
            case Region.Volume:
                _dragVolume = SliderValue(point.X, S.VolumeArea.X, 51);
                _music.Volume = _dragVolume.Value * 100;
                Flash(string.Create(CultureInfo.InvariantCulture, $"Volume: {Math.Round(_dragVolume.Value * 100)}%"));
                break;
            case Region.Balance:
                // Near the middle is the middle, as the classic slider had it.
                var balance = (SliderValue(point.X, S.BalanceArea.X, 24) * 2) - 1;
                if (Math.Abs(balance) < 0.1) balance = 0;
                _dragBalance = balance;
                _music.SetBalance(balance);
                var side = balance < 0 ? "left" : "right";
                Flash(balance == 0 ? "Balance: center" : string.Create(CultureInfo.InvariantCulture, $"Balance: {Math.Round(Math.Abs(balance) * 100)}% {side}"));
                break;
            case Region.Position when _music.Duration > 0:
                _dragPosition = Shaded
                    ? Math.Clamp((point.X - S.ShadePositionArea.X - 1) / (17 - 3), 0, 1)
                    : SliderValue(point.X, S.PositionArea.X, 248 - 29);
                var to = _dragPosition.Value * _music.Duration;
                Flash(string.Create(CultureInfo.InvariantCulture, $"Seek to: {Format.Clock(to)}/{Format.Clock(_music.Duration)} ({Math.Round(_dragPosition.Value * 100)}%)"));
                break;
            case Region.EqBand or Region.EqPreamp:
                _dragEq = EqValue(point.Y);
                var db = (_dragEq.Value * 24) - 12;
                if (Math.Abs(db) < 0.6) db = 0;
                if (_eqBand == -1) _music.SetEqualizerPreamp(db);
                else _music.SetEqualizerBand(_eqBand, db);
                var name = _eqBand == -1 ? "Preamp" : Hertz(S.EqFrequencies[_eqBand]);
                Flash(string.Create(CultureInfo.InvariantCulture, $"EQ: {name}: {db:+0.0;-0.0;0.0} dB"));
                break;
        }
    }

    /// <summary>
    /// The size follows the corner: the pointer's position, projected onto the window's own
    /// proportions, so any drag direction scales it evenly. Near a whole size it snaps there,
    /// where every skin pixel is sharp.
    /// </summary>
    private void Resize(PointerEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var at = e.GetPosition(top) + _gripOffset;
        double width = S.MainWidth;
        double height = LogicalHeight;
        var scale = ((at.X * width) + (at.Y * height)) / ((width * width) + (height * height));
        var whole = Math.Round(scale);
        if (Math.Abs(scale - whole) < 0.04) scale = whole;
        scale = Math.Clamp(scale, MinScale, MaxScale);
        if (Math.Abs(scale - Scale) < 0.001) return;
        Scale = scale;
        Flash(string.Create(CultureInfo.InvariantCulture, $"Size: {Math.Round(scale * 100)}%"));
    }

    private static string Hertz(int frequency) =>
        frequency >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{frequency / 1000}kHz") : string.Create(CultureInfo.InvariantCulture, $"{frequency}Hz");

    private double EqValue(double y)
    {
        var top = (_eqBand == -1 ? S.EqPreampArea.Y : S.EqBandArea(_eqBand).Y) + EqTop;
        return Math.Clamp(1 - ((y - top - 5.5) / (62 - 11)), 0, 1);
    }

    private static double Fraction(double decibels) => Math.Clamp((decibels + 12) / 24, 0, 1);

    private static double SliderValue(double x, double left, double travel) => Math.Clamp((x - left - 7) / travel, 0, 1);

    private int PlaylistRowAt(Point point) => _scroll + (int)((point.Y - PlaylistTop - 20) / PlaylistRow);

    private Point Logical(Point point) => new(point.X / Unit, point.Y / Unit);

    private Region HitTest(Point p, out int band)
    {
        band = -2;
        if (Grip.Contains(p)) return Region.Grip;
        if (Shaded && p.Y < S.ShadeHeight)
        {
            if (S.CloseArea.Contains(p)) return Region.Close;
            if (S.ShadeArea.Contains(p)) return Region.Shade;
            if (S.MinimizeArea.Contains(p)) return Region.Minimize;
            if (S.OptionsArea.Contains(p)) return Region.Options;
            if (S.ShadeVisArea.Contains(p)) return Region.Visualizer;
            if (S.ShadeTimeArea.Contains(p)) return Region.Time;
            if (S.ShadePositionArea.Contains(p)) return Region.Position;
            if (S.ShadePreviousArea.Contains(p)) return Region.Previous;
            if (S.ShadePlayArea.Contains(p)) return Region.Play;
            if (S.ShadePauseArea.Contains(p)) return Region.Pause;
            if (S.ShadeStopArea.Contains(p)) return Region.Stop;
            if (S.ShadeNextArea.Contains(p)) return Region.Next;
            if (S.ShadeEjectArea.Contains(p)) return Region.Eject;
            return Region.Drag;
        }

        if (!Shaded && p.Y < S.MainHeight)
        {
            if (S.CloseArea.Contains(p)) return Region.Close;
            if (S.ShadeArea.Contains(p)) return Region.Shade;
            if (S.MinimizeArea.Contains(p)) return Region.Minimize;
            if (S.OptionsArea.Contains(p)) return Region.Options;
            if (S.ClutterOArea.Contains(p)) return Region.ClutterO;
            if (S.ClutterAArea.Contains(p)) return Region.ClutterA;
            if (S.ClutterIArea.Contains(p)) return Region.ClutterI;
            if (S.ClutterDArea.Contains(p)) return Region.ClutterD;
            if (S.ClutterVArea.Contains(p)) return Region.ClutterV;
            if (S.VisArea.Contains(p)) return Region.Visualizer;
            if (S.TimeArea.Contains(p)) return Region.Time;
            if (S.VolumeArea.Contains(p)) return Region.Volume;
            if (S.BalanceArea.Contains(p)) return Region.Balance;
            if (S.EqButtonArea.Contains(p)) return Region.EqButton;
            if (S.PlButtonArea.Contains(p)) return Region.PlButton;
            if (S.PositionArea.Contains(p)) return Region.Position;
            if (S.PreviousArea.Contains(p)) return Region.Previous;
            if (S.PlayArea.Contains(p)) return Region.Play;
            if (S.PauseArea.Contains(p)) return Region.Pause;
            if (S.StopArea.Contains(p)) return Region.Stop;
            if (S.NextArea.Contains(p)) return Region.Next;
            if (S.EjectArea.Contains(p)) return Region.Eject;
            if (S.ShuffleArea.Contains(p)) return Region.Shuffle;
            if (S.RepeatArea.Contains(p)) return Region.Repeat;
            return Region.Drag;
        }

        if (ShowEqualizer && p.Y < EqTop + S.EqHeight)
        {
            var q = new Point(p.X, p.Y - EqTop);
            if (S.EqCloseArea.Contains(q)) return Region.EqClose;
            if (S.EqOnArea.Contains(q)) return Region.EqOn;
            if (S.EqAutoArea.Contains(q)) return Region.EqAuto;
            if (S.EqPresetsArea.Contains(q)) return Region.EqPresets;
            if (S.EqPreampArea.Contains(q))
            {
                band = -1;
                return Region.EqPreamp;
            }

            for (var i = 0; i < 10; i++)
            {
                if (!S.EqBandArea(i).Contains(q)) continue;
                band = i;
                return Region.EqBand;
            }

            return Region.Drag;
        }

        if (ShowPlaylist)
        {
            var q = new Point(p.X, p.Y - PlaylistTop);
            if (new Rect(264, 3, 9, 9).Contains(q)) return Region.PlaylistClose;
            var transport = new Rect(S.MainWidth - 150 + 3, S.PlaylistHeight - 38 + 22, 60, 10);
            if (transport.Contains(q))
            {
                band = (int)((q.X - transport.X) / 10);
                return Region.PlaylistTransport;
            }

            if (new Rect(12, 20, S.MainWidth - 32, S.PlaylistHeight - 58).Contains(q)) return Region.PlaylistRow;
            return Region.Drag;
        }

        return Region.None;
    }

    private bool Pressed(Region region) => _pressed == region && _hover == region;

    /// <summary>
    /// The bottom right corner resizes, as the playlist's grip did in Winamp; a strip rolled up
    /// on its own is too short for one and keeps its close button.
    /// </summary>
    private Rect Grip => LogicalHeight > S.ShadeHeight ? new Rect(S.MainWidth - 12, LogicalHeight - 12, 12, 12) : default;

    /// <summary>As in Winamp, the part last clicked has the lit title bar, and none has while another window has the focus.</summary>
    private bool IsActive(Part part) => _activePart == part && TopLevel.GetTopLevel(this) is WindowBase { IsActive: true };

    // =============================================================== helpers ==

    private void Draw(DrawingContext c, Sprite sprite, Rect at) => Draw(c, sprite, at.X, at.Y);

    private void Draw(DrawingContext c, Sprite sprite, double x, double y)
    {
        if (_skin.Sheet(sprite.Sheet) is not { } sheet) return;
        var source = ClipToSheet(sprite.Source, sheet);
        if (source.Width <= 0 || source.Height <= 0) return;
        c.DrawImage(sheet, source, new Rect(x, y, source.Width, source.Height));
    }

    private void DrawPart(DrawingContext c, string sheetName, Rect source, double x, double y)
    {
        if (_skin.Sheet(sheetName) is not { } sheet) return;
        var clipped = ClipToSheet(source, sheet);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;
        c.DrawImage(sheet, clipped, new Rect(x, y, clipped.Width, clipped.Height));
    }

    /// <summary>Skins are not always the documented size: draw what the sheet has, never past its edge.</summary>
    private static Rect ClipToSheet(Rect source, Bitmap sheet) =>
        source.Intersect(new Rect(0, 0, sheet.PixelSize.Width, sheet.PixelSize.Height));

    /// <summary>Text in the skin's own 5x6 font, one sheet cell per character.</summary>
    private void DrawText(DrawingContext c, string text, double x, double y, int maxChars)
    {
        for (var i = 0; i < text.Length && i < maxChars; i++) Draw(c, S.Character(text[i]), x + (i * 5), y);
    }

    private IBrush Brush(Color colour)
    {
        if (!_brushes.TryGetValue(colour, out var brush)) _brushes[colour] = brush = new ImmutableSolidColorBrush(colour);
        return brush;
    }

    /// <summary>The analyser's background (colour 0 with a grid of colour 1 dots) and its row colours, per skin.</summary>
    private void BuildVisBitmaps()
    {
        _visBackground?.Dispose();
        _visGradient?.Dispose();
        var colours = _skin.VisColors;
        _visBackground = new WriteableBitmap(new PixelSize(76, 16), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var target = _visBackground.Lock())
        {
            unsafe
            {
                var pixels = (uint*)target.Address;
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 76; x++)
                    {
                        var dot = x % 2 == 0 && y % 2 == 1;
                        pixels[(y * target.RowBytes / 4) + x] = Rgba(dot ? colours[1] : colours[0]);
                    }
                }
            }
        }

        _visGradient = new WriteableBitmap(new PixelSize(1, 16), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var target = _visGradient.Lock())
        {
            unsafe
            {
                var pixels = (uint*)target.Address;
                for (var y = 0; y < 16; y++) pixels[y * target.RowBytes / 4] = Rgba(colours[2 + y]);
            }
        }
    }

    private static uint Rgba(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16) | (0xFF << 24));

    private enum Region
    {
        None,
        Drag,
        Options,
        Minimize,
        Shade,
        Close,
        ClutterO,
        ClutterA,
        ClutterI,
        ClutterD,
        ClutterV,
        Visualizer,
        Time,
        Volume,
        Balance,
        EqButton,
        PlButton,
        Position,
        Previous,
        Play,
        Pause,
        Stop,
        Next,
        Eject,
        Shuffle,
        Repeat,
        EqClose,
        EqOn,
        EqAuto,
        EqPresets,
        EqPreamp,
        EqBand,
        PlaylistClose,
        PlaylistRow,
        PlaylistTransport,
        Grip,
    }
}

internal enum Part
{
    Main,
    Equalizer,
    Playlist,
}

public enum ClassicVis
{
    Spectrum,
    Scope,
    Off,
}
