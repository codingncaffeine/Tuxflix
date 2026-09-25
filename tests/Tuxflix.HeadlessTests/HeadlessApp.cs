using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The application on Avalonia's headless platform, with its own styles, fonts and views, on a
/// thread of its own whose dispatcher turns for the whole run: a window lives on the thread that
/// made it, so every test that builds one runs there through <see cref="Run"/>. Once it is set
/// up, pictures decode on any thread, which the tests that only decode rely on.
/// </summary>
/// <remarks>
/// A dedicated thread rather than a task on the thread pool: a test class's constructor blocks
/// until the application is ready, and a set-up queued behind a blocked pool thread can wait for
/// a pool thread that never comes.
/// </remarks>
internal static class HeadlessApp
{
    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>What the thread sets up: the application as it starts, on the headless platform.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });

    /// <summary>Sets the application up once, and waits until it is.</summary>
    public static void Ensure() => _ = Ui.Value;

    /// <summary>Runs <paramref name="test"/> on the application's thread, its awaits coming back there.</summary>
    public static Task Run(Func<Task> test) => Ui.Value.InvokeAsync(test);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                ready.SetResult(Dispatcher.UIThread);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "Avalonia UI for tests",
        };
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
