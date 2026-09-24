namespace Tuxflix.Player;

/// <summary>
/// A player held by more than one owner, destroyed when the last one lets go.
/// </summary>
/// <remarks>
/// libmpv requires every render context to be freed before its core is destroyed, and
/// <c>mpv_terminate_destroy</c> blocks until that happens. The page that plays and the view that
/// draws let go at different moments: leaving a page happens before its view is taken off the
/// screen. Counting owners makes the order right whichever lets go first, instead of a UI thread
/// waiting on a render context only that same thread would free.
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
