using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views;

/// <summary>
/// One row of a grid that keeps its tile controls and hands them the next row's tiles. A tile of
/// the same kind takes the new tile as its data; only a different kind builds a control. Building
/// a tile is what costs a frame, so scrolling a grid binds rather than builds.
/// </summary>
public sealed class TileRow : StackPanel
{
    /// <summary>How many rows have been built: a probe checks scrolling reuses them.</summary>
    internal static int Built;

    public TileRow()
    {
        Interlocked.Increment(ref Built);
        Orientation = Avalonia.Layout.Orientation.Horizontal;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not GridRowViewModel row)
        {
            return;
        }

        Spacing = row.Spacing;
        Margin = row.Gap;
        while (Children.Count > row.Tiles.Count) Children.RemoveAt(Children.Count - 1);
        for (var i = 0; i < row.Tiles.Count; i++)
        {
            var tile = row.Tiles[i];
            if (i < Children.Count && Children[i].DataContext?.GetType() == tile.GetType())
            {
                Children[i].DataContext = tile;
                continue;
            }

            var view = this.FindDataTemplate(tile)?.Build(tile) ?? new TextBlock { Text = tile.ToString() };
            view.DataContext = tile;
            if (i < Children.Count) Children[i] = view;
            else Children.Add(view);
        }
    }
}

/// <summary>
/// The list of a grid's rows, whose containers are the rows themselves. The virtualising panel
/// keeps containers that scroll away and hands them the rows that scroll in, so a row control is
/// built once per visible row, not once per row of the library.
/// </summary>
public sealed class TileRows : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = DefaultRecycleKey;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new TileRow();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        container.DataContext = item;
    }
}
