using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>What a photo library holds: albums (photo items whose key lists children) and photos.</summary>
public static class PhotoItems
{
    public static bool IsAlbum(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type == "photoalbum" || (item.Type == "photo" && item.Key?.EndsWith("/children", StringComparison.Ordinal) == true);
    }

    /// <summary>The picture to show full size: the photo's own file, or its thumbnail when the listing left the file out.</summary>
    public static string? Picture(MetadataItem photo) => photo.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Key ?? photo.Thumb;

    /// <summary>When the photo was taken, as the file says.</summary>
    public static DateTime? Taken(MetadataItem photo) =>
        DateTime.TryParse(photo.OriginallyAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var taken) ? taken : null;
}

/// <summary>A line of a photo's details: a label and its value.</summary>
public sealed record PhotoInfoRow(string Label, string Value);

/// <summary>A photo's details as a viewer lists them: when, with what, how, and how large.</summary>
public static class PhotoInfo
{
    public static IReadOnlyList<PhotoInfoRow> For(MetadataItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var rows = new List<PhotoInfoRow>();
        if (PhotoItems.Taken(photo) is { } taken) rows.Add(new("Taken", taken.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)));
        if (photo.ParentTitle is { Length: > 0 } album) rows.Add(new("Album", album));
        var media = photo.Media?.FirstOrDefault();
        if (Camera(media?.Make, media?.Model) is { } camera) rows.Add(new("Camera", camera));
        if (media?.Lens is { Length: > 0 } lens) rows.Add(new("Lens", lens));
        var settings = string.Join("   ", new[] { Aperture(media?.Aperture), Exposure(media?.Exposure), media?.Iso is > 0 and var iso ? $"ISO {iso}" : null }
            .Where(s => s is not null));
        if (settings.Length > 0) rows.Add(new("Settings", settings));
        if (media is { Width: > 0, Height: > 0 }) rows.Add(new("Size", string.Create(CultureInfo.InvariantCulture, $"{media.Width} by {media.Height} pixels")));
        if (photo.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.File is { Length: > 0 } file) rows.Add(new("File", Path.GetFileName(file)));
        return rows;
    }

    /// <summary>The maker and the camera, without saying the maker twice when the camera's name starts with it.</summary>
    public static string? Camera(string? make, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return string.IsNullOrWhiteSpace(make) ? null : make.Trim();
        if (string.IsNullOrWhiteSpace(make) || model.StartsWith(make.Trim(), StringComparison.OrdinalIgnoreCase)) return model.Trim();
        return $"{make.Trim()} {model.Trim()}";
    }

    public static string? Aperture(string? aperture) =>
        double.TryParse(aperture, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? string.Create(CultureInfo.InvariantCulture, $"f/{f:0.#}") : null;

    /// <summary>An exposure time as a photographer says it: 1/250 s, 4 s.</summary>
    public static string? Exposure(string? exposure) => string.IsNullOrWhiteSpace(exposure) ? null : $"{exposure.Trim()} s";
}

/// <summary>The order a slideshow shows photos in: onwards from the photo it starts on, or shuffled, every photo once a round.</summary>
public sealed class SlideshowOrder
{
    private readonly int[] _order;
    private int _at;

    public SlideshowOrder(int count, int start, bool shuffle, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        start = Math.Clamp(start, 0, count - 1);
        _order = [.. Enumerable.Range(0, count).Select(i => (start + i) % count)];
        if (shuffle)
        {
            // The photo showing stays first; the rest in a random order.
            var rest = _order[1..];
            (random ?? Random.Shared).Shuffle(rest);
            rest.CopyTo(_order, 1);
        }
    }

    /// <summary>The photo after this one, starting the round again after the last.</summary>
    public int Next()
    {
        _at = (_at + 1) % _order.Length;
        return _order[_at];
    }
}

/// <summary>A photo album, or a photo library's top: its albums, then its photos, as square tiles.</summary>
public sealed partial class PhotosPageViewModel : PageViewModel, IGridPage
{
    private readonly ShellViewModel _shell;
    private readonly ServerSession _session;
    private readonly LibraryDirectory? _section;
    private readonly MetadataItem? _album;
    private readonly TileGrid _grid = new(TileShape.Square);
    private readonly List<MetadataItem> _photos = [];

    public PhotosPageViewModel(ShellViewModel shell, ServerSession session, LibraryDirectory section)
    {
        _shell = shell;
        _session = session;
        _section = section;
    }

