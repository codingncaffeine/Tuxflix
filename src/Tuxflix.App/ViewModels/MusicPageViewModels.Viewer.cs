using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// Music goes into playlists too: an album from its tile's menu, a track from its row's.
public sealed partial class AlbumTileViewModel : IViewerItem
{
    private readonly ItemState _state = shell.Viewer?.For(album) ?? ItemState.Detached(album);

    public ShellViewModel Shell => shell;

    public MetadataItem Item => Album;

    public ItemState State => _state;
}

public sealed partial class TrackRowViewModel : IViewerItem
{
    private ItemState? _state;

    public ShellViewModel Shell => _shell;

    public MetadataItem Item => Track;

    public ItemState State => _state ??= _shell.Viewer?.For(Track) ?? ItemState.Detached(Track);
}
