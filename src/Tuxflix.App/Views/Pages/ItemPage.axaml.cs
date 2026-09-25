using Avalonia.Controls;
using Avalonia.Interactivity;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class ItemPage : UserControl
{
    public ItemPage()
    {
        InitializeComponent();
    }

    private void OnMediaInfo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ItemPageViewModel page && page.Shell.Session is { } session) MediaInfoView.ShowBeside(sender as Control, session, page.Item);
    }
}
