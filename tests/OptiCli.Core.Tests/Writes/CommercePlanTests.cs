using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>A plan's Commerce steps: relation, price, warehouse, inventory and market.</summary>
public class CommercePlanTests
{
    private const string Plan = """
        {"operations": [
          {"op": "market", "market": "NORDIC", "name": "Nordic", "currencies": ["SEK", "NOK"], "languages": "en", "countries": ["SWE"], "pricesIncludeTax": true},
          {"op": "warehouse", "code": "oslo", "name": "Oslo", "pickupLocation": true, "sortOrder": 2},
          {"op": "create", "id": "dress", "type": "FashionProduct", "parent": "code:women", "name": "Dress"},
          {"op": "create", "id": "dress-s", "type": "FashionVariant", "parent": "code:women", "name": "Dress S"},
          {"op": "relation", "parent": "$dress", "child": "$dress-s", "sortOrder": 1},
          {"op": "relation", "parent": "code:set", "child": "$dress-s", "quantity": "2", "remove": true},
          {"op": "price", "entry": "$dress-s", "market": "DEFAULT", "currency": "USD", "amount": 49.9, "validFrom": "2026-12-01T00:00:00Z", "validUntil": "2027-01-01T00:00:00+01:00", "minQuantity": 2, "priceType": "group", "priceCode": "VIP"},
          {"op": "inventory", "entry": "$dress-s", "warehouse": "oslo", "inStock": 25, "backorder": 5, "tracked": false}
        ]}
        """;

