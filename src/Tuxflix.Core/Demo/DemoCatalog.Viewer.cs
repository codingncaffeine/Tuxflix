using System.Globalization;
using System.Reflection;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>A change the demo viewer made, as a server's timeline would name it.</summary>
/// <param name="RatingKey">The item whose record changed.</param>
/// <param name="SectionId">Its library.</param>
public readonly record struct DemoChange(string RatingKey, int? SectionId);

// The demo viewer's own state, kept in memory as a server keeps it: watched marks, ratings, stream
// choices, playlists, a watch history, and a playback on another of the viewer's devices.
//
// The catalogue's records are made once and read by many requests at once, so a change never
// rebuilds a list another request may be walking: it sets the one field in place, through the
// property's init accessor (by reflection: the models are immutable to everything else).
public sealed partial class DemoCatalog
{
    /// <summary>The demo viewer's account, numbered as a server numbers its owner.</summary>
    public const long ViewerAccountId = 1;

    /// <summary>Another account on the demo server, whose history and playback the viewer's pages leave out.</summary>
    public const long OtherAccountId = 2;

    private readonly Lock _viewer = new();
    private readonly List<DemoPlaylist> _playlists = [];
    private readonly List<MetadataItem> _history = [];
    private long _nextPlaylistItemId = 5001;
    private int _nextPlaylistKey = 8001;

    /// <summary>The viewer changed something: the records a server's timeline would announce.</summary>
    public event Action<IReadOnlyList<DemoChange>>? Changed;

    /// <summary>The clock the other device's playback runs on; tests stop it.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    private sealed class DemoPlaylist(string key, string title, string type)
    {
        public string Key { get; } = key;

        public string Title { get; set; } = title;

        public string Type { get; } = type;

        public long AddedAt { get; init; }

        public long UpdatedAt { get; set; }

        public List<(long Id, string RatingKey)> Entries { get; } = [];
    }

    partial void Personalise();

    // Called once, at the end of construction, before any request can read the catalogue.
    partial void Personalise()
    {
        for (var i = 0; i < Movies.Count; i++)
        {
            GiveStreams(Movies[i], i, film: true);
            if (i % 3 == 0) Set(Movies[i], nameof(MetadataItem.UserRating), (double)(4 + (Seed(Movies[i].Title) % 7)));
        }

        foreach (var episode in Shows.SelectMany(s => ChildrenOf(s.RatingKey)).SelectMany(s => ChildrenOf(s.RatingKey)))
        {
            GiveStreams(episode, (int)(long.Parse(episode.RatingKey, CultureInfo.InvariantCulture) % 97), film: false);
        }

        Set(Shows[0], nameof(MetadataItem.UserRating), 9.0);

        var marathon = NewPlaylist("Weekend Marathon", "video");
        foreach (var movie in Movies.Where(m => m.Genre?.Any(g => g.TagText is "Science Fiction" or "Adventure") == true).Take(5)) Append(marathon, movie.RatingKey);
        var mysteries = NewPlaylist("Rainy Day Mysteries", "video");
        foreach (var movie in Movies.Where(m => m.Genre?.Any(g => g.TagText == "Mystery") == true).Take(3)) Append(mysteries, movie.RatingKey);
        foreach (var episode in ChildrenOf(ChildrenOf(Shows[1].RatingKey)[0].RatingKey).Take(2)) Append(mysteries, episode.RatingKey);

        BuildHistory();
    }

