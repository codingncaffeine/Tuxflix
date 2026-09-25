using Avalonia;
using Avalonia.Controls;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views;

/// <summary>
/// A page's grid of tiles: fits its columns to its width, sizes its tiles by the grid size slider,
/// and scrolls to a row when asked.
/// </summary>
/// <remarks>
/// The slider's scale arrives as the application's <c>Tile.Scale</c> resource; the grid turns it
/// into tile sizes of its own, which only its tiles see, so shelves elsewhere keep theirs.
/// </remarks>
public partial class RowsGrid : UserControl
{
    /// <summary>The tiles' size against their usual one, from the application's <c>Tile.Scale</c>.</summary>
    public static readonly StyledProperty<double> TileScaleProperty =
        AvaloniaProperty.Register<RowsGrid, double>(nameof(TileScale), 1);

    /// <summary>Whether the grid sizes its tiles by the slider; the TV interface's does not.</summary>
    public static readonly StyledProperty<bool> FollowsTileSizeProperty =
        AvaloniaProperty.Register<RowsGrid, bool>(nameof(FollowsTileSize), true);

    private LibraryPageViewModel? _library;

    public RowsGrid()
    {
        InitializeComponent();
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Fit();
        };
        Bind(TileScaleProperty, this.GetResourceObservable("Tile.Scale", value => value is double scale ? scale : 1.0));
    }

    public double TileScale
    {
        get => GetValue(TileScaleProperty);
        set => SetValue(TileScaleProperty, value);
    }

    public bool FollowsTileSize
    {
        get => GetValue(FollowsTileSizeProperty);
        set => SetValue(FollowsTileSizeProperty, value);
    }

    /// <summary>The scroller, for probes that drive and measure scrolling.</summary>
    public ScrollViewer ScrollHost => Scroller;

    /// <summary>The scale the grid's tiles are drawn at now.</summary>
    private double Scale => FollowsTileSize ? TileSizes.Clamp(TileScale) : 1;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TileScaleProperty && change.Property != FollowsTileSizeProperty) return;
        var scale = Scale;
        Resources["Tile.Poster.Width"] = TileSizes.PosterWidth * scale;
        Resources["Tile.Poster.Height"] = TileSizes.PosterHeight * scale;
        Resources["Tile.Landscape.Width"] = TileSizes.LandscapeWidth * scale;
        Resources["Tile.Landscape.Height"] = TileSizes.LandscapeHeight * scale;
        Resources["Tile.Square"] = TileSizes.Square * scale;
        Fit();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_library is not null) _library.ScrollToRow -= OnScrollToRow;
        _library = DataContext as LibraryPageViewModel;
        if (_library is not null) _library.ScrollToRow += OnScrollToRow;
        Fit();
    }

    private void Fit()
    {
        // The scale can arrive while the grid's own parts are still being made.
        if (Scroller is null || Items is null) return;
        var width = Scroller.Bounds.Width - Items.Margin.Left - Items.Margin.Right;
        if (width > 0 && DataContext is IGridPage page) page.Fit(width, Scale);
    }

    /// <summary>Puts a row at the top of the view: a letter on the strip was chosen.</summary>
    private void OnScrollToRow(int row)
    {
        if (row < 0) return;
        Items.ScrollIntoView(row);
        if (Items.ContainerFromIndex(row) is Control container && container.TranslatePoint(default, Scroller) is { } at)
        {
            Scroller.Offset = new Vector(0, Math.Max(0, Scroller.Offset.Y + at.Y - Items.Margin.Top));
        }
    }
}
