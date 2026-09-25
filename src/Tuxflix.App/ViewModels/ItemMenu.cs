using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Tuxflix.App.Controls;
using Tuxflix.App.Views;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Downloads;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The menu of anything that shows an item, by the item's kind: what it plays, the viewer's own
/// marks (watched, a rating), what keeps it (the Watchlist, a playlist, a download), and where it
/// belongs. Plex's "…" menu with the viewer's actions only: nothing here edits the library.
/// </summary>
/// <remarks>
/// Built as the menu opens, from the item's shared state, so it says what is true now. Whether a
/// title is on the Watchlist takes a request: that line says what was last known and settles when
/// the answer comes. Everything that asks the server runs on a worker and says in the status bar
/// when it could not.
/// </remarks>
public static class ItemMenu
{
    /// <summary>The menu's lines for an item, in groups parted by separators.</summary>
    /// <param name="anchor">What a panel the menu opens (a new playlist's name) is shown beside.</param>
    public static IReadOnlyList<MenuEntry> For(IViewerItem target, Control? anchor)
    {
        ArgumentNullException.ThrowIfNull(target);
        var entries = new List<MenuEntry>();
        foreach (var group in new[] { Playing(target), Marks(target), Keeping(target, anchor), About(target, anchor) })
        {
            if (group.Count == 0) continue;
            if (entries.Count > 0) entries.Add(MenuEntry.Separator);
            entries.AddRange(group);
        }

        return entries;
    }

    private static List<MenuEntry> Playing(IViewerItem target)
    {
        var shell = target.Shell;
        var item = target.Item;
        var music = shell.Music;
        switch (item.Type)
        {
            case "movie" or "episode":
                return target.State is { HasProgress: true, ViewOffset: { } offset }
                    ?
                    [
                        new($"Resume from {Format.Clock(offset / 1000.0)}", () => shell.Play(item, resume: true)),
                        new("Play from the start", () => shell.Play(item, resume: false)),
                    ]
                    : [new("Play", () => shell.Play(item, resume: false))];

            case "show" or "season":
                return
                [
                    new("Play", () => Run(shell, () => PlayNextAsync(shell, item), "The server could not be asked what to play next")),
                    new("Shuffle", () => Run(shell, () => ShuffleEpisodesAsync(shell, item), $"The episodes of {item.Title} could not be listed")),
                ];

            case "collection":
                return
                [
                    new("Play", () => Run(shell, () => PlayCollectionAsync(shell, item, shuffle: false), $"{item.Title} could not be listed")),
                    new("Shuffle", () => Run(shell, () => PlayCollectionAsync(shell, item, shuffle: true), $"{item.Title} could not be listed")),
                ];

            case "playlist":
                return
                [
                    new("Play", () => Run(shell, () => Playlists.PlayAsync(shell, item, shuffle: false), $"{item.Title} could not be listed")),
                    new("Shuffle", () => Run(shell, () => Playlists.PlayAsync(shell, item, shuffle: true), $"{item.Title} could not be listed")),
                ];

            case "album" when music is not null:
                return
                [
                    new("Play", () => Run(shell, () => music.PlayAlbumAsync(item), $"{item.Title} could not be played")),
                    new("Shuffle", () => Run(shell, () => music.PlayAlbumAsync(item, shuffle: true), $"{item.Title} could not be played")),
                    new("Play next", () => Run(shell, () => EnqueueAlbumAsync(shell, music, item, next: true), $"{item.Title} could not be listed")),
                    new("Add to the queue", () => Run(shell, () => EnqueueAlbumAsync(shell, music, item, next: false), $"{item.Title} could not be listed")),
                ];

            case "artist" when music is not null:
                return
                [
                    new("Play", () => Run(shell, () => music.PlayArtistAsync(item, shuffle: false), $"{item.Title} could not be played")),
                    new("Shuffle", () => Run(shell, () => music.PlayArtistAsync(item, shuffle: true), $"{item.Title} could not be played")),
                ];

            case "track" when music is not null:
                return
                [
                    new("Play", () => Run(shell, () => music.PlayAsync([item]), $"{item.Title} could not be played")),
                    new("Play next", () => Run(shell, () => music.EnqueueAsync([item], next: true), $"{item.Title} could not be played")),
                    new("Add to the queue", () => Run(shell, () => music.EnqueueAsync([item], next: false), $"{item.Title} could not be played")),
                ];

            default:
                return [];
        }
    }

