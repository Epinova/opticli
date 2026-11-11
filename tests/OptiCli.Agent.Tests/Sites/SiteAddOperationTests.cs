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
}
