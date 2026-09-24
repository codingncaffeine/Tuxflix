using System.Collections.Concurrent;
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
/// so the log alone is enough to act on a report. Logging never throws into its caller, and never
/// makes it wait: a line is formatted and queued, and a thread of the log's own writes the file
/// and the terminal, so logging from the UI thread costs no disk or terminal I/O there. A crash
/// report is waited for, briefly, because the process may be about to end.
/// </remarks>
public static class Log
{
    private const int Keep = 5;
    private const int RecentLimit = 500;

    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly Queue<string> Recent = new();
    private static readonly BlockingCollection<(LogLevel Level, string Line)> Pending = [];
    private static readonly object Progress = new();
    private static readonly Thread Writer = new(WriteLoop) { IsBackground = true, Name = "Tuxflix log" };

    private static StreamWriter? _file;
    private static volatile bool _echo = true;
    private static long _queued;
    private static long _written;
    private static int _started;

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
    /// Safe to skip: without it the log goes to the terminal only. Called at start, before any window.
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
                    Volatile.Write(ref _file, new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not open the log file: {ex.Message}");
                }
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromSeconds(1));

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

    /// <summary>Records a crash with its whole chain, waits for it to reach the file, and returns the report for display.</summary>
    public static string Crash(string source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var report = new StringBuilder();
        report.AppendLine($"Unhandled exception in {source}.");
        Describe(report, error, 0);
        Write(LogLevel.Error, report.ToString().TrimEnd(), null);
        Flush(TimeSpan.FromSeconds(2));
        return report.ToString();
    }

    /// <summary>Waits until every line queued so far is written, or <paramref name="limit"/> passes.</summary>
    public static bool Flush(TimeSpan limit)
    {
        var target = Interlocked.Read(ref _queued);
        var clock = Stopwatch.StartNew();
        lock (Progress)
        {
            while (Interlocked.Read(ref _written) < target)
            {
                var left = limit - clock.Elapsed;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(Progress, left);
            }
        }

        return true;
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
        }

        if (Interlocked.Exchange(ref _started, 1) == 0) Writer.Start();
        Interlocked.Increment(ref _queued);
        Pending.Add((level, line));
    }

    /// <summary>The log's own thread: writes queued lines to the file and the terminal.</summary>
    private static void WriteLoop()
    {
        foreach (var (level, line) in Pending.GetConsumingEnumerable())
        {
            var file = Volatile.Read(ref _file);
            try
            {
                file?.WriteLine(line);
                if (Pending.Count == 0) file?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A full disk must not end the log.
            }

            if (_echo)
            {
                if (level >= LogLevel.Warning) Console.Error.WriteLine(line);
                else Console.WriteLine(line);
            }

            lock (Progress)
            {
                Interlocked.Increment(ref _written);
                Monitor.PulseAll(Progress);
            }
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
