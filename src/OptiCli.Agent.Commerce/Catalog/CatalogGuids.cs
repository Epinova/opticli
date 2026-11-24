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
        // A risk: PermanentLinkMapper is in an Internal namespace, so a CMS 13 update may change or remove it (or stop
        // registering it as itself). Then the hook isn't attached and only this is lost: a catalog GUID a write looked up
        // before making the content stays "not found" for up to a minute after (the CLI passes catalog content to the agent
        // as id__CatalogContent, so its own writes don't depend on it). The warning says so in the site's output.
        if (services.GetService<IContentEvents>() is not { } events)
        {
            Console.Error.WriteLine("[opticli] Commerce agent: no IContentEvents, so a catalog GUID looked up before its content was made stays unknown for up to a minute.");
            return;
        }
        EPiServer.Web.Internal.PermanentLinkMapper? mapper;
        try
        {
            mapper = services.GetService<EPiServer.Web.Internal.PermanentLinkMapper>();
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or InvalidOperationException)
        {
            mapper = null;
            Console.Error.WriteLine($"[opticli] Commerce agent: {ex.GetType().Name} getting the CMS's PermanentLinkMapper.");
        }
        if (mapper is null)
        {
            Console.Error.WriteLine("[opticli] Commerce agent: the CMS's PermanentLinkMapper isn't there, so a catalog GUID looked up before its content was made stays unknown for up to a minute.");
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
