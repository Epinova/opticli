using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary>
/// <see cref="AgentRoutes.CommerceResolve"/>: which catalog content a URL shows. Catalog URLs are made by the site's catalog
/// routing (a partial router below the start page), which the CMS database knows nothing of, so the site routes them.
/// </summary>
internal static class CatalogUrlEndpoint
{
    public static CatalogUrl Handle(AgentRequest request)
    {
        var url = request.Context.Request.Query["url"].ToString();
        var resolved = ResolveUrlOperation.Run(request.Call, url);
        return WriteOutputProvider(resolved.Content.Ref) == CatalogRefSyntax.Provider
            ? new CatalogUrl(resolved.Content, resolved.Url)
            : throw AgentException.NotFound($"'{url}' shows CMS content ({resolved.Content.Ref}), not catalog content.", "opticli resolve reads CMS URLs from the database.");
    }

    /// <summary>The provider of a ref as the CMS prints it (<c>1_4_CatalogContent</c>); null for CMS content.</summary>
    private static string? WriteOutputProvider(string reference)
    {
        var parts = reference.Split('_');
        return parts.Length >= 3 && parts[^1].Length > 0 && !char.IsDigit(parts[^1][0]) ? parts[^1] : null;
    }
}
