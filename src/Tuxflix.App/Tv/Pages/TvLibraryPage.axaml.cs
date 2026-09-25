using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv.Pages;

/// <summary>A library's grid in the TV interface, with its order and an unwatched switch.</summary>
public partial class TvLibraryPage : UserControl, ITvPage
{
    private LibraryPageViewModel? _page;

    public TvLibraryPage()
    {
        InitializeComponent();
    }

    /// <summary>The first title, as Plex's TV apps open a library, rather than the buttons above it.</summary>
    public Control? InitialFocus() => Grid.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("tile"));

    public bool Handle(TvInput input) => false;

    private FilterViewModel? Unwatched => _page?.Filters.FirstOrDefault(f => f.IsToggle && f.Filter == "unwatched");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null) _page.Filters.CollectionChanged -= OnFilters;
        _page = DataContext as LibraryPageViewModel;
        if (_page is not null) _page.Filters.CollectionChanged += OnFilters;
        OnFilters(null, null);
    }

    /// <summary>The server offers an unwatched switch for films and series: it gets a button of its own.</summary>
    private void OnFilters(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        UnwatchedButton.IsVisible = Unwatched is not null;
        UnwatchedButton.Classes.Set("selected", Unwatched?.IsOn == true);
    }

    private async void OnUnwatched(object? sender, RoutedEventArgs e)
    {
        if (Unwatched is not { } filter) return;
        await filter.ToggleCommand.ExecuteAsync(null);
        UnwatchedButton.Classes.Set("selected", filter.IsOn);
    }

    /// <summary>The orders the server offers, the current one ticked; choosing it again turns it round.</summary>
    private void OnSort(object? sender, RoutedEventArgs e)
    {
        if (_page is null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var sort in _page.Sorts)
        {
            menu.Items.Add(new MenuItem
            {
                Header = sort.Title,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = sort.IsSelected,
                Command = sort.ChooseCommand,
                FontSize = 20,
                Padding = new Avalonia.Thickness(18, 12),
            });
        }

        menu.ShowAt(SortButton);
    }
}
