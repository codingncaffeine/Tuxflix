using Avalonia;
using Avalonia.Headless;

namespace Tuxflix.HeadlessTests;

/// <summary>Avalonia's headless platform with Skia, set up once for tests that decode pictures.</summary>
internal static class HeadlessSkia
{
    private static readonly Lazy<bool> Ready = new(() =>
    {
        // Setting it up installs Avalonia's synchronization context on this thread, and a test
        // awaiting under it would wait for a dispatcher nobody runs: the test's own context goes back.
        var context = SynchronizationContext.Current;
        try
        {
            // The Inter font the application ships with: the demo draws its artwork with it.
            AppBuilder.Configure<Application>()
                .UseSkia()
                .WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }

        return true;
    });

    public static void Ensure() => _ = Ready.Value;
}
