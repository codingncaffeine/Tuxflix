using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Music;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>A line of lyrics on the page: lit while it is sung, dimmed once it has been.</summary>
public sealed partial class LyricLineViewModel(NowPlayingPageViewModel page, int index, LyricSheetLine line) : ObservableObject
{
    public int Index { get; } = index;

    public string Text { get; } = line.Text;

    /// <summary>A gap between verses: a short blank, no words.</summary>
    public bool IsGap => line.Text.Length == 0;

    public bool IsTimed => line.Start is not null;

    public string? SeekTip => line.Start is { } start ? $"Play from {Format.Clock(start.TotalSeconds)}" : null;

    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }

    [ObservableProperty]
    public partial bool IsPast { get; internal set; }

    [RelayCommand]
    private void Seek()
    {
        if (line.Start is { } start) page.Music.SeekTo(start.TotalSeconds);
    }
}

/// <summary>A choice in a menu of named options: a visualizer mode, a palette, a station.</summary>
public sealed partial class MenuChoice(string title, Action choose, bool isChecked = false) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    [RelayCommand]
    private void Choose() => choose();
}

// The Now Playing page's music extras: the full-window visualizer, lyrics beside the cover, the
// track's shape in the seek bar, and radio from what is playing.
public sealed partial class NowPlayingPageViewModel
{
    private const int LevelReadings = 256;

    private CancellationTokenSource? _extras;
    private string? _extrasFor;
    private int? _stationsFor;
    private IReadOnlyList<MetadataItem> _stations = [];
    private MetadataItem? _full;
    private LyricSheet? _sheet;
    private DispatcherTimer? _lyricClock;

    // ===== The visualizer =====

    /// <summary>The visualizer fills the window: no title bar, no queue, the controls over it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCoverView))]
    public partial bool IsVisualizerOn { get; private set; }

    public bool IsCoverView => !IsVisualizerOn;

    public override bool IsImmersive => IsVisualizerOn;

