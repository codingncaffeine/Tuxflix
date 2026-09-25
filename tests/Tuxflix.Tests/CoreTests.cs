using System.Net;
using System.Text.Json;
using Tuxflix.Core;
using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>A directory of its own under the temporary folder, emptied and removed afterwards.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuxflix-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Left for the next run's temporary-folder cleanup.
        }
    }
}

public sealed class AppPathsTests
{
    [Fact]
    public void XdgLayoutUsesTheVariablesAndIgnoresRelativeOnes()
    {
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = "/home/viewer",
            ["XDG_CONFIG_HOME"] = "/cfg",
            ["XDG_CACHE_HOME"] = "relative/cache",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
        };

        var paths = AppPaths.Resolve(null, name => environment.GetValueOrDefault(name));

        Assert.Equal("/cfg/tuxflix", paths.Config);
        Assert.Equal("/home/viewer/.local/share/tuxflix", paths.Data);
        Assert.Equal("/home/viewer/.local/state/tuxflix", paths.State);
        Assert.Equal("/home/viewer/.cache/tuxflix", paths.Cache);
        Assert.Equal("/run/user/1000/tuxflix/instance.sock", paths.InstanceSocket);
        Assert.False(paths.Portable);
    }

    [Fact]
    public void PortableLayoutKeepsEverythingUnderOneFolderWithItsOwnRuntime()
    {
        var paths = AppPaths.Resolve("/media/stick/tuxflix", name => name == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null);

        Assert.True(paths.Portable);
        Assert.StartsWith("/media/stick/tuxflix/", paths.Config, StringComparison.Ordinal);
        Assert.StartsWith("/media/stick/tuxflix/", paths.Cache, StringComparison.Ordinal);
        Assert.StartsWith("/run/user/1000/tuxflix-", paths.Runtime, StringComparison.Ordinal);
        Assert.NotEqual("/run/user/1000/tuxflix", paths.Runtime);

        // A Unix socket path over 108 bytes cannot be bound at all.
        Assert.True(paths.InstanceSocket.Length < 108);
    }

    [Fact]
    public void WithoutARuntimeDirectoryTheSocketStaysInTheProfilesOwnCache()
    {
        // Never the shared temporary folder, where another user could make the folder first.
        var paths = AppPaths.Resolve(null, name => name == "HOME" ? "/home/viewer" : null);
        var portable = AppPaths.Resolve("/media/stick/tuxflix", _ => null);

        Assert.Equal("/home/viewer/.cache/tuxflix/runtime/instance.sock", paths.InstanceSocket);
        Assert.Equal("/media/stick/tuxflix/cache/runtime", portable.Runtime);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void TheProfileFoldersAreTheUsersAlone()
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        using var scratch = new Scratch();
        var paths = AppPaths.Resolve(Path.Combine(scratch.Root, "profile"), _ => null);

        // One folder made earlier, readable by everyone, as mkdir leaves it.
        Directory.CreateDirectory(paths.Config);
        File.SetUnixFileMode(paths.Config, ownerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        paths.EnsureCreated();

        Assert.All(new[] { paths.Config, paths.Data, paths.State, paths.Cache, paths.Logs, paths.Runtime }, folder => Assert.Equal(ownerOnly, File.GetUnixFileMode(folder)));
    }
}

public sealed class SettingsStoreTests
{
    [Fact]
    public void FirstLoadCreatesAClientIdentifierAndKeepsIt()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");

        var first = SettingsStore.Load(file);
        Assert.Matches("^[0-9a-f]{32}$", first.Current.ClientIdentifier);

        first.Current.RailWidth = 333;
        first.Save();
        Assert.True(first.Flush(TimeSpan.FromSeconds(5)));
        var second = SettingsStore.Load(file);

        Assert.Equal(first.Current.ClientIdentifier, second.Current.ClientIdentifier);
        Assert.Equal(333, second.Current.RailWidth);
    }

    [Fact]
    public void QuickSavesCollapseAndTheLastOneIsOnDisk()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        var store = SettingsStore.Load(file);

        // The writer is held with the first snapshot in hand, so every later save certainly arrives
        // while a write is in progress: the case where a save could be lost.
        using var taken = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        var cancellation = TestContext.Current.CancellationToken;
        store.BeforeWrite = () =>
        {
            taken.Set();
            hold.Wait(TimeSpan.FromSeconds(5), cancellation);
        };
        store.Current.RailWidth = 200;
        store.Save();
        Assert.True(taken.Wait(TimeSpan.FromSeconds(5), cancellation));

        for (var width = 201; width <= 400; width++)
        {
            store.Current.RailWidth = width;
            store.Save();
        }

        store.BeforeWrite = null;
        hold.Set();
        Assert.True(store.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(400, SettingsStore.Load(file).Current.RailWidth);
        Assert.False(File.Exists(file + ".new"));
    }

    [Fact]
    public void AnUnreadableFileIsSetAsideAndTheDefaultsAreUsed()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "settings.json");
        File.WriteAllText(file, "{ this is not json");

        var store = SettingsStore.Load(file);

        Assert.Equal(288, store.Current.RailWidth);
        Assert.True(File.Exists(file + ".unreadable"));
        Assert.Equal("{ this is not json", File.ReadAllText(file + ".unreadable"));
    }
}

