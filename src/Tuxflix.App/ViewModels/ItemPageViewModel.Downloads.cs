using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Downloading from an item page: a film or an episode downloads with one press and shows its
/// progress in place; a series offers the season on show, and rules that keep the next few
/// unwatched episodes of the series or of that season.
/// </summary>
public sealed partial class ItemPageViewModel
{
    /// <summary>The film's or episode's download; "gone" while it is not downloaded.</summary>
    [ObservableProperty]
    public partial DownloadRowViewModel? DownloadRow { get; private set; }

    /// <summary>What is downloaded of the series, and its rule.</summary>
    [ObservableProperty]
    public partial DownloadGroupViewModel? SeriesDownloads { get; private set; }

    /// <summary>What is downloaded of the season on show, and its rule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSeasonLabel), nameof(KeepSeasonLabel), nameof(StopSeasonLabel))]
    public partial DownloadGroupViewModel? SeasonDownloads { get; private set; }

    public bool CanDownload => DownloadRow is not null;

    public bool CanDownloadSeries => SeriesDownloads is not null;

    public string DownloadSeasonLabel => SeasonDownloads is { } season ? $"Download all of {season.Item.Title}" : "Download the season";

    public string KeepSeasonLabel => SeasonDownloads is { } season ? $"Keep the next 3 unwatched of {season.Item.Title}" : string.Empty;

    public string StopSeasonLabel => SeasonDownloads is { } season ? $"Stop keeping episodes of {season.Item.Title}" : string.Empty;

    private void DownloadsFollowItem(MetadataItem value)
    {
        if (session.MachineIdentifier is not { } server) return;
        DownloadRow = value.Type is "movie" or "episode" ? shell.Downloads.RowFor(server, value) : null;
        SeriesDownloads = value.Type == "show" ? shell.Downloads.Group(server, value) : null;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDownloadSeries));
    }

    partial void OnSelectedSeasonChanged(SeasonTabViewModel? value) =>
        SeasonDownloads = value is not null && session.MachineIdentifier is { } server ? shell.Downloads.Group(server, value.Season) : null;

    [RelayCommand]
    private void Download()
    {
        // The page's own record has the file's details once it has loaded; the manager reads them again if not.
        if (DownloadRow is not null) shell.Downloads.Download(session, Item);
    }

    [RelayCommand]
    private async Task DownloadSeasonAsync()
    {
        if (SelectedSeason is not { } season) return;
        try
        {
            await shell.Downloads.DownloadAllAsync(session, season.Season);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Tuxflix.Core.Diagnostics.Log.Warn($"The episodes of {season.Title} could not be listed for download.", ex);
        }
    }

    /// <summary>Keeps the next few unwatched episodes of the series; the count comes as text from the menu.</summary>
    [RelayCommand]
    private void KeepNext(string count) =>
        shell.Downloads.KeepNext(session, Item, int.Parse(count, CultureInfo.InvariantCulture));

    [RelayCommand]
    private void KeepNextOfSeason()
    {
        if (SelectedSeason is { } season) shell.Downloads.KeepNext(session, season.Season, 3);
    }

    [RelayCommand]
    private void StopKeepingSeries()
    {
        if (SeriesDownloads?.Rule is { } rule) shell.Downloads.Manager.RemoveRule(rule.Id);
    }

    [RelayCommand]
    private void StopKeepingSeason()
    {
        if (SeasonDownloads?.Rule is { } rule) shell.Downloads.Manager.RemoveRule(rule.Id);
    }

    [RelayCommand]
    private void OpenDownloads() => shell.ShowDownloadsCommand.Execute(null);
}
