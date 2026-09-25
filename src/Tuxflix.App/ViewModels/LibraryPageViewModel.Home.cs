using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// "Play something": for the evening nothing in particular calls.
public sealed partial class LibraryPageViewModel
{
    /// <summary>A library of films or series can pick something unwatched to play.</summary>
    public bool CanPlaySomething => Section.Type is "movie" or "show";

    public string PlaySomethingTip => Section.Type == "show"
        ? "Play the next episode of a series you have not finished, picked at random"
        : "Play a film you have not seen, picked at random";

    /// <summary>What a press of Play something did, when it could not play.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPickNotice))]
    public partial string? PickNotice { get; private set; }

    public bool HasPickNotice => PickNotice is not null;

    [RelayCommand]
    private async Task PlaySomethingAsync()
    {
        try
        {
            var pick = await Task.Run(() => _session.Client.PickSomethingAsync(Section, CancellationToken.None));
            if (pick is null)
            {
                await SayPickAsync($"Everything in {Section.Title} has been watched.");
                return;
            }

            _shell.Play(pick, resume: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or PlexUnauthorizedException)
        {
            Log.Warn("Something to play could not be picked.", ex);
            await SayPickAsync("The server could not be asked for something to play.");
        }
    }

    private async Task SayPickAsync(string notice)
    {
        PickNotice = notice;
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (PickNotice == notice) PickNotice = null;
    }
}
