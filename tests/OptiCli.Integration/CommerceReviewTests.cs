using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Integration.Schema;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>What a review of the Commerce support found, each fixed and kept fixed, on the Commerce test sites.</summary>
public sealed class CommerceReviewTests
{
    /// <summary>A catalog, a node, a product and two variants of this run, and a CMS page, made through the site.</summary>
    private sealed record Catalog(string Run, string CatalogRef, string Node, string Product, string Variant, string Page);

    private static async Task<Catalog> MakeAsync(SiteUnderTest site, WriteExecutor writes, CancellationToken cancellationToken)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var catalog = (await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            new JsonObject { ["DefaultCurrency"] = "USD", ["DefaultLanguage"] = "en", ["WeightBase"] = "kgs", ["LengthBase"] = "cm" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var node = (await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Women", new JsonObject { ["Code"] = $"it-{run}-women" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var product = (await writes.RunAsync(new CreateOperation(node.Ref!, "FashionProduct", "Dress", new JsonObject { ["Code"] = $"it-{run}-p" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var variant = (await writes.RunAsync(new CreateOperation(node.Ref!, "FashionVariant", "Dress S", new JsonObject { ["Code"] = $"it-{run}-v" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var page = (await writes.RunAsync(new CreateOperation("1", "StartPage", $"opticli-it {run}", Publish: true), dryRun: false, cancellationToken)).Output;
        return new Catalog(run, catalog.Ref!, node.Ref!, product.Ref!, variant.Ref!, page.Ref!);
    }

    /// <summary>M1: a catalog type below CMS content, and a CMS type below catalog content, refused as usage, dry run and plan alike.</summary>
    [CommerceSiteFact]
    public async Task Catalog_content_goes_only_below_catalog_content_and_CMS_content_only_below_CMS_content()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var made = await MakeAsync(site, writes, cancellationToken);

        foreach (var dryRun in new[] { true, false })
        {
            var variant = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new CreateOperation(made.Page, "FashionVariant", "Variant under a page", new JsonObject { ["Code"] = $"it-{made.Run}-x" }), dryRun, cancellationToken));
            Assert.Contains("below a catalog or a node", variant.Message);
            var page = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new CreateOperation($"code:it-{made.Run}-women", "StartPage", "Page under a node"), dryRun, cancellationToken));
            Assert.Contains("CMS content can't go below catalog content", page.Message);
            var node = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new CreateOperation("catalog-root", "FashionNode", "Node under the root", new JsonObject { ["Code"] = $"it-{made.Run}-y" }), dryRun, cancellationToken));
            Assert.Contains("below a catalog or a node", node.Message);
        }
        Assert.Empty(await site.Session.Db.QueryAsync("SELECT fkContentID FROM tblContentLanguage WHERE Name = N'Variant under a page'", r => r.GetInt32(0), cancellationToken));

        // A plan: below planned content too, where the dry run can't ask the site.
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "create", "id": "page", "type": "StartPage", "parent": "1", "name": "opticli-it {{{made.Run}}} plan"},
              {"op": "create", "type": "FashionVariant", "parent": "$page", "name": "Variant", "properties": {"Code": "it-{{{made.Run}}}-z"}},
              {"op": "create", "id": "node", "type": "FashionNode", "parent": "{{{made.CatalogRef}}}", "name": "Node", "properties": {"Code": "it-{{{made.Run}}}-n"}},
              {"op": "create", "type": "StartPage", "parent": "$node", "name": "Page below a node"}
            ]}
            """);
        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
        var run = Assert.IsType<PlanRun>(refused.Details);
        Assert.Equal([PlanStepStatus.Invalid, PlanStepStatus.Invalid], run.Operations.Where(o => o.Index is 1 or 3).Select(o => o.Status));
        Assert.Contains("below a catalog or a node", run.Operations[1].Error!.Message);
        Assert.Contains("CMS content can't go below catalog content", run.Operations[3].Error!.Message);
    }

    /// <summary>M2: what Commerce would refuse only when saving is a usage error in the dry run already.</summary>
    [CommerceSiteFact]
    public async Task A_price_Commerce_would_refuse_is_refused_by_its_dry_run()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var made = await MakeAsync(site, writes, cancellationToken);
        var market = "R" + made.Run[..7].ToUpperInvariant();
        await writes.RunAsync(new MarketOperation(market) { Currencies = ["SEK", "NOK"], Languages = ["en"] }, dryRun: false, cancellationToken);

        foreach (var dryRun in new[] { true, false })
        {
            var order = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation(made.Variant, "DEFAULT", "USD", 10m)
            {
                ValidFrom = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
                ValidUntil = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }, dryRun, cancellationToken));
            Assert.Contains("isn't after validFrom", order.Message);
            var unknown = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation(made.Variant, "DEFAULT", "XYZ", 10m), dryRun, cancellationToken));
            Assert.Contains("doesn't have the currency XYZ", unknown.Message);
            var foreign = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PriceOperation(made.Variant, market, "EUR", 10m), dryRun, cancellationToken));
            Assert.Contains($"The market {market} doesn't have the currency EUR", foreign.Message);
            var currency = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new MarketOperation(market) { Currencies = ["SEK", "XYZ"] }, dryRun, cancellationToken));
            Assert.Contains("no currency XYZ", currency.Message);
            var language = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new MarketOperation(market) { Languages = ["xx-YY"] }, dryRun, cancellationToken));
            Assert.Contains("is not a language", language.Message);
        }
        var prices = await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, made.Variant), null, cancellationToken);
        Assert.Empty(prices.Prices);

        // L8: nothing to remove is absent, with no made-up row; L9: a language the CMS hasn't enabled is a warning.
        var absent = await writes.RunAsync(new PriceOperation(made.Variant, "DEFAULT", "USD") { Remove = true }, dryRun: false, cancellationToken);
        Assert.Equal(CommerceWriteStatus.Absent, absent.Output.Status);
        Assert.Null(absent.Output.Price);
        var disabled = await writes.RunAsync(new MarketOperation(market) { Languages = ["en", "fi"] }, dryRun: true, cancellationToken);
        Assert.Contains(disabled.Warnings, w => w.StartsWith("The CMS hasn't enabled fi", StringComparison.Ordinal));
    }

    /// <summary>L2, L5, L6, L11: catalog refs in CMS properties; not_found without the agent; only real ids; unknown GUIDs.</summary>
    [CommerceSiteFact]
    public async Task Catalog_refs_work_in_properties_and_name_only_what_exists()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var made = await MakeAsync(site, writes, cancellationToken);

        var set = await writes.RunAsync(new SetOperation(made.Page, new JsonObject { ["Catalog"] = $"catalog:opticli-it {made.Run}", ["FeaturedProduct"] = $"code:it-{made.Run}-p" }), dryRun: false, cancellationToken);
        Assert.True(set.Output.Saved);
        var item = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(set.Output.Version!), null, cancellationToken);
        Assert.Equal(made.Product, item.Properties["FeaturedProduct"].Value?.GetString());
        Assert.Equal(made.CatalogRef, item.Properties["Catalog"].Value?.GetString());
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new SetOperation(made.Page, new JsonObject { ["FeaturedProduct"] = $"code:it-{made.Run}-none" }), dryRun: true, cancellationToken));
        Assert.Contains("No catalog content", missing.Message);
        // A text property keeps a value that only looks like a catalog ref.
        var text = await writes.RunAsync(new SetOperation(made.Page, new JsonObject { ["Heading"] = "code:not-a-ref" }), dryRun: true, cancellationToken);
        Assert.Equal("code:not-a-ref", text.Output.Changes.Single(c => c.Property == "Heading").After?.ToString());

        // L6: an id Commerce decodes to something else names nothing.
        var refs = await site.Agent.SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest(["-5__CatalogContent", "-1073741823__CatalogContent"]), cancellationToken);
        Assert.False(refs.Refs[0].Found);
        Assert.True(refs.Refs[1].Found);

        // L5 and L11: no site running is unreachable for a catalog ref, and a GUID nothing has is simply not found.
        var offline = new WriteExecutor(site.Session, _ => throw new UnreachableException("No opticli agent is running for this project."));
        await Assert.ThrowsAsync<NotFoundException>(() => offline.RunAsync(new SetOperation(Guid.NewGuid().ToString(), Name: "x"), dryRun: true, cancellationToken));
        await Assert.ThrowsAsync<UnreachableException>(() => offline.RunAsync(new SetOperation($"code:it-{made.Run}-p", Name: "x"), dryRun: true, cancellationToken));
    }

    /// <summary>L1, L3, L7: what the CLI prints for catalog content, and takes back.</summary>
    [CommerceSiteFact]
    public async Task The_CLI_prints_catalog_kinds_versions_and_links_it_takes_back()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var made = await MakeAsync(site, writes, cancellationToken);
        string[] project = ["--project", site.ProjectDirectory, "--json"];

        var get = await OptiCliProcess.RunAsync(["commerce", "get", $"code:it-{made.Run}-p", .. project]);
        Assert.True(get.Ok, get.Stdout + get.Stderr);
        Assert.Equal(CatalogKinds.Product, get.Data?["kind"]?.GetValue<string>());

        var saved = await OptiCliProcess.RunAsync(["set", $"code:it-{made.Run}-p", "Brand=Acme", .. project]);
        var version = saved.Data?["version"]?.GetValue<string>();
        Assert.EndsWith("_CatalogContent", version);
        var based = await OptiCliProcess.RunAsync(["set", $"code:it-{made.Run}-p", "Brand=Acme 2", "--base-version", version!, .. project]);
        Assert.True(based.Ok, based.Stdout + based.Stderr);

        // A rich-text link names catalog content by its GUID only: get asks the running site.
        var guid = (await site.Agent.SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest([made.Product]), cancellationToken)).Refs[0].Guid;
        await writes.RunAsync(new SetOperation(made.Page, new JsonObject { ["MainBody"] = $"<p><a href=\"~/link/{guid:N}.aspx\">Dress</a></p>" }, Publish: true), dryRun: false, cancellationToken);
        var page = await OptiCliProcess.RunAsync(["get", made.Page, "--fields", "MainBody", .. project]);
        Assert.True(page.Ok, page.Stdout + page.Stderr);
        Assert.Contains(made.Product, page.Stdout);
        Assert.DoesNotContain("\"missing\":true", page.Stdout);
    }
}