    private static List<MenuEntry> Marks(IViewerItem target)
    {
        if (target.Shell.Viewer is not { } viewer) return [];
        var shell = target.Shell;
        var item = target.Item;
        var entries = new List<MenuEntry>();
        if (item.Type is "movie" or "episode" or "show" or "season")
        {
            var watched = target.State.IsWatched;
            entries.Add(new(watched ? "Mark as unwatched" : "Mark as watched", () => Run(shell, async () =>
            {
                if (!await viewer.SetWatchedAsync(item, !watched)) shell.Live.Say($"The server would not mark {item.Title}");
            }, $"The server would not mark {item.Title}")));
        }

        if (item.Type is "movie" or "episode" or "show" or "season" or "album" or "artist" or "track")
        {
            entries.Add(new("Rate", null, Stars(target, viewer)));
        }

        return entries;
    }

    /// <summary>Five stars down to one, the viewer's own ticked (a half star among them when that is theirs), and a way to clear it.</summary>
    private static List<MenuEntry> Stars(IViewerItem target, ViewerState viewer)
    {
        var rating = target.State.UserRating;
        var stars = new List<MenuEntry>();
        for (var whole = 5; whole >= 1; whole--)
        {
            stars.Add(Star(target, viewer, whole * 2, rating));
            if (rating == whole * 2 - 1) stars.Add(Star(target, viewer, whole * 2 - 1, rating));
        }

        if (rating is not null)
        {
            stars.Add(MenuEntry.Separator);
            stars.Add(new("Clear my rating", () => Rate(target, viewer, null)));
        }

        return stars;
    }

