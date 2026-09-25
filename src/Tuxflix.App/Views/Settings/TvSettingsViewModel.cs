using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.Tv;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Settings;

/// <summary>
/// The TV interface's settings, for the settings page: start in it, open it now, and the
/// controller (which is connected, and its buttons, changed in the TV interface where a
/// controller is at hand).
/// </summary>
public sealed partial class TvSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;

    public TvSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ControllerStatus = TvControllerPageViewModel.DescribePads(shell.Gamepads);
        if (shell.Gamepads is { } pads) pads.PadsChanged += OnPadsChanged;
    }

    /// <summary>The next start opens in the TV interface, full screen (as <c>--tv</c> does for one run).</summary>
    public bool StartsInTv
    {
        get => _shell.Settings.Tv.StartInTv;
        set
        {
            if (_shell.Settings.Tv.StartInTv == value) return;
            _shell.Settings.Tv.StartInTv = value;
            _shell.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Which controllers are connected, or why none can be.</summary>
    [ObservableProperty]
    public partial string ControllerStatus { get; private set; }

    public void Dispose()
    {
        if (_shell.Gamepads is { } pads) pads.PadsChanged -= OnPadsChanged;
    }

    [RelayCommand]
    private void OpenTv() => _shell.IsTv = true;

    /// <summary>The map editor, in the TV interface: it is changed with a controller in hand.</summary>
    [RelayCommand]
    private void EditButtons()
    {
        _shell.IsTv = true;
        _shell.ShowControllerCommand.Execute(null);
    }

    private void OnPadsChanged(IReadOnlyList<string> pads) =>
        Dispatcher.UIThread.Post(() => ControllerStatus = TvControllerPageViewModel.DescribePads(_shell.Gamepads));
}
