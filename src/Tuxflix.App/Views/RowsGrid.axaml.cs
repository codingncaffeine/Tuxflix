using Avalonia;
using Avalonia.Controls;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views;

/// <summary>A page's grid of tiles: fits its columns to its width, and scrolls to a row when asked.</summary>
public partial class RowsGrid : UserControl
{
    private LibraryPageViewModel? _library;

    public RowsGrid()
    {
        InitializeComponent();
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Fit();
        };
    }

    /// <summary>The scroller, for probes that drive and measure scrolling.</summary>
    public ScrollViewer ScrollHost => Scroller;

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
        var width = Scroller.Bounds.Width - Items.Margin.Left - Items.Margin.Right;
        if (width > 0 && DataContext is IGridPage page) page.Fit(width);
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
