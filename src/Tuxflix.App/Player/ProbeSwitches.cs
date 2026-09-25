namespace Tuxflix.App.Player;

/// <summary>Environment switches for test runs.</summary>
internal static class ProbeSwitches
{
    /// <summary><c>TUXFLIX_AO=null</c>: a test run opens no sound device at all (mpv's <c>ao</c>).</summary>
    public static string? AudioOutput => Environment.GetEnvironmentVariable("TUXFLIX_AO") is { Length: > 0 } ao ? ao : null;

    /// <summary><c>TUXFLIX_HWDEC=no</c> (or a decoder name): mpv's <c>hwdec</c> for a test run, in place of <c>auto-safe</c>.</summary>
    public static string? HardwareDecoding => Environment.GetEnvironmentVariable("TUXFLIX_HWDEC") is { Length: > 0 } hwdec ? hwdec : null;
}
