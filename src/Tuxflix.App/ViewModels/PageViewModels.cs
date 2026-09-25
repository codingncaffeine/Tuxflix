using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>A film, series, season or episode: Steam's game page, for something to watch.</summary>
public sealed partial class ItemPageViewModel(ShellViewModel shell, ServerSession session, MetadataItem summary) : PageViewModel
{
    public override string Title => Item.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingTitle), nameof(Meta), nameof(Tagline), nameof(Summary), nameof(BackdropPath), nameof(PosterPath),
        nameof(PlayLabel), nameof(RemainingText), nameof(HasProgress), nameof(Progress), nameof(Runtime), nameof(LastWatched), nameof(AudienceRating),
        nameof(ContentRating), nameof(HasContentRating), nameof(Genres), nameof(Director), nameof(Studio), nameof(MediaSummary), nameof(Cast),
        nameof(IsSeries), nameof(Kicker), nameof(HasTagline), nameof(ProgressText), nameof(HasDirector), nameof(HasStudio), nameof(HasMediaSummary),
        nameof(LogoPath), nameof(HasLogo), nameof(ShowTextTitle))]
    public partial MetadataItem Item { get; set; } = summary;

    public ObservableCollection<SeasonTabViewModel> Seasons { get; } = [];

    public ObservableCollection<EpisodeRowViewModel> Episodes { get; } = [];

    [ObservableProperty]
    public partial SeasonTabViewModel? SelectedSeason { get; set; }

    public bool IsSeries => Item.Type is "show" or "season";

    public bool HasSeasons => Seasons.Count > 1;

    /// <summary>The line above the title: "SERIES", "S2 · E5 — LANTERNFALL", "MOVIE".</summary>
    public string Kicker => Item.Type switch
    {
        "show" => "SERIES",
        "season" => $"{Item.ParentTitle?.ToUpperInvariant()} · {Item.Title.ToUpperInvariant()}",
        "episode" => $"{Item.GrandparentTitle?.ToUpperInvariant()} · {Format.EpisodeCode(Item)}",
        _ => "MOVIE",
    };

    public string HeadingTitle => Item.Type == "season" ? Item.ParentTitle ?? Item.Title : Item.Title;

    public string Meta => string.Join("   ·   ", new[]
    {
        Item.Year?.ToString(CultureInfo.InvariantCulture),
        Item.Type == "show" && Item.ChildCount is { } seasons ? (seasons == 1 ? "1 season" : $"{seasons} seasons") : Format.Runtime(Item.Duration),
        Item.Genre is { Count: > 0 } genres ? string.Join(", ", genres.Take(3).Select(g => g.TagText)) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    public string? Tagline => Item.Tagline;

    public bool HasTagline => !string.IsNullOrWhiteSpace(Item.Tagline);

    public string? Summary => Item.Summary;

    public string? BackdropPath => Artwork.Backdrop(Item);

    public string? PosterPath => Artwork.Poster(Item);

    /// <summary>The title as artwork, shown over the hero in place of the typed title, as Steam shows a game's logo.</summary>
    public string? LogoPath => Item.Type is "movie" or "show" or "season" ? Artwork.Logo(Item) : null;

    public bool HasLogo => LogoPath is not null;

    public bool ShowTextTitle => !HasLogo;

    /// <summary>The server's UltraBlur colours for the backdrop: the page takes on the film's own light.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlur), nameof(BlurTop), nameof(BlurBottom))]
    public partial UltraBlurColors? Blur { get; set; }

    public bool HasBlur => BlurTop is not null && BlurBottom is not null;

    public IBrush? BlurTop => Across(Blur?.TopLeft, Blur?.TopRight);

    public IBrush? BlurBottom => Across(Blur?.BottomLeft, Blur?.BottomRight);

    public bool HasProgress => Item.Progress is > 0 and < 1;

    public double Progress => Item.Progress ?? 0;

    public string ProgressText => HasProgress ? $"{Item.Progress!.Value:P0}".Replace(" ", string.Empty, StringComparison.Ordinal) + " watched" : string.Empty;

    public string PlayLabel => HasProgress ? "RESUME" : "PLAY";

    /// <summary>A line under the play bar saying what a press did, when it could not play.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayNotice))]
    public partial string? PlayNotice { get; private set; }

    public bool HasPlayNotice => PlayNotice is not null;

    [RelayCommand]
    private async Task PlayAsync()
    {
        try
        {
            // A film or an episode plays itself; a series or a season plays what is next in it.
            var target = Item.Type is "movie" or "episode" ? Item : await NextEpisodeAsync();
            if (target is null)
            {
                await SayAsync("There is nothing in this to play.");
                return;
            }

            shell.Play(target, resume: target.Progress is > 0 and < 1);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Tuxflix.Core.Diagnostics.Log.Warn("What to play next could not be found.", ex);
            await SayAsync("The server could not be asked what to play next.");
        }
    }

    /// <summary>The episode to play: the first started or unwatched one, else the very first.</summary>
    private async Task<MetadataItem?> NextEpisodeAsync()
    {
        var seasons = Item.Type == "season"
            ? [Item]
            : Seasons.Count > 0 ? Seasons.Select(s => s.Season).ToList() : [.. await Task.Run(() => session.Client.GetChildrenAsync(Item.RatingKey, CancellationToken.None))];

        MetadataItem? first = null;
        foreach (var season in seasons.Where(s => s.Index is not 0).Concat(seasons.Where(s => s.Index is 0)))
        {
            var episodes = await Task.Run(() => session.Client.GetChildrenAsync(season.RatingKey, CancellationToken.None));
            first ??= episodes.FirstOrDefault();
            if (episodes.FirstOrDefault(e => !e.IsWatched) is { } next) return next;
        }

        return first;
    }

    private async Task SayAsync(string notice)
    {
        PlayNotice = notice;
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (PlayNotice == notice) PlayNotice = null;
    }

    public string RemainingText => Format.Remaining(Item);

    public string Runtime => Item.Type == "show" && Item.LeafCount is { } episodes
        ? $"{episodes} episodes"
        : Format.Runtime(Item.Duration) is { Length: > 0 } runtime ? runtime : "—";

    public string LastWatched => Format.WhenWatched(Item.LastViewedAt);

    public string AudienceRating => Item.AudienceRating is { } rating ? rating.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    public string? ContentRating => Item.ContentRating;

    public bool HasContentRating => !string.IsNullOrEmpty(Item.ContentRating);

    public IReadOnlyList<string> Genres => Item.Genre?.Select(g => g.TagText).ToList() ?? [];

    public string Director => Item.Director is { Count: > 0 } directors ? string.Join(", ", directors.Select(d => d.TagText)) : "—";

    public string Studio => Item.Studio ?? "—";

    public string MediaSummary => Format.MediaSummary(Item.Media?.FirstOrDefault());

    public bool HasMediaSummary => MediaSummary.Length > 0;

    public bool HasDirector => Item.Director is { Count: > 0 };

    public bool HasStudio => !string.IsNullOrEmpty(Item.Studio);

    public IReadOnlyList<CastMemberViewModel> Cast => Item.Role?.Take(12).Select(r => new CastMemberViewModel(shell, r.TagText, r.Role, r.Thumb, r.Id)).ToList() ?? [];

    public bool HasCast => Cast.Count > 0;

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        // A season opens as its series, on that season.
        var key = Item.Type == "season" ? Item.ParentRatingKey ?? Item.RatingKey : Item.RatingKey;
        var wantSeason = Item.Type == "season" ? Item.RatingKey : null;

        if (await Task.Run(() => session.Client.GetItemDetailsAsync(key, cancellation), cancellation) is { } full)
        {
            Item = full;
        }

        OnPropertyChanged(nameof(HasCast));
        Extend(cancellation);

        // Current servers send the colours with the item; older ones compute them on request.
        Blur = Item.UltraBlurColors
               ?? (BackdropPath is { } backdrop
                   ? await Task.Run(() => session.Client.GetUltraBlurColorsAsync(backdrop, cancellation), cancellation)
                   : null);

        if (Item.Type != "show") return;

        var seasons = await Task.Run(() => session.Client.GetChildrenAsync(Item.RatingKey, cancellation), cancellation);
        Seasons.Clear();
        foreach (var season in seasons) Seasons.Add(new SeasonTabViewModel(this, season));
        OnPropertyChanged(nameof(HasSeasons));

        // Open on the season asked for, else the first one with something left to watch.
        SelectedSeason = Seasons.FirstOrDefault(s => s.Season.RatingKey == wantSeason)
                         ?? Seasons.FirstOrDefault(s => !s.Season.IsWatched)
                         ?? Seasons.FirstOrDefault();
    }

    private static LinearGradientBrush? Across(string? left, string? right)
    {
        if (!Color.TryParse("#" + left, out var from) || !Color.TryParse("#" + right, out var to)) return null;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
    }

    partial void OnSelectedSeasonChanged(SeasonTabViewModel? oldValue, SeasonTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is null) return;
        newValue.IsSelected = true;
        _ = LoadEpisodesAsync(newValue);
    }

    private async Task LoadEpisodesAsync(SeasonTabViewModel season)
    {
        try
        {
            var episodes = await Task.Run(() => session.Client.GetChildrenAsync(season.Season.RatingKey, CancellationToken.None));
            if (!ReferenceEquals(SelectedSeason, season)) return;
            Episodes.Clear();
            foreach (var episode in episodes) Episodes.Add(new EpisodeRowViewModel(shell, episode));
        }
        catch (Exception ex)
        {
            Tuxflix.Core.Diagnostics.Log.Warn($"The episodes of {season.Title} could not be loaded.", ex);
        }
    }
}

