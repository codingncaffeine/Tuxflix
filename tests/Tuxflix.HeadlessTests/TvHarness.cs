using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.Tv;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views;
using Tuxflix.Core;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The window on the demo library for a test, in the TV interface unless asked otherwise, with a
/// throwaway profile, no keyring, no sound and nothing reported to a server. Runs on
/// <see cref="HeadlessApp"/>'s thread.
/// </summary>
internal sealed class TvHarness : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "tv-" + Guid.NewGuid().ToString("N")[..8]);

    private TvHarness(bool tv, FakePadSource? pad)
    {
        Paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        Paths.EnsureCreated();
        Settings = SettingsStore.Load(Paths.SettingsFile);
        Shell = new ShellViewModel(Settings, Paths)
        {
            Keyring = new NoSecrets(),
            ReportsPlayback = false,
            Silent = true,
            OpenUrl = _ => { },
            IsTv = tv,
        };

        MainWindow? window = null;
        if (pad is not null)
        {
            Shell.Gamepads = new GamepadInput(() => pad, GamepadMap.Standard, input =>
                Dispatcher.UIThread.Post(() => window?.Tv?.Handle(input), DispatcherPriority.Input));
        }

        Window = window = new MainWindow(Shell, Settings) { Width = 1920, Height = 1080 };
        Window.Show();
        Shell.Gamepads?.Start();
    }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public ShellViewModel Shell { get; }

    public MainWindow Window { get; }

    public TvShell Frame => Window.TvFrame ?? throw new InvalidOperationException("The TV interface is not showing.");

    /// <summary>What has focus in the window.</summary>
    public Control? Focused => FocusEngine.Focused((Control)Window.Content!);

    /// <summary>The demo home, in the TV interface, with focus placed on its first tile.</summary>
    public static async Task<TvHarness> OpenHomeAsync(bool tv = true, FakePadSource? pad = null)
    {
        var harness = new TvHarness(tv, pad);
        harness.Shell.Start(demo: true);
        await harness.UntilAsync(() => harness.Shell.Router.Current is HomePageViewModel { IsLoading: false, Shelves.Count: > 1 }, "the demo home");
        if (tv) await harness.UntilAsync(() => harness.Focused?.DataContext is MediaTileViewModel, "focus on the first tile");
        return harness;
    }

    /// <summary>Turns the dispatcher, the layout and the render clock until <paramref name="condition"/> holds.</summary>
    public async Task UntilAsync(Func<bool> condition, string what, double seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail($"{what}: not within {seconds} s; the page is {Shell.Router.Current?.GetType().Name}, focus on {Describe(Focused)}");
            Pump();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Pump();
    }

    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A key pressed and let go on the keyboard.</summary>
    public void Key(Key key)
    {
        Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Pump();
    }

    /// <summary>An action as a controller would send it, straight to the window.</summary>
    public void Act(TvAction action, TvPhase phase = TvPhase.Press, float strength = 1)
    {
        Window.Tv!.Handle(new TvInput(action, phase, strength));
        Pump();
    }

    public static string Describe(Control? control) => control switch
    {
        null => "nothing",
        { DataContext: MediaTileViewModel tile } => $"the tile {tile.Title}",
        _ => $"{control.GetType().Name} {control.Name} ({control.DataContext?.GetType().Name})",
    };

    public T Find<T>()
        where T : Control =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault() ?? throw new InvalidOperationException($"No {typeof(T).Name} is showing.");

    public void Dispose()
    {
        Shell.Router.Current?.Deactivate();
        Window.Close();
        Shell.Gamepads?.Dispose();
        Shell.Session?.Dispose();
        Settings.Flush(TimeSpan.FromSeconds(3));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test folder under the temporary directory; nothing else lives in it.
        }
    }

    /// <summary>A keyring with nothing in it that keeps nothing: the real one is the user's.</summary>
    private sealed class NoSecrets : ISecretStore
    {
        public Task<string?> LookupAsync(string account) => Task.FromResult<string?>(null);

        public Task<bool> StoreAsync(string account, string label, string secret) => Task.FromResult(false);

        public Task ClearAsync(string account) => Task.CompletedTask;
    }
}
