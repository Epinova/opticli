using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Agent.Commerce.Inventory;
using OptiCli.Agent.Commerce.Markets;
using OptiCli.Agent.Commerce.Pricing;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Tests;

/// <summary>The rules of the Commerce writes, apart from Commerce's services: keys, what counts as unchanged, defaults.</summary>
public class CommerceRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    private static CommercePrice Price(decimal amount, DateTime from, DateTime? until = null, string market = "DEFAULT", string currency = "USD") =>
        new(market, currency, amount, PriceTypes.All, "", 0, from, until);

    [Fact]
    public void Without_a_start_a_price_step_names_the_base_price_not_a_campaign()
    {
        var key = PriceKeys.Key("DEFAULT", "usd", null, null, null, null);
        var basePrice = Price(79, Today);
        var campaign = Price(59, Today.AddDays(8), Today.AddDays(39));

        Assert.True(PriceKeys.Matches(basePrice, key));
        Assert.False(PriceKeys.Matches(campaign, key));
        Assert.False(PriceKeys.Matches(Price(79, Today, market: "NORDIC"), key));
        Assert.False(PriceKeys.Matches(Price(79, Today, currency: "SEK"), key));
    }

    [Fact]
    public void With_a_start_a_price_step_names_that_row_to_the_second()
    {
        var key = PriceKeys.Key("DEFAULT", "USD", "all", "", 0, new DateTime(2026, 12, 1, 0, 0, 0, 400, DateTimeKind.Utc));

        Assert.True(PriceKeys.Matches(Price(59, new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), Today.AddDays(39)), key));
        Assert.False(PriceKeys.Matches(Price(79, Today), key));
    }

    [Fact]
    public void A_new_price_starts_today_and_an_existing_one_keeps_its_start()
    {
        var key = PriceKeys.Key("DEFAULT", "USD", null, null, null, null);

        Assert.Equal(Today, PriceKeys.Wanted(key, 49.9m, null, null, Today.AddHours(15)).ValidFrom);
        Assert.Equal(Today.AddDays(-30), PriceKeys.Wanted(key, 49.9m, null, Price(40, Today.AddDays(-30)), Today.AddHours(15)).ValidFrom);
    }

    [Fact]
    public void A_price_is_unchanged_when_its_amount_and_end_are_as_asked()
    {
        var key = PriceKeys.Key("DEFAULT", "USD", null, null, null, Today);
        var existing = Price(49.900000000m, Today, Today.AddDays(10).AddMilliseconds(300));

        Assert.True(PriceKeys.Same(existing, PriceKeys.Wanted(key, 49.9m, Today.AddDays(10), existing, Today)));
        Assert.False(PriceKeys.Same(existing, PriceKeys.Wanted(key, 45m, Today.AddDays(10), existing, Today)));
        Assert.False(PriceKeys.Same(existing, PriceKeys.Wanted(key, 49.9m, null, existing, Today)));
    }

    [Theory]
    [InlineData("US", null, null, "not a currency")]
    [InlineData("USD", "member", null, "not a price type")]
    [InlineData("USD", "all", "VIP", "has no price code")]
    [InlineData("USD", "group", null, "needs the price group")]
    [InlineData("USD", "user", "", "needs the user name")]
    public void A_price_key_that_isnt_one_is_refused(string currency, string? type, string? code, string message)
    {
        var ex = Assert.Throws<AgentException>(() => PriceKeys.Key("DEFAULT", currency, type, code, null, null));

        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Prices_and_quantities_show_without_the_database_s_trailing_zeros()
    {
        Assert.Equal("49.9", Decimals.Plain(49.900000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0", Decimals.Plain(0.000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2", Decimals.Plain(2.000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_warehouse_keeps_what_isnt_given_and_reports_what_changed()
    {
        var created = WarehousesEndpoint.Wanted("oslo", new WarehouseRequest("oslo") { PickupLocation = true }, null);
        Assert.Equal(new CommerceWarehouse("oslo", "oslo", true, false, false, true, false, 0), created);
        Assert.Equal(7, WarehousesEndpoint.Changes(null, created).Count);

        var renamed = WarehousesEndpoint.Wanted("oslo", new WarehouseRequest("oslo") { Name = "Oslo" }, created);
        Assert.Equal(["name"], WarehousesEndpoint.Changes(created, renamed));
        Assert.True(renamed.PickupLocation);
        Assert.Empty(WarehousesEndpoint.Changes(renamed, WarehousesEndpoint.Wanted("oslo", new WarehouseRequest("oslo"), renamed)));
    }

    [Fact]
    public void Stock_keeps_what_isnt_given_and_a_new_record_is_tracked()
    {
        var created = InventoryEndpoint.Wanted("default", new InventoryRequest("code:P-1", "default") { InStock = 25 }, null);
        Assert.Equal(new CommerceStock("default", 25, 0, 0, true), created);

        var backorder = InventoryEndpoint.Wanted("default", new InventoryRequest("code:P-1", "default") { Backorder = 5 }, created);
        Assert.Equal(new CommerceStock("default", 25, 5, 0, true), backorder);
    }

    [Fact]
    public void A_new_market_needs_currencies_and_languages_and_defaults_to_the_first()
    {
        var missing = Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Currencies = ["SEK"] }, null));
        Assert.Contains("give its currencies and languages", missing.Message);

        var market = MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Currencies = ["sek", "NOK"], Languages = ["en", "sv"], Countries = ["swe"] }, null);
        Assert.Equal("SEK", market.DefaultCurrency);
        Assert.Equal(["SEK", "NOK"], market.Currencies);
        Assert.Equal("en", market.DefaultLanguage);
        Assert.Equal(["SWE"], market.Countries);
        Assert.Equal("NORDIC", market.Name);
        Assert.True(market.Enabled);
    }

    [Fact]
    public void A_market_s_lists_are_compared_as_sets_and_a_default_must_be_in_its_list()
    {
        var before = new CommerceMarket("NORDIC", "Nordic", true, "SEK", ["SEK", "NOK"], "en", ["en", "sv"], ["SWE", "NOR"], null, false);

        var same = MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Currencies = ["NOK", "SEK"], Countries = ["NOR", "SWE"] }, before);
        Assert.Empty(MarketsEndpoint.Changes(before, same));

        var changed = MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Languages = ["sv"], Enabled = false }, before);
        Assert.Equal("sv", changed.DefaultLanguage);
        Assert.Equal(["enabled", "defaultLanguage", "languages"], MarketsEndpoint.Changes(before, changed));

        var wrong = Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { DefaultCurrency = "EUR" }, before));
        Assert.Contains("isn't one of the market's currencies", wrong.Message);
        Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Countries = ["SE"] }, before));
    }

    [Fact]
    public void A_price_in_a_currency_its_market_doesnt_have_is_refused_before_a_dry_run_answers()
    {
        PriceKeys.RequireMarketCurrency("NORDIC", ["SEK", "NOK"], "SEK");

        var ex = Assert.Throws<AgentException>(() => PriceKeys.RequireMarketCurrency("NORDIC", ["SEK", "NOK"], "EUR"));
        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("doesn't have the currency EUR: it has SEK, NOK", ex.Message);
        Assert.Contains("--currencies SEK,NOK,EUR", ex.Hint);
    }

    [Fact]
    public void A_price_that_ends_before_it_starts_is_refused_before_a_dry_run_answers()
    {
        var key = PriceKeys.Key("DEFAULT", "USD", null, null, null, Today.AddDays(10));

        var ex = Assert.Throws<AgentException>(() => PriceKeys.RequireOrder(PriceKeys.Wanted(key, 10m, Today, null, Today)));
        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("isn't after validFrom", ex.Message);
        Assert.Throws<AgentException>(() => PriceKeys.RequireOrder(PriceKeys.Wanted(key, 10m, Today.AddDays(10), null, Today)));
        PriceKeys.RequireOrder(PriceKeys.Wanted(key, 10m, Today.AddDays(11), null, Today));
    }

    [Fact]
    public void A_market_s_currencies_must_be_ones_Commerce_has()
    {
        MarketsEndpoint.RequireKnown(["SEK"], new HashSet<string>(["USD", "SEK"], StringComparer.OrdinalIgnoreCase));
        MarketsEndpoint.RequireKnown(["XYZ"], null);

        var ex = Assert.Throws<AgentException>(() => MarketsEndpoint.RequireKnown(["SEK", "XYZ"], new HashSet<string>(["USD", "SEK"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("no currency XYZ", ex.Message);
    }

    [Theory]
    [InlineData("xx-YY")]
    [InlineData("klingon")]
    public void A_market_language_is_a_culture_NET_knows(string language)
    {
        var ex = Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Currencies = ["SEK"], Languages = [language] }, null));

        Assert.Contains("is not a language", ex.Message);
        Assert.Equal(["sv-SE"], MarketsEndpoint.Wanted("NORDIC", new MarketRequest("NORDIC") { Currencies = ["SEK"], Languages = ["sv-se"] }, null).Languages);
    }

    [Theory]
    [InlineData(CatalogKinds.Product, CatalogKinds.Variation, RelationKinds.Variant)]
    [InlineData(CatalogKinds.Bundle, CatalogKinds.Variation, RelationKinds.BundleEntry)]
    [InlineData(CatalogKinds.Bundle, CatalogKinds.Product, RelationKinds.BundleEntry)]
    [InlineData(CatalogKinds.Package, CatalogKinds.Variation, RelationKinds.PackageEntry)]
    [InlineData(CatalogKinds.Node, CatalogKinds.Product, RelationKinds.Category)]
    [InlineData(CatalogKinds.Product, CatalogKinds.Product, null)]
    [InlineData(CatalogKinds.Variation, CatalogKinds.Product, null)]
    [InlineData(CatalogKinds.Node, CatalogKinds.Node, null)]
    [InlineData(CatalogKinds.Catalog, CatalogKinds.Product, null)]
    public void The_relation_follows_from_the_two_kinds(string parent, string child, string? kind) =>
        Assert.Equal(kind, RelationsEndpoint.KindOf(parent, child));
}
