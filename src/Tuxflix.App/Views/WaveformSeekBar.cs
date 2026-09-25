using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Views;

/// <summary>
/// A seek bar drawn as the track's shape: a bar per slice, as loud as that slice, the played part
/// in the accent. Pressing or dragging anywhere moves the position there.
/// </summary>
/// <remarks>
/// The heights are worked out once per width and per track, not per frame: a frame is a few
/// hundred rectangles. The page shows the plain seek bar instead while the server has given no
/// loudness readings for the track.
/// </remarks>
public sealed class WaveformSeekBar : Control
{
    private const double Pitch = 5;
    private const double BarWidth = 3;

    public static readonly StyledProperty<IReadOnlyList<double>?> LevelsProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IReadOnlyList<double>?>(nameof(Levels));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(PlayedBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> RemainingBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(RemainingBrush), new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private float[] _heights = [];
    private int _heightsFor = -1;
    private bool _scrubbing;

    static WaveformSeekBar()
    {
        AffectsRender<WaveformSeekBar>(LevelsProperty, ValueProperty, MaximumProperty, PlayedBrushProperty, RemainingBrushProperty);
        FocusableProperty.OverrideDefaultValue<WaveformSeekBar>(false);
    }

    public WaveformSeekBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The listener pressed on the bar: the position stops following the music until release.</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>The listener let go: seek to <see cref="Value"/>.</summary>
    public event EventHandler? ScrubEnded;

    public IReadOnlyList<double>? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? RemainingBrush
    {
        get => GetValue(RemainingBrushProperty);
        set => SetValue(RemainingBrushProperty, value);
    }

    /// <summary>The bars as drawn now, 0..1 each: what a test reads.</summary>
    internal IReadOnlyList<float> Heights => _heights;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var bars = Math.Max(8, (int)Math.Floor((width + Pitch - BarWidth) / Pitch));
        if (bars != _heightsFor)
        {
            _heights = Levels is { } levels ? LoudnessWaveform.FromLevels(levels, bars) : [];
            _heightsFor = bars;
        }

        if (_heights.Length == 0) return;
        var progress = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        var reach = height - 4;
        var centre = height / 2;
        for (var i = 0; i < _heights.Length; i++)
        {
            var x = i * Pitch;
            var h = Math.Max(2, _heights[i] * reach);
            var brush = x + (BarWidth / 2) <= progress * width ? PlayedBrush : RemainingBrush;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, centre - (h / 2), BarWidth, h), 1.5));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelsProperty) _heightsFor = -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _scrubbing = true;
        e.Pointer.Capture(this);
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        MoveTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scrubbing) MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing) return;
        MoveTo(e.GetPosition(this).X);
        EndScrub();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndScrub();
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ScrubEnded?.Invoke(this, EventArgs.Empty);
    }

    private void MoveTo(double x) => SetCurrentValue(ValueProperty, Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) * Maximum : 0);
}
