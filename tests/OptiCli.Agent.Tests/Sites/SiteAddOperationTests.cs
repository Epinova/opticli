using OptiCli.Agent.Sites;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Sites;

/// <summary>How <c>sites add</c> types the hosts it is given and merges them into a site that exists, without a CMS.</summary>
public class SiteAddOperationTests
{
    private static readonly string[] Languages = ["en", "sv"];

    private static SiteAddRequest Request(params SiteAddHost[] hosts) => new() { Name = "Alloy", StartPage = 5, Hosts = hosts };

    [Fact]
    public void The_first_host_of_each_language_is_primary_and_the_rest_undefined()
    {
        var hosts = SiteAddOperation.Hosts(
            Request(new("alloy.localhost:5000"), new("www.alloy.localhost:5000"), new("alloy-sv.localhost:5000", Language: "SV"), new("*")),
            Languages, applications: false);

        Assert.Equal(
            [
                new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, null),
                new SiteHost("www.alloy.localhost:5000", HostTypes.Undefined, null, null),
                new SiteHost("alloy-sv.localhost:5000", HostTypes.Primary, "sv", null),
                new SiteHost("*", HostTypes.Undefined, null, null),
            ],
            hosts);
    }

    [Fact]
    public void A_typed_primary_host_wins_and_schemes_set_https()
    {
        var hosts = SiteAddOperation.Hosts(
            Request(new("https://edit.alloy.localhost:5001", "edit"), new("http://a.localhost:5000"), new("https://b.localhost:5001", "primary")),
            Languages, applications: false);

        Assert.Equal(
            [
                new SiteHost("edit.alloy.localhost:5001", HostTypes.Edit, null, true),
                new SiteHost("a.localhost:5000", HostTypes.Undefined, null, false),
                new SiteHost("b.localhost:5001", HostTypes.Primary, null, true),
            ],
            hosts);
    }

    [Fact]
    public void Applications_get_http_on_loopback_hosts_and_https_elsewhere()
    {
        var hosts = SiteAddOperation.Hosts(Request(new("alloy.localhost:5000"), new("alloy.example.com"), new("*")), Languages, applications: true);

        Assert.Equal([false, true, null], hosts.Select(h => h.Https));
    }

    [Fact]
    public void Every_bad_host_is_listed()
    {
        var error = Assert.Throws<AgentException>(() => SiteAddOperation.Hosts(
            Request(new("alloy.localhost:5000", Language: "no"), new("a b"), new("x.localhost", "sideways"), new("y.localhost", "media"), new("*", Language: "sv"), new("z.localhost"), new("Z.localhost")),
            Languages, applications: false));

        Assert.Equal(AgentErrorCodes.Validation, error.Code);
        var messages = error.Validation!.Select(v => v.Message).ToList();
        Assert.Equal(6, messages.Count);
        Assert.Contains(messages, m => m.Contains("'no' is not an enabled language") && m.Contains("opticli languages enable no"));
        Assert.Contains(messages, m => m.Contains("'sideways' is not a host type"));
        Assert.Contains(messages, m => m.Contains("media hosts are CMS 13 only"));
        Assert.Contains(messages, m => m.Contains("the wildcard host is for every language"));
        Assert.Contains(messages, m => m.Contains("is given twice"));
    }

    [Fact]
    public void Only_a_wildcard_is_not_enough()
    {
        var error = Assert.Throws<AgentException>(() => SiteAddOperation.Hosts(Request(new SiteAddHost("*")), Languages, applications: false));
        Assert.Equal(AgentErrorCodes.Usage, error.Code);
    }

    [Fact]
    public void Merging_adds_hosts_retypes_existing_ones_and_moves_the_primary()
    {
        var existing = new SiteState(Guid.NewGuid(), "Alloy", "https://alloy.example.com/",
            [new SiteHost("alloy.example.com", HostTypes.Primary, null, true), new SiteHost("alloy.localhost:5000", HostTypes.Undefined, null, null)])
        {
            StartPage = 5,
        };

        var (after, changes) = SiteAddOperation.Merge(existing,
            [new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, false), new SiteHost("*", HostTypes.Undefined, null, null)],
            startPage: 7, applications: false);

        Assert.Equal(
            [
                new SiteHost("alloy.example.com", HostTypes.Undefined, null, true),
                new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, false),
                new SiteHost("*", HostTypes.Undefined, null, null),
            ],
            after.Hosts);
        Assert.Equal("http://alloy.localhost:5000/", after.SiteUrl);
        Assert.Equal(7, after.StartPage);
        Assert.Equal(5, changes.Count);
    }

    [Fact]
    public void Merging_what_the_site_has_already_changes_nothing()
    {
        var existing = new SiteState(Guid.NewGuid(), "Alloy", "http://alloy.localhost:5000/",
            [new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, false), new SiteHost("extra.localhost:5000", HostTypes.Undefined, null, null)])
        {
            StartPage = 5,
        };

        var (after, changes) = SiteAddOperation.Merge(existing, [new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, false)], startPage: 5, applications: false);

        Assert.Empty(changes);
        Assert.Equal(existing.Hosts, after.Hosts);
    }

    private static SiteState Site(string name, int? assetsRoot) =>
        new(Guid.NewGuid(), name, "http://alloy.localhost:5000/", [new SiteHost("alloy.localhost:5000", HostTypes.Primary, null, false)]) { StartPage = 5, AssetsRoot = assetsRoot };

    [Fact]
    public void A_new_site_gets_a_folder_of_its_own_by_default()
    {
        var created = SiteAddOperation.PlanAssets(null, null, folderBelowStart: null, others: []);
        Assert.Equal((SiteAssetsModes.Own, null, true, null), (created.Mode, created.Folder, created.Create, created.Issue));
        Assert.Contains("created below the start page", created.Change);

        // The start page has one already (a site that was deleted, say): it is used, as the CMS does.
        var reused = SiteAddOperation.PlanAssets(null, null, folderBelowStart: 40, others: [Site("Other", 30)]);
        Assert.Equal((SiteAssetsModes.Own, 40, false, null), (reused.Mode, reused.Folder, reused.Create, reused.Issue));
    }

    [Fact]
    public void A_new_site_can_use_the_global_assets_folder()
    {
        var plan = SiteAddOperation.PlanAssets(null, SiteAssetsModes.Global, folderBelowStart: 40, others: []);

        Assert.Equal((SiteAssetsModes.Global, null, false, null), (plan.Mode, plan.Folder, plan.Create, plan.Issue));
        Assert.Equal("site assets: global (/globalassets/)", plan.Change);
    }

    [Fact]
    public void An_existing_site_keeps_its_assets_root_unless_one_is_asked_for()
    {
        foreach (var root in new int?[] { null, 40 })
        {
            var plan = SiteAddOperation.PlanAssets(Site("Alloy", root), null, folderBelowStart: null, others: []);
            Assert.Null(plan.Change);
            Assert.False(plan.Create);
            Assert.Equal(root, plan.Folder);
        }
        Assert.Null(SiteAddOperation.PlanAssets(Site("Alloy", 40), SiteAssetsModes.Own, folderBelowStart: 40, others: []).Change);
        Assert.Null(SiteAddOperation.PlanAssets(Site("Alloy", null), SiteAssetsModes.Global, folderBelowStart: null, others: []).Change);
    }

    [Fact]
    public void An_existing_site_changes_its_assets_root_when_asked()
    {
        var own = SiteAddOperation.PlanAssets(Site("Alloy", null), SiteAssetsModes.Own, folderBelowStart: null, others: []);
        Assert.True(own.Create);
        Assert.StartsWith("site assets: global -> own", own.Change);

        var global = SiteAddOperation.PlanAssets(Site("Alloy", 40), SiteAssetsModes.Global, folderBelowStart: 40, others: []);
        Assert.Equal("site assets: own -> global (/globalassets/)", global.Change);
    }

    [Fact]
    public void A_folder_another_site_has_is_an_issue()
    {
        var plan = SiteAddOperation.PlanAssets(null, null, folderBelowStart: 40, others: [Site("Alloy", 40)]);

        Assert.Contains("Alloy's assets folder already", plan.Issue);
    }
}
