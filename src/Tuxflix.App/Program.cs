using Avalonia;
using Avalonia.Media;
using Tuxflix.Core;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Settings;

namespace Tuxflix.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything opens a file. The first line is the one a script reads; the licence follows, as GNU tools print it.
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"Tuxflix {BuildInfo.Stamp}");
            Console.WriteLine("""
                Copyright (C) 2026 Tuxflix contributors
                License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>.
                This is free software: you are free to change and redistribute it.
                There is NO WARRANTY, to the extent permitted by law.
                """);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("""
                Usage: tuxflix [options]

                  --demo            Open the built-in demo library instead of a server.
                  --portable DIR    Keep settings, cache and logs in DIR instead of the XDG folders.
                  --version         Print the version, build time and licence, then exit.
                """);
            return 0;
        }

        var options = LaunchOptions.Parse(args);
        var paths = AppPaths.Resolve(options.PortableRoot, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();

        Log.Initialize(BuildInfo.Stamp, paths.Logs);
        if (paths.Portable) Log.Info($"Portable profile at {Path.GetDirectoryName(paths.Config)}");

        // The probe is a tool run, not a session: it never hands off to, or waits for, the app.
        using var instance = options.ProbeVideo || options.ProbePlayer || options.ProbeClassic || options.ProbeGrid || options.ProbeMusic ? null : new SingleInstance(paths.InstanceSocket, paths.InstanceLock);
        if (instance?.TryHandOff(args) == true)
        {
            Log.Info("Another Tuxflix is running on this profile; handed the launch to it.");
            return 0;
        }

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        var settings = SettingsStore.Load(paths.SettingsFile);
        App.Launch = new AppLaunch(paths, options, instance, settings);

        try
        {
            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return options.ProbeVideo ? Player.VideoProbe.ExitCode : options.ProbePlayer ? Player.PlayerProbe.ExitCode : options.ProbeClassic ? Classic.ClassicProbe.ExitCode : options.ProbeGrid ? Probes.GridProbe.ExitCode : options.ProbeMusic ? Probes.MusicProbe.ExitCode : code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Log.Crash("startup", ex));
            return 1;
        }
        finally
        {
            // The last saves (the window's place, the server used) reach the disk before the process ends.
            if (!settings.Flush(TimeSpan.FromSeconds(3))) Console.Error.WriteLine("Settings were still being written at exit.");
            Log.Flush(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        WindowingBackend.Apply(
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
                .LogToTrace());
}

/// <summary>What the command line asked for.</summary>
internal sealed record LaunchOptions(bool Demo, string? PortableRoot, bool ProbeVideo = false, bool ProbePlayer = false, bool ProbeClassic = false, bool ProbeGrid = false, bool ProbeMusic = false)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var demo = false;
        string? portable = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    demo = true;
                    break;
                case "--portable" when i + 1 < args.Count:
                    portable = args[++i];
                    break;
            }
        }

        return new LaunchOptions(demo, portable, args.Contains(Player.VideoProbe.Switch), args.Contains(Player.PlayerProbe.Switch), args.Contains(Classic.ClassicProbe.Switch), args.Contains(Probes.GridProbe.Switch), args.Contains(Probes.MusicProbe.Switch));
    }
}

/// <summary>Everything the application needs from the process that started it.</summary>
internal sealed record AppLaunch(AppPaths Paths, LaunchOptions Options, SingleInstance? Instance, SettingsStore Settings);
