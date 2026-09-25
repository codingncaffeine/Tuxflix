using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Tuxflix.App.Tv.Pages;

/// <summary>The controller map editor in the TV interface.</summary>
public partial class TvControllerPage : UserControl, ITvPage
{
    public TvControllerPage()
    {
        InitializeComponent();
    }

    /// <summary>The first action, rather than the button above the list.</summary>
    public Control? InitialFocus() => this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is ControllerRowViewModel);

    public bool Handle(TvInput input) => false;
}