    public PhotosPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem album)
    {
        _shell = shell;
        _session = session;
        _album = album;
    }

    public override string Title => _album?.Title ?? _section?.Title ?? "Photos";

    public string Kicker => _album is null ? "PHOTOS" : "ALBUM";

    public ObservableCollection<GridRowViewModel> Rows => _grid.Rows;

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPhotos { get; private set; }

    public void Fit(double width, double scale) => _grid.Fit(width, scale);

    public bool IsEmpty => !IsLoading && _grid.Count == 0 && !HasError;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    [RelayCommand]
    private void Slideshow() => Open(0, slideshow: true, shuffle: _shell.Settings.Photos.SlideshowShuffle);

    [RelayCommand]
    private void ShuffleSlideshow() => Open(Random.Shared.Next(Math.Max(1, _photos.Count)), slideshow: true, shuffle: true);

    internal void Open(int index, bool slideshow = false, bool shuffle = false)
    {
        if (_photos.Count > 0) _shell.ShowPhotos(_photos, index, slideshow, shuffle);
    }

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        var items = _album is not null
            ? await Task.Run(() => _session.Client.GetChildrenAsync(_album.RatingKey, cancellation), cancellation)
            : (await Task.Run(() => _session.Client.GetSectionItemsAsync(_section!.Key, "titleSort", cancellation), cancellation)).Metadata ?? [];
        var albums = items.Where(PhotoItems.IsAlbum).ToList();
        _photos.Clear();
        _photos.AddRange(items.Where(i => i.Type == "photo" && !PhotoItems.IsAlbum(i)));
        _grid.Clear();
        _grid.Add([.. albums.Select(a => (object)new PhotoTileViewModel(_shell, this, a, -1)), .. _photos.Select((p, i) => (object)new PhotoTileViewModel(_shell, this, p, i))]);
        HasPhotos = _photos.Count > 0;
        CountText = string.Join("   ·   ", new[]
        {
            albums.Count > 0 ? (albums.Count == 1 ? "1 album" : $"{albums.Count} albums") : null,
            _photos.Count > 0 ? (_photos.Count == 1 ? "1 photo" : $"{_photos.Count} photos") : null,
        }.Where(p => p is not null));
    }
}

/// <summary>An album or a photo in a grid.</summary>
public sealed partial class PhotoTileViewModel(ShellViewModel shell, PhotosPageViewModel page, MetadataItem item, int index) : Controls.IMenuSource
{
    public MetadataItem Item { get; } = item;

    /// <summary>A photo shows, or starts a slideshow from itself; an album only opens, which its click does.</summary>
    public IReadOnlyList<Controls.MenuEntry> MenuEntries() => IsAlbum
        ? []
        : [new("Show this photo", () => page.Open(index)), new("Slideshow from here", () => page.Open(index, slideshow: true))];

    public bool IsAlbum => index < 0;

    public string Title => Item.Title;

    public string Caption => IsAlbum
        ? (Item.LeafCount is { } n ? (n == 1 ? "1 photo" : $"{n} photos") : string.Empty)
        : PhotoItems.Taken(Item)?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    public string? ThumbPath => Item.Thumb;

    public string OpenTip => IsAlbum ? $"Open {Item.Title}" : "Show this photo";

    [RelayCommand]
    private void Open()
    {
        if (IsAlbum) shell.OpenItem(Item);
        else page.Open(index);
    }
}

/// <summary>
/// Photos one at a time, filling the window: the server's photo transcoder scales each to the
/// screen, the arrows move through them, a slideshow moves on by itself, and the details of the
/// photo (when, which camera, how it was set) open beside it.
/// </summary>
public sealed partial class PhotoViewerPageViewModel : PageViewModel
{
    private readonly ShellViewModel _shell;
    private readonly ServerSession _session;
    private readonly IReadOnlyList<MetadataItem> _photos;
    private readonly Dictionary<string, MetadataItem> _full = new(StringComparer.Ordinal);
    private SlideshowOrder? _order;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _details;

