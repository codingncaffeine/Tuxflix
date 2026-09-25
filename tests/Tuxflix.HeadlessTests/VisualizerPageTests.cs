using Avalonia.Controls;
using Avalonia.VisualTree;
using Tuxflix.App.Music;
using Tuxflix.App.ViewModels;
using Tuxflix.App.Views.Pages;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>The visualizer's page: its menu of modes by family, and the name of the mode showing.</summary>
[Collection(nameof(KeyboardFocus))]
public sealed class VisualizerPageTests
{
    public VisualizerPageTests() => HeadlessApp.Ensure();

    [Fact]
    public Task TheModeMenuListsEveryModeByFamilyWithTheOneShowingTickedAndChoosesOne() => HeadlessApp.Run(async () =>
    {
        using var app = await TvHarness.OpenHomeAsync(tv: false);
        app.Shell.ShowNowPlayingCommand.Execute(null);
        var now = Assert.IsType<NowPlayingPageViewModel>(app.Shell.Router.Current);
        now.ChooseMode(VisualizerMode.Plasma);
        now.SetVisualizer(true);
        NowPlayingPage? Page() => app.Window.GetVisualDescendants().OfType<NowPlayingPage>().FirstOrDefault(p => p.IsEffectivelyVisible);
        await app.UntilAsync(() => Page() is not null, "the visualizer page");

        var menu = new MenuFlyout();
        Page()!.FillModeMenu(menu);
        var families = menu.Items.Cast<MenuItem>().ToList();
        Assert.Equal(VisualizerModes.Families, families.Select(f => (string)f.Header!));
        foreach (var family in families)
        {
            var modes = family.Items.Cast<MenuItem>().ToList();
            Assert.Equal(VisualizerModes.All.Where(m => m.Family == (string)family.Header!).Select(m => m.Title), modes.Select(m => (string)m.Header!));
            Assert.All(modes, m => Assert.Equal(MenuItemToggleType.Radio, m.ToggleType));
        }

        var ticked = Assert.Single(families.SelectMany(f => f.Items.Cast<MenuItem>()), m => m.IsChecked);
        Assert.Equal("Plasma", ticked.Header);

        // Choosing one shows it, saves it, and names it where the controls show.
        var terrain = families.SelectMany(f => f.Items.Cast<MenuItem>()).Single(m => (string)m.Header! == "Terrain");
        terrain.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(VisualizerMode.Terrain, now.VisualizerMode);
        Assert.Equal("Terrain", app.Shell.Settings.Music.VisualizerMode);
        app.Pump();
        var caption = Page()!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Terrain", caption);
        Assert.Contains($"{VisualizerModes.OverTime}  ·  {now.PaletteName}", caption);
    });
}
