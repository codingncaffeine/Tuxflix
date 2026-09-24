using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Imaging;
using Tuxflix.Core;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The window's state: which server, which page, which tab, and the commands the title bar,
/// rail and status bar offer.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// The keyring entry the Plex account token is kept under: one per profile, because the keyring
    /// is the user's and a portable or test profile must not sign in with another profile's account.
    /// </summary>
    private string KeyringAccount => "plex-" + Identity.ClientIdentifier;

    private readonly SettingsStore _settings;
    private readonly HttpClient _plexTv;

    public ShellViewModel(SettingsStore settings, AppPaths paths)
    {
        _settings = settings;
        Paths = paths;
        Identity = new PlexClientIdentity(settings.Current.ClientIdentifier, BuildInfo.Version, Environment.MachineName);
        _plexTv = Identity.CreateHttpClient();
        Account = new PlexAccountClient(_plexTv);
        Rail = new LibraryRailViewModel(this);
        Router.PropertyChanged += OnRouterChanged;
    }

    public Router Router { get; } = new();

    public LibraryRailViewModel Rail { get; }

    public AppPaths Paths { get; }

    public PlexClientIdentity Identity { get; }

    public PlexAccountClient Account { get; }

    public Keyring Keyring { get; } = new();

    /// <summary>The servers the signed-in account can reach, from the last look.</summary>
    public IReadOnlyList<PlexResource> Servers { get; private set; } = [];

    [ObservableProperty]
    public partial ServerSession? Session { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; private set; }

    public bool IsSignedOut => !IsSignedIn;

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

    /// <summary>Called once the window is on screen: the demo, a remembered sign-in, or the welcome page.</summary>
    public void Start(bool demo)
    {
        if (demo)
        {
            OpenDemo();
        }
        else
        {
            _ = ResumeAsync();
        }
    }

    [RelayCommand]
    public void OpenDemo()
    {
        Log.Info("Opening the demo library.");
        Open(ServerSession.CreateDemo(Identity));
        if (!IsSignedIn) AccountName = "Demo Viewer";
    }

    [RelayCommand]
    private void SignIn() => Router.Navigate(new SignInPageViewModel(this));

    [RelayCommand]
    private void ShowServers() => Router.Navigate(new ServersPageViewModel(this, Servers, message: null));

    [RelayCommand]
    private async Task SignOutAsync()
    {
        Log.Info("Signing out.");
        await Keyring.ClearAsync(KeyringAccount);
        IsSignedIn = false;
        Servers = [];
        AccountName = "Guest";
        _settings.Current.LastServerId = null;
        _settings.Save();
        Close();
        Router.Reset(new WelcomePageViewModel(this));
    }

    /// <summary>Reconnects a remembered sign-in to the server used last time.</summary>
    private async Task ResumeAsync()
    {
        try
        {
            var token = await Keyring.LookupAsync(KeyringAccount);
            if (token is null)
            {
                Log.Info("No remembered Plex sign-in; showing the welcome page.");
                Router.Reset(new WelcomePageViewModel(this));
                return;
            }

            Router.Reset(new StatusPageViewModel("Signing in", "Checking your Plex sign-in…"));
            await CompleteSignInAsync(token, remember: false, CancellationToken.None);
        }
        catch (PlexUnauthorizedException)
        {
            Log.Info("The remembered Plex sign-in is no longer valid.");
            await Keyring.ClearAsync(KeyringAccount);
            Router.Reset(new WelcomePageViewModel(this) { Notice = "Your Plex sign-in has expired. Sign in again to see your servers." });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn("Plex could not be reached at start.", ex);
            Router.Reset(new WelcomePageViewModel(this) { Notice = "plex.tv could not be reached. Check the connection, then sign in again." });
        }
    }

    /// <summary>
    /// Finishes a sign-in with the account token plex.tv handed back: who it is, which servers it
    /// can reach, and on to the one used last time (or the only one), else a choice.
    /// </summary>
    public async Task CompleteSignInAsync(string token, bool remember, CancellationToken cancellation)
    {
        var user = await Account.GetUserAsync(token, cancellation);
        AccountName = user.DisplayName;
        IsSignedIn = true;
        Log.Info("Signed in to Plex.");

        if (remember && !await Keyring.StoreAsync(KeyringAccount, "Tuxflix: Plex sign-in", token))
        {
            Log.Warn("The sign-in holds for this session only: no keyring would keep it.");
        }

        Servers = await Account.GetServersAsync(token, cancellation);
        Log.Info($"The account can reach {Servers.Count} server(s).");

        var last = Servers.FirstOrDefault(s => s.ClientIdentifier == _settings.Current.LastServerId);
        if (last is not null || Servers.Count == 1)
        {
            await ConnectAsync(last ?? Servers[0], cancellation);
        }
        else
        {
            Router.Reset(new ServersPageViewModel(this, Servers, Servers.Count == 0
                ? "This Plex account has no servers yet, and none are shared with it."
                : null));
        }
    }

    /// <summary>Finds the best way to reach <paramref name="server"/> and opens its library.</summary>
    public async Task ConnectAsync(PlexResource server, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(server);
        Router.Reset(new StatusPageViewModel($"Connecting to {server.Name}", "Finding the fastest way to reach it…"));

        var connection = await ConnectionPicker.PickAsync(_plexTv, server, cancellation);
        if (connection is null)
        {
            Router.Reset(new ServersPageViewModel(this, Servers, $"{server.Name} did not answer at any of its addresses. Is it running?"));
            return;
        }

        _settings.Current.LastServerId = server.ClientIdentifier;
        _settings.Save();
        Open(ServerSession.CreateRemote(Identity, server, connection, Paths));
    }

    private void Open(ServerSession session)
    {
        Close();
        ImageLoader.Current = session.Images;
        Session = session;
        Router.Reset(new HomePageViewModel(this, session));
        _ = Rail.LoadAsync(session);
    }

    private void Close()
    {
        Rail.Clear();
        ImageLoader.Current = null;
        Session?.Dispose();
        Session = null;
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