public sealed partial class SeasonTabViewModel(ItemPageViewModel page, MetadataItem season) : ObservableObject
{
    public MetadataItem Season { get; } = season;

    public string Title => Season.Title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => page.SelectedSeason = this;
}

public sealed partial class EpisodeRowViewModel(ShellViewModel shell, MetadataItem episode)
{
    public MetadataItem Episode { get; } = episode;

    public string Heading => $"{Episode.Index}. {Episode.Title}";

    public string? Summary => Episode.Summary;

    public string Runtime => Format.Runtime(Episode.Duration);

    public string? ThumbPath => Episode.Thumb;

    public bool IsWatched => State.IsWatched;

    public bool HasProgress => State.HasProgress;

    public double Progress => State.Progress ?? 0;

    [RelayCommand]
    private void Open() => shell.OpenItem(Episode);
}

/// <summary>A cast member on an item page; opening one lists everything they are in.</summary>
public sealed partial class CastMemberViewModel(ShellViewModel shell, string name, string? role, string? thumbPath, long? id)
{
    public string Name { get; } = name;

    public string? Role { get; } = role;

    public string? ThumbPath { get; } = thumbPath;

    public bool CanOpen => id is not null;

    public string OpenTip => $"Everything {Name} is in";

    [RelayCommand]
    private void Open()
    {
        if (id is { } tag) shell.OpenPerson(Name, ThumbPath, tag);
    }
}

/// <summary>First run with no server: sign in, or look around the demo library.</summary>
public sealed partial class WelcomePageViewModel(ShellViewModel shell) : PageViewModel
{
    public override string Title => "Welcome";

    public override bool ShowsRail => false;

    /// <summary>Why the welcome page is showing when it was not asked for: an expired sign-in, say.</summary>
    public string? Notice { get; init; }

    public bool HasNotice => Notice is not null;

    [RelayCommand]
    private void OpenDemo() => shell.OpenDemo();

    [RelayCommand]
    private void SignIn() => shell.SignInCommand.Execute(null);
}

/// <summary>A section that arrives in a later phase, said plainly rather than left blank.</summary>
public sealed class ComingSoonPageViewModel(TopTab tab, string title, string description, string iconKey) : PageViewModel
{
    public override string Title => title;

    public override TopTab Tab => tab;

    public override bool ShowsRail => false;

    public string Description => description;

    public Geometry? Icon => Application.Current?.FindResource(iconKey) as Geometry;
}
