using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// Relations, prices, warehouses, stock and markets through the Commerce agent, on the Commerce test sites. Each run makes
/// its own catalog, warehouse and market (codes of its own), so runs don't meet.
/// </summary>
public sealed class CommerceServicesTests
{
    private static string Plan(string run, string market) => $$$"""
        {
          "guidNamespace": "{{{Guid.NewGuid()}}}",
          "operations": [
            {"op": "market", "market": "{{{market}}}", "name": "opticli-it {{{run}}}", "currencies": ["SEK", "NOK"], "languages": ["en"], "countries": ["SWE", "NOR"]},
            {"op": "warehouse", "code": "it-{{{run}}}", "name": "opticli-it {{{run}}}", "pickupLocation": true},
            {"op": "create", "id": "catalog", "type": "CatalogContent", "parent": "catalog-root", "name": "opticli-it {{{run}}}", "properties": {"DefaultCurrency": "USD", "DefaultLanguage": "en", "WeightBase": "kgs", "LengthBase": "cm"}, "publish": true},
            {"op": "create", "id": "women", "type": "FashionNode", "parent": "$catalog", "name": "Women", "properties": {"Code": "it-{{{run}}}-women"}, "publish": true},
            {"op": "create", "id": "sale", "type": "FashionNode", "parent": "$catalog", "name": "Sale", "properties": {"Code": "it-{{{run}}}-sale"}, "publish": true},
            {"op": "create", "id": "dress", "type": "FashionProduct", "parent": "$women", "name": "Dress", "properties": {"Code": "it-{{{run}}}-dress"}, "publish": true},
            {"op": "create", "id": "dress-s", "type": "FashionVariant", "parent": "$women", "name": "Dress S", "properties": {"Code": "it-{{{run}}}-dress-s", "Size": "S"}, "publish": true},
            {"op": "create", "id": "dress-m", "type": "FashionVariant", "parent": "$women", "name": "Dress M", "properties": {"Code": "it-{{{run}}}-dress-m", "Size": "M"}, "publish": true},
            {"op": "create", "id": "set", "type": "FashionBundle", "parent": "$women", "name": "Set", "properties": {"Code": "it-{{{run}}}-set"}, "publish": true},
            {"op": "relation", "parent": "$dress", "child": "$dress-s"},
            {"op": "relation", "parent": "$dress", "child": "$dress-m", "sortOrder": 1},
            {"op": "relation", "parent": "$set", "child": "$dress-s", "quantity": 2},
            {"op": "relation", "parent": "$sale", "child": "$dress"},
            {"op": "price", "entry": "$dress-s", "market": "DEFAULT", "currency": "USD", "amount": 79},
            {"op": "price", "entry": "$dress-s", "market": "DEFAULT", "currency": "USD", "amount": 59, "validFrom": "2026-12-01T00:00:00Z", "validUntil": "2027-01-01T00:00:00Z"},
            {"op": "price", "entry": "$dress-s", "market": "{{{market}}}", "currency": "SEK", "amount": 799},
            {"op": "inventory", "entry": "$dress-s", "warehouse": "default", "inStock": 25},
            {"op": "inventory", "entry": "$dress-m", "warehouse": "it-{{{run}}}", "inStock": 0, "backorder": 5}
          ]
        }
        """;