public sealed class PlexJsonTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"0\"", false)]
    public void FlagsReadWhicheverWayTheServerWroteThem(string raw, bool expected)
    {
        var json = $$$"""{"MediaContainer":{"Hub":[{"title":"x","more":{{{raw}}}}]}}""";
        var envelope = JsonSerializer.Deserialize(json, PlexJsonContext.Default.PlexEnvelope);
        Assert.Equal(expected, envelope!.MediaContainer!.Hub![0].More);
    }

    [Fact]
    public void NumbersSentAsStringsAreRead()
    {
        const string json = """{"MediaContainer":{"size":"1","Metadata":[{"ratingKey":"42","type":"movie","title":"T","year":"2019","duration":"7200000","viewOffset":"3600000","librarySectionID":"3"}]}}""";
        var item = JsonSerializer.Deserialize(json, PlexJsonContext.Default.PlexEnvelope)!.MediaContainer!.Metadata![0];

        Assert.Equal(2019, item.Year);
        Assert.Equal(3, item.LibrarySectionId);
        Assert.Equal(0.5, item.Progress);
        Assert.False(item.IsWatched);
    }
}

public sealed class ArtworkTests
{
    [Fact]
    public void TypedImagesWinOverTheClassicAttributes()
    {
        var item = new MetadataItem
        {
            Type = "movie",
            Thumb = "/thumb",
            Art = "/art",
            Image =
            [
                new ItemImage { Type = "coverPoster", Url = "/typed-poster" },
                new ItemImage { Type = "background", Url = "/typed-background" },
                new ItemImage { Type = "clearLogo", Url = "/typed-logo" },
            ],
        };

        Assert.Equal("/typed-poster", Artwork.Poster(item));
        Assert.Equal("/typed-background", Artwork.Backdrop(item));
        Assert.Equal("/typed-logo", Artwork.Logo(item));
    }

    [Fact]
    public void OlderServersFallBackToAttributesAndEpisodesBorrowTheirShow()
    {
        var movie = new MetadataItem { Type = "movie", Thumb = "/thumb", Art = "/art" };
        Assert.Equal("/thumb", Artwork.Poster(movie));
        Assert.Equal("/art", Artwork.Backdrop(movie));
        Assert.Null(Artwork.Logo(movie));

        var episode = new MetadataItem { Type = "episode", Thumb = "/still", GrandparentThumb = "/show-poster", GrandparentArt = "/show-art" };
        Assert.Equal("/show-poster", Artwork.Poster(episode));
        Assert.Equal("/show-art", Artwork.Backdrop(episode));
        Assert.Equal("/still", Artwork.Still(episode));
    }
}

public sealed class DemoPaletteTests
{
    [Theory]
    [InlineData(0, 100, 50, "ff0000")]
    [InlineData(120, 100, 50, "00ff00")]
    [InlineData(240, 100, 50, "0000ff")]
    [InlineData(0, 0, 100, "ffffff")]
    [InlineData(0, 0, 0, "000000")]
    public void HslConvertsToPlexStyleHex(float hue, float saturation, float lightness, string expected) =>
        Assert.Equal(expected, DemoPalette.Hex(hue, saturation, lightness));
}

/// <summary>The demo library walked through the real client, as the application walks it.</summary>
public sealed class DemoLibraryTests
{
    private sealed class StubArt : IDemoArtRenderer
    {
        public List<DemoArtRequest> Requests { get; } = [];

        public DemoImage Render(DemoArtRequest request)
        {
            Requests.Add(request);
            return new DemoImage([1, 2, 3], request.Kind == DemoArtKind.Logo ? "image/png" : "image/jpeg");
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static (PlexServerClient Client, StubArt Art, DemoCatalog Catalog) Create()
    {
        var catalog = DemoCatalog.Create(Now);
        var art = new StubArt();
        var identity = new PlexClientIdentity("test-client", "0.0.0", "tests");
        var client = new PlexServerClient(identity.CreateHttpClient(new DemoPlexHandler(catalog, art)), DemoPlexHandler.BaseUri, null, "Demo", isDemo: true);
        return (client, art, catalog);
    }

    [Fact]
    public async Task SectionsAndHubsComeBackThroughTheClient()
    {
        var (client, _, _) = Create();

        var sections = await client.GetSectionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["movie", "show", "artist", "photo"], sections.Select(s => s.Type));

        var hubs = await client.GetHomeHubsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("home.continue", hubs[0].HubIdentifier);
        Assert.NotEmpty(hubs[0].Metadata!);
        Assert.All(hubs[0].Metadata!, item => Assert.InRange(item.Progress ?? 0, 0.01, 0.99));
    }

