using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Tuxflix.App.Controls;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views.Tiles;
using Tuxflix.Core;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>A tile's menu from the keyboard, in a window of its own.</summary>
[Collection(nameof(KeyboardFocus))]
public sealed class ItemMenuKeyboardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "menukeys-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SettingsStore _settings;
    private readonly ShellViewModel _shell;

    public ItemMenuKeyboardTests()
    {
        HeadlessSkia.Ensure();
        var paths = AppPaths.Resolve(_root, Environment.GetEnvironmentVariable);
        paths.EnsureCreated();
        _settings = SettingsStore.Load(paths.SettingsFile);
        _shell = new ShellViewModel(_settings, paths) { NotificationSources = _ => new FakeNotifications(), Silent = true, ReportsPlayback = false };
        _shell.OpenDemo();
    }

    public void Dispose()
    {
        _shell.Session?.Dispose();
        TestFolder.Delete(_root, _settings);
    }

    [Fact]
    public Task TheMenuOpensOnTheMenuKeyAndShiftF10AndNotAtAllWithNothingInIt() => HeadlessApp.Run(async () =>
    {
        var session = _shell.Session!;
        var film = (await session.Client.GetMetadataAsync(session.Demo!.Movies[8].RatingKey, CancellationToken.None))!;
        var poster = new PosterTile { DataContext = new PosterTileViewModel(_shell, film) };
        var window = new Window { Width = 400, Height = 400, Content = poster };
        window.Show();
        try
        {
            var button = poster.GetLogicalChildren().OfType<Button>().Single();
            var flyout = Assert.IsType<MenuFlyout>(button.ContextFlyout);

            // Every request that reaches the menu is counted, so a check that nothing opened is
            // known to have been asked.
            var asked = 0;
            flyout.Opening += (_, _) => asked++;
            void Press(Key key, RawInputModifiers modifiers)
            {
                Assert.True(button.Focus(NavigationMethod.Tab));
                window.KeyPress(key, modifiers, PhysicalKey.None, null);
                Assert.True(button.IsFocused, $"{key} took focus off the tile, where no check could see the menu");
                window.KeyRelease(key, modifiers, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
            }

            foreach (var (key, modifiers) in new[] { (Key.Apps, RawInputModifiers.None), (Key.F10, RawInputModifiers.Shift) })
            {
                var before = asked;
                Press(key, modifiers);
                Assert.Equal(before + 1, asked);
                Assert.True(flyout.IsOpen, $"{key} did not open the menu");
                Assert.Equal("Play", flyout.Items.OfType<MenuItem>().First().Header);
                flyout.Hide();
                Dispatcher.UIThread.RunJobs();
            }

            // A plain F10 is not the menu's key: with focus on the tile, the menu is not asked.
            Press(Key.F10, RawInputModifiers.None);
            Assert.Equal(2, asked);
            Assert.False(flyout.IsOpen);

            // Nothing to offer, nothing opens (an album of photos only opens, which its click
            // does): the menu is asked and stays shut.
            ViewerMenu.SetFor(button, new NothingToOffer());
            Press(Key.Apps, RawInputModifiers.None);
            Assert.Equal(3, asked);
            Assert.False(flyout.IsOpen);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class NothingToOffer : IMenuSource
    {
        public IReadOnlyList<MenuEntry> MenuEntries() => [];
    }
}
