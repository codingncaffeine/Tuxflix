namespace Tuxflix.HeadlessTests;

/// <summary>
/// Avalonia's headless platform with Skia (and the Inter font), set up once for tests that decode
/// pictures. It is the same set-up the window tests use (<see cref="HeadlessApp"/>), on that
/// thread: the platform can only be set up once in a process, and setting it up on the test's
/// own thread would leave Avalonia's synchronization context there for an async test to hang on.
/// </summary>
internal static class HeadlessSkia
{
    public static void Ensure() => HeadlessApp.Ensure();
}
