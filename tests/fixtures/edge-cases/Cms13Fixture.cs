// Copied into a CMS 13 edge-case site by setup.sh (not into a CMS 12 one), beside EdgeCasesFixture.cs:
// - CMS 12 registers visitor groups with AddCms(); CMS 13 only when the site asks for them, which the CMS 13 Alloy
//   template doesn't. The edge-case plan personalizes content with a visitor group, so this turns them on as a site that
//   uses personalization does.
// - The three hosts sites the site host tests change, as applications (EdgeSitesFixture.cs makes them as site definitions
//   on CMS 12), with start pages of their own below the root. No nested site: see EnsureSites below.
using System.Globalization;
using EPiServer.Applications;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.DependencyInjection;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
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
    /// The start pages of the CMS 13 hosts sites: below the root, not the plan's (below the Alloy start page), since CMS 13
    /// refuses an application whose entry point is below another's ("Application cannot have overlapping entry points").
    /// </summary>
    public static readonly Guid HostsStartA13 = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e14");
    public static readonly Guid HostsStartB13 = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e15");
    public static readonly Guid HostsStartC13 = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e16");

    /// <summary>
    /// The three hosts sites as applications, in the shape of CMS 12's (EdgeSitesFixture.cs), with https on every host as
    /// their CMS 12 SiteUrl has it (CMS 13 has no unset). Application names differ from the display names the tests use,
    /// as an upgraded site's do (<c>Site_&lt;GUID&gt;</c>). No nested site: CMS 13 refuses overlapping entry points, so it
    /// has no equivalent.
    /// </summary>
    static partial void EnsureSites(IServiceProvider locate, IContentRepository content)
    {
        var applications = locate.GetInstance<IApplicationRepository>();
        var english = CultureInfo.GetCultureInfo("en");
        EnsureApplication(applications, "edgeHostsA", HostsSiteA, EnsureStartPage(locate, content, HostsStartA13, "Hosts site A (CMS 13)"),
        [
            new ApplicationHost("hosts-a.localhost") { Type = ApplicationHostType.Primary, PreferredUrlScheme = UrlScheme.Https },
            new ApplicationHost("sv.hosts-a.example") { Type = ApplicationHostType.Primary, Locale = CultureInfo.GetCultureInfo("sv"), PreferredUrlScheme = UrlScheme.Https },
            new ApplicationHost("edit.hosts-a.localhost") { Type = ApplicationHostType.Edit, PreferredUrlScheme = UrlScheme.Https },
        ]);
        EnsureApplication(applications, "edgeHostsB", HostsSiteB, EnsureStartPage(locate, content, HostsStartB13, "Hosts site B (CMS 13)"),
        [
            new ApplicationHost("hosts-b.localhost") { Type = ApplicationHostType.Primary, PreferredUrlScheme = UrlScheme.Https },
        ]);
        EnsureApplication(applications, "edgeHostsC", HostsSiteC, EnsureStartPage(locate, content, HostsStartC13, "Hosts site C (CMS 13)"),
        [
            new ApplicationHost("hosts-c.localhost") { Type = ApplicationHostType.Primary, Locale = english, PreferredUrlScheme = UrlScheme.Https },
            new ApplicationHost("alt.hosts-c.localhost") { Type = ApplicationHostType.Default, Locale = english, PreferredUrlScheme = UrlScheme.Https },
        ]);
    }

    /// <summary>
    /// A published StartPage below the root (Alloy's type, found by name: its namespace is the site's). A StartPage, as
    /// Alloy's layout reads its site's start page as one, so the tests can request the application's pages.
    /// </summary>
    private static ContentReference EnsureStartPage(IServiceProvider locate, IContentRepository content, Guid guid, string name)
    {
        if (Find(content, guid) is { } existing)
        {
            return existing;
        }
        var type = locate.GetInstance<IContentTypeRepository>().Load("StartPage")
            ?? throw new InvalidOperationException("The site has no StartPage type.");
        var page = content.GetDefault<PageData>(ContentReference.RootPage, type.ID);
        page.Name = name;
        page.ContentGuid = guid;
        var link = content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
        Console.Error.WriteLine($"[edge-cases] created page '{name}' ({link.ID}) below the root");
        return link.ToReferenceWithoutVersion();
    }

    private static void EnsureApplication(IApplicationRepository applications, string name, string displayName, ContentReference start, IList<ApplicationHost> hosts)
    {
        if (applications.Get(name) is not null)
        {
            return;
        }
        var application = new InProcessWebsite(name, start) { DisplayName = displayName };
        foreach (var host in hosts)
        {
            application.Hosts.Add(host);
        }
        applications.SaveAsync(application).GetAwaiter().GetResult();
        Console.Error.WriteLine($"[edge-cases] created application '{displayName}' ({name}) with start page {start}");
    }
}
