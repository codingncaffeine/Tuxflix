using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>Something shown with the viewer's own menu: watched marks and playlists.</summary>
public interface IViewerItem
{
    ShellViewModel Shell { get; }

    MetadataItem Item { get; }

    ItemState State { get; }
}

// A tile follows the item's shared state: marked watched anywhere, it shows at once. The state is
// taken when the tile is made, so a tile scrolled out of view keeps in step too; the tile listens
// to it once something shows it.
public abstract partial class MediaTileViewModel : ObservableObject, IViewerItem
{
    private readonly ItemState _state = shell.Viewer?.For(item) ?? ItemState.Detached(item);
    private bool _listening;

    public ShellViewModel Shell => shell;

    /// <summary>The item's state as every view of it shares it.</summary>
    public ItemState State
    {
        get
        {
            if (!_listening)
            {
                _listening = true;
                _state.PropertyChanged += OnStateChanged;
            }

            return _state;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(IsWatched));
        OnPropertyChanged(nameof(UnwatchedCount));
        OnPropertyChanged(nameof(HasUnwatchedCount));
        OnPropertyChanged(nameof(UnwatchedText));
        OnPropertyChanged(nameof(IsNew));
    }
}
