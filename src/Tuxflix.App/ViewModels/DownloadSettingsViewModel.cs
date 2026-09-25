using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Downloads;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The download options: where new downloads go, how many run at once, how fast they may go, and
/// the rules that keep episodes coming. Built from the shell, for the Downloads page and for a
/// settings page alike; every change is saved at once and applies to the downloads running.
/// </summary>
public sealed partial class DownloadSettingsViewModel(ShellViewModel shell) : ObservableObject
{
    public DownloadCentre Centre => shell.Downloads;

    /// <summary>Where new downloads go.</summary>
    public string Folder => Centre.Manager.Folder;

    public bool HasChosenFolder => !string.IsNullOrEmpty(shell.Settings.Downloads.Folder);

    /// <summary>Why the folder last chosen was not taken, in words; null when it was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderProblem))]
    public partial string? FolderProblem { get; private set; }

    public bool HasFolderProblem => FolderProblem is not null;

    public bool TwoAtOnce => shell.Settings.Downloads.Simultaneous >= 2;

    public bool OneAtOnce => !TwoAtOnce;

    /// <summary>"No speed limit", "At most 5 MB/s".</summary>
    public string SpeedLimitText => shell.Settings.Downloads.SpeedLimit is > 0 and var limit
        ? $"At most {(limit / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} MB/s"
        : "No speed limit";

    /// <summary>
    /// New downloads go to <paramref name="folder"/>, once a worker has made sure it can be written;
    /// null goes back to the default. What is already kept stays where it is.
    /// </summary>
    public async Task SetFolderAsync(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var problem = await Task.Run(() => DownloadFolders.Check(folder, Environment.GetEnvironmentVariable));
            FolderProblem = problem;
            if (problem is not null)
            {
                Log.Warn($"Downloads cannot go to {folder}: {problem}");
                return;
            }
        }

        FolderProblem = null;
        shell.Settings.Downloads.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        shell.SaveSettings();
        Log.Info($"New downloads go to {Centre.Manager.Folder}.");
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasChosenFolder));
        Centre.MeasureSpace(now: true);
    }

    [RelayCommand]
    private Task UseDefaultFolderAsync() => SetFolderAsync(null);

    [RelayCommand]
    private void OneAtATime() => SetSimultaneous(1);

    [RelayCommand]
    private void TwoAtATime() => SetSimultaneous(2);

    /// <summary>The most all downloads together may take, in megabytes a second as the menu names it; 0 for no limit.</summary>
    [RelayCommand]
    private void SetSpeedLimit(string limit)
    {
        var megabytes = double.Parse(limit, CultureInfo.InvariantCulture);
        shell.Settings.Downloads.SpeedLimit = (long)(megabytes * 1_000_000);
        shell.SaveSettings();
        Log.Info(megabytes > 0 ? $"Downloads limited to {limit} MB/s." : "Downloads have no speed limit.");
        OnPropertyChanged(nameof(SpeedLimitText));
    }

    private void SetSimultaneous(int count)
    {
        if (shell.Settings.Downloads.Simultaneous == count) return;
        shell.Settings.Downloads.Simultaneous = count;
        shell.SaveSettings();
        Centre.Manager.SettingsChanged();
        OnPropertyChanged(nameof(TwoAtOnce));
        OnPropertyChanged(nameof(OneAtOnce));
    }
}
