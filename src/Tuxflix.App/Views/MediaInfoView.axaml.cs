using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Tuxflix.App.ViewModels;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.Views;

public partial class MediaInfoView : UserControl
{
    public MediaInfoView() => InitializeComponent();

    /// <summary>Opens the panel beside <paramref name="anchor"/> and reads the item's full record behind it.</summary>
    public static MediaInfoViewModel? ShowBeside(Control? anchor, ServerSession session, MetadataItem item)
    {
        if (anchor is null) return null;
        var model = new MediaInfoViewModel(session, item);
        var flyout = new Flyout { Placement = PlacementMode.Right, Content = new MediaInfoView { DataContext = model } };
        flyout.ShowAt(anchor);
        _ = model.LoadAsync();
        return model;
    }
}
