namespace Tuxflix.Core.Settings;

/// <summary>How the libraries are browsed: each server's home shelves, and theme music.</summary>
public sealed class BrowseSettings
{
    /// <summary>Each server's home shelves as the viewer arranged them, by the server's machine identifier.</summary>
    public Dictionary<string, ShelfLayout> Home { get; set; } = [];

    /// <summary>A series' or a film's theme music plays quietly while its page is open.</summary>
    public bool ThemeMusic { get; set; } = true;

    /// <summary>The size of the tiles in library grids against their usual size, from 0.75 to 1.5.</summary>
    public double GridScale { get; set; } = 1;

    /// <summary>The layout of one server's home shelves, begun empty the first time it is asked for.</summary>
    public ShelfLayout HomeOf(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!Home.TryGetValue(server, out var layout)) Home[server] = layout = new ShelfLayout();
        return layout;
    }
}

/// <summary>
/// The order and the hiding of one server's home shelves, by shelf identifier.
/// </summary>
/// <remarks>
/// Only what the viewer changed is kept. A shelf the server starts offering later takes its place
/// after the shelf that comes before it in the server's own order, so it appears where the server
/// put it rather than at the bottom, and the viewer's arrangement around it stays as it was.
/// </remarks>
public sealed class ShelfLayout
{
    /// <summary>Every shelf in the viewer's order, as it was last arranged; empty until the first move.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>The shelves the viewer hid.</summary>
    public List<string> Hidden { get; set; } = [];

    public bool IsHidden(string shelf) => Hidden.Contains(shelf, StringComparer.Ordinal);

    /// <summary>The shelves on offer, <paramref name="offered"/> in the server's order, put in the viewer's order; hidden ones included.</summary>
    public IReadOnlyList<string> Arrange(IReadOnlyList<string> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var arranged = Order.Where(offered.Contains).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < offered.Count; i++)
        {
            if (arranged.Contains(offered[i], StringComparer.Ordinal)) continue;
            var before = offered.Take(i).LastOrDefault(arranged.Contains);
            arranged.Insert(before is null ? 0 : arranged.IndexOf(before) + 1, offered[i]);
        }

        return arranged;
    }

    /// <summary>
    /// Moves a shelf one place up (<paramref name="by"/> -1) or down (1) among the shown ones,
    /// past any hidden shelf between them. <paramref name="arranged"/> is the order in use now.
    /// </summary>
    /// <returns>Whether it moved: the first shelf cannot go up, nor the last down.</returns>
    public bool Move(IReadOnlyList<string> arranged, string shelf, int by)
    {
        ArgumentNullException.ThrowIfNull(arranged);
        var shown = arranged.Where(s => !IsHidden(s)).ToList();
        var from = shown.IndexOf(shelf);
        var to = from + Math.Sign(by);
        if (from < 0 || by == 0 || to < 0 || to >= shown.Count) return false;

        var order = arranged.Where(s => s != shelf).ToList();
        var neighbour = order.IndexOf(shown[to]);
        order.Insert(by < 0 ? neighbour : neighbour + 1, shelf);
        Order = order;
        return true;
    }

    public void Hide(string shelf)
    {
        if (!IsHidden(shelf)) Hidden.Add(shelf);
    }

    public void Show(string shelf) => Hidden.RemoveAll(s => s == shelf);
}
