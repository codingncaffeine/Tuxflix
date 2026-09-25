using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Tv;

/// <summary>
/// The TV interface's frame: the tabs along the top (home, each library, search), the clock, and
/// whether a controller is there to show its buttons' legend.
/// </summary>
public sealed partial class TvShellViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _loading;

    public TvShellViewModel(ShellViewModel shell)
    {
        Shell = shell;
        shell.PropertyChanged += OnShellChanged;
        shell.Router.PropertyChanged += OnRouterChanged;
        if (shell.Gamepads is { } pads)
        {
            pads.PadsChanged += OnPadsChanged;
            HasPads = pads.Pads.Count > 0;
        }

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Clock = Now());
        _clock.Start();
        Clock = Now();
        Load(shell.Session);
    }

    public ShellViewModel Shell { get; }

    /// <summary>HOME, one tab for each film, series and music library, then SEARCH.</summary>
    public ObservableCollection<TvTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial string Clock { get; private set; }

    /// <summary>A controller is connected: the legend shows its buttons rather than keys.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPads))]
    public partial bool HasPads { get; set; }

    public bool HasNoPads => !HasPads;

    /// <summary>The next start opens in the TV interface, full screen.</summary>
    public bool StartsInTv
    {
        get => Shell.Settings.Tv.StartInTv;
        set
        {
            if (Shell.Settings.Tv.StartInTv == value) return;
            Shell.Settings.Tv.StartInTv = value;
            Shell.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartLabel));
        }
    }

    public string StartLabel => StartsInTv ? "Start in the TV interface: on" : "Start in the TV interface: off";

    [RelayCommand]
    private void ToggleStartsInTv() => StartsInTv = !StartsInTv;

    public void Dispose()
    {
        _clock.Stop();
        _loading?.Cancel();
        Shell.PropertyChanged -= OnShellChanged;
        Shell.Router.PropertyChanged -= OnRouterChanged;
        if (Shell.Gamepads is { } pads) pads.PadsChanged -= OnPadsChanged;
    }

    private static string Now() => DateTime.Now.ToString("t", CultureInfo.CurrentCulture);

    private void OnPadsChanged(IReadOnlyList<string> pads) => Dispatcher.UIThread.Post(() => HasPads = pads.Count > 0);

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Session)) Load(Shell.Session);
    }

    private void OnRouterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Router.Current)) Select();
    }

    /// <summary>The libraries come from the server on a worker; until they do, HOME and SEARCH stand alone.</summary>
    private void Load(ServerSession? session)
    {
        _loading?.Cancel();
        Tabs.Clear();
        if (session is null) return;
        Tabs.Add(new TvTabViewModel("HOME", Shell.GoHome));
        Tabs.Add(new TvTabViewModel("SEARCH", Shell.OpenSearch));
        Select();
        var loading = _loading = new CancellationTokenSource();
        _ = LoadSectionsAsync(session, loading.Token);
    }

    private async Task LoadSectionsAsync(ServerSession session, CancellationToken cancellation)
    {
        try
        {
            var sections = await Task.Run(() => session.Client.GetSectionsAsync(cancellation), cancellation);
            if (cancellation.IsCancellationRequested) return;
            var index = 1;
            foreach (var section in sections.Where(s => s.Type is "movie" or "show" or "artist"))
            {
                Tabs.Insert(index++, new TvTabViewModel(section.Title.ToUpperInvariant(), () => Shell.OpenSection(section), section.Key));
            }

            Select();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Another server took over.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The TV interface could not list the libraries.", ex);
        }
    }

    /// <summary>Underlines the tab of the page showing.</summary>
    private void Select()
    {
        var page = Shell.Router.Current;
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.SectionKey is { } key
                ? page is LibraryPageViewModel library && library.Section.Key == key
                : tab.Title == "HOME" ? page is HomePageViewModel : page is SearchPageViewModel;
        }
    }
}

/// <summary>A tab along the top of the TV interface.</summary>
public sealed partial class TvTabViewModel(string title, Action open, string? sectionKey = null) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>The library the tab opens; null for HOME and SEARCH.</summary>
    public string? SectionKey { get; } = sectionKey;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => open();
}
