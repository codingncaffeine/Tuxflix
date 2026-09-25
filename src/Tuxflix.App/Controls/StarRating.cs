using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Tuxflix.App.Controls;

/// <summary>
/// Five stars that take half stars: the viewer's rating, 0 to 10 as Plex keeps it (a half star a
/// point). Hovering shows what a click would give; clicking the rating already given takes it
/// away. Arrow keys step by half a star, Delete clears.
/// </summary>
public sealed class StarRating : Control
{
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<StarRating, double?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> StarSizeProperty =
        AvaloniaProperty.Register<StarRating, double>(nameof(StarSize), 18);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StarRating, IBrush?>(nameof(Fill));

    private const double Gap = 4;
    private double? _hover;

    static StarRating()
    {
        AffectsRender<StarRating>(ValueProperty, FillProperty);
        AffectsMeasure<StarRating>(StarSizeProperty);
        FocusableProperty.OverrideDefaultValue<StarRating>(true);
        CursorProperty.OverrideDefaultValue<StarRating>(new Cursor(StandardCursorType.Hand));
    }

    /// <summary>The rating, 0 to 10; null when there is none.</summary>
    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StarSize
    {
        get => GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    /// <summary>The filled stars' brush; the accent when unset.</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>"3½ stars", "No rating", as a screen reader or a tooltip says it.</summary>
    public static string Describe(double? value) => value is { } points and > 0
        ? (points / 2).ToString("0.#", CultureInfo.CurrentCulture).Replace(".5", "½", StringComparison.Ordinal).Replace(",5", "½", StringComparison.Ordinal) + (points == 2 ? " star" : " stars")
        : "No rating";

    /// <summary>The rating a point along the stars gives: each half star a point, 1 to 10.</summary>
    public static double PointsAt(double x, double starSize)
    {
        var halves = (int)Math.Ceiling(x / ((starSize + Gap) / 2));
        return Math.Clamp(halves, 1, 10);
    }

    protected override Size MeasureOverride(Size availableSize) => new((StarSize * 5) + (Gap * 4), StarSize);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Application.Current?.FindResource("Icon.Star.Fill") is not Geometry star) return;
        var shown = _hover ?? Value ?? 0;
        var fill = _hover is null ? Fill ?? Resource("Brush.Accent") ?? Brushes.Orange : Resource("Brush.Accent.Light") ?? Brushes.Orange;
        var empty = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var scale = StarSize / 256;
        for (var n = 0; n < 5; n++)
        {
            var x = n * (StarSize + Gap);
            var portion = Math.Clamp((shown / 2) - n, 0, 1);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, 0)))
            {
                context.DrawGeometry(empty, null, star);
            }

            if (portion <= 0) continue;
            using (context.PushClip(new Rect(x, 0, StarSize * portion, StarSize)))
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, 0)))
            {
                context.DrawGeometry(fill, null, star);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        _hover = PointsAt(e.GetPosition(this).X, StarSize);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var points = PointsAt(e.GetPosition(this).X, StarSize);
        Value = Value == points ? null : points;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        double? next = e.Key switch
        {
            Key.Right or Key.Up => Math.Min(10, (Value ?? 0) + 1),
            Key.Left or Key.Down => (Value ?? 0) - 1 is var less and > 0 ? less : null,
            Key.Home => 1,
            Key.End => 10,
            Key.Delete or Key.Back or Key.D0 or Key.NumPad0 => null,
            _ => Value,
        };
        if (next != Value || e.Key is Key.Delete or Key.Back)
        {
            Value = next;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private IBrush? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
