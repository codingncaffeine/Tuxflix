using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>An audio or subtitle stream to choose before playing, or no subtitles.</summary>
public sealed class StreamOptionViewModel(MediaStream? stream, string title)
{
    public MediaStream? Stream { get; } = stream;

    /// <summary>The stream's id; 0 for no subtitles.</summary>
    public long Id => Stream?.Id ?? 0;

    public string Title { get; } = title;

    public override string ToString() => Title;
}

// The viewer's own part of an item page: watched or not, their rating, the playlists it goes in,
// and the audio and subtitles it starts with. Changes show at once and go to the server from a
// worker; the page reads the item again once the server has them.
public sealed partial class ItemPageViewModel : IViewerItem, ILiveRefresh
{
    private ItemState? _state;
    private bool _choosing;

    public ShellViewModel Shell => shell;

    public ItemState State => _state ??= Watch(shell.Viewer?.For(Item) ?? ItemState.Detached(Item));

    public bool CanMarkWatched => Item.Type is "movie" or "episode" or "show" or "season";

    public bool IsWatched => State.IsWatched;

    public string WatchedLabel => IsWatched ? "WATCHED" : "MARK WATCHED";

    public string WatchedTip => IsWatched
        ? (Item.Type is "show" or "season" ? "Watched: mark every episode unwatched" : "Watched: mark it unwatched")
        : (Item.Type is "show" or "season" ? "Mark every episode watched" : "Mark it watched");

    public bool CanRate => Item.Type is "movie" or "show" or "season" or "episode";

    /// <summary>The viewer's rating, 0 to 10; setting it rates the item on the server.</summary>
    public double? UserRating
    {
        get => State.UserRating;
        set
        {
            if (value == State.UserRating) return;
            _ = RateAsync(value);
        }
    }

    public string RatingTip => State.UserRating is null ? "Rate it: click a star, or half a star" : $"Your rating: {Controls.StarRating.Describe(State.UserRating)}. Click it again to take it away";

    public bool CanAddToPlaylist => ViewerState.PlaylistTypeOf(Item) is not null;

    public ObservableCollection<StreamOptionViewModel> AudioStreams { get; } = [];

    public ObservableCollection<StreamOptionViewModel> SubtitleStreams { get; } = [];

    [ObservableProperty]
    public partial StreamOptionViewModel? SelectedAudio { get; set; }

    [ObservableProperty]
    public partial StreamOptionViewModel? SelectedSubtitle { get; set; }

    /// <summary>A film or an episode whose streams the server described: its audio and subtitles can be chosen before it plays.</summary>
    public bool HasStreamChoices => AudioStreams.Count > 1 || SubtitleStreams.Count > 1;

    private MediaPart? Part => Item.Media?.FirstOrDefault()?.Part?.FirstOrDefault();

    [RelayCommand]
    private async Task ToggleWatchedAsync()
    {
        if (shell.Viewer is not { } viewer) return;
        if (!await viewer.SetWatchedAsync(Item, !State.IsWatched))
        {
            shell.Live.Say("The server did not take the change");
            return;
        }

        await ReloadAsync();
    }

    private async Task RateAsync(double? rating)
    {
        if (shell.Viewer is not { } viewer) return;
        if (!await viewer.RateAsync(Item, rating)) shell.Live.Say("The server did not take the rating");
    }

