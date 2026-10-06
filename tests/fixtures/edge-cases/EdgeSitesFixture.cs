// Copied into a CMS 12 edge-case site by setup.sh, beside EdgeCasesFixture.cs: the nested site and the three sites the
// site host tests change, as site definitions. On CMS 13, Cms13Fixture.cs makes them as applications instead.
using System.Globalization;
using EPiServer.ServiceLocation;
using EPiServer.Web;

namespace OptiCliEdgeCases;

public partial class EdgeCasesSetup
{
    static partial void EnsureSites(IServiceProvider locate, IContentRepository content)
    {
        var sites = locate.GetInstance<ISiteDefinitionRepository>();
        if (Find(content, NestedStart) is { } nested)
        {
            EnsureNestedSite(sites, nested);
        }
        if (Find(content, HostsStartA) is { } hostsA)
        {
            EnsureSite(sites, HostsSiteA, hostsA, "https://hosts-a.localhost/",
            [
                new HostDefinition { Name = "hosts-a.localhost", Type = HostDefinitionType.Primary },
                new HostDefinition { Name = "sv.hosts-a.example", Type = HostDefinitionType.Primary, Language = CultureInfo.GetCultureInfo("sv") },
                new HostDefinition { Name = "edit.hosts-a.localhost", Type = HostDefinitionType.Edit },
            ]);
        }
        if (Find(content, HostsStartB) is { } hostsB)
        {
            EnsureSite(sites, HostsSiteB, hostsB, "https://hosts-b.localhost/", [new HostDefinition { Name = "hosts-b.localhost", Type = HostDefinitionType.Primary }]);
        }
        if (Find(content, HostsStartC) is { } hostsC)
        {
            var english = CultureInfo.GetCultureInfo("en");
            EnsureSite(sites, HostsSiteC, hostsC, "https://hosts-c.localhost/",
            [
                new HostDefinition { Name = "hosts-c.localhost", Type = HostDefinitionType.Primary, Language = english, UseSecureConnection = true },
                new HostDefinition { Name = "alt.hosts-c.localhost", Language = english, UseSecureConnection = true },
            ]);
        }
    }

    private static void EnsureNestedSite(ISiteDefinitionRepository sites, ContentReference start)
    {
        if (sites.List().Any(s => s.Name == NestedSiteName))
        {
            return;
        }
        var site = new SiteDefinition
        {
            Name = NestedSiteName,
            StartPage = start,
            SiteUrl = new Uri($"http://{NestedHost}/"),
            Hosts = [new HostDefinition { Name = NestedHost }],
        };
        sites.Save(site);
        Console.Error.WriteLine($"[edge-cases] created site '{NestedSiteName}' with start page {start}");
    }

    private static void EnsureSite(ISiteDefinitionRepository sites, string name, ContentReference start, string url, IList<HostDefinition> hosts)
    {
        if (sites.List().Any(s => s.Name == name))
        {
            return;
        }
        sites.Save(new SiteDefinition { Name = name, StartPage = start, SiteUrl = new Uri(url), Hosts = hosts });
        Console.Error.WriteLine($"[edge-cases] created site '{name}' with start page {start}");
    }
}
