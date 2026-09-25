using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Settings;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// The browsing options, for the settings page: theme music, and the open server's home shelves
/// as the viewer arranged them, with a way back to the server's own order.
/// </summary>
public sealed partial class BrowseSettingsViewModel(ShellViewModel shell) : ObservableObject
{
    /// <summary>A series' or a film's theme plays quietly while its page is open.</summary>
    public bool ThemeMusic
    {
        get => shell.Settings.Browse.ThemeMusic;
        set
        {
            if (value == shell.Settings.Browse.ThemeMusic) return;
            shell.Settings.Browse.ThemeMusic = value;
            shell.SaveSettings();
            if (!value) shell.Themes.Stop();
            OnPropertyChanged();
        }
    }

    /// <summary>The tiles' size in library grids, the same as the slider beside a library's sort.</summary>
    public double GridScale
    {
        get => shell.GridScale;
        set
        {
            shell.GridScale = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GridScaleText));
        }
    }

    /// <summary>"Usual size", "125 %".</summary>
    public string GridScaleText => shell.GridScale == 1 ? "Usual size" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{shell.GridScale * 100:0} %");

    public bool HasServer => shell.Session is not null;

    public string HomeHeading => shell.Session is { } session ? $"Home shelves on {session.Name}" : "Home shelves";

    /// <summary>How the open server's home stands: as the server orders it, or as the viewer left it.</summary>
    public string HomeSummary
    {
        get
        {
            if (Layout is not { } layout || !IsArranged) return "In the order the server gives them, none hidden.";
            var hidden = layout.Hidden.Count switch
            {
                0 => "none hidden",
                1 => "one hidden",
                var count => $"{count} hidden",
            };
            return $"Arranged by you, {hidden}.";
        }
    }

    public bool IsArranged => Layout is { } layout && (layout.Order.Count > 0 || layout.Hidden.Count > 0);

    private ShelfLayout? Layout => shell.Session is { } session && shell.Settings.Browse.Home.TryGetValue(ShellViewModel.HomeLayoutKey(session), out var layout) ? layout : null;

    /// <summary>Puts the open server's home back as the server orders it, every shelf shown.</summary>
    [RelayCommand(CanExecute = nameof(IsArranged))]
    private void RestoreHome()
    {
        if (Layout is not { } layout) return;
        layout.Order.Clear();
        layout.Hidden.Clear();
        shell.SaveSettings();
        OnPropertyChanged(nameof(IsArranged));
        OnPropertyChanged(nameof(HomeSummary));
        RestoreHomeCommand.NotifyCanExecuteChanged();
    }
}
