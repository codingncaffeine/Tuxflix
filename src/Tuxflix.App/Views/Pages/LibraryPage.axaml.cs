using Avalonia.Controls;
using Avalonia.Interactivity;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    private LibraryPageViewModel? Page => DataContext as LibraryPageViewModel;

    /// <summary>The orders the server offers, the current one ticked; choosing it again turns it round.</summary>
    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (Page is not { } page || sender is not Control button) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var sort in page.Sorts)
        {
            menu.Items.Add(new MenuItem
            {
                Header = sort.Title,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = sort.IsSelected,
                Command = sort.ChooseCommand,
            });
        }

        menu.ShowAt(button);
    }

    /// <summary>The switches first (unwatched, HDR), then a submenu of values for each list (genre, decade).</summary>
    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (Page is not { } page || sender is not Control button) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var toggles = page.Filters.Where(f => f.IsToggle).ToList();
        foreach (var filter in toggles)
        {
            menu.Items.Add(new MenuItem { Header = filter.Title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = filter.IsOn, Command = filter.ToggleCommand });
        }

        var lists = page.Filters.Where(f => !f.IsToggle && f.HasValues).ToList();
        if (toggles.Count > 0 && lists.Count > 0) menu.Items.Add(new Separator());
        foreach (var filter in lists)
        {
            var submenu = new MenuItem { Header = filter.Title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = filter.IsOn };
            foreach (var value in filter.Values)
            {
                submenu.Items.Add(new MenuItem { Header = value.Title, ToggleType = MenuItemToggleType.Radio, IsChecked = value.IsSelected, Command = value.ChooseCommand });
            }

            menu.Items.Add(submenu);
        }

        if (page.HasActiveFilters)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Clear all filters", Command = page.ClearFiltersCommand });
        }

        menu.ShowAt(button);
    }
}
