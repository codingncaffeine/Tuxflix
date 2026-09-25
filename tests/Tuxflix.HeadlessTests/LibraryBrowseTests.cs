using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Browsing a library against the demo server: the grid's rows, sorting, filters, the letter
/// strip, collections, search and people. The demo answers the same requests a server does.
/// </summary>
public sealed class LibraryBrowseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "browse-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellViewModel _shell;
    private readonly SettingsStore _settings;
    private readonly ServerSession _session;
    private readonly DemoCatalog _catalog = DemoCatalog.Create(DateTimeOffset.Now);

    public LibraryBrowseTests()
    {
        // The demo draws its art with the application's fonts, which the headless platform provides.
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths);
        _session = ServerSession.CreateDemo(_shell.Identity);
    }

    public void Dispose()
    {
        _session.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public void ALaterPageCompletesTheLastRowAndLeavesTheRowsAboveAlone()
    {
        var grid = new TileGrid(TileShape.Poster);
        grid.Fit((182 * 4) - 18);
        Assert.Equal(4, grid.Columns);

        grid.Add(Enumerable.Range(0, 6).Select(i => (object)i));
        var firstRow = grid.Rows[0];
        grid.Add(Enumerable.Range(6, 5).Select(i => (object)i));

        Assert.Same(firstRow, grid.Rows[0]);
        Assert.Equal([4, 4, 3], grid.Rows.Select(r => r.Tiles.Count));
        Assert.Equal(Enumerable.Range(0, 11).Select(i => (object)i), grid.Rows.SelectMany(r => r.Tiles));
        Assert.Equal(2, grid.RowOf(10));

        grid.Fit((182 * 5) - 18);
        Assert.Equal([5, 5, 1], grid.Rows.Select(r => r.Tiles.Count));
    }

    [Fact]
    public async Task TheLibraryListsSortsAndFiltersAsTheServerDoes()
    {
        var page = new LibraryPageViewModel(_shell, _session, Movies());
        page.Fit(1200, 1);
        await page.ActivateAsync();

        Assert.Null(page.ErrorMessage);
        Assert.Equal(_catalog.Movies.Count, page.Grid.Count);
        Assert.Equal(page.Grid.Count, page.TotalCount);
        Assert.Contains(page.Sorts, s => s.Key == "titleSort" && s.IsSelected);
        Assert.Contains(page.Filters, f => f.Filter == "genre" && f.HasValues);
        Assert.True(page.HasCollections);

        // Year, newest first.
        await page.Sorts.Single(s => s.Key == "year").ChooseCommand.ExecuteAsync(null);
        Assert.True(page.Descending);
        Assert.Equal(_catalog.Movies.Max(m => m.Year), Titles(page)[0].Year);
        Assert.Contains("sort=year%3Adesc", page.Query(), StringComparison.Ordinal);

        // A genre, then unwatched on top of it.
        var genre = page.Filters.Single(f => f.Filter == "genre");
        var mystery = genre.Values.Single(v => v.Title == "Mystery");
        await mystery.ChooseCommand.ExecuteAsync(null);
        var withGenre = _catalog.Movies.Where(m => m.Genre!.Any(g => g.TagText == "Mystery")).ToList();
        Assert.Equal(withGenre.Count, page.Grid.Count);
        Assert.All(Titles(page), m => Assert.Contains(m.RatingKey, withGenre.Select(w => w.RatingKey)));

        await page.Filters.Single(f => f.Filter == "unwatched").ToggleCommand.ExecuteAsync(null);
        Assert.Equal(withGenre.Count(m => !m.IsWatched && m.Progress is null), page.Grid.Count);
        Assert.Equal(2, page.ActiveFilters.Count);

        await page.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(_catalog.Movies.Count, page.Grid.Count);
    }

    [Fact]
    public async Task TheLetterStripPointsAtEachInitialsFirstTitle()
    {
        var page = new LibraryPageViewModel(_shell, _session, Movies());
        page.Fit(1200, 1);
        await page.ActivateAsync();

        Assert.True(page.ShowsLetters);
        var titles = Titles(page);
        foreach (var letter in page.Letters)
        {
            var first = titles.FindIndex(m => char.ToUpperInvariant((m.TitleSort ?? m.Title)[0]).ToString() == letter.Letter);
            Assert.Equal(first, letter.Index);
        }
    }

    [Fact]
    public async Task CollectionsListAndOpenOntoTheirMembers()
    {
        var page = new LibraryPageViewModel(_shell, _session, Movies());
        page.Fit(1200, 1);
        await page.ActivateAsync();
        await page.ShowViewCommand.ExecuteAsync(LibraryView.Collections);

        Assert.Equal(_catalog.Collections.Count, page.Grid.Count);
        var collection = _catalog.Collections[0];
        var members = new CollectionPageViewModel(_shell, _session, collection);
        await members.ActivateAsync();
        Assert.Equal(_catalog.ChildrenOf(collection.RatingKey).Count, members.Grid.Count);
    }

    [Fact]
    public async Task SearchFindsTitlesAndPeopleAndAPersonOpensOntoTheirWork()
    {
        var actor = _catalog.Movies[0].Role![0];
        var search = new SearchPageViewModel(_shell, _session);
        await search.SearchAsync(actor.TagText.Split(' ')[1]);

        var people = search.Shelves.Single(s => s.Title == "ACTORS").Tiles.OfType<PersonTileViewModel>().ToList();
        Assert.Contains(people, p => p.Name == actor.TagText);

        var person = new PersonPageViewModel(_shell, _session, actor.TagText, actor.Thumb, actor.Id!.Value);
        await person.ActivateAsync();
        var expected = _catalog.Movies.Concat(_catalog.Shows).Count(i => i.Role?.Any(r => r.Id == actor.Id) == true);
        Assert.Equal(expected, person.Grid.Count);
        Assert.True(expected > 0);
    }

    private LibraryDirectory Movies() => _catalog.Sections.Single(s => s.Key == DemoCatalog.MoviesSectionKey);

    private static List<MetadataItem> Titles(LibraryPageViewModel page) =>
        [.. page.Grid.Tiles.OfType<MediaTileViewModel>().Select(t => t.Item)];
}
