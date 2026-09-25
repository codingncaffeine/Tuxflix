using System.Diagnostics;
using Tmds.DBus.Protocol;
using Tuxflix.App.Platform;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// A link goes to the desktop portal, so the browser starts outside any sandbox Tuxflix is in; a
/// stand-in portal on a private bus of the test's own receives it (never the desktop's: no browser opens).
/// </summary>
public sealed class LinksTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "tuxflix-links-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task ALinkOpensThroughTheDesktopPortal()
    {
        if (_address.Length == 0) Assert.Skip("dbus-daemon is not installed here.");
        using var bus = new DBusConnection(_address);
        await bus.ConnectAsync();
        var portal = new StandInPortal();
        bus.AddMethodHandler(portal);
        await bus.RequestNameAsync("org.freedesktop.portal.Desktop", RequestNameOptions.None);

        await Links.OpenAsync("https://app.plex.tv/auth#?clientID=test&code=abc", _address, orXdgOpen: false);

        Assert.Equal(["https://app.plex.tv/auth#?clientID=test&code=abc"], portal.Opened);
    }

    private sealed class StandInPortal : IPathMethodHandler
    {
        public List<string> Opened { get; } = [];

        public string Path => "/org/freedesktop/portal/desktop";

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request is { InterfaceAsString: "org.freedesktop.portal.OpenURI", MemberAsString: "OpenURI", SignatureAsString: "ssa{sv}" })
            {
                var reader = context.Request.GetBodyReader();
                reader.ReadString();
                lock (Opened) Opened.Add(reader.ReadString());
                using var writer = context.CreateReplyWriter("o");
                writer.WriteObjectPath("/org/freedesktop/portal/desktop/request/1_1/tuxflix");
                context.Reply(writer.CreateMessage());
            }
            else
            {
                context.ReplyUnknownMethodError();
            }

            return default;
        }
    }
}
