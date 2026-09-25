using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;

namespace Tuxflix.App;

public sealed class App : Application
{
    /// <summary>Set by <see cref="Program.Main"/> before the platform starts; null under test.</summary>
    internal static AppLaunch? Launch { get; set; }

    private static PosixSignalRegistration? _terminate;

    /// <summary>Done once the downloads have stopped and their list is written; the process waits for it at exit.</summary>
    internal static Task DownloadsStopped { get; private set; } = Task.CompletedTask;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime probe && Launch is { Options.ProbeVideo: true })
        {
            probe.MainWindow = Player.VideoProbe.Create();
            probe.MainWindow.Opened += (_, _) => WindowingBackend.WindowOpened(probe.MainWindow);
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Launch is { } launch)
        {
            var settings = launch.Settings;
            var probing = launch.Options.ProbePlayer || launch.Options.ProbeClassic || launch.Options.ProbeGrid || launch.Options.ProbeMusic;
            var shell = new ShellViewModel(settings, launch.Paths)
            {
                // A probe may borrow a copied profile's sign-in: it reads the keyring and never
                // writes it, plays in silence, and leaves the server's record of progress alone.
                Keyring = probing ? new ReadOnlySecretStore(new Keyring()) : new Keyring(),
                ReportsPlayback = !probing,
                Silent = probing,
            };
            var window = new MainWindow(shell, settings);
            desktop.MainWindow = window;

            // The desktop's media controls and media keys; a probe run stays off the desktop's bus,
            // except the music probe, which is there to test them.
            if (!probing || launch.Options.ProbeMusic) shell.UseMediaControls(new Platform.MprisService(null, window.BringForward, window.Close));
            desktop.ShutdownRequested += (_, _) =>
            {
                // A player open at exit tells the server where it stopped, as leaving it would.
                shell.Router.Current?.Deactivate();
                shell.StopDownloads();
                DownloadsStopped = shell.DownloadsStopped;
                settings.Save();
            };

            if (launch.Instance is { } instance)
            {
                // A second launch hands its arguments here: bring the window forward.
                instance.ArgumentsReceived += _ => Dispatcher.UIThread.Post(window.BringForward);
            }

            // Opened comes again whenever the window is shown after hiding (the compact player hides
            // it); the application starts once.
            void Started(object? sender, EventArgs e)
            {
                window.Opened -= Started;
                WindowingBackend.WindowOpened(window);
                shell.Start(launch.Options.Demo);
                if (launch.Options.ProbePlayer) Player.PlayerProbe.Run(shell, window);
                if (launch.Options.ProbeClassic) Classic.ClassicProbe.Run(shell, window);
                if (launch.Options.ProbeGrid) Probes.GridProbe.Run(shell, window);
                if (launch.Options.ProbeMusic) Probes.MusicProbe.Run(shell, window);
            }

            window.Opened += Started;

            // A session logout or `kill` asks politely with SIGTERM: close the window the normal way
            // so its placement and settings are saved, instead of vanishing mid-flight.
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            });
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Crash("an unhandled exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("A background task failed without anybody waiting for it.", e.Exception);
            e.SetObserved();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
