using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv;

/// <summary>
/// Which view shows each page in the TV interface: pages laid out for a room where there is one
/// (home, a library, a title, search, sign-in, the controller), the player as it is, and every
/// other page as the desktop draws it, enlarged for the distance and clear of the bar.
/// </summary>
internal sealed class TvPages : IDataTemplate
{
    /// <summary>How much larger a desktop page is drawn in the TV interface.</summary>
    public const double DesktopPageScale = 1.35;

    public static TvPages Template { get; } = new();

    public bool Match(object? data) => data is PageViewModel;

    public Control? Build(object? param) => param switch
    {
        HomePageViewModel => new Pages.TvHomePage(),
        LibraryPageViewModel => new Pages.TvLibraryPage(),
        ItemPageViewModel => new Pages.TvItemPage(),
        SearchPageViewModel => new Pages.TvSearchPage(),
        SignInPageViewModel => new Pages.TvSignInPage(),
        TvControllerPageViewModel => new Pages.TvControllerPage(),
        PlayerPageViewModel => new Views.Pages.PlayerPage(),
        _ => Enlarged(param),
    };

    private static Control? Enlarged(object? page)
    {
        var desktop = Application.Current?.DataTemplates.FirstOrDefault(t => t.Match(page))?.Build(page);
        if (desktop is null) return null;
        desktop.DataContext = page;
        return new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(DesktopPageScale, DesktopPageScale),
            Margin = new Thickness(40, 120, 40, 90),
            Child = desktop,
        };
    }
}