    [Fact]
    public void Every_Commerce_step_parses_with_its_fields_and_depends_on_the_content_it_names()
    {
        var plan = WritePlan.Parse(Plan);

        var market = Assert.IsType<MarketOperation>(plan.Steps[0].Operation);
        Assert.Equal("NORDIC", market.Market);
        Assert.Equal(["SEK", "NOK"], market.Currencies);
        Assert.Equal(["en"], market.Languages);
        Assert.True(market.PricesIncludeTax);
        Assert.Null(market.Enabled);
        Assert.Null(market.Id);

        var warehouse = Assert.IsType<WarehouseOperation>(plan.Steps[1].Operation);
        Assert.Equal(("oslo", "Oslo", true, 2), (warehouse.Code, warehouse.Name, warehouse.PickupLocation, warehouse.SortOrder));
        Assert.Null(warehouse.Active);

        var relation = Assert.IsType<RelationOperation>(plan.Steps[4].Operation);
        Assert.Equal(new HashSet<string> { "dress", "dress-s" }, plan.Steps[4].DependsOn);
        Assert.Equal(1, relation.SortOrder);
        var removal = Assert.IsType<RelationOperation>(plan.Steps[5].Operation);
        Assert.True(removal.Remove);
        Assert.Equal(2m, removal.Quantity);

        var price = Assert.IsType<PriceOperation>(plan.Steps[6].Operation);
        Assert.Equal(49.9m, price.Amount);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), price.ValidFrom);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), price.ValidUntil!.Value.ToUniversalTime());
        Assert.Equal((2m, "group", "VIP"), (price.MinQuantity!.Value, price.PriceType, price.PriceCode));

        var stock = Assert.IsType<InventoryOperation>(plan.Steps[7].Operation);
        Assert.Equal((25m, 5m, false), (stock.InStock!.Value, stock.Backorder!.Value, stock.Tracked!.Value));
        Assert.Equal(new HashSet<string> { "dress-s" }, plan.Steps[7].DependsOn);
    }

    [Fact]
    public void A_step_resolves_catalog_content_to_its_ref()
    {
        var plan = WritePlan.Parse(Plan);
        var refs = new Dictionary<string, string> { ["dress"] = "17__CatalogContent", ["dress-s"] = "18__CatalogContent" };

        var relation = Assert.IsType<RelationOperation>(WritePlan.Resolve(plan.Steps[4], new Dictionary<string, int>(), refs: refs));
        var price = Assert.IsType<PriceOperation>(WritePlan.Resolve(plan.Steps[6], new Dictionary<string, int>(), refs: refs));

        Assert.Equal(("17__CatalogContent", "18__CatalogContent"), (relation.Parent, relation.Child));
        Assert.Equal("18__CatalogContent", price.Entry);
    }

    [Theory]
    [InlineData("""{"op": "price", "entry": "code:x", "market": "DEFAULT"}""", "\"currency\" is required")]
    [InlineData("""{"op": "price", "entry": "code:x", "market": "DEFAULT", "currency": "USD", "amount": "lots"}""", "\"amount\" must be a number")]
    [InlineData("""{"op": "market", "id": "NORDIC"}""", "\"market\" is required")]
    [InlineData("""{"op": "relation", "parent": "code:p"}""", "\"child\" is required")]
    [InlineData("""{"op": "inventory", "entry": "code:x", "warehouse": "default", "inStock": 1, "reserved": 2}""", "unknown field \"reserved\"")]
    public void A_Commerce_step_that_isnt_one_is_a_plan_problem(string step, string problem)
    {
        var ex = Assert.Throws<UsageException>(() => WritePlan.Parse($$"""{"operations": [{{step}}]}"""));

        Assert.Contains(problem, ex.Message);
    }

    [Theory]
    [InlineData(CommerceWriteStatus.Created, true)]
    [InlineData(CommerceWriteStatus.Updated, true)]
    [InlineData(CommerceWriteStatus.Removed, true)]
    [InlineData(CommerceWriteStatus.Unchanged, false)]
    [InlineData(CommerceWriteStatus.Absent, false)]
    public void A_plan_counts_a_Commerce_step_saved_only_when_it_changed_something(string status, bool saved)
    {
        var price = new CommercePrice("DEFAULT", "USD", 49.9m, PriceTypes.All, "", 0, new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), null);

        Assert.Equal(saved, PlanRunner.Changed(new PriceResult("18__CatalogContent", "P-1", price, status, null, false)));
        Assert.Equal(saved, PlanRunner.Changed(new RelationResult("17__CatalogContent", "18__CatalogContent", RelationKinds.Variant, status, null, 0, false)));
        Assert.Equal(saved, PlanRunner.Changed(new MarketResult(new CommerceMarket("NORDIC", "Nordic", true, "SEK", ["SEK"], "en", ["en"], [], null, false), status, [], false)));
    }

    [Fact]
    public void The_undo_of_a_price_names_its_whole_key()
    {
        var price = new CommercePrice("DEFAULT", "USD", 59m, PriceTypes.Group, "VIP", 2, new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), null);
        var op = new PriceOperation("code:P-1", "DEFAULT", "USD", 59m);

        var created = UndoHints.For(op, new PriceResult("18__CatalogContent", "P-1", price, CommerceWriteStatus.Created, null, false));
        var updated = UndoHints.For(op, new PriceResult("18__CatalogContent", "P-1", price, CommerceWriteStatus.Updated, price with { Amount = 79m }, false));
        var unchanged = UndoHints.For(op, new PriceResult("18__CatalogContent", "P-1", price, CommerceWriteStatus.Unchanged, null, false));

        Assert.Equal("opticli commerce price remove 18__CatalogContent --market DEFAULT --currency USD --price-type group --price-code VIP --min-quantity 2 --valid-from 2026-12-01T00:00:00Z", created);
        Assert.EndsWith("--valid-from 2026-12-01T00:00:00Z --amount 79", updated);
        Assert.Null(unchanged);
    }

    private static MarketResult Market(string status, IReadOnlyList<string> changes, params string[] currencies) =>
        new(new CommerceMarket("EURO", "Euro", true, currencies[0], currencies, "en", ["en"], [], null, false), status, changes, true);

    private static WarehouseResult Warehouse(string status) =>
        new(new CommerceWarehouse("berlin", "Berlin", true, false, false, false, false, 0), status, status == CommerceWriteStatus.Unchanged ? [] : ["name"], true);

    /// <summary>
    /// A plan that grows: a price on a market an earlier step makes (or gives new currencies), or stock in a warehouse an
    /// earlier step makes, is dry-run against the planned market's currencies or without the warehouse; anything else an
    /// earlier step does to the market or warehouse (a rename) leaves the step checked as usual.
    /// </summary>
    [Fact]
    public void A_price_or_stock_is_checked_against_the_market_or_warehouse_an_earlier_step_makes()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "market", "market": "EURO", "currencies": ["EUR"], "languages": ["en"]},
              {"op": "warehouse", "code": "berlin"},
              {"op": "price", "entry": "code:P-1001-M", "market": "euro", "currency": "EUR", "amount": 69},
              {"op": "inventory", "entry": "code:P-1001-M", "warehouse": "Berlin", "inStock": 4},
              {"op": "price", "entry": "code:P-1001-M", "market": "DEFAULT", "currency": "USD", "amount": 79},
              {"op": "inventory", "entry": "code:P-1001-M", "warehouse": "default", "inStock": 4}
            ]}
            """);
        PlanStepResult[] Checks(MarketResult market, WarehouseResult warehouse) =>
            [new(0, "market", null, PlanStepStatus.Valid, Result: market), new(1, "warehouse", null, PlanStepStatus.Valid, Result: warehouse)];

        // A new market and warehouse: the price is checked against the planned currencies, the stock without the warehouse.
        var created = Checks(Market(CommerceWriteStatus.Created, ["name", "currencies"], "EUR"), Warehouse(CommerceWriteStatus.Created));
        var price = PlanRunner.CommerceEarlier(plan.Steps[2], plan.Steps, created)!.Value;
        Assert.Equal(["EUR"], Assert.IsType<PriceOperation>(price.Planned).PlannedCurrencies!);
        Assert.Contains("Operation 0 makes the market 'EURO' (EUR)", price.Warning);
        var stock = PlanRunner.CommerceEarlier(plan.Steps[3], plan.Steps, created)!.Value;
        Assert.True(Assert.IsType<InventoryOperation>(stock.Planned).PlannedWarehouse);
        Assert.Contains("Operation 1 makes the warehouse 'Berlin'", stock.Warning);
        Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[4], plan.Steps, created));
        Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[5], plan.Steps, created));
        // A market step after the price doesn't count: the price is checked against the site as it is.
        Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[0], plan.Steps, created));

        // New currencies on a market the site has: checked against them.
        var currencies = Checks(Market(CommerceWriteStatus.Updated, ["currencies"], "EUR", "SEK"), Warehouse(CommerceWriteStatus.Unchanged));
        var updated = PlanRunner.CommerceEarlier(plan.Steps[2], plan.Steps, currencies)!.Value;
        Assert.Equal(["EUR", "SEK"], Assert.IsType<PriceOperation>(updated.Planned).PlannedCurrencies!);
        Assert.Contains("changes the currencies of the market 'EURO'", updated.Warning);

        // A rename, other fields, or a plan run again: the price and stock are checked as usual, so the dry run catches
        // a currency, an entry or dates the site would refuse when the plan runs.
        foreach (var (market, warehouse) in new[]
        {
            (Market(CommerceWriteStatus.Updated, ["name"], "EUR"), Warehouse(CommerceWriteStatus.Updated)),
            (Market(CommerceWriteStatus.Updated, ["countries", "defaultLanguage"], "EUR"), Warehouse(CommerceWriteStatus.Updated)),
            (Market(CommerceWriteStatus.Unchanged, [], "EUR"), Warehouse(CommerceWriteStatus.Unchanged)),
        })
        {
            Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[2], plan.Steps, Checks(market, warehouse)));
            Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[3], plan.Steps, Checks(market, warehouse)));
        }
        // An earlier step the dry run refused (no result) leaves the step checked as usual too.
        Assert.Null(PlanRunner.CommerceEarlier(plan.Steps[2], plan.Steps, []));
    }
}
