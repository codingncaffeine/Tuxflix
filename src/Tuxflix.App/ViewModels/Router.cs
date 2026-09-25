using CommunityToolkit.Mvvm.ComponentModel;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Back and forward through pages, the way Steam's arrows and the mouse's side buttons expect.
/// </summary>
public sealed partial class Router : ObservableObject
{
    private const int Depth = 50;

    private readonly List<PageViewModel> _back = [];
    private readonly List<PageViewModel> _forward = [];

    [ObservableProperty]
    public partial PageViewModel? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public void Navigate(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Current is { } current)
        {
            current.Deactivate();
            _back.Add(current);
            if (_back.Count > Depth) _back.RemoveAt(0);
        }

        _forward.Clear();
        Show(page);
    }

    /// <summary>Puts <paramref name="page"/> in the current one's place: back still returns to where the viewer came from (the next episode takes over the player).</summary>
    public void Replace(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Current?.Deactivate();
        _forward.Clear();
        Show(page);
    }

    /// <summary>Starts over at <paramref name="page"/>, forgetting the history: a new server, a new sign-in.</summary>
    public void Reset(PageViewModel page)
    {
        Current?.Deactivate();
        _back.Clear();
        _forward.Clear();
        Show(page);
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var page = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (Current is { } current)
        {
            current.Deactivate();
            _forward.Add(current);
        }

        Show(page);
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var page = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (Current is { } current)
        {
            current.Deactivate();
            _back.Add(current);
        }

        Show(page);
    }

    private void Show(PageViewModel page)
    {
        Current = page;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        // ActivateAsync reports its own failures, so nothing it throws goes unobserved.
        _ = page.ActivateAsync();
    }
}
