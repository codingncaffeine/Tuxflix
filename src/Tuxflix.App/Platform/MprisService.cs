using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Tmds.DBus.Protocol;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// MPRIS: the desktop's media controls, the lock screen and the keyboard's media keys, for whatever
/// Tuxflix is playing.
/// </summary>
/// <remarks>
/// <para>
/// The bus name is held only while something is loaded, so the desktop shows Tuxflix when there
/// is something to control and not otherwise. A second Tuxflix (another profile) takes
/// <c>org.mpris.MediaPlayer2.tuxflix.instance&lt;pid&gt;</c>, as the specification asks.
/// </para>
/// <para>
/// Nothing here runs on the UI thread or makes it wait: the bus is served on the connection's own
/// thread from the session's latest <see cref="MediaState"/>, and commands are posted to the UI
/// thread and answered at once. Property changes go out as <c>PropertiesChanged</c>; the position
/// is not signalled (the specification says so) except by <c>Seeked</c> when it jumps.
/// </para>
/// </remarks>
internal sealed class MprisService : IPathMethodHandler, IAsyncDisposable
{
    public const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string BaseName = "org.mpris.MediaPlayer2.tuxflix";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string TrackPrefix = "/io/github/codingncaffeine/Tuxflix/track/";

    private static readonly string[] RootProperties = ["CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry", "SupportedUriSchemes", "SupportedMimeTypes"];
    private static readonly string[] PlayerProperties = ["PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume", "Position", "MinimumRate", "MaximumRate", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl"];

    private readonly string? _address;
    private readonly Action _raise;
    private readonly Action _quit;
    private readonly Action<Action> _post;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMediaSession? _session;
    private volatile MediaState _state = MediaState.Nothing;
    private MediaState _sent = MediaState.Nothing;
    private DBusConnection? _connection;

    /// <summary>
    /// How long nothing must stay loaded before Tuxflix leaves the desktop's players: a queue
    /// starting or reshuffling is empty for a moment, and the desktop's widget should not blink.
    /// </summary>
    public TimeSpan LeaveAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <param name="address">The bus to serve; null for the session bus.</param>
    /// <param name="raise">Brings Tuxflix's window forward (run on the UI thread).</param>
    /// <param name="quit">Closes Tuxflix (run on the UI thread).</param>
    /// <param name="post">Runs a command where the players live; the UI thread unless a test says otherwise.</param>
    public MprisService(string? address, Action raise, Action quit, Action<Action>? post = null)
    {
        _address = address;
        _raise = raise;
        _quit = quit;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    /// <summary>The name held on the bus, while one is.</summary>
    public string? BusName { get; private set; }

    public string Path => ObjectPath;

    public bool HandlesChildPaths => false;

    /// <summary>Follows another player (a film opening over the music, say); null follows none. UI thread.</summary>
    public void Attach(IMediaSession? session)
    {
        if (ReferenceEquals(session, _session)) return;
        if (_session is not null)
        {
            _session.Changed -= OnChanged;
            _session.Seeked -= OnSeeked;
        }

        _session = session;
        if (session is not null)
        {
            session.Changed += OnChanged;
            session.Seeked += OnSeeked;
        }

        OnChanged();
    }

    public async ValueTask DisposeAsync()
    {
        Attach(null);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _connection?.Dispose();
            _connection = null;
            BusName = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnChanged()
    {
        _state = _session?.State ?? MediaState.Nothing;
        _ = Task.Run(PublishAsync);
    }

    private void OnSeeked(double seconds)
    {
        var connection = _connection;
        if (connection is null) return;
        _ = Task.Run(() =>
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(path: ObjectPath, @interface: PlayerInterface, member: "Seeked", signature: "x");
            writer.WriteInt64(Micro(seconds));
            connection.TrySendMessage(writer.CreateMessage());
        });
    }

    /// <summary>Holds the name while something is loaded, lets it go when not, and says what changed.</summary>
    private async Task PublishAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = _state;
            if (!state.HasMedia)
            {
                if (_connection is not null)
                {
                    _gate.Release();
                    try
                    {
                        await Task.Delay(LeaveAfter).ConfigureAwait(false);
                    }
                    finally
                    {
                        await _gate.WaitAsync().ConfigureAwait(false);
                    }

                    // Something came back meanwhile: stay, and let that publish say so.
                    if (_state.HasMedia) return;
                }

                if (_connection is not null)
                {
                    _connection.Dispose();
                    _connection = null;
                    Log.Info("Media controls: nothing loaded, Tuxflix leaves the desktop's players.");
                    BusName = null;
                }

                _sent = MediaState.Nothing;
                return;
            }

            if (_connection is null && !await ConnectAsync().ConfigureAwait(false)) return;

            var changed = PlayerProperties.Where(p => p != "Position" && !Same(p, _sent, state)).ToList();
            _sent = state;
            if (changed.Count == 0) return;

            using var writer = _connection!.GetMessageWriter();
            writer.WriteSignalHeader(path: ObjectPath, @interface: PropertiesInterface, member: "PropertiesChanged", signature: "sa{sv}as");
            writer.WriteString(PlayerInterface);
            var dict = writer.WriteDictionaryStart();
            foreach (var name in changed)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                WritePlayerProperty(ref Unsafe(writer), name, state);
            }

            writer.WriteDictionaryEnd(dict);
            writer.WriteArray(Array.Empty<string>());
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex) when (ex is DBusConnectionException or DBusErrorReplyException or IOException or ObjectDisposedException)
        {
            Log.Warn($"Media controls: the desktop's bus refused an update ({ex.Message}).");
            _connection?.Dispose();
            _connection = null;
            BusName = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ConnectAsync()
    {
        var address = _address ?? DBusAddress.Session;
        if (address is null) return false;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            connection.AddMethodHandler(this);
            var name = BaseName;
            if (!await connection.TryRequestNameAsync(name, RequestNameOptions.None).ConfigureAwait(false))
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{BaseName}.instance{Environment.ProcessId}");
                await connection.RequestNameAsync(name, RequestNameOptions.None).ConfigureAwait(false);
            }

            _connection = connection;
            BusName = name;
            Log.Info($"Media controls: Tuxflix shows in the desktop's players as {name}.");
            return true;
        }
        catch (Exception ex) when (ex is DBusConnectionException or DBusErrorReplyException or IOException)
        {
            Log.Warn($"Media controls: the desktop's bus could not be reached ({ex.Message}).");
            connection.Dispose();
            return false;
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var member = request.MemberAsString ?? string.Empty;
        var state = _state;
        switch (request.InterfaceAsString)
        {
            case PropertiesInterface:
                HandleProperties(context, member, state);
                break;
            case "org.freedesktop.DBus.Introspectable" when member == "Introspect":
                context.ReplyIntrospectXml([Introspection]);
                break;
            case RootInterface:
                Post(member switch
                {
                    "Raise" => _raise,
                    "Quit" => _quit,
                    _ => null,
                });
                Done(context, member is "Raise" or "Quit");
                break;
            case PlayerInterface:
                HandlePlayer(context, member, state);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return default;
    }

    private void HandlePlayer(MethodContext context, string member, MediaState state)
    {
        var session = _session;
        var reader = context.Request.GetBodyReader();
        Action? action = member switch
        {
            "Play" => () => session?.Play(),
            "Pause" => () => session?.Pause(),
            "PlayPause" => () => session?.PlayPause(),
            "Stop" => () => session?.Stop(),
            "Next" => () => session?.Next(),
            "Previous" => () => session?.Previous(),
            "Seek" => SeekBy(session, state, reader.ReadInt64()),
            "SetPosition" => SetPosition(session, state, reader.ReadObjectPathAsString(), reader.ReadInt64()),
            _ => null,
        };
        if (member == "OpenUri")
        {
            context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "Tuxflix plays what is on its server.");
            return;
        }

        Post(action);
        Done(context, action is not null || member is "SetPosition");
    }

