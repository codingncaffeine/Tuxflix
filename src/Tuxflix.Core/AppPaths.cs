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
/// </remarks>
public sealed record AppPaths(string Config, string Data, string State, string Cache, string Runtime, bool Portable)
{
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
                RuntimeDirectory(environment, "tuxflix-" + ShortHash(root)),
                Portable: true);
        }

        // The XDG specification says a relative value is invalid and must be ignored.
        string Base(string variable, string fallback) =>
            environment(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
                ? value
                : Path.Combine(home, fallback);

        return new AppPaths(
            Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "tuxflix"),
            Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), "tuxflix"),
            Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), "tuxflix"),
            Path.Combine(Base("XDG_CACHE_HOME", ".cache"), "tuxflix"),
            RuntimeDirectory(environment, "tuxflix"),
            Portable: false);
    }

    /// <summary>Creates every directory; the runtime one readable by this user only.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in new[] { Config, Data, State, Cache, Logs })
        {
            Directory.CreateDirectory(directory);
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Runtime);
        }
        else
        {
            Directory.CreateDirectory(Runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string RuntimeDirectory(Func<string, string?> environment, string name)
    {
        // Kept short on purpose: a Unix socket path over 108 bytes cannot be bound at all.
        if (environment("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Path.IsPathRooted(runtime))
        {
            return Path.Combine(runtime, name);
        }

        return Path.Combine(Path.GetTempPath(), $"{name}-{Environment.UserName}");
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
}
