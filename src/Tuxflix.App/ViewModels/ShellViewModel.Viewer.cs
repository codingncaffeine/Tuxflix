using Tuxflix.Core.Demo;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

// The viewer's own state on the open server, and its live news: both follow the session.
public sealed partial class ShellViewModel
{
    private LiveUpdates? _live;

    /// <summary>The open server's viewer state (watched, ratings, changes on their way); null with none open.</summary>
    public ViewerState? Viewer { get; private set; }

    /// <summary>The open server's live news: what plays elsewhere, what the server is busy with.</summary>
    public LiveUpdates Live => _live ??= new LiveUpdates(this) { Post = LivePost ?? (work => Avalonia.Threading.Dispatcher.UIThread.Post(work)) };

    /// <summary>Hands live news to the UI thread; replaced under test, where no dispatcher runs.</summary>
    public Action<Action>? LivePost { get; init; }

    /// <summary>Makes the notification source for a server; null for the default (the demo's stand-in, or the server's socket).</summary>
    public Func<ServerSession, INotificationSource?>? NotificationSources { get; init; }

    private void ViewerFollowsSession(ServerSession? oldValue, ServerSession? newValue)
    {
        Viewer = newValue is null ? null : new ViewerState(newValue);
        OnPropertyChanged(nameof(Viewer));
        _ = Viewer?.LoadPlaylistsAsync();
        Live.Attach(newValue, newValue is null ? null : NotificationSources is { } make ? make(newValue) : DefaultNotifications(newValue));
    }

    private INotificationSource DefaultNotifications(ServerSession session) =>
        session.Demo is { } demo
            ? new DemoNotificationSource(demo)
            : new PlexNotificationClient(session.Client.BaseUri, session.Client.Token, Identity);
}
