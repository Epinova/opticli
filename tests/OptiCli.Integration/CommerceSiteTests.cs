using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Integration.Schema;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// The Commerce test sites as a shop: a seed plan that grows, catalog URLs that answer, product images
/// (CommerceMediaCollection), and catalog content in a CMS page's ContentArea and reference list.
/// </summary>
public sealed class CommerceSiteTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    /// <summary>A published catalog, node, product and variant of this run, and a CMS page with an image of its own.</summary>
    private sealed record Shop(string Run, string Catalog, string Node, string Product, string Variant, string Page, string Image, Guid ImageGuid);

    private static async Task<Shop> MakeAsync(SiteUnderTest site, WriteExecutor writes, CancellationToken cancellationToken)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var catalog = (await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            new JsonObject { ["DefaultCurrency"] = "USD", ["DefaultLanguage"] = "en", ["WeightBase"] = "kgs", ["LengthBase"] = "cm" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var node = (await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Shirts", new JsonObject { ["Code"] = $"it-{run}-shirts" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var product = (await writes.RunAsync(new CreateOperation(node.Ref!, "FashionProduct", "Shirt", new JsonObject { ["Code"] = $"it-{run}-p", ["Brand"] = "Acme" }, Publish: true), dryRun: false, cancellationToken)).Output;
        var variant = (await writes.RunAsync(new CreateOperation(node.Ref!, "FashionVariant", "Shirt M", new JsonObject { ["Code"] = $"it-{run}-v", ["Size"] = "M" }, Publish: true), dryRun: false, cancellationToken)).Output;
        await writes.RunAsync(new RelationOperation(product.Ref!, variant.Ref!), dryRun: false, cancellationToken);
        var page = (await writes.RunAsync(new CreateOperation("1", "StartPage", $"opticli-it {run}", Publish: true), dryRun: false, cancellationToken)).Output;
        var file = await PngAsync(cancellationToken);
        try
        {
            var image = (await writes.RunAsync(new UploadOperation(file, For: page.Ref, Publish: true), dryRun: false, cancellationToken)).Output;
            return new Shop(run, catalog.Ref!, node.Ref!, product.Ref!, variant.Ref!, page.Ref!, image.Ref!, image.Guid!.Value);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<string> PngAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(Path.GetTempPath(), $"opticli-it-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, Convert.FromBase64String(OnePixelPng), cancellationToken);
        return file;
    }

    private static PlanRunner Runner(SiteUnderTest site, string directory) =>
        new(site.Session, new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true), directory);

    /// <summary>
    /// A seed plan run on a database it already seeded, grown by a market and a warehouse and a price and stock in them,
    /// and by a currency for a market the site has: the dry run checks those price and stock steps when the plan runs.
    /// </summary>
    [CommerceSiteFact]
    public async Task A_seed_plan_grows_by_markets_and_warehouses_with_their_prices_and_stock()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);
        var euro = "E" + shop.Run[..7].ToUpperInvariant();
        var nordic = "N" + shop.Run[..7].ToUpperInvariant();
        await writes.RunAsync(new MarketOperation(nordic) { Currencies = ["SEK"], Languages = ["en"] }, dryRun: false, cancellationToken);
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "price", "entry": "code:it-{{{shop.Run}}}-v", "market": "DEFAULT", "currency": "USD", "amount": 29},
              {"op": "market", "market": "{{{euro}}}", "currencies": ["EUR"], "languages": ["en"]},
              {"op": "warehouse", "code": "it-{{{shop.Run}}}-berlin"},
              {"op": "price", "entry": "code:it-{{{shop.Run}}}-v", "market": "{{{euro}}}", "currency": "EUR", "amount": 27},
              {"op": "inventory", "entry": "code:it-{{{shop.Run}}}-v", "warehouse": "it-{{{shop.Run}}}-berlin", "inStock": 4},
              {"op": "market", "market": "{{{nordic}}}", "currencies": ["SEK", "EUR"]},
              {"op": "price", "entry": "code:it-{{{shop.Run}}}-v", "market": "{{{nordic}}}", "currency": "EUR", "amount": 26}
            ]}
            """);

        var dry = await Runner(site, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
        Assert.Equal([PlanStepStatus.Valid, PlanStepStatus.Valid, PlanStepStatus.Valid, PlanStepStatus.Deferred, PlanStepStatus.Deferred, PlanStepStatus.Valid, PlanStepStatus.Deferred],
            dry.Operations.Select(o => o.Status));
        Assert.Contains($"Operation 1 makes the market '{euro}' (EUR)", dry.Operations[3].Warnings![0]);
        Assert.Contains($"Operation 2 makes the warehouse 'it-{shop.Run}-berlin'", dry.Operations[4].Warnings![0]);
        Assert.Contains($"Operation 5 changes the currencies of the market '{nordic}'", dry.Operations[6].Warnings![0]);
        // Deferred, but dry-run in all else: the site found the entry and answered as it will.
        Assert.Equal(CommerceWriteStatus.Created, Assert.IsType<PriceResult>(dry.Operations[3].Result).Status);
        Assert.Equal(CommerceWriteStatus.Created, Assert.IsType<InventoryResult>(dry.Operations[4].Result).Status);
        Assert.Empty((await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, shop.Variant), null, cancellationToken)).Prices);

        var first = await Runner(site, Path.GetTempPath()).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(first.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        var prices = await site.Agent.SendAsync<PriceList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommercePrices, shop.Variant), null, cancellationToken);
        Assert.Equal([("DEFAULT", "USD", 29m), (euro, "EUR", 27m), (nordic, "EUR", 26m)],
            prices.Prices.Select(p => (p.Market, p.Currency, p.Amount)).OrderBy(p => p.Amount == 29m ? 0 : 1).ThenByDescending(p => p.Amount));
        var stock = await site.Agent.SendAsync<InventoryList>(HttpMethod.Get, AgentRoutes.WithRef(AgentRoutes.CommerceInventory, shop.Variant), null, cancellationToken);
        Assert.Contains(stock.Stock, s => s.Warehouse == $"it-{shop.Run}-berlin" && s.InStock == 4m);

        // Run again, the plan finds everything as it wants it, and its dry run checks every step against the site.
        var second = await Runner(site, Path.GetTempPath()).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(second.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));
        var again = await Runner(site, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
        Assert.All(again.Operations, o => Assert.Equal(PlanStepStatus.Valid, o.Status));

        // A market no step makes is still refused by the dry run.
        var unknown = WritePlan.Parse($$$"""{"operations": [{"op": "price", "entry": "code:it-{{{shop.Run}}}-v", "market": "NOWHERE", "currency": "EUR", "amount": 1}]}""");
        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => Runner(site, Path.GetTempPath()).RunAsync(unknown, dryRun: true, publishAll: false, cancellationToken));
        Assert.Equal(PlanStepStatus.Invalid, Assert.IsType<PlanRun>(refused.Details).Operations[0].Status);
    }

    /// <summary>
    /// A market or warehouse step before a price or stock step: the dry run still checks everything in that step that
    /// doesn't wait for the market or warehouse (the entry, the dates, the currency against the planned market), so a
    /// plan the site would refuse halfway is refused before anything is saved; a rename alone checks it all as usual.
    /// </summary>
    [CommerceSiteFact]
    public async Task Price_and_stock_steps_after_a_market_or_warehouse_step_are_still_checked_before_anything_is_saved()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);
        var market = "R" + shop.Run[..7].ToUpperInvariant();
        var fresh = "F" + shop.Run[..7].ToUpperInvariant();
        var warehouse = $"it-{shop.Run}-r";
        var before = $"Before {shop.Run}";
        await writes.RunAsync(new MarketOperation(market) { Name = before, Currencies = ["USD"], Languages = ["en"] }, dryRun: false, cancellationToken);
        await writes.RunAsync(new WarehouseOperation(warehouse) { Name = before }, dryRun: false, cancellationToken);
        var variant = $"code:it-{shop.Run}-v";

        string Rename(string name) => $$$"""{"op": "market", "market": "{{{market}}}", "name": "{{{name}}} {{{shop.Run}}}"}""";
        string Make() => $$$"""{"op": "market", "market": "{{{fresh}}}", "currencies": ["EUR"], "languages": ["en"]}""";
        var cases = new (string First, string Price, string Error)[]
        {
            (Rename("Renamed 1"), $$$"""{"op": "price", "entry": "{{{variant}}}", "market": "{{{market}}}", "currency": "XYZ", "amount": 1}""", "doesn't have the currency XYZ"),
            (Rename("Renamed 2"), $$$"""{"op": "price", "entry": "code:it-{{{shop.Run}}}-p", "market": "{{{market}}}", "currency": "USD", "amount": 1}""", "Commerce keeps prices and stock on variants and packages"),
            (Rename("Renamed 3"), $$$"""{"op": "price", "entry": "code:it-{{{shop.Run}}}-nope", "market": "{{{market}}}", "currency": "USD", "amount": 1}""", "No catalog content"),
            (Rename("Renamed 4"), $$$"""{"op": "price", "entry": "{{{variant}}}", "market": "{{{market}}}", "currency": "USD", "amount": 1, "validFrom": "2027-02-01T00:00:00Z", "validUntil": "2027-01-01T00:00:00Z"}""", "isn't after validFrom"),
            ($$$"""{"op": "warehouse", "code": "{{{warehouse}}}", "name": "Renamed {{{shop.Run}}}"}""", $$$"""{"op": "inventory", "entry": "code:it-{{{shop.Run}}}-p", "warehouse": "{{{warehouse}}}", "inStock": 1}""", "Commerce keeps prices and stock on variants and packages"),
            // A market or warehouse the plan makes: checked against it, in all but its existence.
            (Make(), $$$"""{"op": "price", "entry": "{{{variant}}}", "market": "{{{fresh}}}", "currency": "SEK", "amount": 1}""", $"The market {fresh} doesn't have the currency SEK"),
            (Make(), $$$"""{"op": "price", "entry": "code:it-{{{shop.Run}}}-p", "market": "{{{fresh}}}", "currency": "EUR", "amount": 1}""", "Commerce keeps prices and stock on variants and packages"),
            (Make(), $$$"""{"op": "price", "entry": "{{{variant}}}", "market": "{{{fresh}}}", "currency": "EUR", "amount": 1, "validFrom": "2027-02-01T00:00:00Z", "validUntil": "2027-01-01T00:00:00Z"}""", "isn't after validFrom"),
            ($$$"""{"op": "warehouse", "code": "{{{warehouse}}}-new"}""", $$$"""{"op": "inventory", "entry": "code:it-{{{shop.Run}}}-p", "warehouse": "{{{warehouse}}}-new", "inStock": 1}""", "Commerce keeps prices and stock on variants and packages"),
        };
        foreach (var (first, price, error) in cases)
        {
            var plan = WritePlan.Parse($$$"""{"operations": [{{{first}}}, {{{price}}}]}""");
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => Runner(site, Path.GetTempPath()).RunAsync(plan, dryRun, publishAll: false, cancellationToken));
                var run = Assert.IsType<PlanRun>(refused.Details);
                Assert.Equal(PlanStepStatus.Invalid, run.Operations[1].Status);
                Assert.Contains(error, run.Operations[1].Error!.Message);
            }
        }

        // Nothing was saved: not the renames, nor the market and warehouse the plans would have made.
        var markets = await site.Agent.SendAsync<MarketList>(HttpMethod.Get, AgentRoutes.CommerceMarkets, null, cancellationToken);
        Assert.Equal(before, markets.Markets.Single(m => m.Id == market).Name);
        Assert.DoesNotContain(markets.Markets, m => m.Id == fresh);
        var warehouses = await site.Agent.SendAsync<WarehouseList>(HttpMethod.Get, AgentRoutes.CommerceWarehouses, null, cancellationToken);
        Assert.Equal(before, warehouses.Warehouses.Single(w => w.Code == warehouse).Name);
        Assert.DoesNotContain(warehouses.Warehouses, w => w.Code == $"{warehouse}-new");

        // A name another market has is refused by the dry run too (Commerce keeps them unique).
        var taken = WritePlan.Parse($$$"""{"operations": [{"op": "market", "market": "{{{market}}}", "name": "{{{markets.Markets.First(m => m.Id != market).Name}}}"}]}""");
        var named = await Assert.ThrowsAnyAsync<OptiCliException>(() => Runner(site, Path.GetTempPath()).RunAsync(taken, dryRun: true, publishAll: false, cancellationToken));
        Assert.Contains("market names are unique", Assert.IsType<PlanRun>(named.Details).Operations[0].Error!.Message);

        // A rename and a valid price: both checked fully (valid), then saved.
        var renamed = WritePlan.Parse($$$"""{"operations": [{{{Rename("Renamed")}}}, {"op": "price", "entry": "{{{variant}}}", "market": "{{{market}}}", "currency": "USD", "amount": 5}]}""");
        var dry = await Runner(site, Path.GetTempPath()).RunAsync(renamed, dryRun: true, publishAll: false, cancellationToken);
        Assert.Equal([PlanStepStatus.Valid, PlanStepStatus.Valid], dry.Operations.Select(o => o.Status));
        var saved = await Runner(site, Path.GetTempPath()).RunAsync(renamed, dryRun: false, publishAll: false, cancellationToken);
        Assert.Equal([PlanStepStatus.Saved, PlanStepStatus.Saved], saved.Operations.Select(o => o.Status));
    }

    /// <summary>
    /// The fixture's catalog routing: a product's and variant's url answers on the served site and shows the item's
    /// images, and resolve takes the URL back to the catalog content.
    /// </summary>
    [CommerceSiteFact]
    public async Task Catalog_urls_answer_show_the_images_and_resolve_back()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        Assert.NotNull(site.SiteUrl);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);
        await writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray(shop.Image) }, Publish: true), dryRun: false, cancellationToken);
        string[] project = ["--project", site.ProjectDirectory, "--json"];
        using var http = new HttpClient { BaseAddress = site.SiteUrl };

        foreach (var (item, css) in new[] { (shop.Product, "brand"), (shop.Variant, "size") })
        {
            var get = await OptiCliProcess.RunAsync(["commerce", "get", item, .. project]);
            Assert.True(get.Ok, get.Stdout + get.Stderr);
            var url = get.Data?["url"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(url), get.Stdout);
            var path = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : url!;
            Assert.Contains($"/opticli-it-{shop.Run}/", path, StringComparison.OrdinalIgnoreCase);

            using var response = await http.GetAsync(path.TrimStart('/'), cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
            Assert.Contains($"class=\"{css}\"", html);

            var resolved = await OptiCliProcess.RunAsync(["resolve", url!, .. project]);
            Assert.True(resolved.Ok, resolved.Stdout + resolved.Stderr);
            Assert.Equal(item, resolved.Data?["ref"]?.GetValue<string>());
            Assert.Equal("catalog", resolved.Data?["matchedBy"]?.GetValue<string>());
            var byPath = await OptiCliProcess.RunAsync(["resolve", path, .. project]);
            Assert.Equal(item, byPath.Data?["ref"]?.GetValue<string>());
        }

        // The product page shows its image, which answers as an image.
        var productUrl = (await OptiCliProcess.RunAsync(["commerce", "get", shop.Product, .. project])).Data?["url"]?.GetValue<string>()!;
        var page = await http.GetStringAsync((Uri.TryCreate(productUrl, UriKind.Absolute, out var a) ? a.PathAndQuery : productUrl).TrimStart('/'), cancellationToken);
        var src = System.Text.RegularExpressions.Regex.Match(page, "<img class=\"asset\" data-group=\"default\" src=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(src), page);
        using var image = await http.GetAsync((Uri.TryCreate(src, UriKind.Absolute, out var s) ? s.PathAndQuery : src).TrimStart('/'), cancellationToken);
        Assert.True(image.IsSuccessStatusCode, $"{src}: {(int)image.StatusCode}");
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        // A catalog path nothing has stays not found.
        var none = await OptiCliProcess.RunAsync(["resolve", $"/en/opticli-it-{shop.Run}/no-such-thing/", .. project]);
        Assert.False(none.Ok);
        Assert.Contains("not_found", none.Stdout + none.Stderr);
    }

    /// <summary>Product images: CommerceMediaCollection takes media by id, GUID or a plan's $id, with group and order.</summary>
    [CommerceSiteFact]
    public async Task Product_images_are_set_by_ref_guid_or_plan_id_and_read_back()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);

        async Task<JsonElement?> MediaAsync(string item) =>
            (await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(item), null, cancellationToken)).Properties["CommerceMediaCollection"].Value;

        var set = await writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray(shop.Image) }, Publish: true), dryRun: false, cancellationToken);
        Assert.True(set.Output.Saved);
        Assert.Equal($$"""[{"ref":"{{shop.Image}}","groupName":"default","sortOrder":0}]""", (await MediaAsync(shop.Product))?.GetRawText());

        // The same image by its GUID changes nothing.
        var same = await writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray(shop.ImageGuid.ToString()) }), dryRun: true, cancellationToken);
        Assert.Empty(same.Output.Changes);

        // Groups and order; the read lists them by sort order.
        var second = (await writes.RunAsync(new UploadOperation(await PngAsync(cancellationToken), For: shop.Page, Publish: true), dryRun: false, cancellationToken)).Output.Ref!;
        var grouped = new JsonArray(
            new JsonObject { ["ref"] = second, ["groupName"] = "swatch", ["sortOrder"] = 2 },
            new JsonObject { ["ref"] = shop.Image, ["groupName"] = "default", ["sortOrder"] = 1 });
        await writes.RunAsync(new SetOperation(shop.Variant, new JsonObject { ["CommerceMediaCollection"] = grouped }, Publish: true), dryRun: false, cancellationToken);
        Assert.Equal($$"""[{"ref":"{{shop.Image}}","groupName":"default","sortOrder":1},{"ref":"{{second}}","groupName":"swatch","sortOrder":2}]""", (await MediaAsync(shop.Variant))?.GetRawText());

        // What isn't media is refused, and so is media that doesn't exist.
        var page = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray(shop.Page) }), dryRun: true, cancellationToken));
        Assert.Contains("not media", page.Message);
        await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray(Guid.NewGuid().ToString()) }), dryRun: true, cancellationToken));

        // The same media item twice (here by ref and by GUID) is refused before anything is saved, dry run or not.
        foreach (var dryRun in new[] { true, false })
        {
            var twice = new JsonArray(shop.Image, new JsonObject { ["ref"] = shop.ImageGuid.ToString(), ["groupName"] = "swatch" });
            var duplicate = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new SetOperation(shop.Variant, new JsonObject { ["CommerceMediaCollection"] = twice }), dryRun, cancellationToken));
            Assert.Contains($"has the media item {shop.Image} twice", duplicate.Message);
        }

        // A change's before and after are in the shape commerce get shows: {ref, groupName, sortOrder}.
        var emptied = await writes.RunAsync(new SetOperation(shop.Product, new JsonObject { ["CommerceMediaCollection"] = new JsonArray() }), dryRun: true, cancellationToken);
        var change = emptied.Output.Changes.Single(c => c.Property == "CommerceMediaCollection");
        Assert.Equal($$"""[{"ref":"{{shop.Image}}","groupName":"default","sortOrder":0}]""", JsonSerializer.Serialize(change.Before));

        // A plan: an image it uploads, by its $id, on a product it makes; run again, nothing changes.
        var directory = Directory.CreateTempSubdirectory("opticli-it-").FullName;
        File.WriteAllBytes(Path.Combine(directory, "shirt.png"), Convert.FromBase64String(OnePixelPng));
        var plan = WritePlan.Parse($$$"""
            {"guidNamespace": "{{{Guid.NewGuid()}}}", "operations": [
              {"op": "upload", "id": "image", "file": "shirt.png", "for": "{{{shop.Page}}}", "publish": true},
              {"op": "create", "id": "tee", "type": "FashionProduct", "parent": "{{{shop.Node}}}", "name": "Tee", "publish": true,
                "properties": {"Code": "it-{{{shop.Run}}}-tee", "CommerceMediaCollection": [{"ref": "$image", "groupName": "default", "sortOrder": 0}]}},
              {"op": "set", "ref": "{{{shop.Variant}}}", "properties": {"CommerceMediaCollection": ["$image"]}, "publish": true}
            ]}
            """);
        try
        {
            var dry = await Runner(site, directory).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
            Assert.DoesNotContain(dry.Operations, o => o.Status == PlanStepStatus.Invalid);
            var first = await Runner(site, directory).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
            Assert.All(first.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
            var uploaded = first.Created["image"];
            Assert.Equal($$"""[{"ref":"{{uploaded}}","groupName":"default","sortOrder":0}]""", (await MediaAsync(first.Created["tee"]))?.GetRawText());
            Assert.Equal($$"""[{"ref":"{{uploaded}}","groupName":"default","sortOrder":0}]""", (await MediaAsync(shop.Variant))?.GetRawText());
            var again = await Runner(site, directory).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
            Assert.All(again.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        // commerce get prints it as the agent reads it.
        var get = await OptiCliProcess.RunAsync(["commerce", "get", shop.Product, "--project", site.ProjectDirectory, "--json"]);
        Assert.True(get.Ok, get.Stdout + get.Stderr);
        Assert.Contains(shop.Image, get.Data?["properties"]?["CommerceMediaCollection"]?.ToJsonString());
    }

    /// <summary>Blocks and media "for" catalog content, or below it: refused, saying to use CommerceMediaCollection.</summary>
    [CommerceSiteFact]
    public async Task Blocks_and_media_for_catalog_content_are_refused_with_what_to_do_instead()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);
        var file = await PngAsync(cancellationToken);
        try
        {
            foreach (var dryRun in new[] { true, false })
            {
                WriteOperation[] refused =
                [
                    new UploadOperation(file, For: shop.Product),
                    new UploadOperation(file, For: $"code:it-{shop.Run}-v"),
                    new UploadOperation(file, Parent: shop.Catalog),
                    new BlockCreateOperation("TeaserBlock", $"Teaser {shop.Run}", For: shop.Product),
                    new BlockCreateOperation("TeaserBlock", $"Teaser {shop.Run}", Parent: shop.Node),
                    new CreateOperation(null, "TeaserBlock", $"Teaser {shop.Run}") { For = shop.Product },
                ];
                foreach (var op in refused)
                {
                    var error = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(op, dryRun, cancellationToken));
                    Assert.Contains("is catalog content", error.Message);
                    Assert.Contains("CommerceMediaCollection", error.Hint);
                }
            }
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Empty(await site.Session.Db.QueryAsync($"SELECT fkContentID FROM tblContentLanguage WHERE Name = N'Teaser {shop.Run}'", r => r.GetInt32(0), cancellationToken));

        // A plan: for catalog content the plan makes, and for catalog content the site has.
        var directory = Directory.CreateTempSubdirectory("opticli-it-").FullName;
        File.WriteAllBytes(Path.Combine(directory, "shirt.png"), Convert.FromBase64String(OnePixelPng));
        try
        {
            var plan = WritePlan.Parse($$$"""
                {"operations": [
                  {"op": "create", "id": "tee", "type": "FashionProduct", "parent": "{{{shop.Node}}}", "name": "Tee", "properties": {"Code": "it-{{{shop.Run}}}-tee"}},
                  {"op": "upload", "file": "shirt.png", "for": "$tee"},
                  {"op": "block", "type": "TeaserBlock", "name": "Teaser", "for": "$tee"},
                  {"op": "upload", "file": "shirt.png", "for": "{{{shop.Product}}}"}
                ]}
                """);
            var failed = await Assert.ThrowsAnyAsync<OptiCliException>(() => Runner(site, directory).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
            var run = Assert.IsType<PlanRun>(failed.Details);
            Assert.Equal([PlanStepStatus.Invalid, PlanStepStatus.Invalid, PlanStepStatus.Invalid], run.Operations.Skip(1).Select(o => o.Status));
            Assert.All(run.Operations.Skip(1), o => Assert.Contains("is catalog content", o.Error!.Message));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A CMS page's ContentArea and reference list hold catalog content beside CMS content, by any catalog ref.</summary>
    [CommerceSiteFact]
    public async Task ContentAreas_and_reference_lists_take_catalog_content_beside_CMS_content()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var shop = await MakeAsync(site, writes, cancellationToken);
        var teaser = (await writes.RunAsync(new BlockCreateOperation("TeaserBlock", "Teaser", For: shop.Page, Properties: new JsonObject { ["Heading"] = "Shirts" }, Publish: true), dryRun: false, cancellationToken)).Output.Ref!;

        var values = new JsonObject
        {
            ["MainContentArea"] = new JsonArray(new[] { $"code:it-{shop.Run}-p", teaser, $"catalog:opticli-it {shop.Run}", shop.Variant }.Select(r => (JsonNode)new JsonObject { ["ref"] = r }).ToArray()),
            ["RelatedContent"] = new JsonArray($"code:it-{shop.Run}-v", shop.Image, $"catalog:opticli-it {shop.Run}", shop.Node),
        };
        var dry = await writes.RunAsync(new SetOperation(shop.Page, values), dryRun: true, cancellationToken);
        Assert.Equal(["MainContentArea", "RelatedContent"], dry.Output.Changes.Select(c => c.Property).Order());
        var saved = await writes.RunAsync(new SetOperation(shop.Page, values, Publish: true), dryRun: false, cancellationToken);
        Assert.True(saved.Output.Saved);

        var item = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(shop.Page), null, cancellationToken);
        var area = item.Properties["MainContentArea"].Value!.Value.EnumerateArray().Select(e => e.GetProperty("ref").GetString()).ToList();
        Assert.Equal([shop.Product, teaser, shop.Catalog, shop.Variant], area);
        var related = item.Properties["RelatedContent"].Value!.Value.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([shop.Variant, shop.Image, shop.Catalog, shop.Node], related);

        // The same values again change nothing; get on the CMS database prints the catalog refs, not missing content.
        var again = await writes.RunAsync(new SetOperation(shop.Page, values), dryRun: true, cancellationToken);
        Assert.Empty(again.Output.Changes);
        var get = await OptiCliProcess.RunAsync(["get", shop.Page, "--fields", "MainContentArea,RelatedContent", "--project", site.ProjectDirectory, "--json"]);
        Assert.True(get.Ok, get.Stdout + get.Stderr);
        Assert.Contains(shop.Product, get.Stdout);
        Assert.Contains(shop.Catalog, get.Stdout);
        Assert.Contains(shop.Node, get.Stdout);
        Assert.DoesNotContain("\"missing\":true", get.Stdout);

        // A catalog ref that names nothing is refused before anything is saved.
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new SetOperation(shop.Page, new JsonObject { ["RelatedContent"] = new JsonArray($"code:it-{shop.Run}-none") }), dryRun: true, cancellationToken));
        Assert.Contains("No catalog content", missing.Message);
    }
}
