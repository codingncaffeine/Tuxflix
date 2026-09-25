using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Tuxflix.App.Tv;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Views;

/// <summary>The window's TV interface: it takes the whole screen in place of the desktop frame.</summary>
public partial class MainWindow
{
    private TvShell? _tvShell;
    private Control? _desktop;
    private WindowState _stateBeforeTv = WindowState.Normal;

    /// <summary>Where controller and remote actions go, in either interface.</summary>
    internal TvController? Tv { get; private set; }

    /// <summary>The TV interface is showing.</summary>
    public bool IsTvShowing => _tvShell is not null && ReferenceEquals(Content, _tvShell);

    /// <summary>The TV frame, while it shows; for tests and captures.</summary>
    internal TvShell? TvFrame => IsTvShowing ? _tvShell : null;

    private void AttachTv(ShellViewModel shell)
    {
        // In the TV interface actions go through its frame; on the desktop, to the page (the
        // player takes a controller there too) and otherwise move focus around the window.
        Tv = new TvController(
            this,
            shell,
            () => IsTvShowing ? _tvShell!.ActiveScope : Content as Control,
            () => IsTvShowing ? _tvShell : PageTarget(shell));
        shell.PropertyChanged += OnShellTvChanged;
        if (shell.IsTv) ShowTv(true);
    }

    private void OnShellTvChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsTv) && sender is ShellViewModel shell) ShowTv(shell.IsTv);
    }

    private ITvInputTarget? PageTarget(ShellViewModel shell) =>
        (Content as Visual)?.GetVisualDescendants().OfType<ITvInputTarget>()
            .FirstOrDefault(view => view is Control control && ReferenceEquals(control.DataContext, shell.Router.Current));

    /// <summary>Swaps the frame the window shows, and goes full screen for the TV (back to how it was for the desktop).</summary>
    private void ShowTv(bool on)
    {
        if (_shell is null || Tv is null || on == IsTvShowing) return;
        if (on)
        {
            _desktop ??= Content as Control;
            _tvShell ??= new TvShell(new TvShellViewModel(_shell), Tv);
            _stateBeforeTv = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            Content = _tvShell;
            WindowState = WindowState.FullScreen;
            Log.Info("The TV interface is showing.");
        }
        else
        {
            Content = _desktop;
            WindowState = _stateBeforeTv;
            Log.Info("The desktop interface is showing.");
        }
    }
}
