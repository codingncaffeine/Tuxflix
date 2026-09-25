using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Music;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>UltraBlur colours as brushes: the page takes on the artwork's own light.</summary>
internal static class Blur
{
    public static LinearGradientBrush? Across(string? left, string? right)
    {
        if (!Color.TryParse("#" + left, out var from) || !Color.TryParse("#" + right, out var to)) return null;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
    }
}

/// <summary>An artist: their photo and story, then every album, newest first.</summary>
public sealed partial class ArtistPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem summary) : PageViewModel
{
    public override string Title => Artist.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Meta), nameof(Summary), nameof(HasSummary), nameof(PhotoPath), nameof(BackdropPath))]
    public partial MetadataItem Artist { get; private set; } = summary;

    public string Name => Artist.Title;

    public string Meta => string.Join("   ·   ", new[]
    {
        Artist.Genre is { Count: > 0 } genres ? string.Join(", ", genres.Take(3).Select(g => g.TagText)) : null,
        Artist.Country is { Count: > 0 } countries ? countries[0].TagText : null,
        Albums.Count > 0 ? (Albums.Count == 1 ? "1 album" : $"{Albums.Count} albums") : null,
    }.Where(p => !string.IsNullOrEmpty(p)));

    public string? Summary => Artist.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Artist.Summary);

    public string? PhotoPath => Artist.Thumb;

    public string? BackdropPath => Artist.Art;

    public ObservableCollection<AlbumTileViewModel> Albums { get; } = [];

    /// <summary>An artist heard only on other artists' albums (compilations, guest spots) has no albums of their own: their songs instead.</summary>
    public ObservableCollection<TrackRowViewModel> Songs { get; } = [];

    public bool HasAlbums => Albums.Count > 0;

    public bool HasSongs => Songs.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlur), nameof(BlurTop), nameof(BlurBottom))]
    public partial UltraBlurColors? Colors { get; private set; }

    public bool HasBlur => BlurTop is not null && BlurBottom is not null;

    public IBrush? BlurTop => Blur.Across(Colors?.TopLeft, Colors?.TopRight);

    public IBrush? BlurBottom => Blur.Across(Colors?.BottomLeft, Colors?.BottomRight);

    [RelayCommand]
    private Task PlayAsync() => shell.Music is { } music ? music.PlayArtistAsync(Artist, shuffle: false) : Task.CompletedTask;

    [RelayCommand]
    private Task ShuffleAsync() => shell.Music is { } music ? music.PlayArtistAsync(Artist, shuffle: true) : Task.CompletedTask;

    public override void Deactivate()
    {
        base.Deactivate();
        foreach (var row in Songs) row.Detach();
    }

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        if (await Task.Run(() => session.Client.GetMetadataAsync(Artist.RatingKey, cancellation), cancellation) is { } full) Artist = full;
        var albums = await Task.Run(() => session.Client.GetChildrenAsync(Artist.RatingKey, cancellation), cancellation);
        Albums.Clear();
        foreach (var album in albums.OrderByDescending(a => a.Year ?? 0).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Albums.Add(new AlbumTileViewModel(shell, album));
        }

        OnPropertyChanged(nameof(HasAlbums));
        if (Albums.Count == 0)
        {
            var songs = (await Task.Run(() => session.Client.GetAllLeavesAsync(Artist.RatingKey, cancellation), cancellation)).Where(t => t.Type == "track").ToList();
            Songs.Clear();
            for (var i = 0; i < songs.Count; i++)
            {
                Songs.Add(new TrackRowViewModel(shell, songs[i], i, songs[i].ParentTitle, index => shell.Music?.PlayAsync(songs, index) ?? Task.CompletedTask, numberFromTrack: false));
            }

            OnPropertyChanged(nameof(HasSongs));
        }

        OnPropertyChanged(nameof(Meta));
        Colors = Artist.UltraBlurColors
                 ?? ((Artist.Art ?? Artist.Thumb) is { } art ? await Task.Run(() => session.Client.GetUltraBlurColorsAsync(art, cancellation), cancellation) : null);
    }
}

/// <summary>An album as a square of its cover, with a play button on hover.</summary>
public sealed partial class AlbumTileViewModel(ShellViewModel shell, MetadataItem album)
{
    public MetadataItem Album { get; } = album;

    public string Title => Album.Title;

    public string Caption => Album.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string? CoverPath => Album.Thumb;

    public string PlayTip => $"Play {Album.Title}";

    [RelayCommand]
    private void Open() => shell.OpenItem(Album);

