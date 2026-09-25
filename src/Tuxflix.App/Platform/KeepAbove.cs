using Tmds.DBus.Protocol;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// Keeps the picture-in-picture window above the others and in a corner where the window cannot
/// ask for that itself: a Wayland client has no way to stay on top or to place itself, so under
/// Plasma KWin is asked, through its scripting interface, to do it for this process's window.
/// </summary>
/// <remarks>
/// Under X11 the window's own <c>Topmost</c> and position do the job. The script is written to the
/// cache folder, loaded, run once and unloaded; it touches only this process's normal windows.
/// Everything here runs on a worker.
/// </remarks>
internal static class KeepAbove
{
    private const string KWin = "org.kde.KWin";

    /// <summary>Wayland under Plasma: the only place this helper is needed and can work.</summary>
    public static bool ViaKWin =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps this process's window above the others, tucked into the bottom right corner of its
    /// screen, or lets it go and centres it again.
    /// </summary>
    public static async Task<bool> SetAsync(bool above, string folder)
    {
        if (DBusAddress.Session is not { } address) return false;
        var name = $"tuxflix-keep-above-{Environment.ProcessId}";
        var script = Path.Combine(folder, "keep-above.js");
        var place = above
            ? "x: area.x + area.width - g.width - 24, y: area.y + area.height - g.height - 24"
            : "x: area.x + (area.width - g.width) / 2, y: area.y + (area.height - g.height) / 2";
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(script, $$"""
                for (const w of workspace.windowList()) {
                    if (w.pid !== {{Environment.ProcessId}} || !w.normalWindow || w.skipTaskbar) continue;
                    w.keepAbove = {{(above ? "true" : "false")}};
                    const area = workspace.clientArea(KWin.PlacementArea, w);
                    const g = w.frameGeometry;
                    w.frameGeometry = { {{place}}, width: g.width, height: g.height };
                }
                """).ConfigureAwait(false);

            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            await UnloadAsync(connection, name).ConfigureAwait(false);
            var id = await connection.CallMethodAsync(
                Call(connection, "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss", (ref MessageWriter w) =>
                {
                    w.WriteString(script);
                    w.WriteString(name);
                }),
                static (Message m, object? _) => m.GetBodyReader().ReadInt32(),
                null).ConfigureAwait(false);
            if (id < 0) return false;
            await connection.CallMethodAsync(Call(connection, $"/Scripting/Script{id}", "org.kde.kwin.Script", "run", null, static (ref MessageWriter _) => { })).ConfigureAwait(false);
            await UnloadAsync(connection, name).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Info($"KWin could not {(above ? "keep the small window above the others" : "let the window go")} ({ex.Message}).");
            return false;
        }
    }

    private static async Task UnloadAsync(DBusConnection connection, string name) =>
        await connection.CallMethodAsync(
            Call(connection, "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s", (ref MessageWriter w) => w.WriteString(name)),
            static (Message m, object? _) => m.GetBodyReader().ReadBool(),
            null).ConfigureAwait(false);

    private static MessageBuffer Call(DBusConnection connection, string path, string @interface, string member, string? signature, WriteArguments arguments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: KWin, path: path, @interface: @interface, member: member, signature: signature);
            arguments(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
