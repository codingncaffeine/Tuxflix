using Tmds.DBus.Protocol;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// Keeps the computer from sleeping while music plays, through the desktop portal: the screen may
/// still blank and lock, as it should when nobody watches it, but the music does not stop.
/// </summary>
/// <remarks>
/// <c>org.freedesktop.portal.Inhibit.Inhibit</c> with the suspend flag hands back a request; closing
/// the request (or the connection) ends it. The portal works the same inside a sandbox.
/// </remarks>
internal sealed class SuspendInhibitor : IDisposable
{
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const uint Suspend = 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
    private DBusConnection? _connection;
    private string? _request;

    public static SuspendInhibitor Shared { get; } = new();

    /// <summary>Holds sleep off for <paramref name="owner"/> (music, a film); it stays held while any owner holds it.</summary>
    public async Task HoldAsync(string owner, string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _owners.Add(owner);
            if (_request is not null) return;
            if (DBusAddress.Session is not { } address) return;
            _connection ??= new DBusConnection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            var call = Build(_connection, PortalPath, "org.freedesktop.portal.Inhibit", "Inhibit", "sua{sv}", (ref MessageWriter w) =>
            {
                w.WriteString(string.Empty);
                w.WriteUInt32(Suspend);
                var options = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("reason");
                w.WriteVariantString(reason);
                w.WriteDictionaryEnd(options);
            });
            _request = await _connection.CallMethodAsync(call, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
            Log.Info("The computer stays awake while music plays.");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException)
        {
            Log.Warn($"The desktop would not keep the computer awake: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(string owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _owners.Remove(owner);
            if (_owners.Count > 0 || _request is not { } request || _connection is not { } connection) return;
            _request = null;
            await connection.CallMethodAsync(Build(connection, request, "org.freedesktop.portal.Request", "Close", null, static (ref MessageWriter _) => { })).ConfigureAwait(false);
            Log.Info("The computer may sleep again.");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException)
        {
            Log.Warn($"The sleep inhibition could not be given back: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Closing the connection is itself the release.
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    private static MessageBuffer Build(DBusConnection connection, string path, string @interface, string member, string? signature, WriteArguments arguments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Portal, path: path, @interface: @interface, member: member, signature: signature);
            arguments(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
