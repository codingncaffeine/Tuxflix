using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

// The demo server's answers for the home screen and the item page's shelves.
public sealed partial class DemoPlexHandler
{
    private static readonly bool HomeRoutes = Add((handler, segments, query, request) => segments switch
    {
        ["hubs", "promoted"] => Json(new MediaContainer { Size = handler.Catalog.PromotedHubs().Count, Hub = [.. handler.Catalog.PromotedHubs()] }),
        ["library", "metadata", var key, "related"] => Json(new MediaContainer { Size = handler.Catalog.RelatedHubs(key).Count, Hub = [.. handler.Catalog.RelatedHubs(key)] }),
        ["library", "metadata", var key, "extras"] => handler.Catalog.Find(key) is { } item
            ? Json(new MediaContainer { Size = item.Extras?.Size ?? 0, Metadata = [.. item.Extras?.Metadata ?? []] })
            : NotFound(),
        ["library", "all"] => Json(new MediaContainer { Size = handler.Catalog.FindByGuid(query["guid"]).Count, Metadata = [.. handler.Catalog.FindByGuid(query["guid"])] }),
        ["library", "metadata", var key, "allLeaves"] => Json(new MediaContainer { Metadata = [.. handler.Catalog.ChildrenOf(key).SelectMany(season => handler.Catalog.ChildrenOf(season.RatingKey))] }),
        _ => null,
    });
}
