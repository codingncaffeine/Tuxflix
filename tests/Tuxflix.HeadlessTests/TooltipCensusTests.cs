using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Every control that shows only an icon says what it does (decision 18): the capture tool walks
/// every page of the demo headlessly and names each one without a tooltip. It runs as a process of
/// its own, with its own UI thread, as the application does.
/// </summary>
public sealed class TooltipCensusTests
{
    [Fact]
    public async Task EveryIconOnlyControlOnEveryPageSaysWhatItDoes()
    {
        var cancel = TestContext.Current.CancellationToken;
        var here = AppContext.BaseDirectory;
        var root = Up(here, "Tuxflix.slnx");
        var configuration = new DirectoryInfo(here.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";
        var capture = Path.Combine(root, "tools", "Tuxflix.Capture", "bin", configuration, "net10.0", "Tuxflix.Capture.dll");
        Assert.True(File.Exists(capture), $"Build the solution first: {capture} is missing.");

        var output = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "census-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var start = new ProcessStartInfo(DotnetHost())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { capture, output, "1600x1000", "census" }) start.ArgumentList.Add(argument);
            start.Environment["TUXFLIX_AO"] = "null";
            using var process = Process.Start(start)!;
            var reading = process.StandardOutput.ReadToEndAsync(cancel);
            _ = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            var report = string.Join('\n', (await reading).Split('\n').Where(l => l.StartsWith("census:", StringComparison.Ordinal) || l.StartsWith("  ", StringComparison.Ordinal)));

            Assert.True(process.ExitCode == 0, report.Length > 0 ? report : "The census did not run.");
            Assert.Matches(@"census: [1-9]\d+ icon-only controls looked at on [1-9]\d* pages; 0 without a tooltip", report);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static string Up(string from, string marker)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
        }

        throw new InvalidOperationException($"No {marker} above {from}.");
    }

    /// <summary>The dotnet host this test runs under: the one `dotnet test` names, else the runtime's own.</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } named && File.Exists(named)) return named;
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var host = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet"));
        return File.Exists(host) ? host : "dotnet";
    }
}
