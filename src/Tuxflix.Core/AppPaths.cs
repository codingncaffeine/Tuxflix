using System.Security.Cryptography;
using System.Text;

namespace Tuxflix.Core;

/// <summary>
/// Where Tuxflix keeps its files: the XDG base directories, or one folder in portable mode.
/// </summary>
/// <remarks>
/// Portable mode (<c>--portable DIR</c>) puts configuration, data, state and cache under one
/// folder, which is also how every test and smoke run keeps its hands off the real profile. The
/// runtime directory holds only the single-instance socket and lock; a portable profile gets a
/// runtime directory of its own so it never hands its arguments to an instance on the real one.
/// Without <c>XDG_RUNTIME_DIR</c> they live in the profile's own cache, never the shared
/// temporary folder, where another user could make the folder first and own it.
/// </remarks>
public sealed record AppPaths(string Config, string Data, string State, string Cache, string Runtime, bool Portable)
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public string Logs => Path.Combine(State, "logs");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string ImageCache => Path.Combine(Cache, "images");

    public string InstanceSocket => Path.Combine(Runtime, "instance.sock");

    public string InstanceLock => Path.Combine(Runtime, "instance.lock");

    /// <summary>Resolves the directories from the environment, or under <paramref name="portableRoot"/>.</summary>
    /// <param name="portableRoot">The portable folder, or null for the XDG layout.</param>
    /// <param name="environment">Reads an environment variable; injected so tests can pose one.</param>
    public static AppPaths Resolve(string? portableRoot, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var home = environment("HOME") is { Length: > 0 } h
            ? h
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(portableRoot))
        {
            var root = Path.GetFullPath(portableRoot);
            return new AppPaths(
                Path.Combine(root, "config"),
                Path.Combine(root, "data"),
                Path.Combine(root, "state"),
                Path.Combine(root, "cache"),
                RuntimeDirectory(environment, "tuxflix-" + ShortHash(root), Path.Combine(root, "cache")),
                Portable: true);
        }

        // The XDG specification says a relative value is invalid and must be ignored.
        string Base(string variable, string fallback) =>
            environment(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
                ? value
                : Path.Combine(home, fallback);

        var cache = Path.Combine(Base("XDG_CACHE_HOME", ".cache"), "tuxflix");
        return new AppPaths(
            Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "tuxflix"),
            Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), "tuxflix"),
            Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), "tuxflix"),
            cache,
            RuntimeDirectory(environment, "tuxflix", cache),
            Portable: false);
    }

    /// <summary>
    /// Creates every directory, each readable by this user only: the settings, the log (what was
    /// watched, and when) and the cache are nobody else's business on a shared computer. A folder
    /// made earlier with looser permissions is tightened; one this user may not change is left be.
    /// </summary>
    public void EnsureCreated()
    {
        foreach (var directory in new[] { Config, Data, State, Cache, Logs, Runtime })
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, OwnerOnly);
                try
                {
                    if ((File.GetUnixFileMode(directory) & ~OwnerOnly) != 0) File.SetUnixFileMode(directory, OwnerOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another user's folder (a shared portable one): its owner decides.
                }
            }
        }
    }

    private static string RuntimeDirectory(Func<string, string?> environment, string name, string cache)
    {
        // Kept short on purpose: a Unix socket path over 108 bytes cannot be bound at all.
        if (environment("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Path.IsPathRooted(runtime))
        {
            return Path.Combine(runtime, name);
        }

        return Path.Combine(cache, "runtime");
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
}
