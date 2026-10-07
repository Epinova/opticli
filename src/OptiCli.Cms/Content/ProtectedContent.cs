using EPiServer.Core;

namespace OptiCli.Cms.Content;

/// <summary>
/// The structural roots and every site's start page and asset roots: moving, deleting or locking those down breaks a
/// whole site or the edit UI, even when it can be undone.
/// </summary>
internal static class ProtectedContent
{
    public static IReadOnlyList<ContentReference> Links(CmsCall call)
    {
        var links = new List<ContentReference>
        {
            ContentReference.RootPage, ContentReference.WasteBasket, ContentReference.GlobalBlockFolder,
        };
        links.AddRange(Compat.CmsSites.Roots(call).OfType<ContentReference>());
        links.RemoveAll(ContentReference.IsNullOrEmpty);
        return links;
    }

    public static bool Contains(IEnumerable<ContentReference> links, ContentReference link) =>
        links.Any(p => p.CompareToIgnoreWorkID(link));
}
