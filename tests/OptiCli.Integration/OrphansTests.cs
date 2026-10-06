using OptiCli.Core.Cms;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>types --orphaned</c> and <c>type</c>'s stored values on the edge-case site, whose OrphansFixture.cs leaves a page
/// type without its class (used by one page) and an EdgePage property that isn't in its class (with one value).
/// </summary>
public sealed class OrphansTests
{
    private static readonly Guid RemovedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A10");

    [SiteFact]
    public async Task A_type_whose_class_is_gone_is_found_by_the_site_and_by_the_source_scan_alike()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken, countInstances: true);
        if (types.FirstOrDefault(t => t.Guid == RemovedType) is not { } removed)
        {
            return;
        }

        var fromSite = OrphanedTypes.FromSite(types, await site.Agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken));
        var fromSource = OrphanedTypes.FromSource(types, CSharpSourceIndex.Build(site.ProjectDirectory), ScheduledJobSources.Assemblies(site.ProjectDirectory));

        Assert.Equal(("EdgeRemovedPage", 1), (Assert.Single(fromSite).Name, removed.Instances));
        Assert.Equal(fromSite.Select(t => t.Name), fromSource.Select(t => t.Name));
    }

    [SiteFact]
    public async Task A_property_that_isnt_in_its_class_has_its_stored_values_counted()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken);
        if (!types.Any(t => t.Guid == RemovedType))
        {
            return;
        }
        var edgePage = types.Single(t => t.Name == "EdgePage");

        var properties = await ContentTypeReader.ListPropertiesAsync(site.Session.Db, edgePage.Id, cancellationToken);
        var values = await ContentTypeReader.OrphanValuesAsync(site.Session.Db, edgePage.Id, cancellationToken);

        var removed = properties.Single(p => !p.ExistsOnModel);
        Assert.Equal("EdgeRemovedText", removed.Name);
        Assert.Equal((1, 1), values[removed.Id]);
        Assert.Single(values);
    }
}
