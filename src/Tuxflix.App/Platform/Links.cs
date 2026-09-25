using Tmds.DBus.Protocol;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Platform;

/// <summary>
/// Opens a web address in the desktop's browser: through the desktop portal, so the browser runs
/// outside whatever sandbox Tuxflix is in (a browser started from inside the hardened unit would
/// inherit its read-only home); <c>xdg-open</c> where there is no portal. On a worker.
/// </summary>
/// <remarks>
/// Only web addresses: a link can come from a server's answer (a review's), and the desktop hands
/// any other scheme to whatever claims it, a local file to its default application included.
/// </remarks>
internal static class Links
{
    private const string Portal = "org.freedesktop.portal.Desktop";

    /// <summary>Whether <paramref name="url"/> is a web address, the only kind opened.</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    /// <param name="bus">The session bus to ask on; the desktop's own when null (a test gives its private one).</param>
    /// <param name="orXdgOpen">Whether to fall back to <c>xdg-open</c>; a test says no, so that nothing it does can open a real browser.</param>
    public static async Task OpenAsync(string url, string? bus = null, bool orXdgOpen = true)
    {
        if (!IsWebAddress(url))
        {
            Log.Warn("A link that is not a web address was not opened.");
            return;
        }

        if ((bus ?? DBusAddress.Session) is { } address && await ThroughPortalAsync(address, url).ConfigureAwait(false)) return;
        if (!orXdgOpen) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(url);
            using var _ = System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("The browser could not be opened.", ex);
        }
    }

    private static async Task<bool> ThroughPortalAsync(string address, string url)
    {
        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            var writer = connection.GetMessageWriter();
            MessageBuffer call;
            try
            {
                writer.WriteMethodCallHeader(destination: Portal, path: "/org/freedesktop/portal/desktop", @interface: "org.freedesktop.portal.OpenURI", member: "OpenURI", signature: "ssa{sv}");
                writer.WriteString(string.Empty);
                writer.WriteString(url);
                var options = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(options);
                call = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }

            await connection.CallMethodAsync(call, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
            Log.Info("The browser was asked to open the page (through the desktop portal).");
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or IOException or InvalidOperationException)
        {
            Log.Info($"The desktop portal could not open the page ({ex.Message}); trying xdg-open.");
            return false;
        }
    }
}