    private static Action? SeekBy(IMediaSession? session, MediaState state, long offset) =>
        () => session?.SeekTo(Math.Max(0, state.PositionNow + (offset / 1e6)));

    /// <summary>A position for a track other than the current one is ignored, as the specification says.</summary>
    private static Action? SetPosition(IMediaSession? session, MediaState state, string trackId, long position) =>
        trackId == TrackPath(state) && position >= 0 && position <= Micro(state.LengthSeconds) ? () => session?.SeekTo(position / 1e6) : null;

    private void HandleProperties(MethodContext context, string member, MediaState state)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "Get":
            {
                var iface = reader.ReadString();
                var name = reader.ReadString();
                using var writer = context.CreateReplyWriter("v");
                if (iface == RootInterface && RootProperties.Contains(name)) WriteRootProperty(ref Unsafe(writer), name);
                else if (iface == PlayerInterface && PlayerProperties.Contains(name)) WritePlayerProperty(ref Unsafe(writer), name, state);
                else
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name} on {iface}.");
                    return;
                }

                context.Reply(writer.CreateMessage());
                break;
            }

            case "GetAll":
            {
                var iface = reader.ReadString();
                using var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                foreach (var name in iface == RootInterface ? RootProperties : iface == PlayerInterface ? PlayerProperties : [])
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    if (iface == RootInterface) WriteRootProperty(ref Unsafe(writer), name);
                    else WritePlayerProperty(ref Unsafe(writer), name, state);
                }

                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                break;
            }

            case "Set":
            {
                var iface = reader.ReadString();
                var name = reader.ReadString();
                var value = reader.ReadVariantValue();
                var session = _session;
                Action? action = (iface, name) switch
                {
                    (PlayerInterface, "Volume") => () => session?.SetVolume(Math.Max(0, value.GetDouble())),
                    (PlayerInterface, "LoopStatus") when state.LoopStatus is not null => () => session?.SetLoop(value.GetString()),
                    (PlayerInterface, "Shuffle") when state.Shuffle is not null => () => session?.SetShuffle(value.GetBool()),
                    _ => null,
                };
                if (action is null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", $"{name} cannot be set.");
                    return;
                }

                Post(action);
                Done(context, true);
                break;
            }

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private static void WriteRootProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "CanQuit" or "CanRaise":
                writer.WriteVariantBool(true);
                break;
            case "HasTrackList":
                writer.WriteVariantBool(false);
                break;
            case "Identity":
                writer.WriteVariantString("Tuxflix");
                break;
            case "DesktopEntry":
                writer.WriteVariantString(DesktopIdentity.AppId);
                break;
            default:
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                break;
        }
    }

    private static void WritePlayerProperty(ref MessageWriter writer, string name, MediaState state)
    {
        switch (name)
        {
            case "PlaybackStatus":
                writer.WriteVariantString(state.Status);
                break;
            case "LoopStatus":
                writer.WriteVariantString(state.LoopStatus ?? "None");
                break;
            case "Shuffle":
                writer.WriteVariantBool(state.Shuffle ?? false);
                break;
            case "Rate" or "MinimumRate" or "MaximumRate":
                writer.WriteVariantDouble(1);
                break;
            case "Volume":
                writer.WriteVariantDouble(state.Volume);
                break;
            case "Position":
                writer.WriteVariantInt64(Micro(state.PositionNow));
                break;
            case "CanGoNext":
                writer.WriteVariantBool(state.CanGoNext);
                break;
            case "CanGoPrevious":
                writer.WriteVariantBool(state.CanGoPrevious);
                break;
            case "CanPlay" or "CanPause" or "CanControl":
                writer.WriteVariantBool(state.HasMedia);
                break;
            case "CanSeek":
                writer.WriteVariantBool(state.CanSeek);
                break;
            case "Metadata":
                WriteMetadata(ref writer, state);
                break;
        }
    }

    private static void WriteMetadata(ref MessageWriter writer, MediaState state)
    {
        writer.WriteSignature("a{sv}");
        var dict = writer.WriteDictionaryStart();
        if (state.HasMedia)
        {
            Entry(ref writer, "mpris:trackid");
            writer.WriteSignature("o");
            writer.WriteObjectPath(TrackPath(state));
            Entry(ref writer, "mpris:length");
            writer.WriteVariantInt64(Micro(state.LengthSeconds));
            Entry(ref writer, "xesam:title");
            writer.WriteVariantString(state.Title);
            if (state.Artists.Count > 0)
            {
                Entry(ref writer, "xesam:artist");
                writer.WriteSignature("as");
                writer.WriteArray(state.Artists.ToArray());
            }

            if (!string.IsNullOrEmpty(state.Album))
            {
                Entry(ref writer, "xesam:album");
                writer.WriteVariantString(state.Album);
            }

            if (state.ArtFile is { } art)
            {
                Entry(ref writer, "mpris:artUrl");
                writer.WriteVariantString(new Uri(art).AbsoluteUri);
            }
        }

        writer.WriteDictionaryEnd(dict);

        static void Entry(ref MessageWriter writer, string key)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
        }
    }

    private static bool Same(string name, MediaState a, MediaState b) => name switch
    {
        "PlaybackStatus" => a.Status == b.Status,
        "LoopStatus" => a.LoopStatus == b.LoopStatus,
        "Shuffle" => a.Shuffle == b.Shuffle,
        "Volume" => Math.Abs(a.Volume - b.Volume) < 0.001,
        "CanGoNext" => a.CanGoNext == b.CanGoNext,
        "CanGoPrevious" => a.CanGoPrevious == b.CanGoPrevious,
        "CanSeek" => a.CanSeek == b.CanSeek,
        "CanPlay" or "CanPause" or "CanControl" => a.HasMedia == b.HasMedia,
        "Metadata" => a.TrackId == b.TrackId && a.Title == b.Title && a.Album == b.Album && a.ArtFile == b.ArtFile
                      && Math.Abs(a.LengthSeconds - b.LengthSeconds) < 0.5 && a.Artists.SequenceEqual(b.Artists),
        _ => true,
    };

    private static string TrackPath(MediaState state) =>
        TrackPrefix + string.Concat((state.TrackId ?? "none").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    private static long Micro(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 1e6);

    private void Post(Action? action)
    {
        if (action is not null) _post(action);
    }

    private static void Done(MethodContext context, bool known)
    {
        if (!known)
        {
            context.ReplyUnknownMethodError();
            return;
        }

        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    // MessageWriter is a ref struct handed out by value; its writes go to a shared buffer.
    private static ref MessageWriter Unsafe(in MessageWriter writer) => ref System.Runtime.CompilerServices.Unsafe.AsRef(in writer);

    private static readonly ReadOnlyMemory<byte> Introspection = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/><method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/><property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/><property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/><property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/><method name="Previous"/><method name="Pause"/><method name="PlayPause"/><method name="Stop"/><method name="Play"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition"><arg name="TrackId" type="o" direction="in"/><arg name="Position" type="x" direction="in"/></method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/><property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/><property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/><property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/><property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/><property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/><property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/><property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);
}
