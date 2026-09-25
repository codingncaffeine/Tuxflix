using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// Editing a playlist: renaming and deleting it, and taking entries out or moving them. Each
// change shows at once and goes to the server from a worker; if the server says no, the page
// reads the playlist again, so what it shows is what the server has.
public sealed partial class PlaylistPageViewModel
{
    /// <summary>The name given here, until the page is read again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial string? Renamed { get; private set; }

    /// <summary>A smart playlist is filled by the server's rules: its entries cannot be moved or taken out.</summary>
    public bool CanEditEntries => !Playlist.Smart;

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    [RelayCommand]
    private void StartRename()
    {
        NewTitle = Heading;
        IsConfirmingDelete = false;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task SaveRenameAsync()
    {
        var title = NewTitle.Trim();
        IsRenaming = false;
        if (title.Length == 0 || title == Heading) return;
        var before = Renamed;
        Renamed = title;
        if (!await EditAsync(() => session.Client.RenamePlaylistAsync(Playlist.RatingKey, title, CancellationToken.None), reloadOnFailure: false))
        {
            Renamed = before;
            shell.Live.Say("The server did not take the new name");
        }
    }

    [RelayCommand]
    private void AskDelete()
    {
        IsRenaming = false;
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        IsConfirmingDelete = false;
        if (!await EditAsync(() => session.Client.DeletePlaylistAsync(Playlist.RatingKey, CancellationToken.None), reloadOnFailure: false))
        {
            shell.Live.Say("The server did not delete the playlist");
            return;
        }

        shell.Live.Say($"Deleted {Heading}");
        if (shell.Router.CanGoBack) shell.Router.Back();
        else shell.Router.Replace(new PlaylistsPageViewModel(shell, session));
    }

    /// <summary>Takes an entry out: the row goes at once.</summary>
    [RelayCommand]
    private async Task RemoveAsync(object? row)
    {
        if (!CanEditEntries || EntryOf(row) is not { } entry || entry.PlaylistItemId is not { } id) return;
        var at = _items.IndexOf(entry);
        _items.RemoveAt(at);
        Rebuild();
        await EditAsync(() => session.Client.RemoveFromPlaylistAsync(Playlist.RatingKey, id, CancellationToken.None), reloadOnFailure: true);
    }

    [RelayCommand]
    private Task MoveUpAsync(object? row) => MoveAsync(row, -1);

    [RelayCommand]
    private Task MoveDownAsync(object? row) => MoveAsync(row, +1);

    [RelayCommand]
    private Task MoveToTopAsync(object? row) => MoveAsync(row, int.MinValue);

    /// <summary>Moves an entry by <paramref name="by"/> places (to the top with <see cref="int.MinValue"/>).</summary>
    private async Task MoveAsync(object? row, int by)
    {
        if (!CanEditEntries || EntryOf(row) is not { } entry || entry.PlaylistItemId is not { } id) return;
        var from = _items.IndexOf(entry);
        var to = by == int.MinValue ? 0 : Math.Clamp(from + by, 0, _items.Count - 1);
        if (to == from) return;
        _items.RemoveAt(from);
        _items.Insert(to, entry);
        Rebuild();

        // The server places an entry after another one, or at the top.
        var after = to == 0 ? null : _items[to - 1].PlaylistItemId;
        await EditAsync(() => session.Client.MovePlaylistItemAsync(Playlist.RatingKey, id, after, CancellationToken.None), reloadOnFailure: true);
    }

    private MetadataItem? EntryOf(object? row) => row switch
    {
        PlaylistVideoRowViewModel video => video.Item,
        TrackRowViewModel track => track.Track,
        MetadataItem item => item,
        _ => null,
    } is { } found && _items.Contains(found) ? found : null;

    /// <summary>The rows again from the entries, so their numbers and play positions follow the new order.</summary>
    private void Rebuild()
    {
        var items = _items.ToList();
        _items.Clear();
        Tracks.Clear();
        Videos.Clear();
        foreach (var item in items) Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task<bool> EditAsync(Func<Task> send, bool reloadOnFailure)
    {
        try
        {
            await Task.Run(send);
            shell.Viewer?.PlaylistsEdited();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn($"{Title}: the server did not take a change.", ex);
            if (reloadOnFailure)
            {
                shell.Live.Say("The server did not take the change");
                _items.Clear();
                Tracks.Clear();
                Videos.Clear();
                _ = ActivateAsync();
            }

            return false;
        }
    }
}
