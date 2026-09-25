using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Controls;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Discover: the account's Watchlist, every title with whether the open server has it. A title the
/// server has opens its page; the others say so.
/// </summary>
public sealed partial class WatchlistPageViewModel(ShellViewModel shell, ServerSession? session) : PageViewModel
{
    public override string Title => "Watchlist";

    public override TopTab Tab => TopTab.Discover;

    public override bool ShowsRail => false;

    public ObservableCollection<WatchlistTileViewModel> Titles { get; } = [];

    /// <summary>Signed out, and no demo open: there is no Watchlist to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool NeedsSignIn { get; private set; }

    /// <summary>Signed in with no server open: the Watchlist is shown against a server's library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool NeedsServer { get; private set; }

    public bool IsEmpty => !IsLoading && !HasError && !NeedsSignIn && !NeedsServer && Titles.Count == 0;

    /// <summary>There is a Watchlist to list: signed in, or the demo, with a server open.</summary>
    public bool HasList => !NeedsSignIn && !NeedsServer;

    partial void OnNeedsSignInChanged(bool value) => OnPropertyChanged(nameof(HasList));

    partial void OnNeedsServerChanged(bool value) => OnPropertyChanged(nameof(HasList));

    protected override IEnumerable<string> LoadingDependents => [nameof(IsEmpty)];

    public string CountText
    {
        get
        {
            var titles = Titles.Count == 1 ? "1 title" : $"{Titles.Count.ToString(CultureInfo.CurrentCulture)} titles";
            var here = Titles.Count(t => t.HasCopy);
            return session is null ? titles : $"{titles}   ·   {here.ToString(CultureInfo.CurrentCulture)} on {session.Name}";
        }
    }

    /// <summary>A line saying what a press did when it could not do what was asked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    [RelayCommand]
    private void SignIn() => shell.SignInCommand.Execute(null);

    [RelayCommand]
    private void ChooseServer() => shell.ShowServersCommand.Execute(null);

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        NeedsSignIn = shell.Discover is null;
        NeedsServer = !NeedsSignIn && shell.Watchlist is null;
        if (shell.Watchlist is not { } watchlist)
        {
            Titles.Clear();
            return;
        }

        var titles = await watchlist.GetAsync(cancellation);
        Titles.Clear();
        foreach (var title in titles) Titles.Add(new WatchlistTileViewModel(shell, watchlist, title) { Said = Say, Removed = Remove });
        OnPropertyChanged(nameof(CountText));

        // Which titles the server has: the tiles are up already, the marks follow.
        await Task.WhenAll(Titles.ToList().Select(t => t.CheckAsync()));
        OnPropertyChanged(nameof(CountText));
    }

    private void Remove(WatchlistTileViewModel tile)
    {
        Titles.Remove(tile);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Shows a notice for a few seconds; the press that caused it is done at once.</summary>
    private void Say(string notice)
    {
        Notice = notice;
        _ = ClearAsync();

        async Task ClearAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (Notice == notice) Notice = null;
        }
    }
}

/// <summary>A title on the Watchlist: its poster, and whether the open server has it.</summary>
public sealed partial class WatchlistTileViewModel(ShellViewModel shell, WatchlistService watchlist, MetadataItem title) : ObservableObject, IMenuSource
{
    /// <summary>Open it (a film the server has plays at once), or take it off the Watchlist.</summary>
    public IReadOnlyList<MenuEntry> MenuEntries()
    {
        var entries = new List<MenuEntry>();
        if (Copy is { Type: "movie" } film) entries.Add(new("Play", () => shell.Play(film, resume: true)));
        entries.Add(new("Open", () => OpenCommand.Execute(null)));
        entries.Add(MenuEntry.Separator);
        entries.Add(new("Remove from Watchlist", () => RemoveCommand.Execute(null)));
        return entries;
    }

    /// <summary>The title as Plex's catalogue has it.</summary>
    public MetadataItem Item => title;

    public string Title => title.Title;

    public string Subtitle => string.Join("  ·  ", new[]
    {
        title.Year?.ToString(CultureInfo.InvariantCulture),
        title.Type == "show" ? "Series" : "Film",
    }.Where(part => part is not null));

    /// <summary>The catalogue's poster; a server fetches a web address like its own.</summary>
    public string? ImagePath => title.Thumb;

    public string AccessibleName => IsMissing ? $"{Title}, {Subtitle}, not on this server" : $"{Title}, {Subtitle}";

    /// <summary>The server's copy, once looked up; null when it has none, or before the look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopy), nameof(IsMissing), nameof(AccessibleName))]
    public partial MetadataItem? Copy { get; private set; }

    /// <summary>Whether the server has been asked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing), nameof(AccessibleName))]
    public partial bool IsChecked { get; private set; }

    public bool HasCopy => Copy is not null;

    /// <summary>The server was asked and has no copy.</summary>
    public bool IsMissing => IsChecked && Copy is null;

    public string MissingText => $"Not on {watchlist.Session.Name}";

    /// <summary>Where the tile says what went wrong: its page's notice line.</summary>
    internal Action<string>? Said { get; init; }

    /// <summary>Called once the title is off the Watchlist, for its page to let the tile go.</summary>
    internal Action<WatchlistTileViewModel>? Removed { get; init; }

    /// <summary>Asks the server for its copy; a failure leaves the tile unmarked, to be asked again when opened.</summary>
    public async Task CheckAsync()
    {
        try
        {
            Copy = await watchlist.FindCopyAsync(title);
            IsChecked = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or PlexUnauthorizedException)
        {
            Log.Warn($"Whether {watchlist.Session.Name} has a Watchlist title could not be found out.", ex);
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!IsChecked) await CheckAsync();
        if (Copy is { } copy)
        {
            shell.OpenItem(copy);
        }
        else if (Said is { } say)
        {
            say(IsChecked ? $"{Title} is not on {watchlist.Session.Name}." : $"{watchlist.Session.Name} could not be asked for {Title}.");
        }
        else
        {
            // A home shelf has no line to say it on: the Watchlist page does.
            shell.ShowDiscoverCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if ((PlexDiscoverClient.CatalogKey(title.Guid) ?? title.RatingKey) is not { Length: > 0 } key) return;
        try
        {
            await watchlist.SetAsync(key, on: false);
            Removed?.Invoke(this);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("A title could not be taken off the Watchlist.", ex);
            Said?.Invoke($"{Title} could not be taken off the Watchlist. Plex did not answer.");
        }
    }
}
