using CommunityToolkit.Mvvm.Input;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The Downloads page, Steam's downloads page for films: the queue with its progress, what is kept
/// (it plays without the server), the rules, the space, and the download options. Opened by itself
/// at start when no server can be reached.
/// </summary>
public sealed partial class DownloadsPageViewModel(ShellViewModel shell) : PageViewModel
{
    public override string Title => "Downloads";

    public override bool ShowsRail => false;

    public DownloadCentre Centre => shell.Downloads;

    /// <summary>Where they go, how many at once, how fast: the same options a settings page shows.</summary>
    public DownloadSettingsViewModel Options { get; } = new(shell);

    /// <summary>Why the window is offline, over the list; null when a server is open.</summary>
    public string? Notice { get; init; }

    public bool HasNotice => Notice is not null;

    public bool IsEmpty => !IsLoading && Centre.IsEmpty;

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        await Centre.Loaded;
        Centre.MeasureSpace(now: true);
        OnPropertyChanged(nameof(IsEmpty));
        Centre.PropertyChanged += OnCentreChanged;
    }

    public override void Deactivate()
    {
        base.Deactivate();
        Centre.PropertyChanged -= OnCentreChanged;
    }

    [RelayCommand]
    private void PauseAll() => Centre.Manager.PauseAll();

    [RelayCommand]
    private void ResumeAll() => Centre.Manager.ResumeAll();

    [RelayCommand]
    private void TryAgain() => shell.Reconnect();

    private void OnCentreChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadCentre.Summary)) OnPropertyChanged(nameof(IsEmpty));
    }
}
