using EPiServer;
using EPiServer.Core;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary>
/// CMS 13 remembers a GUID it didn't find for a minute (<c>PermanentLinkMapper</c>'s <c>pl:&lt;guid&gt;</c> entry). CMS content
/// registers its GUID when it is saved, which hides that; Commerce 15 doesn't for catalog content, so content made with a
/// GUID that was looked up first (as <c>create</c> with a fixed GUID does) can't be found by it for up to a minute. The
/// GUID's miss is cleared when catalog content is created. Nothing to do on CMS 12, which keeps no misses.
/// </summary>
internal static class CatalogGuids
{
    public static void Start(IServiceProvider services)
    {
#if CMS13
        if (services.GetService<IContentEvents>() is not { } events || services.GetService<EPiServer.Web.Internal.PermanentLinkMapper>() is not { } mapper)
        {
            return;
        }
        events.CreatedContent += (_, e) =>
        {
            if (e.Content is { } content && string.Equals(content.ContentLink?.ProviderName, CatalogRefSyntax.Provider, StringComparison.Ordinal))
            {
                mapper.ClearItemFromCache(content.ContentGuid, raiseEvent: false);
            }
        };
#else
        _ = services;
#endif
    }
}