    [Fact]
    public async Task SectionListingsPageWithTheContainerHeaders()
    {
        var (client, _, catalog) = Create();

        var page = await client.GetSectionItemsAsync(DemoCatalog.MoviesSectionKey, "titleSort", TestContext.Current.CancellationToken, start: 10, size: 5);

        Assert.Equal(5, page.Size);
        Assert.Equal(catalog.Movies.Count, page.TotalSize);
        Assert.Equal(10, page.Offset);
        Assert.Equal(5, page.Metadata!.Count);
    }

    [Fact]
    public async Task ItemsCarryTypedImagesAndTheirArtworkComesBackInTheRightFormat()
    {
        var (client, art, catalog) = Create();
        var movie = await client.GetMetadataAsync(catalog.Movies[0].RatingKey, TestContext.Current.CancellationToken);

        Assert.NotNull(movie);
        var logo = Artwork.Logo(movie);
        Assert.NotNull(logo);
        Assert.NotNull(movie.UltraBlurColors);

        var poster = await client.GetBytesAsync(client.ImageUri(Artwork.Poster(movie)!, 164, 246), TestContext.Current.CancellationToken);
        var clear = await client.GetBytesAsync(client.ImageUri(logo, 520, 170, ImageFormat.Png), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], poster);
        Assert.Equal([1, 2, 3], clear);
        Assert.Equal(DemoArtKind.Poster, art.Requests[0].Kind);
        Assert.Equal(DemoArtKind.Logo, art.Requests[1].Kind);
        Assert.Equal((164, 246), (art.Requests[0].Width, art.Requests[0].Height));
    }

    [Fact]
    public async Task UltraBlurColoursAreFourHexColours()
    {
        var (client, _, catalog) = Create();

        var colours = await client.GetUltraBlurColorsAsync(catalog.Movies[3].Art!, TestContext.Current.CancellationToken);

        Assert.NotNull(colours);
        foreach (var hex in new[] { colours.TopLeft, colours.TopRight, colours.BottomRight, colours.BottomLeft })
        {
            Assert.Matches("^[0-9a-f]{6}$", hex);
        }
    }

    [Fact]
    public async Task AnUnknownPathIsNotFound()
    {
        var (client, _, _) = Create();
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/no/such/thing", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
    }

    [Fact]
    public void SeriesHaveSeasonsAndEpisodesWithConsistentCounts()
    {
        var catalog = DemoCatalog.Create(Now);
        foreach (var show in catalog.Shows)
        {
            var seasons = catalog.ChildrenOf(show.RatingKey);
            var episodes = seasons.SelectMany(s => catalog.ChildrenOf(s.RatingKey)).ToList();
            Assert.Equal(show.ChildCount, seasons.Count);
            Assert.Equal(show.LeafCount, episodes.Count);
            Assert.Equal(show.ViewedLeafCount, episodes.Count(e => e.IsWatched));
        }
    }
}

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task ASecondLaunchHandsItsArgumentsToTheFirst()
    {
        // Sockets need a short path (the 108-byte limit), so this one lives in the runtime directory.
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : "/tmp";
        var root = Path.Combine(runtime, "tuxflix-t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        using var cleanup = new DeleteOnDispose(root);
        var socket = Path.Combine(root, "i.sock");
        var lockFile = Path.Combine(root, "i.lock");
        var received = new TaskCompletionSource<IReadOnlyList<string>>();

        using var first = new SingleInstance(socket, lockFile);
        first.ArgumentsReceived += arguments => received.TrySetResult(arguments);
        Assert.False(first.TryHandOff([]));

        using var second = new SingleInstance(socket, lockFile);
        Assert.True(second.TryHandOff(["--demo", "tuxflix://item/1001"]));

        var arguments = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["--demo", "tuxflix://item/1001"], arguments);
    }
}

/// <summary>Removes one directory the test made, by name.</summary>
internal sealed class DeleteOnDispose(string directory) : IDisposable
{
    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Nothing else lives in it; the next boot clears the runtime directory anyway.
        }
    }
}

public sealed class SingleInstanceLimitTests
{
    [Fact]
    public void ASocketPathTooLongToBindRunsAsALoneInstanceInsteadOfFailing()
    {
        using var scratch = new Scratch();
        var deep = Path.Combine(scratch.Root, new string('d', 120));
        Directory.CreateDirectory(deep);
        using var instance = new SingleInstance(Path.Combine(deep, "i.sock"), Path.Combine(deep, "i.lock"));

        Assert.False(instance.TryHandOff(["--demo"]));
    }
}