    [RelayCommand]
    private Task PlayAsync() => shell.Music is not { } music ? Task.CompletedTask
        : Album.Type == "artist" ? music.PlayArtistAsync(Album, shuffle: false)
        : music.PlayAlbumAsync(Album);
}

/// <summary>An album: its cover large, then every track, by disc.</summary>
public sealed partial class AlbumPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem summary) : PageViewModel
{
    private readonly List<MetadataItem> _tracks = [];

    public override string Title => Album.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Kicker), nameof(ArtistName), nameof(CoverPath), nameof(Meta), nameof(Styles), nameof(HasStyles), nameof(Studio), nameof(HasStudio))]
    public partial MetadataItem Album { get; private set; } = summary;

    public string Heading => Album.Title;

    /// <summary>"ALBUM", "SINGLE", "EP": what the server calls the release, when it says.</summary>
    public string Kicker => (Album.Subformat?.FirstOrDefault()?.TagText ?? Album.Format?.FirstOrDefault()?.TagText ?? "Album").ToUpperInvariant();

    public string ArtistName => Album.ParentTitle ?? string.Empty;

    public string? CoverPath => Album.Thumb;

    public string Meta => string.Join("   ·   ", new[]
    {
        Album.Year?.ToString(CultureInfo.InvariantCulture),
        _tracks.Count > 0 ? (_tracks.Count == 1 ? "1 track" : $"{_tracks.Count} tracks") : null,
        _tracks.Count > 0 ? Format.Runtime(_tracks.Sum(t => t.Duration ?? 0)) : null,
    }.Where(p => !string.IsNullOrEmpty(p)));

    public IReadOnlyList<string> Styles => (Album.Style ?? Album.Genre)?.Take(6).Select(g => g.TagText).ToList() ?? [];

    public bool HasStyles => Styles.Count > 0;

    public string? Studio => Album.Studio;

    public bool HasStudio => !string.IsNullOrWhiteSpace(Album.Studio);

    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlur), nameof(BlurTop), nameof(BlurBottom))]
    public partial UltraBlurColors? Colors { get; private set; }

    public bool HasBlur => BlurTop is not null && BlurBottom is not null;

    public IBrush? BlurTop => Blur.Across(Colors?.TopLeft, Colors?.TopRight);

    public IBrush? BlurBottom => Blur.Across(Colors?.BottomLeft, Colors?.BottomRight);

    [RelayCommand]
    private Task PlayAsync() => PlayFromAsync(0);

    [RelayCommand]
    private Task ShuffleAsync() => shell.Music is { } music ? music.PlayAsync(_tracks, 0, shuffle: true) : Task.CompletedTask;

    [RelayCommand]
    private void OpenArtist()
    {
        if (Album.ParentRatingKey is { } key) shell.OpenItem(new MetadataItem { RatingKey = key, Type = "artist", Title = ArtistName });
    }

    [RelayCommand]
    private Task PlayNextAsync() => shell.Music is { } music ? music.EnqueueAsync(_tracks, next: true) : Task.CompletedTask;

    [RelayCommand]
    private Task AddToQueueAsync() => shell.Music is { } music ? music.EnqueueAsync(_tracks, next: false) : Task.CompletedTask;

    internal Task PlayFromAsync(int index) => shell.Music is { } music ? music.PlayAsync(_tracks, index) : Task.CompletedTask;

    /// <summary>The rows follow the music player; leaving the page lets go of them, or every visit would leak its rows.</summary>
    public override void Deactivate()
    {
        base.Deactivate();
        foreach (var row in Rows.OfType<TrackRowViewModel>()) row.Detach();
    }

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        if (await Task.Run(() => session.Client.GetMetadataAsync(Album.RatingKey, cancellation), cancellation) is { } full) Album = full;
        var tracks = await Task.Run(() => session.Client.GetChildrenAsync(Album.RatingKey, cancellation), cancellation);
        _tracks.Clear();
        _tracks.AddRange(tracks.Where(t => t.Type == "track"));

        // Discs get headings only when there is more than one.
        Rows.Clear();
        var discs = _tracks.GroupBy(t => t.ParentIndex ?? 1).OrderBy(g => g.Key).ToList();
        var index = 0;
        foreach (var disc in discs)
        {
            if (discs.Count > 1) Rows.Add(new DiscHeadingViewModel($"DISC {disc.Key}"));
            foreach (var track in disc)
            {
                var guest = !string.IsNullOrWhiteSpace(track.OriginalTitle) && track.OriginalTitle != Album.ParentTitle ? track.OriginalTitle : null;
                Rows.Add(new TrackRowViewModel(shell, track, index++, guest, PlayFromAsync));
            }
        }

        OnPropertyChanged(nameof(Meta));
        Colors = Album.UltraBlurColors
                 ?? (Album.Thumb is { } thumb ? await Task.Run(() => session.Client.GetUltraBlurColorsAsync(thumb, cancellation), cancellation) : null);
    }
}

