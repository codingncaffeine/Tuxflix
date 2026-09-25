using System.Collections.Concurrent;
using System.Diagnostics;
using Tmds.DBus.Protocol;
using Tuxflix.App.Platform;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The desktop's media controls, over a private bus of the test's own: never the desktop's. A
/// client on that bus reads what plays, drives it, hears it change, and sees Tuxflix leave when
/// nothing is loaded.
/// </summary>
public sealed class MprisTests : IAsyncLifetime
{
    private const string Player = "org.mpris.MediaPlayer2.Player";
    private readonly string _folder = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "tuxflix-mpris-" + Guid.NewGuid().ToString("N")[..8]);
    private Process? _daemon;
    private string _address = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!File.Exists("/usr/bin/dbus-daemon") && !File.Exists("/bin/dbus-daemon")) return;
        Directory.CreateDirectory(_folder);
        _daemon = Process.Start(new ProcessStartInfo("dbus-daemon", $"--session --nofork --print-address --address=unix:path={_folder}/bus")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        _address = (await _daemon!.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))?.Trim() ?? string.Empty;
    }

    public ValueTask DisposeAsync()
    {
        if (_daemon is { HasExited: false }) _daemon.Kill();
        _daemon?.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        return default;
    }

    [Fact]
    public async Task TheDesktopSeesWhatPlaysDrivesItHearsItChangeAndSeesItLeave()
    {
        if (_address.Length == 0) Assert.Skip("dbus-daemon is not installed here.");
        var cancel = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        await using var service = new MprisService(_address, () => session.Calls.Enqueue("Raise"), () => session.Calls.Enqueue("Quit"), action => action());
        service.Attach(session);
        await Until(() => service.BusName is not null, cancel);
        var name = service.BusName!;
        Assert.Equal("org.mpris.MediaPlayer2.tuxflix", name);

        using var client = new DBusConnection(_address);
        await client.ConnectAsync();

        // What plays.
        Assert.Equal("Paused", (await Get(client, name, Player, "PlaybackStatus")).GetString());
        Assert.Equal("Tuxflix", (await Get(client, name, "org.mpris.MediaPlayer2", "Identity")).GetString());
        var metadata = (await Get(client, name, Player, "Metadata")).GetDictionary<string, VariantValue>();
        Assert.Equal("Song", metadata["xesam:title"].GetString());
        Assert.Equal(["Band"], metadata["xesam:artist"].GetArray<string>());
        Assert.Equal(200_000_000, metadata["mpris:length"].GetInt64());
        var track = metadata["mpris:trackid"].GetObjectPathAsString();
        Assert.Equal(12_000_000, (await Get(client, name, Player, "Position")).GetInt64());

        // Driving it: the keys, a seek, a jump to a place, the volume; a place in another track is ignored.
        await Call(client, name, "PlayPause", null, static (ref MessageWriter _) => { });
        await Call(client, name, "Seek", "x", static (ref MessageWriter w) => w.WriteInt64(5_000_000));
        await Call(client, name, "SetPosition", "ox", (ref MessageWriter w) =>
        {
            w.WriteObjectPath(track);
            w.WriteInt64(30_000_000);
        });
        await Call(client, name, "SetPosition", "ox", static (ref MessageWriter w) =>
        {
            w.WriteObjectPath("/io/github/codingncaffeine/Tuxflix/track/other");
            w.WriteInt64(40_000_000);
        });
        await SetVolume(client, name, 0.8);
        Assert.Equal(["PlayPause", "SeekTo 17", "SeekTo 30", "Volume 0.80"], session.Calls);

        // Hearing it change: the status, and not the position (the specification says not to).
        var heard = new TaskCompletionSource<Dictionary<string, VariantValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var match = await client.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged", Path = MprisService.ObjectPath },
            static (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString();
                return reader.ReadDictionaryOfStringToVariantValue();
            },
            notification =>
            {
                if (notification.HasValue) heard.TrySetResult(notification.Value);
            },
            emitOnCapturedContext: false);
        session.State = session.State with { Status = "Playing", PositionSeconds = 31 };
        session.Raise();
        var changed = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), cancel);
        Assert.Equal("Playing", changed["PlaybackStatus"].GetString());
        Assert.False(changed.ContainsKey("Position"));

        // Nothing loaded: Tuxflix leaves the desktop's players.
        session.State = MediaState.Nothing;
        session.Raise();
        await Until(() => service.BusName is null, cancel);
        Assert.False(await HasOwner(client, name));
    }

    [Fact]
    public async Task ABriefGapInTheQueueKeepsTuxflixInTheDesktopsPlayers()
    {
        if (_address.Length == 0) Assert.Skip("dbus-daemon is not installed here.");
        var cancel = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        await using var service = new MprisService(_address, () => { }, () => { }, action => action()) { LeaveAfter = TimeSpan.FromMilliseconds(600) };
        service.Attach(session);
        await Until(() => service.BusName is not null, cancel);
        using var client = new DBusConnection(_address);
        await client.ConnectAsync();

        // A queue starting over: empty for a moment, then full again.
        var loaded = session.State;
        session.State = MediaState.Nothing;
        session.Raise();
        await Task.Delay(150, cancel);
        session.State = loaded;
        session.Raise();

        // Well past the wait, the name never went.
        for (var i = 0; i < 12; i++)
        {
            Assert.True(await HasOwner(client, "org.mpris.MediaPlayer2.tuxflix"));
            await Task.Delay(100, cancel);
        }

        Assert.NotNull(service.BusName);
    }

    private static async Task Until(Func<bool> condition, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25, cancel);
        Assert.True(condition());
    }

    private static Task<VariantValue> Get(DBusConnection client, string name, string iface, string property) =>
        client.CallMethodAsync(
            Build(client, name, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) =>
            {
                w.WriteString(iface);
                w.WriteString(property);
            }),
            static (Message m, object? _) => m.GetBodyReader().ReadVariantValue(),
            null);

    private static Task Call(DBusConnection client, string name, string member, string? signature, WriteBody body) =>
        client.CallMethodAsync(Build(client, name, Player, member, signature, body));

    private static Task SetVolume(DBusConnection client, string name, double volume) =>
        client.CallMethodAsync(Build(client, name, "org.freedesktop.DBus.Properties", "Set", "ssv", (ref MessageWriter w) =>
        {
            w.WriteString(Player);
            w.WriteString("Volume");
            w.WriteVariantDouble(volume);
        }));

    private static Task<bool> HasOwner(DBusConnection client, string name)
    {
        var writer = client.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
            writer.WriteString(name);
            return client.CallMethodAsync(writer.CreateMessage(), static (Message m, object? _) => m.GetBodyReader().ReadBool(), null);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer Build(DBusConnection client, string name, string iface, string member, string? signature, WriteBody body)
    {
        var writer = client.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: name, path: MprisService.ObjectPath, @interface: iface, member: member, signature: signature);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private delegate void WriteBody(ref MessageWriter writer);

    private sealed class FakeSession : IMediaSession
    {
        public MediaState State { get; set; } = new()
        {
            HasMedia = true,
            Status = "Paused",
            TrackId = "42",
            Title = "Song",
            Artists = ["Band"],
            Album = "Record",
            LengthSeconds = 200,
            PositionSeconds = 12,
            Volume = 0.5,
            CanGoNext = true,
            CanGoPrevious = true,
            CanSeek = true,
            LoopStatus = "None",
            Shuffle = false,
        };

        public ConcurrentQueue<string> Calls { get; } = new();

        public event Action? Changed;

        public event Action<double>? Seeked;

        public void Raise() => Changed?.Invoke();

        public void Play() => Calls.Enqueue("Play");

        public void Pause() => Calls.Enqueue("Pause");

        public void PlayPause() => Calls.Enqueue("PlayPause");

        public void Stop() => Calls.Enqueue("Stop");

        public void Next() => Calls.Enqueue("Next");

        public void Previous() => Calls.Enqueue("Previous");

        public void SeekTo(double seconds)
        {
            Calls.Enqueue(FormattableString.Invariant($"SeekTo {seconds:0}"));
            Seeked?.Invoke(seconds);
        }

        public void SetVolume(double volume) => Calls.Enqueue(FormattableString.Invariant($"Volume {volume:0.00}"));

        public void SetLoop(string loop) => Calls.Enqueue("Loop " + loop);

        public void SetShuffle(bool shuffle) => Calls.Enqueue("Shuffle " + shuffle);
    }
}
