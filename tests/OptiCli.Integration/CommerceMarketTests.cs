using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// A market written with SQL (no currency or language lists, which the fixture's <c>POST /opticli-fixture/bare-market</c>
/// makes), changed with <c>market set</c>, and markets removed with <c>market remove</c>, on the Commerce test sites.
/// </summary>
public sealed class CommerceMarketTests
{
    [CommerceSiteFact]
    public async Task A_market_without_lists_is_renamed_then_removed_with_its_prices()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var id = "B" + run[..7].ToUpperInvariant();
        using (var http = new HttpClient { BaseAddress = site.SiteUrl ?? throw new InvalidOperationException("The site's serve state has no URL.") })
        {
            using var response = await http.PostAsync($"opticli-fixture/bare-market?id={id}&currency=USD&language=en", null, cancellationToken);
            Assert.True(response.IsSuccessStatusCode, "The Commerce test site's CommerceFixture.cs has no /opticli-fixture/bare-market: run tests/fixtures/commerce/setup.sh again.");
            // Commerce reads the lists as they are: empty, not the defaults.
            var bare = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            Assert.Empty(bare!["currencies"]!.AsArray());
            Assert.Empty(bare["languages"]!.AsArray());
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));

        // A rename keeps the lists; Commerce's save lists the defaults, which the result says.
        var renamed = await writes.RunAsync(new MarketOperation(id) { Name = $"opticli-it {run}" }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Updated, renamed.Output.Status);
        Assert.Equal(["name", "currencies", "languages"], renamed.Output.Changes);
        Assert.Equal(["USD"], renamed.Output.Market!.Currencies);
        Assert.Equal(["en"], renamed.Output.Market.Languages);
        var markets = await site.Agent.SendAsync<MarketList>(HttpMethod.Get, AgentRoutes.CommerceMarkets, null, cancellationToken);
        var saved = Assert.Single(markets.Markets, m => m.Id == id);
        Assert.Equal($"opticli-it {run}", saved.Name);
        Assert.Equal(["USD"], saved.Currencies);
        Assert.Equal(["en"], saved.Languages);
        Assert.Equal(CommerceWriteStatus.Unchanged, (await writes.RunAsync(new MarketOperation(id) { Name = $"opticli-it {run}" }, dryRun: false, cancellationToken)).Output.Status);

        // A price in it, and one in DEFAULT: the removal takes only the market's.
        await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            new JsonObject { ["DefaultCurrency"] = "USD", ["DefaultLanguage"] = "en", ["WeightBase"] = "kgs", ["LengthBase"] = "cm" }, Publish: true), dryRun: false, cancellationToken);
        await writes.RunAsync(new CreateOperation($"catalog:opticli-it {run}", "FashionNode", "Women", new JsonObject { ["Code"] = $"it-{run}-women" }, Publish: true), dryRun: false, cancellationToken);
        await writes.RunAsync(new CreateOperation($"code:it-{run}-women", "FashionVariant", "Shirt", new JsonObject { ["Code"] = $"it-{run}-shirt" }, Publish: true), dryRun: false, cancellationToken);
        await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", id, "USD", 29m), dryRun: false, cancellationToken);
        await writes.RunAsync(new PriceOperation($"code:it-{run}-shirt", "DEFAULT", "USD", 30m), dryRun: false, cancellationToken);

        var dry = await writes.RunAsync(new MarketOperation(id) { Remove = true }, dryRun: true, cancellationToken);
        Assert.Equal((CommerceWriteStatus.Removed, true), (dry.Output.Status, dry.Output.DryRun));
        Assert.Contains((await site.Agent.SendAsync<MarketList>(HttpMethod.Get, AgentRoutes.CommerceMarkets, null, cancellationToken)).Markets, m => m.Id == id);

        var removed = await writes.RunAsync(new MarketOperation(id) { Remove = true }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Removed, removed.Output.Status);
        Assert.Equal(id, removed.Output.Market!.Id);
        Assert.Contains(removed.Warnings, w => w.Contains("removes a market's prices with it", StringComparison.Ordinal));
        Assert.DoesNotContain((await site.Agent.SendAsync<MarketList>(HttpMethod.Get, AgentRoutes.CommerceMarkets, null, cancellationToken)).Markets, m => m.Id == id);
        var prices = await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, $"code:it-{run}-shirt"), null, cancellationToken);
        Assert.Equal(["DEFAULT"], prices.Prices.Select(p => p.Market));

        var again = await writes.RunAsync(new MarketOperation(id) { Remove = true }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Absent, again.Output.Status);
        Assert.Null(again.Output.Market);
        var builtin = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new MarketOperation("default") { Remove = true }, dryRun: true, cancellationToken));
        Assert.Contains("Commerce's own market", builtin.Message);
    }

    [CommerceSiteFact]
    public async Task A_plan_makes_a_market_and_removes_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var id = "R" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        PlanRunner Runner() => new(site.Session, new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent)), Path.GetTempPath());
        var make = WritePlan.Parse($$"""{"operations": [{"op": "market", "market": "{{id}}", "currencies": ["EUR"], "languages": ["en"]}]}""");
        var remove = WritePlan.Parse($$"""{"operations": [{"op": "market", "market": "{{id}}", "remove": true}]}""");

        Assert.Equal(PlanStepStatus.Saved, (await Runner().RunAsync(make, dryRun: false, publishAll: false, cancellationToken)).Operations[0].Status);
        var removed = await Runner().RunAsync(remove, dryRun: false, publishAll: false, cancellationToken);
        Assert.Equal(PlanStepStatus.Saved, removed.Operations[0].Status);
        Assert.Equal(CommerceWriteStatus.Removed, Assert.IsType<MarketResult>(removed.Operations[0].Result).Status);
        Assert.StartsWith($"Make it again with `opticli commerce market set {id} --name {id} --currencies EUR --default-currency EUR --languages en", removed.Operations[0].Undo);
        Assert.Equal(PlanStepStatus.Unchanged, (await Runner().RunAsync(remove, dryRun: false, publishAll: false, cancellationToken)).Operations[0].Status);

        var both = WritePlan.Parse($$"""{"operations": [{"op": "market", "market": "{{id}}", "remove": true, "name": "x"}]}""");
        var refused = await Assert.ThrowsAsync<UsageException>(() => Runner().RunAsync(both, dryRun: true, publishAll: false, cancellationToken));
        Assert.Contains("takes only its id", refused.Message);
    }
}
