using System.Diagnostics;
using System.Text;

namespace Tuxflix.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// The application log: a file under the state directory, echoed to the terminal.
/// </summary>
/// <remarks>
/// Deliberately not a framework. One file per run, the previous four kept, one line per entry
/// with the wall time and the time since start, and exceptions with their whole inner chain,
/// so the log alone is enough to act on a report. Logging never throws into its caller.
/// </remarks>
public static class Log
{
    private const int Keep = 5;
    private const int RecentLimit = 500;

    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly Queue<string> Recent = new();

    private static StreamWriter? _file;
    private static bool _echo = true;

    public static string? FilePath { get; private set; }

    public static LogLevel Minimum { get; set; } =
        Environment.GetEnvironmentVariable("TUXFLIX_LOG_LEVEL")?.ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

    /// <summary>
    /// Opens <c>tuxflix.log</c> in <paramref name="directory"/>, rolling the previous runs along.
    /// Safe to skip: without it the log goes to the terminal only.
    /// </summary>
    public static void Initialize(string stamp, string directory)
    {
        lock (Gate)
        {
            if (_file is null)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    Roll(directory);
                    FilePath = Path.Combine(directory, "tuxflix.log");
                    _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not open the log file: {ex.Message}");
                }
            }
        }

        Info($"Tuxflix {stamp}");
        Info($"Runtime {Environment.Version} on {Environment.OSVersion}");
        Info($"Session {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"}, "
             + $"desktop {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown"}");
        if (FilePath is not null) Info($"Log file {FilePath}");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? error = null) => Write(LogLevel.Warning, message, error);

    public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

    /// <summary>Records a crash with its whole chain and returns the report for display.</summary>
    public static string Crash(string source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var report = new StringBuilder();
        report.AppendLine($"Unhandled exception in {source}.");
        Describe(report, error, 0);
        Write(LogLevel.Error, report.ToString().TrimEnd(), null);
        return report.ToString();
    }

    /// <summary>The most recent entries, newest last.</summary>
    public static IReadOnlyList<string> RecentEntries()
    {
        lock (Gate) return [.. Recent];
    }

    public static void SetConsoleEcho(bool enabled) => _echo = enabled;

    private static void Roll(string directory)
    {
        var current = Path.Combine(directory, "tuxflix.log");
        if (!File.Exists(current)) return;

        try
        {
            for (var i = Keep - 1; i >= 1; i--)
            {
                var older = Path.Combine(directory, $"tuxflix.{i}.log");
                if (File.Exists(older)) File.Move(older, Path.Combine(directory, $"tuxflix.{i + 1}.log"), overwrite: true);
            }

            File.Move(current, Path.Combine(directory, "tuxflix.1.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rolling is a convenience; the new log simply replaces the old one.
        }
    }

    private static void Describe(StringBuilder report, Exception error, int depth)
    {
        var indent = new string(' ', depth * 2);
        report.AppendLine($"{indent}{error.GetType().FullName}: {error.Message}");
        foreach (var line in (error.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            report.AppendLine($"{indent}  {line.TrimEnd()}");
        }

        if (error.InnerException is { } inner)
        {
            report.AppendLine($"{indent}Caused by:");
            Describe(report, inner, depth + 1);
        }
    }

    private static void Write(LogLevel level, string message, Exception? error)
    {
        if (level < Minimum) return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} {Uptime.Elapsed.TotalSeconds,8:0.000}s {Label(level)} {message}";
        if (error is not null)
        {
            var report = new StringBuilder();
            Describe(report, error, 1);
            line += Environment.NewLine + report.ToString().TrimEnd();
        }

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentLimit) Recent.Dequeue();

            try
            {
                _file?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A full disk must not take the caller with it.
            }

            if (!_echo) return;
            if (level >= LogLevel.Warning) Console.Error.WriteLine(line);
            else Console.WriteLine(line);
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };
}
