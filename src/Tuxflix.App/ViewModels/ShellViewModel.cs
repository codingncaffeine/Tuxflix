using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Imaging;
using Tuxflix.Core;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The window's state: which server, which page, which tab, and the commands the title bar,
/// rail and status bar offer.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    public ShellViewModel(SettingsStore settings, AppPaths paths)
    {
        _settings = settings;
        Paths = paths;
        Identity = new PlexClientIdentity(settings.Current.ClientIdentifier, BuildInfo.Version, Environment.MachineName);
        Rail = new LibraryRailViewModel(this);
        Router.PropertyChanged += OnRouterChanged;
    }

    public Router Router { get; } = new();

    public LibraryRailViewModel Rail { get; }

    public AppPaths Paths { get; }

    public PlexClientIdentity Identity { get; }

    [ObservableProperty]
    public partial ServerSession? Session { get; private set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = "Guest";

    public string AccountInitials => string.Concat(AccountName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string ServerName => Session?.Name ?? "Not connected";

    public string ServerDetail => Session?.Detail ?? "Sign in to see your servers";

    public bool IsConnected => Session is not null;

    public TopTab CurrentTab => Router.Current?.Tab ?? TopTab.Library;

    public bool IsLibraryTab => CurrentTab == TopTab.Library;

    public bool IsDiscoverTab => CurrentTab == TopTab.Discover;

    public bool IsActivityTab => CurrentTab == TopTab.Activity;

    public bool ShowRail => Router.Current?.ShowsRail == true && Session is not null;

    public bool IsHome => Router.Current is HomePageViewModel;

    /// <summary>Called once the window is on screen.</summary>
    public void Start(bool demo)
    {
        if (demo)
        {
            OpenDemo();
        }
        else
        {
            Router.Reset(new WelcomePageViewModel(this));
        }
    }

    [RelayCommand]
    public void OpenDemo()
    {
        Log.Info("Opening the demo library.");
        var session = ServerSession.CreateDemo(Identity);
        ImageLoader.Current = session.Images;
        Session = session;
        AccountName = "Demo Viewer";
        Router.Reset(new HomePageViewModel(this, session));
        _ = Rail.LoadAsync(session);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Router.Back();

    private bool CanGoBack() => Router.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Router.Forward();

    private bool CanGoForward() => Router.CanGoForward;

    [RelayCommand]
    public void GoHome()
    {
        if (Session is { } session)
        {
            if (Router.Current is not HomePageViewModel) Router.Navigate(new HomePageViewModel(this, session));
        }
        else
        {
            Router.Navigate(new WelcomePageViewModel(this));
        }
    }

    [RelayCommand]
    private void ShowDiscover()
    {
        if (Router.Current?.Tab != TopTab.Discover)
        {
            Router.Navigate(new ComingSoonPageViewModel(
                TopTab.Discover,
                "Discover",
                "Your Plex Watchlist, what is trending and what is new on your servers will live here.",
                "Icon.Compass"));
        }
    }

    [RelayCommand]
    private void ShowActivity()
    {
        if (Router.Current?.Tab != TopTab.Activity)
        {
            Router.Navigate(new ComingSoonPageViewModel(
                TopTab.Activity,
                "Activity",
                "What is playing on your servers, your watch history and your viewing statistics will live here.",
                "Icon.Pulse"));
        }
    }

    [RelayCommand]
    private void ShowDownloads() => Router.Navigate(new ComingSoonPageViewModel(
        TopTab.Library,
        "Downloads",
        "Download movies and episodes to watch without a connection, with a queue, pause and a speed limit.",
        "Icon.DownloadSimple"));

    public void OpenItem(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Session is not { } session) return;
        Router.Navigate(new ItemPageViewModel(this, session, item));
        Rail.Highlight(item);
    }

    public void SaveSettings() => _settings.Save();

    public AppSettings Settings => _settings.Current;

    partial void OnSessionChanged(ServerSession? value)
    {
        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(ServerDetail));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowRail));
    }

    partial void OnAccountNameChanged(string value) => OnPropertyChanged(nameof(AccountInitials));

    private void OnRouterChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentTab));
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsDiscoverTab));
        OnPropertyChanged(nameof(IsActivityTab));
        OnPropertyChanged(nameof(ShowRail));
        OnPropertyChanged(nameof(IsHome));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }
}
