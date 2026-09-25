using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Looking for subtitles online through the server's providers (OpenSubtitles), and keeping the
/// one chosen with the item.
/// </summary>
/// <remarks>
/// Only the server's owner is offered this: keeping a subtitle adds it to the item on the server,
/// for everyone who watches it, and the panel says so. The server fetches the file in the
/// background; the finder waits for it to show among the item's streams, then the player turns it on.
/// </remarks>
/// <summary>What the finder needs of the player: the subtitles the item has, and turning on the one kept.</summary>
public interface ISubtitleHost
{
    HashSet<long> SubtitleStreamIds();

    void TakeAddedSubtitle(MetadataItem item, MediaPart part, MediaStream added);
}

public sealed partial class SubtitleFinderViewModel(ISubtitleHost page, ServerSession session, string ratingKey) : ObservableObject
{
    private static readonly TimeSpan ArrivalLimit = TimeSpan.FromSeconds(30);

    /// <summary>The languages offered, as two-letter codes with their names; the desktop's own first.</summary>
    public static IReadOnlyList<SubtitleLanguage> Languages { get; } = BuildLanguages();

    [ObservableProperty]
    public partial SubtitleLanguage Language { get; set; } = Languages[0];

    public ObservableCollection<FoundSubtitle> Results { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSearch => !IsBusy;

    partial void OnLanguageChanged(SubtitleLanguage value) => _ = SearchAsync();

    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = $"Looking for {Language.Name} subtitles…";
        Results.Clear();
        try
        {
            var code = Language.Code;
            var found = await Task.Run(() => session.Client.SearchSubtitlesAsync(ratingKey, code, CancellationToken.None));
            foreach (var stream in found) Results.Add(new FoundSubtitle(this, stream));
            Status = found.Count == 0 ? $"No {Language.Name} subtitles found for this." : string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The subtitle search failed.", ex);
            Status = "The server could not search just now.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Has the server keep <paramref name="found"/> with the item, waits for it to arrive, and turns it on.</summary>
    internal async Task AddAsync(FoundSubtitle found)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "The server is fetching it…";
        try
        {
            var known = page.SubtitleStreamIds();
            await Task.Run(() => session.Client.AddSubtitleAsync(ratingKey, found.Stream, CancellationToken.None));

            // The server fetches in the background: the new stream shows in the item's part shortly.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < ArrivalLimit)
            {
                var item = await Task.Run(() => session.Client.GetMetadataAsync(ratingKey, CancellationToken.None));
                var part = item?.Media?.FirstOrDefault()?.Part?.FirstOrDefault();
                if (item is not null && part?.Stream?.FirstOrDefault(s => s is { StreamType: 3, IsExternal: true } && !known.Contains(s.Id)) is { } added)
                {
                    page.TakeAddedSubtitle(item, part, added);
                    Status = "Kept with this item and turned on.";
                    Log.Info($"A subtitle found online was kept with the item ({found.Stream.ProviderTitle}).");
                    return;
                }

                await Task.Delay(1000);
            }

            Status = "The server has not finished fetching it; it will be in the list when it has.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Warn("The subtitle could not be kept.", ex);
            Status = "The server could not keep it.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static List<SubtitleLanguage> BuildLanguages()
    {
        string[] codes = ["en", "es", "fr", "de", "it", "pt", "nl", "sv", "no", "da", "fi", "pl", "cs", "hu", "ro", "el", "tr", "ru", "uk", "ar", "he", "hi", "ja", "ko", "zh"];
        var own = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return
        [
            .. codes.Where(c => c == own).Concat(codes.Where(c => c != own))
                .Select(c => new SubtitleLanguage(c, CultureInfo.GetCultureInfo(c).EnglishName)),
        ];
    }
}

/// <summary>A language to search in: the code the server takes and the name people read.</summary>
public sealed record SubtitleLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One subtitle found online.</summary>
public sealed partial class FoundSubtitle(SubtitleFinderViewModel finder, MediaStream stream)
{
    public MediaStream Stream { get; } = stream;

    public string Title { get; } = stream.Title ?? stream.DisplayTitle ?? "Subtitles";

    public string Detail { get; } = string.Join(" · ", new[]
    {
        stream.DisplayTitle,
        stream.HearingImpaired ? "describes sounds too" : null,
        stream.ProviderTitle,
    }.Where(p => !string.IsNullOrWhiteSpace(p)));

    [RelayCommand]
    private Task Add() => finder.AddAsync(this);
}
