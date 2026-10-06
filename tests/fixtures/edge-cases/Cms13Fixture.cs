// Copied into a CMS 13 edge-case site by setup.sh (not into a CMS 12 one), beside EdgeCasesFixture.cs:
// - CMS 12 registers visitor groups with AddCms(); CMS 13 only when the site asks for them, which the CMS 13 Alloy
//   template doesn't. The edge-case plan personalizes content with a visitor group, so this turns them on as a site that
//   uses personalization does.
// - No nested site or hosts sites, which EdgeSitesFixture.cs makes on CMS 12 (see EnsureSites below).
using EPiServer.DependencyInjection;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;

namespace OptiCliEdgeCases;

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class EdgeCasesCms13Services : IConfigurableModule
{
    public void ConfigureContainer(ServiceConfigurationContext context) => context.Services.AddVisitorGroupsMvc();

    public void Initialize(InitializationEngine context)
    {
    }

    public void Uninitialize(InitializationEngine context)
    {
    }
}

public partial class EdgeCasesSetup
{
    /// <summary>
    /// None yet. CMS 13 refuses an application whose start page is below another's ("Application cannot have overlapping
    /// entry points"), and the plan puts the nested site's and the hosts sites' start pages below the Alloy start page. The
    /// nested site has no CMS 13 equivalent; the hosts sites need start pages of their own outside it first.
    /// </summary>
    static partial void EnsureSites(IServiceProvider locate, IContentRepository content)
    {
    }
}