    private static MenuEntry Star(IViewerItem target, ViewerState viewer, int points, double? rating)
    {
        var words = points switch
        {
            1 => "½ star",
            2 => "1 star",
            _ when points % 2 == 1 => string.Create(CultureInfo.InvariantCulture, $"{points / 2}½ stars"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{points / 2} stars"),
        };
        return new(words, () => Rate(target, viewer, points)) { IsChecked = rating == points };
    }

    private static void Rate(IViewerItem target, ViewerState viewer, double? points) => Run(target.Shell, async () =>
    {
        if (!await viewer.RateAsync(target.Item, points)) target.Shell.Live.Say($"The server would not take the rating of {target.Item.Title}");
    }, $"The server would not take the rating of {target.Item.Title}");

    private static List<MenuEntry> Keeping(IViewerItem target, Control? anchor)
    {
        var entries = new List<MenuEntry>();
        if (Watchlisting(target.Shell, target.Item) is { } watchlist) entries.Add(watchlist);
        var playlists = ViewerMenu.PlaylistEntries(target, anchor);
        if (playlists.Count > 0) entries.Add(new("Add to playlist", null, playlists));
        if (Downloading(target.Shell, target.Item) is { } download) entries.Add(download);
        return entries;
    }

    /// <summary>A film or a series Plex's catalogue knows goes on the Watchlist or comes off it.</summary>
    private static MenuEntry? Watchlisting(ShellViewModel shell, MetadataItem item)
    {
        if (item.Type is not ("movie" or "show") || PlexDiscoverClient.CatalogKey(item.Guid) is not { } key || shell.Watchlist is not { } watchlist) return null;

        MenuEntry Line(bool on) => new(on ? "Remove from Watchlist" : "Add to Watchlist", () => Run(shell, async () =>
        {
            await watchlist.SetAsync(key, !on);
            shell.Live.Say(on ? $"{item.Title} is off your Watchlist" : $"{item.Title} is on your Watchlist");
        }, on ? $"{item.Title} could not be taken off the Watchlist. Plex did not answer" : $"{item.Title} could not be added to the Watchlist. Plex did not answer"));

        async Task<MenuEntry?> AskAsync()
        {
            try
            {
                return Line(await watchlist.ContainsAsync(key, CancellationToken.None));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException or JsonException)
            {
                Log.Warn("Whether a title is on the Watchlist could not be found out.", ex);
                return new MenuEntry("The Watchlist did not answer") { IsEnabled = false };
            }
        }

        var first = watchlist.Known(key) is { } known ? Line(known) : new MenuEntry("Checking the Watchlist…") { IsEnabled = false };
        return first with { Later = AskAsync() };
    }

    /// <summary>A film or an episode downloads, or says how its download stands; a series or a season keeps episodes by rule.</summary>
    private static MenuEntry? Downloading(ShellViewModel shell, MetadataItem item)
    {
        if (shell.Session is not { MachineIdentifier: { } server } session) return null;
        var downloads = shell.Downloads;
        switch (item.Type)
        {
            case "movie" or "episode":
            {
                var row = downloads.RowFor(server, item);
                if (row.IsGone) return new("Download", () => downloads.Download(session, item));
                var lines = new List<MenuEntry>();
                if (row.IsDone) lines.Add(new("Play the downloaded copy", () => row.PlayCommand.Execute(null)));
                if (row.CanPause) lines.Add(new("Pause the download", () => row.PauseCommand.Execute(null)));
                if (row.CanResume) lines.Add(new("Resume the download", () => row.ResumeCommand.Execute(null)));
                lines.Add(new("Delete the download", () => row.RemoveCommand.Execute(null)));
                lines.Add(MenuEntry.Separator);
                lines.Add(new("Open downloads", () => shell.ShowDownloadsCommand.Execute(null)));
                return new(DownloadHeading(row), null, lines);
            }

            case "show":
            {
                var rule = downloads.Group(server, item).Rule;
                var lines = new List<MenuEntry>();
                foreach (var count in new[] { 3, 5, 10 })
                {
                    lines.Add(new(string.Create(CultureInfo.InvariantCulture, $"Keep the next {count} unwatched episodes"), () => downloads.KeepNext(session, item, count))
                    {
                        IsChecked = rule?.Keep == count,
                    });
                }

                if (rule is not null)
                {
                    lines.Add(MenuEntry.Separator);
                    lines.Add(new("Stop keeping episodes of the series", () => downloads.Manager.RemoveRule(rule.Id)));
                }

                return new("Download", null, lines);
            }

            case "season":
            {
                var rule = downloads.Group(server, item).Rule;
                var lines = new List<MenuEntry>
                {
                    new($"Download all of {item.Title}", () => Run(shell, () => downloads.DownloadAllAsync(session, item), $"The episodes of {item.Title} could not be listed")),
                    new($"Keep the next 3 unwatched of {item.Title}", () => downloads.KeepNext(session, item, 3)) { IsChecked = rule?.Keep == 3 },
                };
                if (rule is not null) lines.Add(new($"Stop keeping episodes of {item.Title}", () => downloads.Manager.RemoveRule(rule.Id)));
                return new("Download", null, lines);
            }

            default:
                return null;
        }
    }

    /// <summary>"Downloaded", "Downloading · 42%", "Download paused".</summary>
    private static string DownloadHeading(DownloadRowViewModel row) => row.Record.State switch
    {
        DownloadState.Done => "Downloaded",
        DownloadState.Downloading => string.Create(CultureInfo.InvariantCulture, $"Downloading · {Math.Floor(row.Fraction * 100)}%"),
        DownloadState.Paused => "Download paused",
        DownloadState.Failed => "Download stopped",
        _ => "Download queued",
    };

    /// <summary>
    /// What an item belongs to (an episode's season and series, a season's series, an album's
    /// artist, a track's album and artist), and what a film or an episode is made of.
    /// </summary>
    private static List<MenuEntry> About(IViewerItem target, Control? anchor)
    {
        var shell = target.Shell;
        var item = target.Item;
        var entries = new List<MenuEntry>();
        void Go(string words, string? key, string type, string? title, string? parentKey = null)
        {
            if (key is null || Showing(shell, key)) return;
            var place = new MetadataItem { RatingKey = key, Type = type, Title = title ?? string.Empty, ParentRatingKey = parentKey };
            entries.Add(new(words, () => shell.OpenItem(place)));
        }

        switch (item.Type)
        {
            case "episode":
                Go("Go to the season", item.ParentRatingKey, "season", item.ParentTitle, item.GrandparentRatingKey);
                Go("Go to the series", item.GrandparentRatingKey, "show", item.GrandparentTitle);
                break;
            case "season":
                Go("Go to the series", item.ParentRatingKey, "show", item.ParentTitle);
                break;
            case "album":
                Go("Go to the artist", item.ParentRatingKey, "artist", item.ParentTitle);
                break;
            case "track":
                Go("Go to the album", item.ParentRatingKey, "album", item.ParentTitle);
                Go("Go to the artist", item.GrandparentRatingKey, "artist", item.GrandparentTitle);
                break;
        }

        // The panel is the desktop's, beside the tile: the TV interface leaves it out.
        if (item.Type is "movie" or "episode" && shell.Session is { } session && !shell.IsTv)
        {
            entries.Add(new("Media info", () => MediaInfoView.ShowBeside(anchor, session, item)));
        }

        return entries;
    }

    /// <summary>The page open now is the place (a series page on that season counts as the season).</summary>
    private static bool Showing(ShellViewModel shell, string key) => shell.Router.Current switch
    {
        ItemPageViewModel page => page.Item.RatingKey == key || page.SelectedSeason?.Season.RatingKey == key,
        AlbumPageViewModel album => album.Album.RatingKey == key,
        ArtistPageViewModel artist => artist.Artist.RatingKey == key,
        _ => false,
    };

    private static async Task PlayNextAsync(ShellViewModel shell, MetadataItem showOrSeason)
    {
        if (shell.Session is not { } session) return;
        if (await SeriesPlayback.NextAsync(session, showOrSeason) is { } next) shell.Play(next, resume: next.Progress is > 0 and < 1);
        else shell.Live.Say($"There is nothing in {showOrSeason.Title} to play");
    }

    private static async Task ShuffleEpisodesAsync(ShellViewModel shell, MetadataItem showOrSeason)
    {
        if (shell.Session is not { } session) return;
        if (VideoQueue.Of(await SeriesPlayback.AllAsync(session, showOrSeason), shuffle: true) is { } queue) shell.Play(queue.Current, resume: false, queue);
        else shell.Live.Say($"There is nothing in {showOrSeason.Title} to play");
    }

    private static async Task PlayCollectionAsync(ShellViewModel shell, MetadataItem collection, bool shuffle)
    {
        if (shell.Session is not { } session) return;
        var members = await Task.Run(() => session.Client.GetCollectionItemsAsync(collection.RatingKey, CancellationToken.None));
        if (VideoQueue.Of(members, shuffle) is { } queue) shell.Play(queue.Current, resume: !shuffle, queue);
        else shell.Live.Say($"There is nothing in {collection.Title} to play");
    }

    private static async Task EnqueueAlbumAsync(ShellViewModel shell, Music.MusicPlayer music, MetadataItem album, bool next)
    {
        if (shell.Session is not { } session) return;
        var tracks = await Task.Run(() => session.Client.GetChildrenAsync(album.RatingKey, CancellationToken.None));
        await music.EnqueueAsync(tracks, next);
        shell.Live.Say(next ? $"{album.Title} plays next" : $"{album.Title} is in the queue");
    }

    /// <summary>Starts work that may ask the server; says <paramref name="failure"/> in the status bar when it could not.</summary>
    private static void Run(ShellViewModel shell, Func<Task> work, string failure) => _ = RunAsync(shell, work, failure);

    private static async Task RunAsync(ShellViewModel shell, Func<Task> work, string failure)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException or JsonException)
        {
            Log.Warn(failure + ".", ex);
            shell.Live.Say(failure);
        }
    }
}
