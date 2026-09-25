using Avalonia;
using Avalonia.Controls;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App;

/// <summary>
/// Which windowing backend Tuxflix runs on.
/// </summary>
/// <remarks>
/// On a Wayland session the native Wayland backend is the default, because it renders at the
/// monitor's fractional scale instead of being scaled by the compositor. <c>TUXFLIX_WAYLAND=0</c>
/// pins X11 through XWayland and <c>=1</c> pins Wayland strictly.
/// <para>
/// A backend that starts and then dies on its own worker thread (a session with no outputs yet,
/// for instance) cannot be caught, so the attempt is written down before it is made and rubbed
/// out once a window is open. A run that dies in between leaves the note, and the next run takes
/// X11. The pattern and its reasons come from Mailbox, where it was measured.
/// </para>
/// </remarks>
internal static class WindowingBackend
{
    public const string Variable = "TUXFLIX_WAYLAND";

    private static bool _attempting;

    private static bool? Requested =>
        Environment.GetEnvironmentVariable(Variable)?.Trim() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => null,
        };

    private static bool InWaylandSession =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string Breadcrumb => Path.Combine(
        App.Launch?.Paths.State ?? Path.GetTempPath(), "wayland-attempt");

    public static AppBuilder Apply(AppBuilder builder)
    {
        // Every window draws its own frame; X11 is told the same so its popups share the window.
        builder = builder.With(new X11PlatformOptions { OverlayPopups = true });

        switch (Requested)
        {
            case true:
                return WithDrawnDecorations(builder).UseWayland();
            case false:
                return builder;
        }

        if (!InWaylandSession) return builder;

        if (File.Exists(Breadcrumb))
        {
            Log.Warn($"Windowing: the Wayland backend did not open a window last time; using X11. Delete {Breadcrumb} or set {Variable}=1 to retry.");
            return builder;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Breadcrumb)!);
            File.WriteAllText(Breadcrumb, DateTimeOffset.Now.ToString("O") + "\n");
            _attempting = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Windowing: the Wayland attempt could not be noted ({ex.Message}).");
        }

        return WithDrawnDecorations(builder).UseWaylandWithFallback();
    }

    /// <summary>Called once a window is on screen, the only proof the backend works.</summary>
    public static void WindowOpened(TopLevel window)
    {
        Log.Info($"Windowing: {Describe(window)}");
        if (!_attempting) return;
        _attempting = false;

        // Called on the UI thread: the file goes on a worker.
        var breadcrumb = Breadcrumb;
        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(breadcrumb);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Windowing: the Wayland attempt note could not be cleared ({ex.Message}).");
            }
        });
    }

    // The window draws its own title bar, so the compositor is never asked for one: asking and then
    // withdrawing the request does not survive a window being hidden and shown again.
    private static AppBuilder WithDrawnDecorations(AppBuilder builder)
    {
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
        return builder.With(new WaylandPlatformOptions { ForceDrawnDecorations = true });
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
    }

    /// <summary>Whether the window is on the native Wayland backend, where a client cannot keep itself above others.</summary>
    public static bool IsWayland(TopLevel window) =>
        window.PlatformImpl?.GetType().Namespace?.StartsWith("Avalonia.Wayland", StringComparison.Ordinal) == true;

    private static string Describe(TopLevel window)
    {
        var implementation = window.PlatformImpl?.GetType().Namespace ?? string.Empty;
        return IsWayland(window) ? "native Wayland"
            : implementation.StartsWith("Avalonia.X11", StringComparison.Ordinal)
                ? (InWaylandSession ? "X11 through XWayland" : "X11")
            : implementation.Length > 0 ? implementation : "unknown";
    }
}
