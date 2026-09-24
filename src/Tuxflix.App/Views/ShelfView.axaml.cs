using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tuxflix.App.Views;

public partial class ShelfView : UserControl
{
    public ShelfView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => UpdateArrows();
        Scroller.SizeChanged += (_, _) => UpdateArrows();
    }

    private void OnScrollLeft(object? sender, RoutedEventArgs e) => Page(-1);

    private void OnScrollRight(object? sender, RoutedEventArgs e) => Page(1);

    /// <summary>Glides by most of a screenful, keeping one tile of context from the last page.</summary>
    private void Page(int direction)
    {
        var maximum = Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width);
        var target = Math.Clamp(Scroller.Offset.X + direction * Scroller.Viewport.Width * 0.82, 0, maximum);

        // The glide is only for the arrows: a wheel or a touchpad must follow the hand exactly.
        Scroller.Transitions =
        [
            new VectorTransition { Property = ScrollViewer.OffsetProperty, Duration = TimeSpan.FromMilliseconds(360), Easing = new CubicEaseOut() },
        ];
        Scroller.Offset = new Vector(target, Scroller.Offset.Y);
        _ = ClearGlideAsync();
    }

    private async Task ClearGlideAsync()
    {
        await Task.Delay(400);
        Scroller.Transitions = null;
    }

    private void UpdateArrows()
    {
        var maximum = Scroller.Extent.Width - Scroller.Viewport.Width;
        LeftButton.IsEnabled = Scroller.Offset.X > 1;
        RightButton.IsEnabled = Scroller.Offset.X < maximum - 1;
    }
}
