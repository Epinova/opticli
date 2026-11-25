using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// Price types of the site's own (the Commerce test sites' <c>MEMBER</c> 3 and <c>OUTLET</c> 4, which setup.sh puts in
/// their appsettings.json) next to the base price: read and written by name, never taken for <c>all</c>.
/// </summary>
public sealed class CommercePriceTypeTests
{
    private const string Code = "{\"rule\":\"clearance\",\"min\":2}";

    private static string Plan(string run) => $$$"""
        {
          "guidNamespace": "{{{Guid.NewGuid()}}}",
          "operations": [
            {"op": "create", "id": "catalog", "type": "CatalogContent", "parent": "catalog-root", "name": "opticli-it {{{run}}}", "properties": {"DefaultCurrency": "USD", "DefaultLanguage": "en", "WeightBase": "kgs", "LengthBase": "cm"}, "publish": true},
            {"op": "create", "id": "shirt", "type": "FashionVariant", "parent": "$catalog", "name": "Shirt", "properties": {"Code": "it-{{{run}}}-shirt"}, "publish": true},
            {"op": "price", "entry": "$shirt", "market": "DEFAULT", "currency": "USD", "amount": 29},
            {"op": "price", "entry": "$shirt", "market": "DEFAULT", "currency": "USD", "amount": 25, "priceType": "member"},
            {"op": "price", "entry": "$shirt", "market": "DEFAULT", "currency": "USD", "amount": 19, "priceType": "OUTLET", "priceCode": {{{System.Text.Json.JsonSerializer.Serialize(Code)}}}}
          ]
        }
        """;

    [CommerceSiteFact]
    public async Task The_site_s_own_price_types_are_read_and_written_by_name()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var info = await site.Agent.SendAsync<CommerceInfo>(HttpMethod.Get, AgentRoutes.Commerce, null, cancellationToken);
        Assert.True(info.PriceTypes.Any(t => t.Name == "member"), "The Commerce test site has no MEMBER price type in its appsettings.json: run tests/fixtures/commerce/setup.sh again.");
        Assert.Equal([("all", 0), ("user", 1), ("group", 2), ("member", 3), ("outlet", 4)], info.PriceTypes.Select(t => (t.Name, t.Id)));
        Assert.Equal("Members", info.PriceTypes[3].Description);

        var run = Guid.NewGuid().ToString("N")[..8];
        var plan = WritePlan.Parse(Plan(run));
        PlanRunner Runner() => new(site.Session, new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true), Path.GetTempPath());
        var first = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(first.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        Assert.Contains($"--price-type outlet --price-code '{Code}' --valid-from ", first.Operations[4].Undo);
        var second = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(second.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));

        // Three rows with the same market, currency, quantity and start, each with its own type.
        var shirt = first.Created["shirt"];
        var prices = await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, shirt), null, cancellationToken);
        Assert.Equal([("all", "", 29m), ("member", "", 25m), ("outlet", Code, 19m)], prices.Prices.Select(p => (p.PriceType, p.PriceCode, p.Amount)));

        // Each one set again names its own row; the base price no longer meets the others.
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        Assert.Equal(CommerceWriteStatus.Unchanged, (await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 29m), dryRun: false, cancellationToken)).Output.Status);
        Assert.Equal(CommerceWriteStatus.Unchanged, (await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 25m) { PriceType = "3" }, dryRun: false, cancellationToken)).Output.Status);
        Assert.Equal(CommerceWriteStatus.Unchanged, (await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 19m) { PriceType = "outlet", PriceCode = Code }, dryRun: false, cancellationToken)).Output.Status);
        var member = await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 24m) { PriceType = "Member" }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Updated, member.Output.Status);
        Assert.Equal(("member", 25m), (member.Output.Before!.PriceType, member.Output.Before.Amount));

        var nope = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 10m) { PriceType = "nope" }, dryRun: true, cancellationToken));
        Assert.Contains("it has all, user, group, member, outlet", nope.Message);
        var removed = await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD") { PriceType = "outlet", PriceCode = Code, Remove = true }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Removed, removed.Output.Status);
        Assert.Equal(["all", "member"], (await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, shirt), null, cancellationToken)).Prices.Select(p => p.PriceType));
    }
}
