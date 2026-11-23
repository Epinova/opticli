using OptiCli.Core.Discovery;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>The Commerce agent: loaded into Commerce sites only, and answering there.</summary>
public sealed class CommerceAgentTests
{
    /// <summary>Runs on any site: a CMS-only one gets no Commerce agent, a Commerce one gets it loaded.</summary>
    [SiteFact]
    public async Task The_Commerce_agent_is_loaded_exactly_where_the_build_has_Commerce()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var builds = PackageVersions.FindCommerce(ProjectLocator.Locate(site.ProjectDirectory, site.ProjectDirectory).Project);

        var ping = await site.Agent.PingAsync(cancellationToken);

        if (builds is null)
        {
            Assert.Null(ping.Commerce);
            var missing = await Assert.ThrowsAsync<Core.Errors.NotFoundException>(() => site.Agent.SendAsync<CommerceInfo>(HttpMethod.Get, AgentRoutes.Commerce, null, cancellationToken));
            Assert.Contains("No agent route", missing.Message);
        }
        else
        {
            Assert.NotNull(ping.Commerce);
            Assert.Equal(builds, ping.Commerce.Version);
            Assert.True(ping.Commerce.Agent, ping.Commerce.Error);
        }
    }

    [CommerceSiteFact]
    public async Task The_Commerce_agent_resolves_the_catalog_root_by_every_form_and_nothing_else()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        var info = await site.Agent.SendAsync<CommerceInfo>(HttpMethod.Get, AgentRoutes.Commerce, null, cancellationToken);
        Assert.Equal("-1073741823__CatalogContent", info.CatalogRoot);
        Assert.Equal((await site.Agent.PingAsync(cancellationToken)).Commerce!.Version, info.Version);

        var refs = await site.Agent.SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs,
            new CatalogRefsRequest([info.CatalogRoot, info.CatalogRootGuid.ToString(), "code:opticli-it-no-such-code", "catalog:opticli-it no such catalog", "1", Guid.NewGuid().ToString()]), cancellationToken);

        Assert.Equal(6, refs.Refs.Count);
        Assert.All(refs.Refs.Take(2), r =>
        {
            Assert.True(r.Found);
            Assert.Equal(info.CatalogRoot, r.Ref);
            Assert.Equal(info.CatalogRootGuid, r.Guid);
            Assert.Equal(CatalogKinds.Root, r.Kind);
            Assert.Null(r.Parent);
        });
        Assert.All(refs.Refs.Skip(2), r => Assert.False(r.Found));
        Assert.Equal("1", refs.Refs[4].Input);
    }
}
