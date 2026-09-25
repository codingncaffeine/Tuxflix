using Avalonia.Controls;

namespace Tuxflix.App.Views.Tiles;

public partial class PosterTile : UserControl
{
    /// <summary>How many poster tiles have been built: a probe checks scrolling reuses them.</summary>
    internal static int Built;

    public PosterTile()
    {
        Interlocked.Increment(ref Built);
        InitializeComponent();
    }
}
