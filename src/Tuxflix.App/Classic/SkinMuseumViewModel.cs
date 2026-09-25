using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Classic;

/// <summary>The museum in a window: its skins a page at a time, a search, and a button that adds a skin to the library.</summary>
public sealed partial class SkinMuseumViewModel : ObservableObject, IDisposable
{
    private const int Page = 30;

    private readonly SkinMuseum _museum;
    private readonly SkinLibrary _library;
    private readonly Action<string>? _added;
    private CancellationTokenSource _paging = new();
    private string? _searching;
    private int _offset;

    public SkinMuseumViewModel(SkinMuseum museum, SkinLibrary library, Action<string>? added = null)
    {
        _museum = museum;
        _library = library;
        _added = added;
    }

    public ObservableCollection<MuseumSkinViewModel> Skins { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanLoadMore { get; private set; }

    internal SkinLibrary Library => _library;

    internal SkinMuseum Museum => _museum;

    internal Action<string>? Added => _added;

    /// <summary>The museum's first page, in its own order.</summary>
    public Task StartAsync() => LoadAsync(fresh: true, search: null);

    [RelayCommand]
    private Task SearchAsync() => LoadAsync(fresh: true, search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());

    [RelayCommand]
    private Task MoreAsync() => LoadAsync(fresh: false, search: _searching);

    public void Dispose()
    {
        _paging.Cancel();
        foreach (var skin in Skins) skin.Dispose();
        _museum.Dispose();
    }

    private async Task LoadAsync(bool fresh, string? search)
    {
        if (fresh)
        {
            _paging.Cancel();
            _paging = new CancellationTokenSource();
            _offset = 0;
            _searching = search;
            foreach (var skin in Skins) skin.Dispose();
            Skins.Clear();
        }

        var paging = _paging.Token;
        IsLoading = true;
        Status = search is null ? "Loading the museum…" : $"Searching for {search}…";
        try
        {
            int? total = null;
            IReadOnlyList<MuseumSkin> found;
            if (search is null)
            {
                (found, var count) = await Task.Run(() => _museum.BrowseAsync(_offset, Page, paging), paging);
                total = count;
            }
            else
            {
                found = await Task.Run(() => _museum.SearchAsync(search, _offset, Page, paging), paging);
            }

            if (paging.IsCancellationRequested) return;
            _offset += Page;
            foreach (var skin in found)
            {
                var entry = new MuseumSkinViewModel(this, skin);
                Skins.Add(entry);
                entry.LoadScreenshot();
            }

            CanLoadMore = found.Count > 0 && (total is null || _offset < total);
            Status = search is null
                ? (total is { } all ? $"{all:N0} skins, the museum's favourites first" : string.Empty)
                : Skins.Count == 0 ? $"No skins match {search}." : $"Skins matching {search}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (paging.IsCancellationRequested) return;
            Log.Warn("The skin museum could not be read.", ex);
            Status = "The museum did not answer. Try again in a moment.";
        }
        finally
        {
            if (!paging.IsCancellationRequested) IsLoading = false;
        }
    }
}

/// <summary>One skin in the museum window: its picture, its name, and whether it is in the library yet.</summary>
public sealed partial class MuseumSkinViewModel(SkinMuseumViewModel museum, MuseumSkin skin) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _life = new();

    public MuseumSkin Skin { get; } = skin;

    public string Name => Skin.Name;

    [ObservableProperty]
    public partial Bitmap? Screenshot { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLabel), nameof(AddTip))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsAdding { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLabel), nameof(AddTip))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsAdded { get; private set; }

    public string AddLabel => IsAdded ? "ADDED" : IsAdding ? "ADDING…" : "ADD";

    public string AddTip => IsAdded ? "In your skins: choose it from the compact player's Skins menu" : $"Add {Name} to your skins";

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Fetches and decodes the picture on workers; the window only shows it.</summary>
    internal async void LoadScreenshot()
    {
        try
        {
            var token = _life.Token;
            var bitmap = await Task.Run(async () =>
            {
                var bytes = await museum.Museum.ScreenshotAsync(Skin, token).ConfigureAwait(false);
                Imaging.ImageBounds.Check(bytes, Imaging.ImageBounds.SmallPixels);
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }, token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            Screenshot = bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Log.Debug($"Skin museum: no picture for {Skin.FileName} ({ex.Message}).");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        IsAdding = true;
        Problem = null;
        try
        {
            var name = await Task.Run(() => museum.Museum.AddAsync(Skin, museum.Library, _life.Token), _life.Token);
            IsAdded = true;
            museum.Added?.Invoke(name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Skin museum: {Skin.FileName} could not be added.", ex);
            Problem = ex is InvalidDataException ? ex.Message : "It could not be fetched.";
        }
        finally
        {
            IsAdding = false;
        }
    }

    private bool CanAdd() => !IsAdding && !IsAdded;

    public void Dispose()
    {
        _life.Cancel();
        var picture = Screenshot;
        Screenshot = null;
        if (picture is not null) Dispatcher.UIThread.Post(picture.Dispose, DispatcherPriority.Background);
    }
}
