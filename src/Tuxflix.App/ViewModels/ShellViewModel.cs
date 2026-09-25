using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Imaging;
using Tuxflix.App.Music;
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
    private readonly HttpMessageHandler? _network;
    private readonly HttpClient _plexTv;
    private CancellationTokenSource? _connecting;

    /// <param name="settings">The profile's settings.</param>
    /// <param name="paths">The profile's folders.</param>
    /// <param name="network">Stands in for the network under test; null for the real one.</param>
    public ShellViewModel(SettingsStore settings, AppPaths paths, HttpMessageHandler? network = null)
    {
        _settings = settings;
        _network = network;
        Paths = paths;
        Identity = new PlexClientIdentity(settings.Current.ClientIdentifier, BuildInfo.Version, Environment.MachineName);
        _plexTv = CreateHttpClient();
        Account = new PlexAccountClient(_plexTv);
        Rail = new LibraryRailViewModel(this);
        Router.PropertyChanged += OnRouterChanged;
    }

    public Router Router { get; } = new();

    public LibraryRailViewModel Rail { get; }

    public AppPaths Paths { get; }

    public PlexClientIdentity Identity { get; }

    public PlexAccountClient Account { get; }

    public ISecretStore Keyring { get; init; } = new Keyring();

    /// <summary>
    /// Whether players tell the server where playback is and mark what was finished as watched.
    /// Off for probe runs against a real server, which must leave the viewer's progress as it was.
    /// </summary>
    public bool ReportsPlayback { get; init; } = true;

    /// <summary>
    /// Whether players start muted. Probe runs do: the sound device still opens and is fed, so the
    /// whole audio path is exercised, but nothing is heard, even when the probe changes the volume.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>Opens a web page in the desktop's browser; replaced under test so no browser opens.</summary>
    public Action<string> OpenUrl { get; init; } = OpenInBrowser;

    /// <summary>An HTTP client carrying this installation's identity, over the shell's network.</summary>
    public HttpClient CreateHttpClient() => Identity.CreateHttpClient(_network, disposeHandler: _network is null);

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

    /// <summary>The page takes the whole window: the title and status bars step aside.</summary>
    public bool IsImmersive => Router.Current?.IsImmersive == true;

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
        AccountDiscover = null;
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
        AccountDiscover = new PlexDiscoverClient(_plexTv, token);
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
            await ConnectAsync(last ?? Servers[0]);
        }
        else
        {
            Router.Reset(new ServersPageViewModel(this, Servers, Servers.Count == 0
                ? "This Plex account has no servers yet, and none are shared with it."
                : null));
        }
    }

    /// <summary>Finds the best way to reach <paramref name="server"/> and opens its library.</summary>
    /// <remarks>
    /// Connecting owns its cancellation rather than borrowing the caller's. The first thing it does
    /// is show the "Connecting" page, which leaves whichever page asked, and leaving a page cancels
    /// that page's work: a borrowed token was cancelled by the very navigation that announced the
    /// connection, and every address probe died unasked. A newer connection cancels an older one.
    /// </remarks>
    public async Task ConnectAsync(PlexResource server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();

        Router.Reset(new StatusPageViewModel($"Connecting to {server.Name}", "Finding the fastest way to reach it…"));

        var connection = await ConnectionPicker.PickAsync(_plexTv, server, connecting.Token);
        if (connecting.IsCancellationRequested) return;
        if (connection is null)
        {
            Router.Reset(new ServersPageViewModel(this, Servers, $"{server.Name} did not answer at any of its addresses. Is it running?"));
            return;
        }

        _settings.Current.LastServerId = server.ClientIdentifier;
        _settings.Save();
        Open(ServerSession.CreateRemote(Identity, server, connection, Paths, _network));
    }

    /// <summary>The desktop's browser, asked on a worker (D-Bus, or a process: never the UI thread).</summary>
    private static void OpenInBrowser(string url) => _ = Task.Run(() => Platform.Links.OpenAsync(url));

    private Platform.MprisService? _mediaControls;
    private Platform.CoverCache? _covers;
    private Platform.MusicSession? _musicSession;
    private Platform.VideoSession? _videoSession;

    /// <summary>The desktop's media controls follow what plays; a probe run gets none.</summary>
    internal void UseMediaControls(Platform.MprisService controls)
    {
        _mediaControls = controls;
        _covers = new Platform.CoverCache(Path.Combine(Paths.Cache, "covers"));
        AttachMedia();
    }

    /// <summary>A film playing is what the media controls drive; otherwise the music queue.</summary>
    private void AttachMedia()
    {
        if (_mediaControls is null || _covers is null) return;
        if (Router.Current is PlayerPageViewModel page && Session is { } session)
        {
            if (_videoSession?.Page != page)
            {
                _videoSession?.Dispose();
                _videoSession = new Platform.VideoSession(page, session, _covers);
            }

            _mediaControls.Attach(_videoSession);
            return;
        }

        _videoSession?.Dispose();
        _videoSession = null;
        if (_musicSession is null && Music is { } music && Session is { } open) _musicSession = new Platform.MusicSession(music, open, _covers);
        _mediaControls.Attach(_musicSession);
    }

    private void Open(ServerSession session)
    {
        Close();
        ImageLoader.Current = session.Images;
        Session = session;
        Music = new MusicPlayer(this, session);
        Router.Reset(new HomePageViewModel(this, session));
        _ = Rail.LoadAsync(session);
        AttachMedia();
    }

    private void Close()
    {
        _mediaControls?.Attach(null);
        _musicSession?.Dispose();
        _musicSession = null;
        _videoSession?.Dispose();
        _videoSession = null;
        Rail.Clear();
        Music?.Dispose();
        Music = null;
        ImageLoader.Current = null;
        Session?.Dispose();
        Session = null;
    }

    /// <summary>The music player of the open server: one queue, whatever page is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNowPlayingBar))]
    public partial MusicPlayer? Music { get; private set; }

    /// <summary>The now-playing bar stands under every page once music is queued, except over a film.</summary>
    public bool ShowNowPlayingBar => Music is { HasQueue: true } && !IsImmersive && Router.Current is not NowPlayingPageViewModel;

    partial void OnMusicChanged(MusicPlayer? oldValue, MusicPlayer? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnMusicPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnMusicPropertyChanged;
    }

    private void OnMusicPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlayer.HasQueue)) OnPropertyChanged(nameof(ShowNowPlayingBar));
    }

    [RelayCommand]
    private void ShowNowPlaying()
    {
        if (Session is { } session && Music is { } music && Router.Current is not NowPlayingPageViewModel)
        {
            Router.Navigate(new NowPlayingPageViewModel(this, session, music));
        }
    }

    /// <summary>The compact classic player was asked for: the main window opens it and steps aside.</summary>
    public event Action? CompactPlayerRequested;

    [RelayCommand]
    private void ShowCompactPlayer()
    {
        if (Music is not null) CompactPlayerRequested?.Invoke();
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
        if (Router.Current?.Tab != TopTab.Discover) Router.Navigate(new WatchlistPageViewModel(this, Session));
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
        PageViewModel page = item.Type switch
        {
            "artist" => new ArtistPageViewModel(this, session, item),
            "album" => new AlbumPageViewModel(this, session, item),

            // A track opens as its album, where it can be played in its place.
            "track" when item.ParentRatingKey is { } album => new AlbumPageViewModel(this, session, new MetadataItem { RatingKey = album, Type = "album", Title = item.ParentTitle ?? string.Empty }),
            "collection" => new CollectionPageViewModel(this, session, item),
            "playlist" => new PlaylistPageViewModel(this, session, item),
            _ => new ItemPageViewModel(this, session, item),
        };
        Router.Navigate(page);
        Rail.Highlight(item);
    }

    /// <summary>A library as a grid of its titles.</summary>
    public void OpenSection(LibraryDirectory section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (Session is not { } session) return;
        if (Router.Current is LibraryPageViewModel open && open.Section.Key == section.Key) return;
        Router.Navigate(new LibraryPageViewModel(this, session, section));
        Rail.ClearHighlight();
    }

    /// <summary>Everything one person is in.</summary>
    public void OpenPerson(string name, string? thumb, long tagId)
    {
        if (Session is not { } session) return;
        Router.Navigate(new PersonPageViewModel(this, session, name, thumb, tagId));
    }

    [RelayCommand]
    private void ShowSettings()
    {
        if (Router.Current is SettingsPageViewModel) return;
        Router.Navigate(new SettingsPageViewModel(this));
    }

    [RelayCommand]
    private void ShowPlaylists()
    {
        if (Session is { } session && Router.Current is not PlaylistsPageViewModel) Router.Navigate(new PlaylistsPageViewModel(this, session));
    }

    /// <summary>What the title bar's search box holds; a pause in typing searches.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    private Avalonia.Threading.DispatcherTimer? _searchPause;

    partial void OnSearchTextChanged(string value)
    {
        _searchPause?.Stop();
        _searchPause ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), Avalonia.Threading.DispatcherPriority.Normal, (_, _) => SearchNow());
        _searchPause.Start();
    }

    /// <summary>Searches for what the box holds now: Enter, or a pause in typing.</summary>
    [RelayCommand]
    private void SearchNow()
    {
        _searchPause?.Stop();
        if (Session is not { } session) return;
        var query = SearchText.Trim();
        if (Router.Current is SearchPageViewModel page)
        {
            _ = page.SearchAsync(query);
            return;
        }

        if (query.Length == 0) return;
        page = new SearchPageViewModel(this, session);
        Router.Navigate(page);
        _ = page.SearchAsync(query);
    }

    /// <summary>Plays a film or an episode, from where it was left when <paramref name="resume"/>.</summary>
    /// <param name="queue">What plays after it (a playlist, a collection); an episode without one goes on through its show.</param>
    public void Play(MetadataItem item, bool resume, VideoQueue? queue = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Session is not { } session) return;

        // A film takes over the sound: the music waits where it was.
        Music?.Pause();
        Router.Navigate(new PlayerPageViewModel(this, session, item, resume, queue));
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
        OnPropertyChanged(nameof(IsImmersive));
        OnPropertyChanged(nameof(ShowNowPlayingBar));
        AttachMedia();
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }
}
