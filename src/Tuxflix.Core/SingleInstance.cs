using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core;

/// <summary>
/// One Tuxflix per profile: a second launch hands its arguments to the running one and exits.
/// </summary>
/// <remarks>
/// Ownership is an exclusive lock on a file, not the socket: two launches racing each other can
/// both fail to connect and then both bind, but only one of them can hold the lock. The owner
/// keeps the lock for its lifetime, removes any stale socket a crash left behind and listens;
/// everyone else connects and sends one line of JSON.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>sun_path is 108 bytes on Linux, including its terminating zero.</summary>
    public const int MaximumSocketPath = 108;

    private readonly string _socketPath;
    private readonly string _lockPath;
    private FileStream? _lock;
    private Socket? _listener;
    private CancellationTokenSource? _stop;

    public SingleInstance(string socketPath, string lockPath)
    {
        _socketPath = socketPath;
        _lockPath = lockPath;
    }

    /// <summary>Raised on a pool thread with the arguments another launch handed over.</summary>
    public event Action<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>
    /// True when another instance owns the profile and took <paramref name="arguments"/>; false
    /// when this process is now the owner and listening.
    /// </summary>
    public bool TryHandOff(IReadOnlyList<string> arguments)
    {
        // The kernel refuses a socket path of 108 bytes or more outright; run as a lone instance
        // rather than fail to start.
        if (Encoding.UTF8.GetByteCount(_socketPath) >= MaximumSocketPath)
        {
            Log.Warn($"The single-instance socket path is too long for a Unix socket ({_socketPath}); every launch opens its own window.");
            return false;
        }

        try
        {
            _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Somebody else holds the lock; they may still be starting, so give them a moment.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (TrySend(arguments)) return true;
                Thread.Sleep(100);
            }

            Log.Warn("Another Tuxflix holds the profile but did not answer; starting anyway.");
            return false;
        }

        try
        {
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(4);
            _stop = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _stop.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("The single-instance socket could not be opened; later launches will start their own window.", ex);
        }

        return false;
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener?.Dispose();
        try
        {
            if (_listener is not null && File.Exists(_socketPath)) File.Delete(_socketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next owner removes it.
        }

        _lock?.Dispose();
        _stop?.Dispose();
    }

    private bool TrySend(IReadOnlyList<string> arguments)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            var payload = JsonSerializer.Serialize(arguments.ToArray(), CoreJsonContext.Default.StringArray) + "\n";
            client.Send(Encoding.UTF8.GetBytes(payload));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptAsync(stop).ConfigureAwait(false);
                using var stream = new NetworkStream(client);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (line is null) continue;

                var arguments = JsonSerializer.Deserialize(line, CoreJsonContext.Default.StringArray) ?? [];
                ArgumentsReceived?.Invoke(arguments);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or JsonException or ObjectDisposedException)
            {
                Log.Warn("A hand-off from another launch could not be read.", ex);
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class CoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