    [CommerceSiteFact]
    public async Task A_seeding_plan_runs_and_runs_again_without_changing_anything()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var market = "I" + run[..7].ToUpperInvariant();
        var plan = WritePlan.Parse(Plan(run, market));
        PlanRunner Runner() => new(site.Session, new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true), Path.GetTempPath());

        var first = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(first.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        Assert.Equal(CommerceWriteStatus.Created, Assert.IsType<RelationResult>(first.Operations[11].Result).Status);
        Assert.Equal(RelationKinds.BundleEntry, Assert.IsType<RelationResult>(first.Operations[11].Result).Kind);
        Assert.Equal(RelationKinds.Category, Assert.IsType<RelationResult>(first.Operations[12].Result).Kind);
        Assert.StartsWith("opticli commerce price remove ", first.Operations[13].Undo);

        var second = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(second.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));
        var dry = await Runner().RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
        Assert.All(dry.Operations, o => Assert.Equal(PlanStepStatus.Valid, o.Status));

        // What the site has now, read back through the Commerce agent.
        var dress = first.Created["dress"];
        var relations = await site.Agent.SendAsync<RelationList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceRelations, $"code:it-{run}-dress"), null, cancellationToken);
        Assert.Equal(dress, relations.Ref);
        Assert.Equal([$"it-{run}-dress-s", $"it-{run}-dress-m"], relations.Children.Select(c => c.Code));
        Assert.Equal([RelationKinds.Category], relations.Parents.Select(p => p.Kind));
        var prices = await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, first.Created["dress-s"]), null, cancellationToken);
        Assert.Equal([79m, 59m, 799m], prices.Prices.OrderBy(p => p.Market == market).ThenBy(p => p.ValidFrom).Select(p => p.Amount));
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), prices.Prices.Single(p => p.Amount == 59m).ValidUntil);
        var stock = await site.Agent.SendAsync<InventoryList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceInventory, $"code:it-{run}-dress-m"), null, cancellationToken);
        Assert.Equal([new CommerceStock($"it-{run}", 0, 5, 0, true)], stock.Stock);
        var markets = await site.Agent.SendAsync<MarketList>(HttpMethod.Get, AgentRoutes.CommerceMarkets, null, cancellationToken);
        Assert.Contains(markets.Markets, m => m.Id == market && m.DefaultCurrency == "SEK" && m.Countries.Order().SequenceEqual(["NOR", "SWE"]));
        var warehouses = await site.Agent.SendAsync<WarehouseList>(HttpMethod.Get, AgentRoutes.CommerceWarehouses, null, cancellationToken);
        Assert.Contains(warehouses.Warehouses, w => w.Code == $"it-{run}" && w.PickupLocation && w.Active);

        // A change is an update, and a removal removes only that row.
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var updated = await writes.RunAsync(new PriceOperation($"code:it-{run}-dress-s", "DEFAULT", "USD", 75m), dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Updated, updated.Output.Status);
        Assert.Equal(79m, updated.Output.Before?.Amount);
        var removed = await writes.RunAsync(new PriceOperation($"code:it-{run}-dress-s", "DEFAULT", "USD") { Remove = true, ValidFrom = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Removed, removed.Output.Status);
        Assert.Equal(2, (await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, $"code:it-{run}-dress-s"), null, cancellationToken)).Prices.Count);
        var unrelated = await writes.RunAsync(new RelationOperation(dress, $"code:it-{run}-dress-m", Remove: true), dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Removed, unrelated.Output.Status);
        Assert.Equal(CommerceWriteStatus.Absent, (await writes.RunAsync(new RelationOperation(dress, $"code:it-{run}-dress-m", Remove: true), dryRun: false, cancellationToken)).Output.Status);
    }

    [CommerceSiteFact]
    public async Task What_Commerce_doesnt_take_is_refused_before_anything_is_saved()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            new System.Text.Json.Nodes.JsonObject { ["DefaultCurrency"] = "USD", ["DefaultLanguage"] = "en", ["WeightBase"] = "kgs", ["LengthBase"] = "cm" }, Publish: true), dryRun: false, cancellationToken);
        await writes.RunAsync(new CreateOperation($"catalog:opticli-it {run}", "FashionProduct", "Product", new System.Text.Json.Nodes.JsonObject { ["Code"] = $"it-{run}-p" }), dryRun: false, cancellationToken);
        await writes.RunAsync(new CreateOperation($"catalog:opticli-it {run}", "FashionVariant", "Variant", new System.Text.Json.Nodes.JsonObject { ["Code"] = $"it-{run}-v" }), dryRun: false, cancellationToken);

        var product = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation($"code:it-{run}-p", "DEFAULT", "USD", 10m), dryRun: true, cancellationToken));
        Assert.Contains("is a product", product.Message);
        var campaign = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation($"code:it-{run}-v", "DEFAULT", "USD", 10m) { ValidUntil = DateTimeOffset.UtcNow.AddDays(3) }, dryRun: false, cancellationToken));
        Assert.Contains("needs its start (validFrom)", campaign.Message);
        var market = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new PriceOperation($"code:it-{run}-v", "NOWHERE", "USD", 10m), dryRun: false, cancellationToken));
        Assert.Contains("No market 'NOWHERE'", market.Message);
        var warehouse = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new InventoryOperation($"code:it-{run}-v", "nowhere") { InStock = 1 }, dryRun: false, cancellationToken));
        Assert.Contains("No warehouse 'nowhere'", warehouse.Message);
        var relation = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new RelationOperation($"code:it-{run}-v", $"code:it-{run}-p"), dryRun: false, cancellationToken));
        Assert.Contains("can't be related", relation.Message);
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new InventoryOperation($"code:it-{run}-none", "default"), dryRun: false, cancellationToken));
        Assert.Contains("No catalog content", missing.Message);

        var dry = await writes.RunAsync(new PriceOperation($"code:it-{run}-v", "DEFAULT", "USD", 10m), dryRun: true, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Created, dry.Output.Status);
        Assert.True(dry.Output.DryRun);
        Assert.Empty((await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, $"code:it-{run}-v"), null, cancellationToken)).Prices);
    }
}
