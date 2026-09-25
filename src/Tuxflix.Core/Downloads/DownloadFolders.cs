namespace Tuxflix.Core.Downloads;

/// <summary>
/// Whether a folder can take downloads, and why not in words. Packaged, Tuxflix runs in a sandbox
/// that lets it write only to its own folders and the desktop's user folders; the launcher names
/// them in <c>TUXFLIX_SANDBOX_WRITABLE</c> (separated by colons) and sets <c>TUXFLIX_SANDBOX=1</c>,
/// and a refusal there names the folders that would work.
/// </summary>
public static class DownloadFolders
{
    /// <summary>The folders a sandboxed Tuxflix may write to, or null when it runs without one.</summary>
    public static IReadOnlyList<string>? Writable(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment("TUXFLIX_SANDBOX") != "1") return null;
        return [.. (environment("TUXFLIX_SANDBOX_WRITABLE") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Why <paramref name="folder"/> could not be written, in words for the viewer.</summary>
    public static string Explain(string folder, Exception error, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Writable(environment) is { Count: > 0 } allowed
            ? $"Tuxflix may not write to {folder}: installed as a package, it may only write inside {string.Join(", ", allowed)}. Choose a download folder in one of those."
            : $"{folder} cannot be written to: {error.Message}";
    }

    /// <summary>
    /// Makes the folder if need be and writes and deletes a small file in it; null when that works,
    /// else the reason in words. Touches the disk: a worker's job.
    /// </summary>
    public static string? Check(string folder, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var probe = Path.Combine(folder, ".tuxflix-write-check");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Explain(folder, ex, environment);
        }
    }
}
