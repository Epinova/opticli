using EPiServer.Core;
using EPiServer.Web;
using EPiServer.Web.Routing;
#if CMS13
using EPiServer.Applications;
#endif

namespace OptiCli.Cms.Compat;

/// <summary>
/// The sites as the shared operations see them: CMS 12's site definitions, CMS 13's applications (whose site definition
/// API is an obsolete shim). Only what the operations need: start pages, asset roots and URLs.
/// </summary>
internal static class CmsSites
{
    /// <summary>Every site's start page (CMS 13: every routable application's entry point).</summary>
    public static IEnumerable<ContentReference> StartPages(CmsCall call)
    {
#if CMS13
        return Applications(call).Select(a => a.EntryPoint);
#else
        return call.Service<ISiteDefinitionRepository>().List().Select(s => s.StartPage);
#endif
    }

    /// <summary>
    /// Every site's start page and asset roots: its own assets folder, and the global and content assets folders (which
    /// CMS 12 names per site, and CMS 13 once, in <see cref="SystemDefinition"/>). Some may be empty.
    /// </summary>
    public static IEnumerable<ContentReference?> Roots(CmsCall call)
    {
#if CMS13
        var system = call.Service<SystemDefinition>();
        return Applications(call)
            .SelectMany(a => new[] { a.EntryPoint, (a as IResourceableApplication)?.AssetsRoot })
            .Concat([system.GlobalAssetsRoot, system.ContentAssetsRoot]);
#else
        return call.Service<ISiteDefinitionRepository>().List()
            .SelectMany(s => new[] { s.StartPage, s.SiteAssetsRoot, s.GlobalAssetsRoot, s.ContentAssetsRoot });
#endif
    }

    /// <summary>Every site's URL (CMS 12: SiteUrl; CMS 13: the application's, its first primary host, else its first default one).</summary>
    public static IEnumerable<Uri> Urls(CmsCall call)
    {
#if CMS13
        return Applications(call).Select(a => a.Url).OfType<Uri>();
#else
        return call.Service<ISiteDefinitionRepository>().List().Select(s => s.SiteUrl).OfType<Uri>();
#endif
    }

    /// <summary>
    /// Arguments that route a URL on a host no site has as the CMS does a request: to the site with the <c>*</c> host
    /// (CMS 13: the default application).
    /// </summary>
    public static RouteArguments FallbackRouting() => new()
    {
        ContextMode = ContextMode.Default,
#if CMS13
        MatchHost = HostMatching.Default,
#else
        MatchWildcardHost = true,
#endif
    };

#if CMS13
    private static IEnumerable<IRoutableApplication> Applications(CmsCall call) =>
        call.Service<IApplicationRepository>().List().OfType<IRoutableApplication>();
#endif
}