    /// <summary>The server has a newer word on the item: its play bar, its episodes and its streams.</summary>
    private async Task ReloadAsync()
    {
        if (shell.Session is not { } session) return;
        try
        {
            var key = Item.RatingKey;
            if (await Task.Run(() => session.Client.GetMetadataAsync(key, CancellationToken.None)) is { } fresh && fresh.RatingKey == Item.RatingKey)
            {
                Item = fresh;
            }

            if (SelectedSeason is { } season)
            {
                SelectedSeason = null;
                SelectedSeason = season;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Debug($"{Title} could not be read again: {ex.Message}");
        }
    }

    public void OnLibraryChanged(IReadOnlySet<string> sections, IReadOnlySet<string> items)
    {
        var mine = items.Contains(Item.RatingKey)
                   || (Item.ParentRatingKey is { } parent && items.Contains(parent))
                   || Episodes.Any(e => items.Contains(e.Episode.RatingKey));
        if (mine) _ = ReloadAsync();
    }

    partial void OnItemChanged(MetadataItem value)
    {
        // A season opens as its series: the state follows whichever item the page shows.
        if (_state is { } old && old.RatingKey != value.RatingKey)
        {
            old.PropertyChanged -= OnStateChanged;
            _state = null;
        }

        if (shell.Viewer is { } viewer && value.FetchedAt != 0) viewer.Take(value);
        StateChanged();
        OnPropertyChanged(nameof(CanMarkWatched));
        OnPropertyChanged(nameof(CanRate));
        OnPropertyChanged(nameof(CanAddToPlaylist));
        FillStreams();
    }

    private ItemState Watch(ItemState state)
    {
        state.PropertyChanged += OnStateChanged;
        return state;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => StateChanged();

    private void StateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsWatched));
        OnPropertyChanged(nameof(WatchedLabel));
        OnPropertyChanged(nameof(WatchedTip));
        OnPropertyChanged(nameof(UserRating));
        OnPropertyChanged(nameof(RatingTip));
    }

    /// <summary>The part's audio and subtitle streams, with the ones the server selected for this viewer chosen.</summary>
    private void FillStreams()
    {
        _choosing = true;
        try
        {
            AudioStreams.Clear();
            SubtitleStreams.Clear();
            if (Item.Type is "movie" or "episode" && Part is { } part && StreamChoice.IsKnown(part))
            {
                foreach (var audio in part.Stream!.Where(s => s.StreamType == StreamChoice.Audio)) AudioStreams.Add(new StreamOptionViewModel(audio, Name(audio)));
                SubtitleStreams.Add(new StreamOptionViewModel(null, "None"));
                foreach (var subtitle in part.Stream!.Where(s => s.StreamType == StreamChoice.Subtitle)) SubtitleStreams.Add(new StreamOptionViewModel(subtitle, Name(subtitle)));
                SelectedAudio = AudioStreams.FirstOrDefault(a => a.Stream!.Selected) ?? AudioStreams.FirstOrDefault();
                SelectedSubtitle = SubtitleStreams.FirstOrDefault(s => s.Stream?.Selected == true) ?? SubtitleStreams[0];
            }
            else
            {
                SelectedAudio = null;
                SelectedSubtitle = null;
            }
        }
        finally
        {
            _choosing = false;
        }

        OnPropertyChanged(nameof(HasStreamChoices));

        static string Name(MediaStream stream) =>
            (stream.ExtendedDisplayTitle ?? stream.DisplayTitle ?? stream.Language ?? "Unknown") + (stream.Forced && stream.StreamType == StreamChoice.Subtitle && stream.ExtendedDisplayTitle?.Contains("Forced", StringComparison.OrdinalIgnoreCase) != true ? " (Forced)" : string.Empty);
    }

    partial void OnSelectedAudioChanged(StreamOptionViewModel? value)
    {
        if (!_choosing && value?.Stream is { } stream) _ = ChooseAsync(stream.Id, null);
    }

    partial void OnSelectedSubtitleChanged(StreamOptionViewModel? value)
    {
        if (!_choosing && value is not null) _ = ChooseAsync(null, value.Id);
    }

    /// <summary>Keeps the choice on the server for this item and its other parts, as every Plex player then starts with it.</summary>
    private async Task ChooseAsync(long? audio, long? subtitle)
    {
        if (Part is not { Id: > 0 } part || shell.Session is not { } session) return;
        try
        {
            await Task.Run(() => session.Client.ChooseStreamsAsync(part.Id, audio, subtitle, CancellationToken.None));
            var key = Item.RatingKey;
            if (await Task.Run(() => session.Client.GetMetadataAsync(key, CancellationToken.None)) is { } fresh && fresh.RatingKey == Item.RatingKey)
            {
                // The new selection rides on the item the Play button hands the player.
                Item = fresh;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The stream choice could not be kept.", ex);
            shell.Live.Say("The server did not keep the choice");
            FillStreams();
        }
    }
}

// An episode row follows the episode's shared state, and has the viewer's menu.
public sealed partial class EpisodeRowViewModel : ObservableObject, IViewerItem
{
    private readonly ItemState _state = shell.Viewer?.For(episode) ?? ItemState.Detached(episode);
    private bool _listening;

    public ShellViewModel Shell => shell;

    public MetadataItem Item => Episode;

    public ItemState State
    {
        get
        {
            if (!_listening)
            {
                _listening = true;
                _state.PropertyChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(IsWatched));
                    OnPropertyChanged(nameof(HasProgress));
                    OnPropertyChanged(nameof(Progress));
                };
            }

            return _state;
        }
    }
}

// A season tab has the viewer's menu too: a whole season watched at once.
public sealed partial class SeasonTabViewModel : IViewerItem
{
    private readonly ItemState _state = page.Shell.Viewer?.For(season) ?? ItemState.Detached(season);

    public ShellViewModel Shell => page.Shell;

    public MetadataItem Item => Season;

    public ItemState State => _state;
}
