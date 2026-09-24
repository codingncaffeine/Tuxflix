using System.Reflection;

namespace Tuxflix.App;

/// <summary>The version and the time this build was made.</summary>
internal static class BuildInfo
{
    public static string Version =>
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>"0.1.0 (built 2026-09-24 13:05)": the question a version number alone cannot answer
    /// is whether the running build is the one that was just made.</summary>
    public static string Stamp
    {
        get
        {
            var informational = typeof(BuildInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // SourceLink appends the commit after a dot in the build metadata; the stamp stops before it.
            return informational?.Split('+') is [var version, var built]
                ? $"{version} (built {built.Split('.')[0]})"
                : Version;
        }
    }
}
