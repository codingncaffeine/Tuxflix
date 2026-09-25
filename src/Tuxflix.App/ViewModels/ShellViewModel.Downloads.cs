using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.App.Imaging;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Downloads;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Downloads and the offline library: the download manager follows whichever server is open, a
/// kept copy plays with or without its server, and a start with no server in reach opens what is
/// kept instead of an error.
/// </summary>
public sealed partial class ShellViewModel
{
    private DownloadCentre? _downloads;
    private ServerSession? _offline;
    private Task _downloadsStopped = Task.CompletedTask;

    /// <summary>The downloads, made on first use; the index is read on a worker.</summary>
    public DownloadCentre Downloads => _downloads ??= new DownloadCentre(
        this,
        new DownloadManager(Path.Combine(Paths.Data, "downloads.json"), Path.Combine(Paths.Data, "downloads"), () => Settings.Downloads),
        PostToUi);

    /// <summary>Runs an action on the UI thread; tests replace it with a queue they run by hand.</summary>
    internal Action<Action> PostToUi { get; init; } = action => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    /// <summary>No server could be reached: the window shows what is downloaded.</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; private set; }

    /// <summary>Done once the downloads have stopped and their list is on disk, after <see cref="StopDownloads"/>.</summary>
    public Task DownloadsStopped => _downloadsStopped;

    partial void OnIsOfflineChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(ServerDetail));
    }

    // Downloads run from whichever server is open, and wait while it is not.
    partial void OnSessionChanged(ServerSession? oldValue, ServerSession? newValue)
    {
        if (oldValue?.MachineIdentifier is { } closed) Downloads.Manager.Detach(closed);
        if (newValue?.MachineIdentifier is not { } opened) return;
        IsOffline = false;
        Downloads.Manager.Attach(opened, newValue.Client);
    }

    /// <summary>Closing: the transfers stop where they are, to carry on at the next start.</summary>
    public void StopDownloads()
    {
        if (_downloads is { } downloads) _downloadsStopped = downloads.Manager.DisposeAsync().AsTask();
    }

    /// <summary>
    /// At start, with no server in reach: opens the offline library if anything is downloaded, and
    /// says whether it did. With nothing kept, the page that explains the failure is more use.
    /// </summary>
    private async Task<bool> OpenOfflineAsync(string reason)
    {
        await Downloads.Loaded;
        if (Downloads.IsEmpty) return false;
        Log.Info($"Starting offline: {reason}");
        IsOffline = true;

        // Artwork kept beside the downloads loads through a loader that never asks a server.
        _offline ??= ServerSession.CreateOffline(Identity, "offline", "Offline");
        ImageLoader.Current ??= _offline.Images;
        Router.Reset(new DownloadsPageViewModel(this) { Notice = reason + " What is downloaded can be watched without it." });
        return true;
    }

    /// <summary>Tries the remembered sign-in and server again, from the offline library.</summary>
    internal void Reconnect()
    {
        IsOffline = false;
        _ = ResumeAsync();
    }

    /// <summary>Plays a kept copy: through the open server when it is the copy's own, else on its own.</summary>
    internal async Task PlayDownloadAsync(DownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var item = await Task.Run(() => ReadKept(record));
        ServerSession session;
        if (Session is { } open && open.MachineIdentifier == record.ServerId)
        {
            session = open;
        }
        else
        {
            _offline ??= ServerSession.CreateOffline(Identity, "offline", "Offline");
            session = _offline;
        }

        Music?.Pause();
        Log.Info($"Playing the downloaded copy of {record.Title}.");
        Router.Navigate(new PlayerPageViewModel(this, session, item, resume: true) { Download = record });
    }

    /// <summary>The item as kept beside its file; what the record knows when that cannot be read. A worker's job.</summary>
    internal static MetadataItem ReadKept(DownloadRecord record)
    {
        try
        {
            if (File.Exists(record.MetadataPath)
                && JsonSerializer.Deserialize(File.ReadAllText(record.MetadataPath), PlexJsonContext.Default.PlexEnvelope)?.MediaContainer?.Metadata?.FirstOrDefault() is { } kept)
            {
                return kept;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The kept details of {record.Title} could not be read; playing with what the download list knows.", ex);
        }

        return new MetadataItem
        {
            RatingKey = record.RatingKey,
            Type = record.Type,
            Title = record.Title,
            GrandparentTitle = record.ShowTitle,
            GrandparentRatingKey = record.ShowKey,
            ParentRatingKey = record.SeasonKey,
            ParentIndex = record.Season,
            Index = record.Episode,
            Year = record.Year,
            Duration = record.Duration,
        };
    }
}
