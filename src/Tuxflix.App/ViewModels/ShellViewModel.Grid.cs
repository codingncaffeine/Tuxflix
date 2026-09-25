using Avalonia.Threading;

namespace Tuxflix.App.ViewModels;

// The size of the tiles in library grids, as Steam's library and Plex's own have a slider for.
public sealed partial class ShellViewModel
{
    /// <summary>The tiles' size in every library grid against their usual size: 0.75 to 1.5, in eighths.</summary>
    /// <remarks>
    /// Kept in the settings and handed to the views as the application's <c>Tile.Scale</c>
    /// resource, from which each grid sizes its own tiles; so it is set on the UI thread.
    /// </remarks>
    public double GridScale
    {
        get => TileSizes.Clamp(Settings.Browse.GridScale);
        set
        {
            var scale = TileSizes.Clamp(value);
            if (scale.Equals(Settings.Browse.GridScale)) return;
            Settings.Browse.GridScale = scale;
            SaveSettings();
            ShowGridScale();
            OnPropertyChanged();
        }
    }

    /// <summary>Hands the grid size to the views: at start, and when it changes.</summary>
    internal void ShowGridScale()
    {
        if (Avalonia.Application.Current is not { } app) return;
        Dispatcher.UIThread.VerifyAccess();
        app.Resources["Tile.Scale"] = GridScale;
    }
}
