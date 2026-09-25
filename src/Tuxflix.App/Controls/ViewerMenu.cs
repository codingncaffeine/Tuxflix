using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Controls;

/// <summary>
/// The menu on anything that shows an item: what it plays, the viewer's marks and keepsakes, and
/// where it belongs (<see cref="ItemMenu"/>), or a menu of the thing's own.
/// </summary>
/// <remarks>
/// <c>ui:ViewerMenu.For="{Binding}"</c> gives a control the menu on a right click or the Menu key
/// (tiles, rows): an <see cref="IViewerItem"/> gets the item's menu, an <see cref="IMenuSource"/>
/// its own. <c>ui:ViewerMenu.Playlists="{Binding}"</c> gives a button the playlist menu on a click.
/// The menus are built each time they open, from the item's state and the playlists as last read
/// (read again as the menu opens), so a recycled tile never shows another item's menu.
/// </remarks>
public static class ViewerMenu
{
    public static readonly AttachedProperty<object?> ForProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("For", typeof(ViewerMenu));

    public static readonly AttachedProperty<IViewerItem?> PlaylistsProperty =
        AvaloniaProperty.RegisterAttached<Button, IViewerItem?>("Playlists", typeof(ViewerMenu));

    static ViewerMenu()
    {
        ForProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.ContextFlyout is not null) return;
            var menu = new MenuFlyout();
            menu.Opening += (_, e) => Fill(menu, GetFor(control), e);
            control.ContextFlyout = menu;
        });
        SubmenuProperty.Changed.AddClassHandler<MenuItem>((item, _) => HookSubmenu(item));
        PlaylistsProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            if (button.Flyout is not null) return;
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            menu.Opening += (_, e) => Fill(menu, GetPlaylists(button) is { } target ? new PlaylistsOnly(target) : null, e);
            button.Flyout = menu;
        });
    }

    /// <summary>A menu item of an existing menu that opens the playlists for an item: a track row's "Add to playlist".</summary>
    public static readonly AttachedProperty<IViewerItem?> SubmenuProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, IViewerItem?>("Submenu", typeof(ViewerMenu));

    public static IViewerItem? GetSubmenu(MenuItem item) => item?.GetValue(SubmenuProperty);

    public static void SetSubmenu(MenuItem item, IViewerItem? value) => item?.SetValue(SubmenuProperty, value);

    // A placeholder makes the item open as a submenu; the real lines are made as it opens.
    private static void HookSubmenu(MenuItem item)
    {
        if (item.Items.Count > 0) return;
        item.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
        item.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, item) || GetSubmenu(item) is not { } target) return;
            var anchor = item.GetLogicalAncestors().OfType<Popup>().FirstOrDefault()?.PlacementTarget;
            item.Items.Clear();
            foreach (var entry in PlaylistEntries(target, anchor)) item.Items.Add(Build(entry));
            target.Shell.Viewer?.PlaylistsEdited();
        };
    }

    public static object? GetFor(Control control) => control?.GetValue(ForProperty);

    public static void SetFor(Control control, object? value) => control?.SetValue(ForProperty, value);

    public static IViewerItem? GetPlaylists(Button button) => button?.GetValue(PlaylistsProperty);

    public static void SetPlaylists(Button button, IViewerItem? value) => button?.SetValue(PlaylistsProperty, value);

    private static void Fill(MenuFlyout menu, object? target, EventArgs opening)
    {
        menu.Items.Clear();
        foreach (var entry in Entries(target, menu.Target)) menu.Items.Add(Build(entry));

        // Nothing to offer: no empty box opens.
        if (menu.Items.Count == 0 && opening is CancelEventArgs cancel) cancel.Cancel = true;

        // The playlists as the server has them now, for the next time the menu opens.
        if (target is IViewerItem item) item.Shell.Viewer?.PlaylistsEdited();
        else if (target is PlaylistsOnly only) only.Target.Shell.Viewer?.PlaylistsEdited();
    }

    /// <summary>The lines of the menu for <paramref name="target"/>: the item's menu, a menu of its own, or none.</summary>
    /// <param name="anchor">What a panel the menu opens (a new playlist's name) is shown beside.</param>
    public static IReadOnlyList<MenuEntry> Entries(object? target, Control? anchor) => target switch
    {
        IMenuSource own => own.MenuEntries(),
        IViewerItem item => ItemMenu.For(item, anchor),
        PlaylistsOnly only => PlaylistEntries(only.Target, anchor),
        _ => [],
    };

    /// <summary>
    /// The playlists an item can go in: "New playlist…" (not in the TV interface, which has no
    /// keyboard to name one with), then the viewer's playlists of the item's kind that are not
    /// smart, alphabetically. None for a kind no playlist takes.
    /// </summary>
    /// <param name="anchor">What a new playlist's name is asked beside.</param>
    public static IReadOnlyList<MenuEntry> PlaylistEntries(IViewerItem target, Control? anchor)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Shell.Viewer is not { } viewer || ViewerState.PlaylistTypeOf(target.Item) is not { } type) return [];
        var item = target.Item;
        var playlists = new List<MenuEntry>();
        if (!target.Shell.IsTv) playlists.Add(new("New playlist…", () => Dispatcher.UIThread.Post(() => AskForName(anchor, target))));
        var fitting = viewer.Playlists.Where(p => !p.Smart && p.PlaylistType == type).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (fitting.Count > 0 && playlists.Count > 0) playlists.Add(MenuEntry.Separator);
        foreach (var playlist in fitting)
        {
            playlists.Add(new MenuEntry(playlist.Title, async () =>
            {
                var added = await viewer.AddToPlaylistAsync(playlist, [item]);
                target.Shell.Live.Say(added ? $"Added to {playlist.Title}" : $"{playlist.Title} could not take it");
            }));
        }

        return playlists;
    }

    private static Control Build(MenuEntry entry)
    {
        if (entry.Header is null) return new Separator();
        var line = new MenuItem();
        Show(line, entry);
        line.Click += (_, e) =>
        {
            // A line with a submenu opens it; only its own lines act.
            if (ReferenceEquals(e.Source, line) && line.Tag is MenuEntry { Act: { } act }) act();
        };
        foreach (var child in entry.Children ?? []) line.Items.Add(Build(child));
        if (entry.Later is { } later) _ = BecomeAsync(line, later);
        return line;
    }

    private static void Show(MenuItem line, MenuEntry entry)
    {
        line.Tag = entry;
        line.Header = entry.Header;
        line.IsEnabled = entry.IsEnabled;
        line.ToggleType = entry.IsChecked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None;
        line.IsChecked = entry.IsChecked;
    }

    /// <summary>A line whose words wait on a request takes them once they come, while the menu is up or not.</summary>
    private static async Task BecomeAsync(MenuItem line, Task<MenuEntry?> later)
    {
        try
        {
            if (await later is { } entry) Show(line, entry);
        }
        catch (Exception ex)
        {
            Log.Warn("A menu line could not be filled in.", ex);
        }
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

    /// <summary>A button's playlist menu: the playlists alone.</summary>
    private sealed record PlaylistsOnly(IViewerItem Target);
}

/// <summary>Something with a menu of its own rather than an item's: a Watchlist title, a photo.</summary>
public interface IMenuSource
{
    IReadOnlyList<MenuEntry> MenuEntries();
}

/// <summary>One line of a menu: its words and what it does, a submenu, or a separator (no words).</summary>
public sealed record MenuEntry(string? Header, Action? Act = null, IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new(Header: null);

    /// <summary>Shown with a tick: the choice in force (the viewer's rating).</summary>
    public bool IsChecked { get; init; }

    /// <summary>Greyed out: a line that says something and does nothing yet.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>What the line becomes once a request answers (whether a title is on the Watchlist); null keeps it.</summary>
    public Task<MenuEntry?>? Later { get; init; }
}