    /// <summary>Records for several keys at once, as <c>/library/metadata/1,2,3</c> asks; unknown keys are left out.</summary>
    public IReadOnlyList<MetadataItem> FindMany(string keys) =>
        [.. keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Find).OfType<MetadataItem>()];

    /// <summary>Marks an item watched or not; a show or a season marks every episode in it. False for an unknown key.</summary>
    public bool MarkWatched(string? ratingKey, bool watched)
    {
        if (ratingKey is null || Find(ratingKey) is not { } item) return false;
        var leaves = item.Type switch
        {
            "show" => [.. ChildrenOf(item.RatingKey).SelectMany(season => ChildrenOf(season.RatingKey))],
            "season" => ChildrenOf(item.RatingKey),
            "movie" or "episode" => [item],
            _ => [],
        };
        if (leaves.Count == 0) return false;

        var changed = new List<DemoChange>();
        lock (_viewer)
        {
            var now = Clock().ToUnixTimeSeconds();
            foreach (var leaf in leaves)
            {
                Set(leaf, nameof(MetadataItem.ViewCount), watched ? (leaf.ViewCount ?? 0) + 1 : null);
                Set(leaf, nameof(MetadataItem.ViewOffset), null);
                Set(leaf, nameof(MetadataItem.LastViewedAt), watched ? now : leaf.LastViewedAt);
                changed.Add(new DemoChange(leaf.RatingKey, leaf.LibrarySectionId));
            }

            // A season and its show count what is watched under them.
            foreach (var parent in leaves.Select(l => l.ParentRatingKey).Concat(leaves.Select(l => l.GrandparentRatingKey)).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (Find(parent) is not { } container) continue;
                var episodes = container.Type == "show"
                    ? ChildrenOf(parent).SelectMany(season => ChildrenOf(season.RatingKey)).ToList()
                    : [.. ChildrenOf(parent)];
                Set(container, nameof(MetadataItem.ViewedLeafCount), episodes.Count(e => e.IsWatched));
                if (watched) Set(container, nameof(MetadataItem.LastViewedAt), now);
                changed.Add(new DemoChange(container.RatingKey, container.LibrarySectionId));
            }
        }

        Changed?.Invoke(changed);
        return true;
    }

    /// <summary>Rates an item 0 to 10; a negative rating takes it away. False for an unknown key.</summary>
    public bool Rate(string? ratingKey, double rating)
    {
        if (ratingKey is null || Find(ratingKey) is not { } item) return false;
        lock (_viewer)
        {
            Set(item, nameof(MetadataItem.UserRating), rating < 0 ? null : Math.Clamp(rating, 0, 10));
            Set(item, nameof(MetadataItem.LastRatedAt), rating < 0 ? null : Clock().ToUnixTimeSeconds());
        }

        Changed?.Invoke([new DemoChange(item.RatingKey, item.LibrarySectionId)]);
        return true;
    }

    /// <summary>Selects an audio stream, a subtitle stream (0: none) or both, for every part of the item a part belongs to.</summary>
    public bool ChooseStreams(long partId, long? audioStreamId, long? subtitleStreamId)
    {
        var item = FindByPart(partId) ?? AllLeaves().FirstOrDefault(i => i.Media?.Any(m => m.Part?.Any(p => p.Id == partId) == true) == true);
        if (item is null) return false;
        lock (_viewer)
        {
            foreach (var part in item.Media?.SelectMany(m => m.Part ?? []) ?? [])
            {
                foreach (var stream in part.Stream ?? [])
                {
                    if (stream.StreamType == StreamChoice.Audio && audioStreamId is { } audio) Set(stream, nameof(MediaStream.Selected), stream.Id == audio);
                    if (stream.StreamType == StreamChoice.Subtitle && subtitleStreamId is { } subtitle) Set(stream, nameof(MediaStream.Selected), stream.Id == subtitle);
                }
            }
        }

        return true;
    }

    /// <summary>The viewer's playlists, newest changes last, as <c>/playlists</c> lists them.</summary>
    public IReadOnlyList<MetadataItem> Playlists()
    {
        lock (_viewer)
        {
            return [.. _playlists.Select(Describe)];
        }
    }

    /// <summary>One playlist's record, or null.</summary>
    public MetadataItem? Playlist(string key)
    {
        lock (_viewer)
        {
            return _playlists.FirstOrDefault(p => p.Key == key) is { } playlist ? Describe(playlist) : null;
        }
    }

    /// <summary>A playlist's entries in order, each with its own <c>playlistItemID</c>; null for an unknown playlist.</summary>
    public IReadOnlyList<MetadataItem>? PlaylistItems(string key)
    {
        lock (_viewer)
        {
            if (_playlists.FirstOrDefault(p => p.Key == key) is not { } playlist) return null;
            return [.. playlist.Entries.Select(e => Find(e.RatingKey) is { } item ? Entry(item, e.Id) : null).OfType<MetadataItem>()];
        }
    }

    /// <summary>Makes a playlist of the items a <c>server://…/library/metadata/1,2</c> address names.</summary>
    public MetadataItem? CreatePlaylist(string? title, string? type, string? uri)
    {
        if (string.IsNullOrWhiteSpace(title) || type is not ("video" or "audio" or "photo")) return null;
        lock (_viewer)
        {
            var playlist = NewPlaylist(title.Trim(), type);
            foreach (var key in KeysIn(uri)) Append(playlist, key);
            return Describe(playlist);
        }
    }

    /// <summary>Adds what an address names to the end of a playlist.</summary>
    public bool AddToPlaylist(string key, string? uri)
    {
        lock (_viewer)
        {
            if (_playlists.FirstOrDefault(p => p.Key == key) is not { } playlist) return false;
            foreach (var item in KeysIn(uri)) Append(playlist, item);
            return true;
        }
    }

    public bool RemoveFromPlaylist(string key, long playlistItemId)
    {
        lock (_viewer)
        {
            if (_playlists.FirstOrDefault(p => p.Key == key) is not { } playlist) return false;
            var removed = playlist.Entries.RemoveAll(e => e.Id == playlistItemId) > 0;
            if (removed) playlist.UpdatedAt = Clock().ToUnixTimeSeconds();
            return removed;
        }
    }

    /// <summary>Moves an entry to just after another, or to the top with none.</summary>
    public bool MovePlaylistItem(string key, long playlistItemId, long? after)
    {
        lock (_viewer)
        {
            if (_playlists.FirstOrDefault(p => p.Key == key) is not { } playlist) return false;
            var from = playlist.Entries.FindIndex(e => e.Id == playlistItemId);
            if (from < 0 || (after is { } anchor && (anchor == playlistItemId || playlist.Entries.All(e => e.Id != anchor)))) return false;
            var entry = playlist.Entries[from];
            playlist.Entries.RemoveAt(from);
            var to = after is { } id ? playlist.Entries.FindIndex(e => e.Id == id) + 1 : 0;
            playlist.Entries.Insert(to, entry);
            playlist.UpdatedAt = Clock().ToUnixTimeSeconds();
            return true;
        }
    }

    public bool RenamePlaylist(string key, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        lock (_viewer)
        {
            if (_playlists.FirstOrDefault(p => p.Key == key) is not { } playlist) return false;
            playlist.Title = title.Trim();
            playlist.UpdatedAt = Clock().ToUnixTimeSeconds();
            return true;
        }
    }

    public bool DeletePlaylist(string key)
    {
        lock (_viewer)
        {
            return _playlists.RemoveAll(p => p.Key == key) > 0;
        }
    }

    /// <summary>
    /// What plays on the server now: an episode on another of the viewer's devices, moving with
    /// <see cref="Clock"/>, and a film on another account's device.
    /// </summary>
    public IReadOnlyList<MetadataItem> Sessions()
    {
        var episode = ChildrenOf(ChildrenOf(Shows[2].RatingKey)[0].RatingKey)[5];
        var film = Movies[6];
        return
        [
            Playing(episode, "1", "Living Room", "Plex for Android (TV)", "Android", "demo-living-room", ViewerAccountId, "Demo Viewer", 0.4),
            Playing(film, "2", "Robin's iPad", "Plex for iOS", "iPadOS", "demo-robins-ipad", OtherAccountId, "Robin", 0.7),
        ];
    }

    /// <summary>The other device's playback, as a <c>playing</c> notification reports it.</summary>
    public PlaySessionState ViewerPlayback()
    {
        var session = Sessions()[0];
        return new PlaySessionState { SessionKey = session.SessionKey, RatingKey = session.RatingKey, Key = session.Key, ViewOffset = session.ViewOffset, State = "playing", ClientIdentifier = session.Player?.MachineIdentifier };
    }

    /// <summary>
    /// Watch history, newest first: <paramref name="accountId"/> keeps one account's, <paramref name="since"/>
    /// (seconds since 1970) what was watched from then on.
    /// </summary>
    public IReadOnlyList<MetadataItem> History(long? accountId, long? since)
    {
        lock (_viewer)
        {
            return [.. _history.Where(h => (accountId is null || h.AccountId == accountId) && (since is null || h.ViewedAt >= since))];
        }
    }

    private MetadataItem Playing(MetadataItem item, string sessionKey, string device, string product, string platform, string machine, long account, string name, double startsAt)
    {
        // Round and round the item from where it started, at the speed of the clock.
        var duration = item.Duration ?? 1;
        var played = (long)(Clock() - Now).TotalMilliseconds;
        var offset = (((long)(duration * startsAt)) + played) % duration;
        return new MetadataItem
        {
            RatingKey = item.RatingKey,
            Key = item.Key,
            Type = item.Type,
            Title = item.Title,
            Index = item.Index,
            ParentIndex = item.ParentIndex,
            ParentRatingKey = item.ParentRatingKey,
            GrandparentRatingKey = item.GrandparentRatingKey,
            GrandparentTitle = item.GrandparentTitle,
            GrandparentThumb = item.GrandparentThumb,
            GrandparentArt = item.GrandparentArt,
            Thumb = item.Thumb,
            Art = item.Art,
            Year = item.Year,
            Duration = duration,
            ViewOffset = offset,
            LibrarySectionId = item.LibrarySectionId,
            SessionKey = sessionKey,
            Player = new PlaybackPlayer { Title = device, Product = product, Platform = platform, MachineIdentifier = machine, State = "playing", Local = true },
            User = new PlaybackUser { Id = account.ToString(CultureInfo.InvariantCulture), Title = name },
        };
    }

    // The history a server would have kept: every watched episode and film, spread back over the
    // last twelve weeks, newest first, and a few entries from the other account.
    private void BuildHistory()
    {
        var entries = new List<MetadataItem>();
        var id = 900;
        for (var s = 0; s < Shows.Count; s++)
        {
            var watched = ChildrenOf(Shows[s].RatingKey).SelectMany(season => ChildrenOf(season.RatingKey)).Where(e => e.IsWatched).ToList();
            for (var n = 0; n < watched.Count; n++)
            {
                var daysAgo = (((watched.Count - 1 - n) * 2) + (s * 3)) % 84;
                entries.Add(Viewed(watched[n], Now.AddDays(-daysAgo).AddHours(-(s + n) % 5), ViewerAccountId, id++));
            }
        }

        for (var m = 0; m < Movies.Count; m++)
        {
            if (!Movies[m].IsWatched) continue;
            entries.Add(Viewed(Movies[m], Now.AddDays(-((m * 5) % 70)).AddHours(-2), ViewerAccountId, id++));
        }

        entries.Add(Viewed(Movies[0], Now.AddDays(-1), OtherAccountId, id++));
        entries.Add(Viewed(Movies[3], Now.AddDays(-9), OtherAccountId, id++));
        _history.AddRange(entries.OrderByDescending(e => e.ViewedAt));
    }

    private static MetadataItem Viewed(MetadataItem item, DateTimeOffset at, long account, int id) => new()
    {
        RatingKey = item.RatingKey,
        Key = item.Key,
        Type = item.Type,
        Title = item.Title,
        Index = item.Index,
        ParentIndex = item.ParentIndex,
        ParentRatingKey = item.ParentRatingKey,
        GrandparentRatingKey = item.GrandparentRatingKey,
        GrandparentTitle = item.GrandparentTitle,
        GrandparentThumb = item.GrandparentThumb,
        GrandparentArt = item.GrandparentArt,
        Thumb = item.Thumb,
        LibrarySectionId = item.LibrarySectionId,
        HistoryKey = $"/status/sessions/history/{id}",
        ViewedAt = at.ToUnixTimeSeconds(),
        AccountId = account,
        DeviceId = account == ViewerAccountId ? 3 : 7,
    };

    private DemoPlaylist NewPlaylist(string title, string type)
    {
        var key = (_nextPlaylistKey++).ToString(CultureInfo.InvariantCulture);
        var now = Clock().ToUnixTimeSeconds();
        var playlist = new DemoPlaylist(key, title, type) { AddedAt = now, UpdatedAt = now };
        _playlists.Add(playlist);
        return playlist;
    }

    // A series or a season goes in as its episodes, as a server adds them.
    private void Append(DemoPlaylist playlist, string ratingKey)
    {
        if (Find(ratingKey) is not { } item) return;
        if (item.Type is "show" or "season")
        {
            foreach (var child in ChildrenOf(ratingKey)) Append(playlist, child.RatingKey);
            return;
        }

        playlist.Entries.Add((_nextPlaylistItemId++, ratingKey));
        playlist.UpdatedAt = Clock().ToUnixTimeSeconds();
    }

    private MetadataItem Describe(DemoPlaylist playlist)
    {
        var items = playlist.Entries.Select(e => Find(e.RatingKey)).OfType<MetadataItem>().ToList();
        return new MetadataItem
        {
            RatingKey = playlist.Key,
            Key = $"/playlists/{playlist.Key}/items",
            Type = "playlist",
            Title = playlist.Title,
            PlaylistType = playlist.Type,
            Smart = false,

            // Its first item's artwork stands in for the mosaic a server draws.
            Composite = items.FirstOrDefault() is { } first ? Artwork.Poster(first) : null,
            LeafCount = items.Count,
            Duration = items.Sum(i => i.Duration ?? 0),
            AddedAt = playlist.AddedAt,
            UpdatedAt = playlist.UpdatedAt,
        };
    }

    // A playlist entry: the item's own record, with the entry's id on it.
    private static MetadataItem Entry(MetadataItem item, long id)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(item, PlexJsonContext.Default.MetadataItem)!.AsObject();
        node["playlistItemID"] = id;
        return System.Text.Json.JsonSerializer.Deserialize(node, PlexJsonContext.Default.MetadataItem)!;
    }

    // The rating keys at the end of a `server://machine/com.plexapp.plugins.library/library/metadata/1,2` address.
    private static IEnumerable<string> KeysIn(string? uri)
    {
        const string Marker = "/library/metadata/";
        var at = uri?.LastIndexOf(Marker, StringComparison.Ordinal) ?? -1;
        if (at < 0) return [];
        return uri![(at + Marker.Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IEnumerable<MetadataItem> AllLeaves() =>
        Movies.Concat(Shows.SelectMany(s => ChildrenOf(s.RatingKey)).SelectMany(s => ChildrenOf(s.RatingKey)));

    // The streams a ripped film or an episode carries: its video, audio in a few languages and a
    // commentary, and subtitles, one of them a file kept beside the media.
    private static void GiveStreams(MetadataItem item, int n, bool film)
    {
        if (item.Media?.FirstOrDefault()?.Part?.FirstOrDefault() is not { } part) return;
        var id = part.Id * 10;
        var media = item.Media[0];
        var surround = media.AudioCodec?.ToUpperInvariant() is { } codec ? $"{(codec == "TRUEHD" ? "TrueHD" : codec)} {(media.AudioChannels >= 8 ? "7.1" : "5.1")}" : "EAC3 5.1";
        var streams = new List<MediaStream>
        {
            new() { Id = id, StreamType = 1, Index = 0, Codec = media.VideoCodec, DisplayTitle = $"{media.VideoResolution}p ({media.VideoCodec?.ToUpperInvariant()})", Width = media.Width, Height = media.Height },
            new() { Id = id + 1, StreamType = StreamChoice.Audio, Index = 1, Codec = media.AudioCodec, Language = "English", LanguageCode = "eng", Channels = media.AudioChannels, Selected = true, Default = true, DisplayTitle = $"English ({surround})", ExtendedDisplayTitle = $"English ({surround})" },
        };

        if (film)
        {
            streams.Add(new() { Id = id + 2, StreamType = StreamChoice.Audio, Index = 2, Codec = "eac3", Language = "Français", LanguageCode = "fra", Channels = 6, DisplayTitle = "Français (EAC3 5.1)", ExtendedDisplayTitle = "Français (EAC3 5.1)" });
            streams.Add(new() { Id = id + 3, StreamType = StreamChoice.Audio, Index = 3, Codec = "aac", Language = "English", LanguageCode = "eng", Channels = 2, Title = "Director's Commentary", DisplayTitle = "English (AAC Stereo)", ExtendedDisplayTitle = "Director's Commentary (English AAC Stereo)" });
            streams.Add(new() { Id = id + 4, StreamType = StreamChoice.Subtitle, Index = 4, Codec = "pgs", Language = "English", LanguageCode = "eng", DisplayTitle = "English (PGS)", ExtendedDisplayTitle = "English (PGS)" });
            streams.Add(new() { Id = id + 5, StreamType = StreamChoice.Subtitle, Index = 5, Codec = "pgs", Language = "English", LanguageCode = "eng", Title = "SDH", DisplayTitle = "English SDH (PGS)", ExtendedDisplayTitle = "SDH (English PGS)" });
            streams.Add(new() { Id = id + 6, StreamType = StreamChoice.Subtitle, Index = 6, Codec = "pgs", Language = "Français", LanguageCode = "fra", Forced = n % 2 == 0, DisplayTitle = "Français (PGS)", ExtendedDisplayTitle = "Français (PGS)" });
            streams.Add(new() { Id = id + 7, StreamType = StreamChoice.Subtitle, Codec = "srt", Language = "Español", LanguageCode = "spa", Key = $"/library/streams/{id + 7}", DisplayTitle = "Español (SRT External)", ExtendedDisplayTitle = "Español (SRT External)" });
        }
        else
        {
            streams.Add(new() { Id = id + 2, StreamType = StreamChoice.Audio, Index = 2, Codec = "aac", Language = "Español", LanguageCode = "spa", Channels = 2, DisplayTitle = "Español (AAC Stereo)", ExtendedDisplayTitle = "Español (AAC Stereo)" });
            streams.Add(new() { Id = id + 3, StreamType = StreamChoice.Subtitle, Index = 3, Codec = "srt", Language = "English", LanguageCode = "eng", DisplayTitle = "English (SRT)", ExtendedDisplayTitle = "English (SRT)" });
            streams.Add(new() { Id = id + 4, StreamType = StreamChoice.Subtitle, Index = 4, Codec = "srt", Language = "Español", LanguageCode = "spa", DisplayTitle = "Español (SRT)", ExtendedDisplayTitle = "Español (SRT)" });
        }

        Set(part, nameof(MediaPart.Stream), streams);
    }

    // Sets an init-only property in place: see the note at the top of the file.
    private static void Set<T>(T target, string property, object? value)
        where T : class =>
        typeof(T).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);
}
