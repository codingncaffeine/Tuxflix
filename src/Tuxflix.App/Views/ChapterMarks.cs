using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Tuxflix.App.Views;

/// <summary>Ticks on the seek bar where each chapter begins, drawn under the thumb.</summary>
public sealed class ChapterMarks : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> MarksProperty =
        AvaloniaProperty.Register<ChapterMarks, IReadOnlyList<double>?>(nameof(Marks));

    private static readonly IBrush Tick = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    static ChapterMarks()
    {
        AffectsRender<ChapterMarks>(MarksProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ChapterMarks>(false);
    }

    public IReadOnlyList<double>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Marks is not { Count: > 0 } marks) return;
        var height = Math.Min(8, Bounds.Height);
        var top = (Bounds.Height - height) / 2;
        foreach (var mark in marks)
        {
            var x = Math.Round(Math.Clamp(mark, 0, 1) * Bounds.Width);
            context.FillRectangle(Tick, new Rect(x - 1, top, 2, height));
        }
    }
}
