using EPiServer.Core;

namespace OptiCli.Cms.Content;

internal static class ContentVersions
{
    /// <summary>
    /// A version of the content <paramref name="link"/> names, from the same content provider: provider content (catalog
    /// content, <c>1__CatalogContent</c>) without its provider's name would be the CMS's content of that id.
    /// </summary>
    public static ContentReference Of(ContentReference link, int versionId) => new(link.ID, versionId, link.ProviderName);
}
