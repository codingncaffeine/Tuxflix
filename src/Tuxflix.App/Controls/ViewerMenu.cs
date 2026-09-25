using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Controls;

/// <summary>
/// The viewer's own menu on anything that shows an item: watched or not, and into a playlist.
/// </summary>
/// <remarks>
/// <c>ui:ViewerMenu.For="{Binding}"</c> gives a control the menu on a right click (tiles, rows);
/// <c>ui:ViewerMenu.Playlists="{Binding}"</c> gives a button the playlist menu on a click. The
/// menus are built each time they open, from the item's state and the playlists as last read
/// (read again as the menu opens), so a recycled tile never shows another item's menu.
/// </remarks>
public static class ViewerMenu
{
    public static readonly AttachedProperty<IViewerItem?> ForProperty =
        AvaloniaProperty.RegisterAttached<Control, IViewerItem?>("For", typeof(ViewerMenu));

    public static readonly AttachedProperty<IViewerItem?> PlaylistsProperty =
        AvaloniaProperty.RegisterAttached<Button, IViewerItem?>("Playlists", typeof(ViewerMenu));

    static ViewerMenu()
    {
        ForProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.ContextFlyout is not null) return;
            var menu = new MenuFlyout();
            menu.Opening += (_, _) => Fill(menu, GetFor(control), whole: true);
            control.ContextFlyout = menu;
        });
        PlaylistsProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            if (button.Flyout is not null) return;
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            menu.Opening += (_, _) => Fill(menu, GetPlaylists(button), whole: false);
            button.Flyout = menu;
        });
    }

    public static IViewerItem? GetFor(Control control) => control?.GetValue(ForProperty);

    public static void SetFor(Control control, IViewerItem? value) => control?.SetValue(ForProperty, value);

    public static IViewerItem? GetPlaylists(Button button) => button?.GetValue(PlaylistsProperty);

    public static void SetPlaylists(Button button, IViewerItem? value) => button?.SetValue(PlaylistsProperty, value);

    private static void Fill(MenuFlyout menu, IViewerItem? target, bool whole)
    {
        menu.Items.Clear();
        if (target is null) return;
        foreach (var entry in Entries(target, whole, menu.Target)) menu.Items.Add(Build(entry));

        // The playlists as the server has them now, for the next time the menu opens.
        target.Shell.Viewer?.PlaylistsEdited();
    }

    /// <summary>
    /// The lines of the viewer's menu for an item: watched or not (the whole menu only), then the
    /// playlists of the item's kind that are not smart, alphabetically, after "New playlist…".
    /// </summary>
    /// <param name="whole">The whole menu, with the playlists in a submenu; else the playlists alone.</param>
    /// <param name="anchor">What a new playlist's name is asked beside.</param>
    public static IReadOnlyList<MenuEntry> Entries(IViewerItem target, bool whole, Control? anchor)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Shell.Viewer is not { } viewer) return [];
        var item = target.Item;
        var entries = new List<MenuEntry>();
        if (whole && item.Type is "movie" or "episode" or "show" or "season")
        {
            var watched = target.State.IsWatched;
            entries.Add(new MenuEntry(watched ? "Mark as unwatched" : "Mark as watched", () => _ = viewer.SetWatchedAsync(item, !watched)));
        }

        if (ViewerState.PlaylistTypeOf(item) is not { } type) return entries;
        var playlists = new List<MenuEntry> { new("New playlist…", () => Dispatcher.UIThread.Post(() => AskForName(anchor, target))) };
        var fitting = viewer.Playlists.Where(p => !p.Smart && p.PlaylistType == type).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (fitting.Count > 0) playlists.Add(MenuEntry.Separator);
        foreach (var playlist in fitting)
        {
            playlists.Add(new MenuEntry(playlist.Title, async () =>
            {
                var added = await viewer.AddToPlaylistAsync(playlist, [item]);
                target.Shell.Live.Say(added ? $"Added to {playlist.Title}" : $"{playlist.Title} could not take it");
            }));
        }

        if (whole) entries.Add(new MenuEntry("Add to playlist", null, playlists));
        else entries.AddRange(playlists);
        return entries;
    }

    private static Control Build(MenuEntry entry)
    {
        if (entry.Header is null) return new Separator();
        var line = new MenuItem { Header = entry.Header };
        if (entry.Act is { } act) line.Click += (_, _) => act();
        foreach (var child in entry.Children ?? []) line.Items.Add(Build(child));
        return line;
    }

    /// <summary>A small panel by the item asking the new playlist's name.</summary>
    private static void AskForName(Control? anchor, IViewerItem target)
    {
        if (anchor is null || target.Shell.Viewer is not { } viewer) return;
        var name = new TextBox { PlaceholderText = "Playlist name", Width = 240, Text = string.Empty };
        AutomationPropertiesName(name, "Playlist name");
        var create = new Button { Content = "CREATE", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        if (Application.Current?.FindResource("GhostButton") is ControlTheme theme) create.Theme = theme;
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(4),
                Children =
                {
                    new TextBlock { Text = "NEW PLAYLIST", Classes = { "label" } },
                    name,
                    create,
                },
            },
        };

        async void Create()
        {
            var title = name.Text?.Trim();
            if (string.IsNullOrEmpty(title)) return;
            flyout.Hide();
            var made = await viewer.CreatePlaylistAsync(title, [target.Item]);
            target.Shell.Live.Say(made is null ? "The playlist could not be made" : $"Added to {made.Title}");
        }

        create.Click += (_, _) => Create();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Create();
        };
        flyout.Opened += (_, _) => name.Focus();
        flyout.ShowAt(anchor);
    }

    private static void AutomationPropertiesName(Control control, string name) =>
        Avalonia.Automation.AutomationProperties.SetName(control, name);
}

/// <summary>One line of a viewer menu: its words and what it does, a submenu, or a separator (no words).</summary>
public sealed record MenuEntry(string? Header, Action? Act = null, IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new(Header: null);
}
