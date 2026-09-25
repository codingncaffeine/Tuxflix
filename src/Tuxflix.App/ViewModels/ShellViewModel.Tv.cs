using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tuxflix.App.ViewModels;

/// <summary>The window's side of the TV interface: whether it shows, and the pages only it opens.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>The 10-foot interface is showing: full screen, for a television, a remote or a controller.</summary>
    [ObservableProperty]
    public partial bool IsTv { get; set; }

    /// <summary>The controllers, when the application reads them; null under test and in probe runs.</summary>
    internal Tv.GamepadInput? Gamepads { get; set; }

    /// <summary>Into the TV interface, or back to the desktop one.</summary>
    [RelayCommand]
    private void ToggleTv() => IsTv = !IsTv;

    /// <summary>Search as a page of its own, typed on the on-screen keyboard: the TV interface's search.</summary>
    [RelayCommand]
    public void OpenSearch()
    {
        if (Session is not { } session || Router.Current is SearchPageViewModel) return;
        Router.Navigate(new SearchPageViewModel(this, session));
        SearchText = string.Empty;
    }

    /// <summary>What the controller's buttons do, and a press to change one.</summary>
    [RelayCommand]
    private void ShowController()
    {
        if (Router.Current is not Tv.TvControllerPageViewModel) Router.Navigate(new Tv.TvControllerPageViewModel(this));
    }
}
