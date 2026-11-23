using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary><see cref="AgentRoutes.CommerceRefs"/>: catalog refs resolved, for the CLI, which can't find catalog content in the CMS database.</summary>
internal static class CatalogRefsEndpoint
{
    /// <summary>At most this many refs per request: a plan's worth.</summary>
    internal const int MaxRefs = 1000;

    public static CatalogRefsResponse Handle(AgentRequest request, CatalogRefsRequest body)
    {
        if (body.Refs is not { Count: > 0 } refs)
        {
            throw AgentException.Usage("refs is empty: give the refs to resolve.");
        }
        if (refs.Count > MaxRefs)
        {
            throw AgentException.Usage($"At most {MaxRefs} refs per request.");
        }
        var finder = new CatalogContentFinder(request.Context.RequestServices);
        return new CatalogRefsResponse(refs.Select(r => finder.Find(r) is { } content && request.Call.CanRead(content)
            ? finder.Describe(r, content)
            : new CatalogRef(r, false)).ToList());
    }
}

/// <summary><see cref="AgentRoutes.Commerce"/>: the site's Commerce version and catalogs.</summary>
internal static class CommerceStatusEndpoint
{
    public static CommerceInfo Handle(AgentRequest request)
    {
        var finder = new CatalogContentFinder(request.Context.RequestServices);
        var root = finder.Find(CatalogRefSyntax.Format(finder.RootLink.ID)) ?? throw new InvalidOperationException("Commerce has no catalog root.");
        return new CommerceInfo(
            Extensions.AgentExtensions.Commerce?.Version ?? "unknown",
            CatalogRefSyntax.Format(root.ContentLink.ID),
            root.ContentGuid,
            finder.Catalogs().Select(c => new CatalogSummary(CatalogRefSyntax.Format(c.ContentLink.ID), c.ContentGuid, c.Name)).ToList());
    }
}
