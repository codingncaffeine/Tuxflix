using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>The top-level sections, as the tabs in the title bar name them.</summary>
public enum TopTab
{
    Library,
    Discover,
    Activity,
}

/// <summary>
/// A page the router can show. Loading runs when the page is shown and is cancelled when it is
/// left, and every failure lands in <see cref="ErrorMessage"/> and the log, never nowhere.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    private CancellationTokenSource? _loading;

    public abstract string Title { get; }

    public virtual TopTab Tab => TopTab.Library;

    /// <summary>Whether the library rail stands beside this page, as it does in Steam's library.</summary>
    public virtual bool ShowsRail => Tab == TopTab.Library;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public async Task ActivateAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await LoadAsync(loading.Token);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            // Left before it finished.
        }
        catch (PlexUnauthorizedException ex)
        {
            ErrorMessage = "The server no longer accepts this sign-in. Sign in again from the account menu.";
            Log.Warn($"{Title}: {ex.Message}");
        }
        catch (Exception ex)
        {
            ErrorMessage = "This page could not be loaded. The log has the details.";
            Log.Warn($"{Title} could not be loaded.", ex);
        }
        finally
        {
            if (ReferenceEquals(loading, _loading)) IsLoading = false;
        }
    }

    public void Deactivate() => _loading?.Cancel();

    protected virtual Task LoadAsync(CancellationToken cancellation) => Task.CompletedTask;
}
