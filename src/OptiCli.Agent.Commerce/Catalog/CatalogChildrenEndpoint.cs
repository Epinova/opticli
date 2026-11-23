using System.Globalization;
using EPiServer;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Core;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary><see cref="AgentRoutes.CommerceChildren"/>: catalog content below catalog content, as the catalog UI lists it.</summary>
internal static class CatalogChildrenEndpoint
{
    internal const int DefaultLimit = 200;

    public static CatalogChildren Handle(AgentRequest request)
    {
        var query = request.Context.Request.Query;
        var limit = int.TryParse(query["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var given) && given > 0 ? given : DefaultLimit;
        var finder = new CatalogContentFinder(request.Context.RequestServices);
        var reference = query["ref"].ToString();
        var parent = finder.Require(string.IsNullOrWhiteSpace(reference) ? CatalogRefSyntax.Root : reference);
        var children = request.Service<IContentLoader>()
            .GetChildren<CatalogContentBase>(parent.ContentLink.ToReferenceWithoutVersion(), CatalogContentFinder.AnyLanguage())
            .Where(c => request.Call.CanRead(c))
            .ToList();
        return new CatalogChildren(
            finder.Describe(CatalogRefSyntax.Format(parent.ContentLink.ID), parent),
            children.Take(limit).Select(c => finder.Describe(CatalogRefSyntax.Format(c.ContentLink.ID), c)).ToList(),
            children.Count > limit);
    }
}
