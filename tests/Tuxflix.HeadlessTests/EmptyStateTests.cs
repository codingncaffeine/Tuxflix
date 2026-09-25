using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Every page says what is wrong when it could not be loaded, with TRY AGAIN, and says what an
/// empty list means rather than showing a blank. The pages are drawn in a window of their own,
/// so these run alone.
/// </summary>
[Collection(nameof(KeyboardFocus))]
public sealed class EmptyStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "empty-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SettingsStore _settings;
    private readonly ShellViewModel _shell;

    public EmptyStateTests()
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

    private ServerSession Session => _shell.Session!;

    private async Task<(LibraryDirectory Movies, LibraryDirectory Photos)> SectionsAsync()
    {
        var sections = await Session.Client.GetSectionsAsync(TestContext.Current.CancellationToken);
        return (sections.First(s => s.Type == "movie"), sections.First(s => s.Type == "photo"));
    }

    /// <summary>Draws a page in a window, as the router's view would, and hands back the window.</summary>
    private static Window Draw(PageViewModel page)
    {
        var window = new Window { Width = 1400, Height = 900, Content = new ContentControl { Content = page } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>The one state on show that says <paramref name="text"/>.</summary>
    private static EmptyState Saying(Window window, string text) =>
        Assert.Single(window.GetVisualDescendants().OfType<EmptyState>(), s => s.IsEffectivelyVisible && s.Text == text);

    [Fact]
    public Task EveryPageThatCouldNotBeLoadedSaysSoAndOffersToTryAgain() => HeadlessApp.Run(async () =>
    {
        var (movies, photos) = await SectionsAsync();
        var demo = Session.Demo!;
        var film = demo.Movies[0];
        var album = demo.Albums[0];
        PageViewModel[] pages =
        [
            new HomePageViewModel(_shell, Session),
            new LibraryPageViewModel(_shell, Session, movies),
            new CollectionPageViewModel(_shell, Session, demo.CollectionsOf(Core.Demo.DemoCatalog.MoviesSectionKey)[0]),
            new PersonPageViewModel(_shell, Session, "Someone", null, 1),
            new PlaylistsPageViewModel(_shell, Session),
            new PlaylistPageViewModel(_shell, Session, new MetadataItem { RatingKey = "1", Type = "playlist", Title = "A playlist", PlaylistType = "video" }),
            new PhotosPageViewModel(_shell, Session, photos),
            new SearchPageViewModel(_shell, Session),
            new WatchlistPageViewModel(_shell, Session),
            new ItemPageViewModel(_shell, Session, film),
            new AlbumPageViewModel(_shell, Session, album),
            new ArtistPageViewModel(_shell, Session, new MetadataItem { RatingKey = album.ParentRatingKey!, Type = "artist", Title = album.ParentTitle ?? string.Empty }),
        ];

        foreach (var page in pages)
        {
            page.ErrorMessage = $"{page.GetType().Name} broke.";
            var window = Draw(page);
            try
            {
                var state = Saying(window, page.ErrorMessage);
                Assert.True(state.IsProblem);
                Assert.True(state.Action.IsEffectivelyVisible, $"{page.GetType().Name} offers no way to try again");
                Assert.Equal("TRY AGAIN", state.Action.Content);
                Assert.Same(page.RetryCommand, state.Action.Command);
            }
            finally
            {
                window.Close();
            }
        }

        // TRY AGAIN loads the page again: the failure goes and the library fills.
        var library = new LibraryPageViewModel(_shell, Session, movies) { ErrorMessage = "The server did not answer." };
        var shown = Draw(library);
        try
        {
            await library.RetryCommand.ExecuteAsync(null);
            Assert.False(library.HasError);
            Assert.True(library.Grid.Count > 0);
        }
        finally
        {
            shown.Close();
        }
    });

    [Fact]
    public Task EveryListWithNothingInItSaysWhatThatMeans() => HeadlessApp.Run(async () =>
    {
        var (movies, photos) = await SectionsAsync();
        var demo = Session.Demo!;

        // A page not loaded yet has nothing in its list: what each says of that.
        (PageViewModel Page, string Text)[] empties =
        [
            (new HomePageViewModel(_shell, Session), "Nothing to show on this server yet: its libraries are empty, or none are shared with you."),
            (new LibraryPageViewModel(_shell, Session, movies), "This library is empty. Titles appear here as the server adds them."),
            (new CollectionPageViewModel(_shell, Session, demo.CollectionsOf(Core.Demo.DemoCatalog.MoviesSectionKey)[0]), "This collection is empty."),
            (new PersonPageViewModel(_shell, Session, "Someone", null, 1), "Nothing on this server features Someone."),
            (new PlaylistsPageViewModel(_shell, Session), "No playlists yet. Make one from any title's menu, under Add to playlist."),
            (new PlaylistPageViewModel(_shell, Session, new MetadataItem { RatingKey = "1", Type = "playlist", Title = "A playlist", PlaylistType = "video" }),
                "This playlist is empty. Films, episodes and tracks go in from their menus, under Add to playlist."),
            (new PhotosPageViewModel(_shell, Session, photos), "No photos here."),
            (new SearchPageViewModel(_shell, Session), "Type in the search box to look through every library."),
        ];

        foreach (var (page, text) in empties)
        {
            var window = Draw(page);
            try
            {
                Saying(window, text);
            }
            finally
            {
                window.Close();
            }
        }

        // A search that found nothing says what was asked.
        var search = new SearchPageViewModel(_shell, Session);
        await search.SearchAsync("zzqx");
        var searched = Draw(search);
        try
        {
            Saying(searched, "Nothing in your libraries matches “zzqx”.");
        }
        finally
        {
            searched.Close();
        }

        // Filters that leave nothing say so, and offer to come off: pressed, the library is back.
        var library = new LibraryPageViewModel(_shell, Session, movies);
        await library.ActivateAsync();
        var filtered = Draw(library);
        try
        {
            var genre = library.Filters.Single(f => f.Filter == "genre");
            await library.SetFilterAsync(genre, new FilterValueViewModel(genre, "999999", "No such genre"));
            Dispatcher.UIThread.RunJobs();
            filtered.UpdateLayout();
            var state = Saying(filtered, "Nothing here matches these filters.");
            Assert.Equal("CLEAR FILTERS", state.Action.Content);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)state.Action.Command!).ExecuteAsync(null);
            Assert.Empty(library.ActiveFilters);
            Assert.True(library.Grid.Count > 0);
        }
        finally
        {
            filtered.Close();
        }
    });
}
