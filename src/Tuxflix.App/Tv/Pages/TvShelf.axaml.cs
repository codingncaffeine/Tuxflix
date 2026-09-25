using Avalonia;
using Avalonia.Controls;

namespace Tuxflix.App.Tv.Pages;

/// <summary>A shelf of tiles in the TV interface.</summary>
public partial class TvShelf : UserControl
{
    /// <summary>How far in from the left the shelf starts, in the TV layout's pixels (the overscan-safe margin by default).</summary>
    public static readonly StyledProperty<double> InsetProperty =
        AvaloniaProperty.Register<TvShelf, double>(nameof(Inset), 96);

    /// <summary>The scale the tiles are drawn at, as the row's transform sets it.</summary>
    private const double TileScale = 1.3;

    public TvShelf()
    {
        InitializeComponent();
        Apply();
    }

    public double Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InsetProperty) Apply();
    }

    private void Apply()
    {
        Heading.Margin = new Thickness(Inset, 0, 0, 0);
        Tiles.Margin = new Thickness(Inset / TileScale, 14, 74, 24);
    }
}