    [ObservableProperty]
    public partial VisualizerMode VisualizerMode { get; private set; } = VisualizerMode.Spectrum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Palette))]
    public partial string PaletteName { get; private set; } = VisualizerPalettes.AlbumName;

    /// <summary>The colours the visualizer wears now: Album follows the cover playing.</summary>
    public VisualizerPalette Palette => VisualizerPalettes.Resolve(PaletteName, Colors);

    public string ModeTip => $"Mode: {VisualizerMode} (click the picture for the next)";

    public string PaletteTip => $"Colours: {PaletteName}";

    public ObservableCollection<MenuChoice> ModeChoices { get; } = [];

    public ObservableCollection<MenuChoice> PaletteChoices { get; } = [];

    [RelayCommand]
    private void ToggleVisualizer() => SetVisualizer(!IsVisualizerOn);

    /// <summary>Clicking the picture moves to the next mode, as clicking Winamp's analyser did.</summary>
    [RelayCommand]
    private void NextVisualizerMode() => ChooseMode((VisualizerMode)(((int)VisualizerMode + 1) % Enum.GetValues<VisualizerMode>().Length));

    public void SetVisualizer(bool on)
    {
        if (on == IsVisualizerOn) return;
        IsVisualizerOn = on;
        _shell.ImmersionChanged();
    }

    public void ChooseMode(VisualizerMode mode)
    {
        VisualizerMode = mode;
        _shell.Settings.Music.VisualizerMode = mode.ToString();
        _shell.SaveSettings();
        foreach (var choice in ModeChoices) choice.IsChecked = choice.Title == mode.ToString();
        OnPropertyChanged(nameof(ModeTip));
    }

    public void ChoosePalette(string name)
    {
        PaletteName = VisualizerPalettes.Names.Contains(name) ? name : VisualizerPalettes.AlbumName;
        _shell.Settings.Music.VisualizerPalette = PaletteName;
        _shell.SaveSettings();
        foreach (var choice in PaletteChoices) choice.IsChecked = choice.Title == PaletteName;
        OnPropertyChanged(nameof(PaletteTip));
    }

    partial void OnColorsChanged(UltraBlurColors? value) => OnPropertyChanged(nameof(Palette));

    // ===== Lyrics =====

    public ObservableCollection<LyricLineViewModel> LyricLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLyricsPanel), nameof(IsQueuePanel))]
    public partial bool HasLyrics { get; private set; }

    [ObservableProperty]
    public partial bool IsLyricsTimed { get; private set; }

    [ObservableProperty]
    public partial string? LyricsCredit { get; private set; }

    /// <summary>The line sung now, -1 before the first and in untimed lyrics.</summary>
    [ObservableProperty]
    public partial int CurrentLyric { get; private set; } = -1;

    /// <summary>The listener chose the lyrics over the queue (kept between runs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLyricsPanel), nameof(IsQueuePanel))]
    public partial bool PrefersLyrics { get; private set; }

    public bool IsLyricsPanel => PrefersLyrics && HasLyrics;

    public bool IsQueuePanel => !IsLyricsPanel;

    [RelayCommand]
    private void ShowLyricsPanel() => SetPrefersLyrics(true);

    [RelayCommand]
    private void ShowQueuePanel() => SetPrefersLyrics(false);

    private void SetPrefersLyrics(bool lyrics)
    {
        PrefersLyrics = lyrics;
        _shell.Settings.Music.ShowLyrics = lyrics;
        _shell.SaveSettings();
        UpdateLyricClock();
    }

    // ===== The seek bar's shape =====

    /// <summary>The track's loudness over its length, dB; null until read, or when the server has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaveform), nameof(HasPlainSeek))]
    public partial IReadOnlyList<double>? Levels { get; private set; }

    public bool HasWaveform => Levels is { Count: > 1 };

    public bool HasPlainSeek => !HasWaveform;

    // ===== Radio =====

    public ObservableCollection<MenuChoice> RadioChoices { get; } = [];

    public bool HasRadio => RadioChoices.Count > 0;

    private void InitialiseExtras()
    {
        var music = _shell.Settings.Music;
        VisualizerMode = Enum.TryParse<VisualizerMode>(music.VisualizerMode, out var mode) ? mode : VisualizerMode.Spectrum;
        PaletteName = VisualizerPalettes.Names.Contains(music.VisualizerPalette) ? music.VisualizerPalette : VisualizerPalettes.AlbumName;
        PrefersLyrics = music.ShowLyrics;
        foreach (var each in Enum.GetValues<VisualizerMode>())
        {
            ModeChoices.Add(new MenuChoice(each.ToString(), () => ChooseMode(each), each == VisualizerMode));
        }

        foreach (var name in VisualizerPalettes.Names)
        {
            PaletteChoices.Add(new MenuChoice(name, () => ChoosePalette(name), name == PaletteName));
        }
    }

    private void StopExtras()
    {
        _extras?.Cancel();
        _lyricClock?.Stop();
        _lyricClock = null;
        if (IsVisualizerOn) SetVisualizer(false);
    }

    /// <summary>A new track: its lyrics, its shape and its radio, read on workers.</summary>
    private async void LoadTrackExtras()
    {
        var track = Music.Current?.Track;
        if (track?.RatingKey == _extrasFor) return;
        _extrasFor = track?.RatingKey;
        _extras?.Cancel();
        var extras = _extras = new CancellationTokenSource();
        var token = extras.Token;
        SetLyrics(null);
        Levels = null;
        _full = null;
        if (track is null)
        {
            RebuildRadio();
            return;
        }

        try
        {
            var client = _session.Client;
            var full = track.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Stream is { Count: > 0 }
                ? track
                : await Task.Run(() => client.GetMetadataAsync(track.RatingKey, token), token) ?? track;
            if (token.IsCancellationRequested) return;
            _full = full;
            var part = full.Media?.FirstOrDefault()?.Part?.FirstOrDefault();
            var audio = part?.Stream?.FirstOrDefault(s => s.StreamType == StreamChoice.Audio);
            var lyric = part?.Stream?.Where(s => s.IsLyrics).OrderByDescending(s => s.Timed).FirstOrDefault();
            var levels = audio is null ? Task.FromResult<IReadOnlyList<double>>([]) : Task.Run(() => client.GetLoudnessLevelsAsync(audio.Id, LevelReadings, token), token);
            var sheet = lyric is null ? Task.FromResult<LyricSheet?>(null) : Task.Run(() => client.GetLyricsAsync(lyric, token), token);
            var section = full.LibrarySectionId;
            var stations = section is { } id && id != _stationsFor
                ? Task.Run(() => client.GetStationsAsync(id, token), token)
                : Task.FromResult(_stations);

            SetLyrics(await sheet);
            var read = await levels;
            if (token.IsCancellationRequested) return;
            Levels = read.Count > 1 ? read : null;
            _stations = await stations;
            _stationsFor = section;
            RebuildRadio();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or PlexUnauthorizedException or System.Text.Json.JsonException)
        {
            Log.Debug($"Now playing: the track's extras could not all be read ({ex.Message}).");
            RebuildRadio();
        }
    }

    private void SetLyrics(LyricSheet? sheet)
    {
        _sheet = sheet;
        LyricLines.Clear();
        CurrentLyric = -1;
        if (sheet is not null)
        {
            for (var i = 0; i < sheet.Lines.Count; i++) LyricLines.Add(new LyricLineViewModel(this, i, sheet.Lines[i]));
        }

        HasLyrics = sheet is not null;
        IsLyricsTimed = sheet?.IsTimed == true;
        LyricsCredit = sheet?.Credit;
        UpdateLyricClock();
    }

    // Timed lyrics being shown follow the music ten times a second; nothing runs otherwise.
    private void UpdateLyricClock()
    {
        var wanted = IsLyricsPanel && IsLyricsTimed && !IsVisualizerOn;
        if (wanted && _lyricClock is null)
        {
            _lyricClock = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FollowLyrics());
            _lyricClock.Start();
            FollowLyrics();
        }
        else if (!wanted && _lyricClock is not null)
        {
            _lyricClock.Stop();
            _lyricClock = null;
        }
    }

    partial void OnIsVisualizerOnChanged(bool value) => UpdateLyricClock();

    /// <summary>Lights the line sung at the music's position now.</summary>
    internal void FollowLyrics()
    {
        if (_sheet is null) return;
        var now = _sheet.LineAt(TimeSpan.FromSeconds(Music.Clock.Now));
        if (now == CurrentLyric) return;
        CurrentLyric = now;
        foreach (var line in LyricLines)
        {
            line.IsCurrent = line.Index == now;
            line.IsPast = line.Index < now;
        }
    }

    private void RebuildRadio()
    {
        RadioChoices.Clear();
        var track = _full ?? Music.Current?.Track;
        if (track is { MusicAnalysisVersion: > 0, Type: "track" })
        {
            RadioChoices.Add(new MenuChoice($"Radio from {track.Title}", () => _ = Music.PlayTrackRadioAsync(track)));
        }

        if (track?.GrandparentRatingKey is { } artistKey)
        {
            var artist = track.GrandparentTitle ?? "the artist";
            RadioChoices.Add(new MenuChoice($"{artist} Radio", () => _ = PlayArtistRadioAsync(artistKey)));
        }

        foreach (var station in _stations)
        {
            RadioChoices.Add(new MenuChoice(station.Title, () => _ = Music.PlayStationAsync(station)));
        }

        OnPropertyChanged(nameof(HasRadio));
    }

    private async Task PlayArtistRadioAsync(string artistKey)
    {
        try
        {
            var stations = await Task.Run(() => _session.Client.GetArtistStationsAsync(artistKey, CancellationToken.None));
            if (stations.FirstOrDefault() is { } station) await Music.PlayStationAsync(station);
            else Log.Info("Now playing: the server offers no station for this artist.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("Now playing: the artist's station could not be read.", ex);
        }
    }
}
