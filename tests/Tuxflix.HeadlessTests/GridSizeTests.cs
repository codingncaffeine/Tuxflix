using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.App.Views.Tiles;
using Tuxflix.Core;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The grid size slider: library grids draw their tiles larger or smaller and fit their columns to
/// match, while tiles anywhere else keep their size. The size is the application's, so these run
/// alone and put it back.
/// </summary>
[Collection(nameof(KeyboardFocus))]
public sealed class GridSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "grid-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SettingsStore _settings;
    private readonly ShellViewModel _shell;

    public GridSizeTests()
    {
        HeadlessApp.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths) { NotificationSources = _ => new FakeNotifications(), Silent = true, ReportsPlayback = false };
        _shell.OpenDemo();
    }

    public void Dispose()
    {
        _shell.Session?.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public void LargerTilesFitFewerColumnsAndTheSizeKeepsToTheSlidersSteps()
    {
        var grid = new TileGrid(TileShape.Poster);
        grid.Add(Enumerable.Range(0, 30).Select(i => (object)i));

        // 1,200 px: a column is a tile and 18 px, less the last gap.
        grid.Fit(1200, 1);
        Assert.Equal(6, grid.Columns);
        grid.Fit(1200, 1.5);
        Assert.Equal(4, grid.Columns);
        Assert.Equal(8, grid.Rows.Count);
        grid.Fit(1200, 0.75);
        Assert.Equal(8, grid.Columns);

        Assert.Equal(1.25, TileSizes.Clamp(1.3));
        Assert.Equal(0.75, TileSizes.Clamp(0.1));
        Assert.Equal(1.5, TileSizes.Clamp(9));
        Assert.Equal(1, TileSizes.Clamp(double.NaN));
    }

    [Fact]
    public Task TheSliderSizesTheTilesOfLibraryGridsAndOnlyThose() => HeadlessApp.Run(async () =>
    {
        var session = _shell.Session!;
        var section = (await session.Client.GetSectionsAsync(TestContext.Current.CancellationToken)).First(s => s.Type == "movie");
        var library = new LibraryPageViewModel(_shell, session, section);
        var room = new LibraryPageViewModel(_shell, session, section);
        var grid = new RowsGrid { DataContext = library, Width = 1300, Height = 700 };
        var tvGrid = new RowsGrid { DataContext = room, Width = 1300, Height = 700, FollowsTileSize = false };
        var loose = new PosterTile { DataContext = new PosterTileViewModel(_shell, session.Demo!.Movies[0]) };
        var window = new Window { Width = 1400, Height = 2000, Content = new StackPanel { Children = { grid, tvGrid, loose } } };
        window.Show();
        try
        {
            await library.ActivateAsync();
            await room.ActivateAsync();
            double Width(Control within) => within.GetVisualDescendants().OfType<RemoteImage>().First().DecodeWidth;
            void Settle()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }

            Settle();
            Assert.Equal(TileSizes.PosterWidth, Width(grid));
            var columns = library.Grid.Columns;

            _shell.GridScale = 1.5;
            Settle();
            Assert.Equal(TileSizes.PosterWidth * 1.5, Width(grid));
            Assert.Equal(1.5, library.Grid.Scale);
            Assert.True(library.Grid.Columns < columns, $"{library.Grid.Columns} columns at 1.5, {columns} at the usual size");
            Assert.Equal(TileSizes.PosterWidth, Width(tvGrid));
            Assert.Equal(TileSizes.PosterWidth, Width(loose));
            Assert.Equal(1.5, _settings.Current.Browse.GridScale);
            Assert.Equal(1.5, library.GridScale);

            // The slider's steps only: a value between two is taken to the nearer.
            _shell.GridScale = 0.8;
            Settle();
            Assert.Equal(TileSizes.PosterWidth * 0.75, Width(grid));
        }
        finally
        {
            _shell.GridScale = 1;
            window.Close();
            library.Deactivate();
            room.Deactivate();
        }
    });
}