public sealed record DiscHeadingViewModel(string Title);

/// <summary>
/// A track in a list: its number, title, second line (a guest artist on an album page, the album on
/// an artist's) and length; the one playing marks itself.
/// </summary>
public sealed partial class TrackRowViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Func<int, Task> _playFrom;
    private readonly int _index;

    internal TrackRowViewModel(ShellViewModel shell, MetadataItem track, int index, string? secondLine, Func<int, Task> playFrom, bool numberFromTrack = true)
    {
        _shell = shell;
        _playFrom = playFrom;
        _index = index;
        Track = track;
        Number = numberFromTrack && track.Index is { } n ? n.ToString(CultureInfo.InvariantCulture) : (index + 1).ToString(CultureInfo.InvariantCulture);
        Guest = string.IsNullOrWhiteSpace(secondLine) ? null : secondLine;
        if (shell.Music is { } music)
        {
            music.TrackChanged += Refresh;
            Refresh();
        }
    }

    public MetadataItem Track { get; }

    public string Number { get; }

    public string Title => Track.Title;

    public string? Guest { get; }

    public bool HasGuest => Guest is not null;

    public string DurationText => Track.Duration is > 0 ? Format.Clock(Track.Duration.Value / 1000.0) : string.Empty;

    public string PlayTip => $"Play {Track.Title}";

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [RelayCommand]
    private Task PlayAsync() => _playFrom(_index);

    [RelayCommand]
    private Task PlayNextAsync() => _shell.Music is { } music ? music.EnqueueAsync([Track], next: true) : Task.CompletedTask;

    [RelayCommand]
    private Task AddToQueueAsync() => _shell.Music is { } music ? music.EnqueueAsync([Track], next: false) : Task.CompletedTask;

    internal void Detach()
    {
        if (_shell.Music is { } music) music.TrackChanged -= Refresh;
    }

    private void Refresh() => IsPlaying = _shell.Music?.Current?.Track.RatingKey == Track.RatingKey;
}

/// <summary>
/// What is playing, the whole page: the cover large in its own light, the transport, and what comes next.
/// </summary>
public sealed partial class NowPlayingPageViewModel : PageViewModel
{
    private readonly ShellViewModel _shell;
    private readonly ServerSession _session;
    private string? _blurFor;

    public NowPlayingPageViewModel(ShellViewModel shell, ServerSession session, MusicPlayer music)
    {
        _shell = shell;
        _session = session;
        Music = music;
        Music.TrackChanged += OnTrackChanged;
    }

    public override string Title => "Now playing";

    public override bool ShowsRail => false;

    public MusicPlayer Music { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlur), nameof(BlurTop), nameof(BlurBottom))]
    public partial UltraBlurColors? Colors { get; private set; }

    public bool HasBlur => BlurTop is not null && BlurBottom is not null;

    public IBrush? BlurTop => Blur.Across(Colors?.TopLeft, Colors?.TopRight);

    public IBrush? BlurBottom => Blur.Across(Colors?.BottomLeft, Colors?.BottomRight);

    [RelayCommand]
    private void OpenAlbum()
    {
        if (Music.Current?.Track is { ParentRatingKey: { } key } track)
        {
            _shell.OpenItem(new MetadataItem { RatingKey = key, Type = "album", Title = track.ParentTitle ?? string.Empty });
        }
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (Music.Current?.Track is { GrandparentRatingKey: { } key } track)
        {
            _shell.OpenItem(new MetadataItem { RatingKey = key, Type = "artist", Title = track.GrandparentTitle ?? string.Empty });
        }
    }

    public override void Deactivate()
    {
        base.Deactivate();
        Music.TrackChanged -= OnTrackChanged;
    }

    protected override Task LoadAsync(CancellationToken cancellation)
    {
        OnTrackChanged();
        return Task.CompletedTask;
    }

    private async void OnTrackChanged()
    {
        var art = Music.Current?.ArtPath;
        if (art == _blurFor) return;
        _blurFor = art;
        if (art is null)
        {
            Colors = null;
            return;
        }

        try
        {
            var colors = await Task.Run(() => _session.Client.GetUltraBlurColorsAsync(art, CancellationToken.None));
            if (_blurFor == art) Colors = colors;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"Now playing: no colours for the cover ({ex.Message}).");
        }
    }
}
