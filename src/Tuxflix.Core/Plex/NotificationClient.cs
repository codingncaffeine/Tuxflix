using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core.Plex;

/// <summary>Where live notifications come from: a server's socket, or a stand-in in the demo and under test.</summary>
public interface INotificationSource : IDisposable
{
    /// <summary>A message arrived. Raised on the source's own thread, never on the UI's.</summary>
    event Action<NotificationContainer>? Received;

    /// <summary>The source connected (true) or lost its connection (false).</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>Starts listening; a source that is started twice listens once.</summary>
    void Start();
}

/// <summary>
/// Listens to a server's notification socket on a thread of its own, reconnecting with a growing
/// pause when the connection drops, until it is disposed.
/// </summary>
/// <remarks>
/// The token travels in the <c>X-Plex-Token</c> header of the upgrade request, never in the
/// socket's address. The thread blocks on the socket, so nothing it waits for is anybody else's:
/// messages are parsed there and handed on through <see cref="Received"/>. Transport pings every
/// 30 seconds turn a peer that vanished (a sleeping laptop, a dropped Wi-Fi) into a closed socket.
/// A refused sign-in stops it: the same token would be refused again.
/// </remarks>
public sealed class PlexNotificationClient : INotificationSource
{
    /// <summary>A message longer than this is skipped rather than held: no notification comes near it.</summary>
    private const int MaxMessage = 4 * 1024 * 1024;

    private readonly Uri _uri;
    private readonly string? _token;
    private readonly PlexClientIdentity _identity;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private volatile ClientWebSocket? _socket;

    public PlexNotificationClient(Uri serverUri, string? token, PlexClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentNullException.ThrowIfNull(identity);
        _uri = SocketUri(serverUri);
        _token = token;
        _identity = identity;
    }

    public event Action<NotificationContainer>? Received;

    public event Action<bool>? ConnectionChanged;

    /// <summary>The socket's address for a server: its own scheme made a socket's, its own path prefix kept, no token.</summary>
    public static Uri SocketUri(Uri serverUri)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        var builder = new UriBuilder(serverUri)
        {
            Scheme = serverUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = serverUri.AbsolutePath.TrimEnd('/') + "/:/websockets/notifications",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>
    /// How long to wait before the <paramref name="attempt"/>th reconnection: 1, 2, 4, 8… seconds,
    /// never more than a minute, each stretched or shrunk by up to a fifth (<paramref name="jitter"/>,
    /// from -1 to 1) so clients that lost the same server do not all knock at once.
    /// </summary>
    public static TimeSpan ReconnectDelay(int attempt, double jitter = 0)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 16)));
        return TimeSpan.FromSeconds(seconds * (1 + (0.2 * Math.Clamp(jitter, -1, 1))));
    }

    public void Start()
    {
        if (_thread is not null || _stop.IsCancellationRequested) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Plex notifications" };
        _thread.Start();
    }

    /// <summary>Stops listening without waiting: the socket is torn down and the thread ends on its own.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _socket?.Abort();
        if (_thread is null) _stop.Dispose();
    }

    private void Run()
    {
        var attempt = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Listen())
                {
                    attempt = 0;
                }

                if (_stop.IsCancellationRequested) return;
                attempt++;
                var delay = ReconnectDelay(attempt, (Random.Shared.NextDouble() * 2) - 1);
                Log.Debug(string.Create(CultureInfo.InvariantCulture, $"Server notifications: reconnecting in {delay.TotalSeconds:0.0} s."));
                if (_stop.Token.WaitHandle.WaitOne(delay)) return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn("Server notifications stopped: the server refused the sign-in.");
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>One connection, until it closes; true when it connected at all.</summary>
    private bool Listen()
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.CollectHttpResponseDetails = true;
        if (_token is not null) socket.Options.SetRequestHeader("X-Plex-Token", _token);
        socket.Options.SetRequestHeader("X-Plex-Client-Identifier", _identity.ClientIdentifier);
        socket.Options.SetRequestHeader("X-Plex-Product", PlexClientIdentity.Product);
        socket.Options.SetRequestHeader("X-Plex-Version", _identity.Version);
        socket.Options.SetRequestHeader("X-Plex-Device-Name", _identity.DeviceName);
        _socket = socket;

        var connected = false;
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connecting.CancelAfter(TimeSpan.FromSeconds(15));
                socket.ConnectAsync(_uri, connecting.Token).GetAwaiter().GetResult();
            }

            connected = true;
            Log.Info("Server notifications connected.");
            Raise(ConnectionChanged, true);
            Receive(socket);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("The notification socket refused the sign-in.");
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            if (!_stop.IsCancellationRequested) Log.Debug($"Server notifications: {(connected ? "the connection dropped" : "no connection")} ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            _socket = null;
            if (connected) Raise(ConnectionChanged, false);
        }

        return connected;
    }

    private void Receive(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        var skipping = false;
        while (!_stop.IsCancellationRequested)
        {
            var result = socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).AsTask().GetAwaiter().GetResult();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answer the server's goodbye, so it closes cleanly rather than seeing the line cut.
                using var closing = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                closing.CancelAfter(TimeSpan.FromSeconds(2));
                socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).GetAwaiter().GetResult();
                return;
            }

            if (!skipping) message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessage)
            {
                skipping = true;
                message.SetLength(0);
            }

            if (!result.EndOfMessage) continue;
            if (!skipping && result.MessageType == WebSocketMessageType.Text
                && NotificationContainer.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) is { } container)
            {
                Raise(Received, container);
            }

            skipping = false;
            message.SetLength(0);
        }
    }

    // A listener that throws must not end the listening.
    private static void Raise<T>(Action<T>? handler, T value)
    {
        try
        {
            handler?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Warn("A server notification could not be handled.", ex);
        }
    }
}