    public PhotoViewerPageViewModel(ShellViewModel shell, ServerSession session, IReadOnlyList<MetadataItem> photos, int index, bool slideshow = false, bool shuffle = false)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0) throw new ArgumentException("A viewer needs a photo.", nameof(photos));
        _shell = shell;
        _session = session;
        _photos = [.. photos];
        Index = Math.Clamp(index, 0, _photos.Count - 1);
        IsShuffled = shuffle;
        if (slideshow) StartSlideshow();
    }

    public override string Title => Current.Title;

    public override bool IsImmersive => true;

    public override bool ShowsRail => false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(Picture), nameof(PositionText), nameof(Caption), nameof(Heading))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))]
    public partial int Index { get; private set; }

    public int Count => _photos.Count;

    public MetadataItem Current => _full.GetValueOrDefault(_photos[Index].RatingKey) ?? _photos[Index];

    /// <summary>What the transcoder scales: the photo's file, else its thumbnail.</summary>
    public string? Picture => PhotoItems.Picture(_photos[Index]);

    public string Heading => _photos[Index].ParentTitle ?? _photos[Index].Title;

    public string PositionText => string.Create(CultureInfo.InvariantCulture, $"{Index + 1} of {_photos.Count}");

    public string Caption => PhotoItems.Taken(_photos[Index])?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) ?? _photos[Index].Title;

    /// <summary>The slideshow is moving on by itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowTip))]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleTip))]
    public partial bool IsShuffled { get; private set; }

    public string SlideshowTip => IsPlaying ? "Pause the slideshow" : "Play a slideshow";

    public string ShuffleTip => IsShuffled ? "Shuffle is on" : "Shuffle";

    /// <summary>How long each photo stays in a slideshow.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(_shell.Settings.Photos.SlideshowSeconds, 2, 60));

    [ObservableProperty]
    public partial bool IsInfoOpen { get; private set; }

    public ObservableCollection<PhotoInfoRow> Info { get; } = [];

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => Show(Index + 1);

    private bool CanGoNext() => Index < _photos.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => Show(Index - 1);

    private bool CanGoPrevious() => Index > 0;

    [RelayCommand]
    private void ToggleSlideshow()
    {
        if (IsPlaying) StopSlideshow();
        else StartSlideshow();
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        IsShuffled = !IsShuffled;
        _order = new SlideshowOrder(_photos.Count, Index, IsShuffled);
    }

    [RelayCommand]
    private void ToggleInfo()
    {
        IsInfoOpen = !IsInfoOpen;
        if (IsInfoOpen) LoadDetails();
    }

    [RelayCommand]
    private void Close() => _shell.Router.Back();

    /// <summary>The slideshow's step: the next photo in its order, round again after the last.</summary>
    internal void Advance()
    {
        _order ??= new SlideshowOrder(_photos.Count, Index, IsShuffled);
        Index = _order.Next();
        AfterMove();
    }

    public override void Deactivate()
    {
        base.Deactivate();
        StopSlideshow();
        _details?.Cancel();
    }

    protected override Task LoadAsync(CancellationToken cancellation)
    {
        AfterMove();
        return Task.CompletedTask;
    }

    private void Show(int index)
    {
        Index = Math.Clamp(index, 0, _photos.Count - 1);

        // Moving by hand carries a playing slideshow on from here.
        if (IsPlaying) RestartTimer();
        _order = new SlideshowOrder(_photos.Count, Index, IsShuffled);
        AfterMove();
    }

    private void StartSlideshow()
    {
        _order = new SlideshowOrder(_photos.Count, Index, IsShuffled);
        IsPlaying = true;
        RestartTimer();
    }

    private void StopSlideshow()
    {
        IsPlaying = false;
        _timer?.Stop();
        _timer = null;
    }

    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Advance());
        _timer.Start();
    }

    private void AfterMove()
    {
        OnPropertyChanged(nameof(Title));
        Info.Clear();
        foreach (var row in PhotoInfo.For(Current)) Info.Add(row);
        if (IsInfoOpen) LoadDetails();
    }

    // A listing may leave a photo's camera and file out: its full record fills them in, on a worker.
    private async void LoadDetails()
    {
        var photo = _photos[Index];
        if (_full.ContainsKey(photo.RatingKey)) return;
        _details?.Cancel();
        var details = _details = new CancellationTokenSource();
        try
        {
            var full = await Task.Run(() => _session.Client.GetMetadataAsync(photo.RatingKey, details.Token), details.Token);
            if (full is null || details.IsCancellationRequested) return;
            _full[photo.RatingKey] = full;
            if (_photos[Index].RatingKey != photo.RatingKey) return;
            Info.Clear();
            foreach (var row in PhotoInfo.For(full)) Info.Add(row);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"Photos: the details of {photo.Title} could not be read ({ex.Message}).");
        }
    }
}
