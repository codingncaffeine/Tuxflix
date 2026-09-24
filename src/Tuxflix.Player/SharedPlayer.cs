namespace Tuxflix.Player;

/// <summary>
/// A player held by more than one owner, destroyed when the last one lets go.
/// </summary>
/// <remarks>
/// libmpv requires every render context to be freed before its core is destroyed, and
/// <c>mpv_terminate_destroy</c> blocks until that happens. The page that plays, the video thread
/// that draws and any worker reading the player let go at different moments. Counting owners makes
/// the order right whichever lets go first. The last <see cref="Release"/> destroys the player and
/// waits for mpv, so owners let go on a worker, never on the UI thread.
/// </remarks>
public sealed class SharedPlayer
{
    private readonly object _gate = new();
    private int _owners = 1;

    public SharedPlayer(MpvPlayer player) => Player = player;

    public MpvPlayer Player { get; }

    public bool IsDisposed { get; private set; }

    /// <summary>Adds an owner; false when the player is already gone.</summary>
    public bool Acquire()
    {
        lock (_gate)
        {
            if (IsDisposed) return false;
            _owners++;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (IsDisposed || --_owners > 0) return;
            IsDisposed = true;
        }

        Player.Dispose();
    }
}
