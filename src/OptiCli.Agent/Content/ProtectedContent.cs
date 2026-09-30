using EPiServer.Core;
using EPiServer.Web;

namespace OptiCli.Agent.Content;

/// <summary>
/// The structural roots and every site's start page and asset roots: moving, deleting or locking those down breaks a
/// whole site or the edit UI, even when it can be undone.
/// </summary>
internal static class ProtectedContent
{
    public static IReadOnlyList<ContentReference> Links(ISiteDefinitionRepository sites)
    {
        var links = new List<ContentReference>
        {
            ContentReference.RootPage, ContentReference.WasteBasket, ContentReference.GlobalBlockFolder,
        };
        foreach (var site in sites.List())
        {
            links.AddRange([site.StartPage, site.SiteAssetsRoot, site.GlobalAssetsRoot, site.ContentAssetsRoot]);
        }
        links.RemoveAll(ContentReference.IsNullOrEmpty);
        return links;
    }

    public static bool Contains(IEnumerable<ContentReference> links, ContentReference link) =>
        links.Any(p => p.CompareToIgnoreWorkID(link));
}
