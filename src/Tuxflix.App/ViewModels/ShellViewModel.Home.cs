using Tuxflix.App.Music;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// What the home screen, the Watchlist and the item pages share: Plex's catalogue for the
// Watchlist, and the theme music player.
public sealed partial class ShellViewModel
{
    private WatchlistService? _watchlist;

    /// <summary>The signed-in account's side of Plex's catalogue; null when signed out.</summary>
    private PlexDiscoverClient? AccountDiscover { get; set; }

    /// <summary>
    /// Where the Watchlist lives: the demo's own catalogue while the demo is open, else the
    /// signed-in account's; null when there is neither.
    /// </summary>
    public PlexDiscoverClient? Discover => Session?.Discover ?? AccountDiscover;

    /// <summary>The Watchlist as the open server sees it; null without a catalogue or a server.</summary>
    public WatchlistService? Watchlist
    {
        get
        {
            if (Session is not { } session || Discover is not { } discover) return null;
            if (_watchlist is null || _watchlist.Session != session || _watchlist.Discover != discover) _watchlist = new WatchlistService(session, discover);
            return _watchlist;
        }
    }

    /// <summary>Plays theme music behind item pages; replaced under test with one that only records.</summary>
    public IThemeMusic Themes { get; init; } = new ThemeMusic();

    /// <summary>The name a server's home layout is kept under: its machine identifier.</summary>
    internal static string HomeLayoutKey(ServerSession session) => session.MachineIdentifier ?? session.Name;
}
