using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>Catalog content read through the site: what <c>commerce get|children|tree</c> ask the agents for.</summary>
public sealed class CommerceReadTests
{
    [CommerceSiteFact]
    public async Task Catalog_content_is_read_through_the_site_and_the_CMS_reads_point_there()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var catalog = (await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            new JsonObject { ["DefaultCurrency"] = "USD", ["DefaultLanguage"] = "en", ["WeightBase"] = "kgs", ["LengthBase"] = "cm" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var node = (await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Men", new JsonObject { ["Code"] = $"it-{run}-men" }, Publish: true), dryRun: false, cancellationToken)).Output;
        await writes.RunAsync(new CreateOperation($"code:it-{run}-men", "FashionProduct", "Shirt", new JsonObject { ["Code"] = $"it-{run}-shirt", ["Brand"] = "Acme" }, Publish: true), dryRun: false, cancellationToken);

        var root = await site.Agent.SendAsync<CatalogChildren>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceChildren, CatalogRefSyntax.Root), null, cancellationToken);
        Assert.Equal(CatalogKinds.Root, root.Parent.Kind);
        Assert.Contains(root.Children, c => c.Ref == catalog.Ref && c.Kind == CatalogKinds.Catalog);
        var below = await site.Agent.SendAsync<CatalogChildren>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceChildren, $"catalog:opticli-it {run}") + "&limit=1", null, cancellationToken);
        Assert.Equal([node.Ref], below.Children.Select(c => c.Ref));
        Assert.False(below.More);
        var shirts = await site.Agent.SendAsync<CatalogChildren>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceChildren, node.Ref!), null, cancellationToken);
        Assert.Equal([($"it-{run}-shirt", CatalogKinds.Product, node.Ref)], shirts.Children.Select(c => (c.Code, c.Kind, c.Parent)));

        // The site agent reads catalog content by its ref, a catalog's negative one too.
        var item = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(catalog.Ref!), null, cancellationToken);
        Assert.Equal(catalog.Ref, item.Ref);
        Assert.Equal("USD", item.Properties["DefaultCurrency"].Value?.GetString());
        var product = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(shirts.Children[0].Ref!), null, cancellationToken);
        Assert.Equal("Acme", product.Properties["Brand"].Value?.GetString());

        // get, children and tree read the CMS database: they say where catalog content is read.
        var byCode = await Assert.ThrowsAsync<UsageException>(() => site.Session.LocateAsync($"code:it-{run}-shirt", null, cancellationToken));
        Assert.Contains("opticli commerce get", byCode.Message);
        var byRef = await Assert.ThrowsAsync<UsageException>(() => site.Session.LocateAsync(catalog.Ref!, null, cancellationToken));
        Assert.Contains("opticli commerce get", byRef.Hint);
    }
}
