namespace Tuxflix.App.ViewModels;

// A library on screen lists itself again when the server says it changed: a scan added films, a
// match renamed one. The sort, the filters and the view stay as they were.
public sealed partial class LibraryPageViewModel : ILiveRefresh
{
    public void OnLibraryChanged(IReadOnlySet<string> sections, IReadOnlySet<string> items)
    {
        if (IsLoading || (sections.Count > 0 && !sections.Contains(Section.Key))) return;
        _ = ListSafelyAsync();
    }
}
