using Tmds.DBus.Protocol;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// Keeps the screen awake while something plays, through the desktop's
/// <c>org.freedesktop.ScreenSaver</c> service, as browsers do.
/// </summary>
/// <remarks>
/// A player drawing into a window of its own asks the compositor; this one draws inside Tuxflix's
/// window, so libmpv cannot, and the application asks the desktop. On Plasma 6 the service is
/// KWin's, which hands the request to PowerDevil as a "change screen settings" inhibition about
/// five seconds later (PowerDevil waits so that brief toggles do not flicker its policy), and
/// PowerDevil is what dims and switches the screen off. Desktops drop an inhibition when the
/// connection that asked for it closes, so the connection is held while Tuxflix runs, and a crash
/// can never leave the screen awake.
/// </remarks>
internal sealed class ScreenSaverInhibitor : IDisposable
{
    private const string Service = "org.freedesktop.ScreenSaver";
    private const string ObjectPath = "/org/freedesktop/ScreenSaver";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private uint? _cookie;

    public static ScreenSaverInhibitor Shared { get; } = new();

    public async Task InhibitAsync(string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cookie is not null || await ConnectAsync().ConfigureAwait(false) is not { } connection) return;
            var request = Build(connection, "Inhibit", "ss", (ref MessageWriter w) =>
            {
                w.WriteString("Tuxflix");
                w.WriteString(reason);
            });
            _cookie = await connection.CallMethodAsync(request, static (Message message, object? _) => message.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
            Log.Info("The screen stays awake while something plays.");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException)
        {
            Log.Warn($"The desktop would not keep the screen awake: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cookie is not { } cookie || _connection is not { } connection) return;
            _cookie = null;
            await connection.CallMethodAsync(Build(connection, "UnInhibit", "u", (ref MessageWriter w) => w.WriteUInt32(cookie))).ConfigureAwait(false);
            Log.Info("The screen may sleep again.");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException)
        {
            Log.Warn($"The screensaver could not be given back: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Closing the connection is itself the release, whatever state the cookie is in.
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    private async Task<DBusConnection?> ConnectAsync()
    {
        if (_connection is not null) return _connection;
        if (DBusAddress.Session is not { } address) return null;
        var connection = new DBusConnection(address);
        await connection.ConnectAsync().ConfigureAwait(false);
        return _connection = connection;
    }

    private static MessageBuffer Build(DBusConnection connection, string member, string signature, WriteArguments arguments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: member, signature: signature);
            arguments(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}

/// <summary>
/// Writes a call's arguments. The writer is a struct: it goes by reference, or the arguments land
/// in a copy and the bus receives a header that promises a body it never gets, which dbus-daemon
/// answers by closing the connection.
/// </summary>
internal delegate void WriteArguments(ref MessageWriter writer);
