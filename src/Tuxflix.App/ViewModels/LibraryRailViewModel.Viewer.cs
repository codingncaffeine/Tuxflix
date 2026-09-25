using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// A rail row follows the item's shared state, and has the viewer's menu: marked watched on its
// page, the rail's percentage and dimmed title follow at once.
public sealed partial class RailItemRow : IViewerItem
{
    private readonly ItemState _state = rail.Shell.Viewer?.For(item) ?? ItemState.Detached(item);
    private bool _listening;

    public ShellViewModel Shell => rail.Shell;

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
                    OnPropertyChanged(nameof(Trailing));
                    OnPropertyChanged(nameof(IsInProgress));
                    OnPropertyChanged(nameof(HasTrailing));
                };
            }

            return _state;
        }
    }
}
