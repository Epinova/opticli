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

    private static CommercePrice Price(decimal amount, DateTime from, DateTime? until = null, string market = "DEFAULT", string currency = "USD", string type = PriceTypes.All, string code = "") =>
        new(market, currency, amount, type, code, 0, from, until);

    /// <summary>A site with two price types of its own, as the Commerce test sites have.</summary>
    private static readonly PriceTypeSet SiteTypes = PriceTypeSet.Of([("MEMBER", 3, "Members"), ("OUTLET", 4, "Outlet")]);

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
    public void A_site_s_own_price_types_are_named_by_their_key_in_lower_case()
    {
        Assert.Equal(["all", "user", "group", "member", "outlet"], SiteTypes.Types.Select(t => t.Name));
        Assert.Equal([0, 1, 2, 3, 4], SiteTypes.Types.Select(t => t.Id));
        Assert.Equal("Members", SiteTypes.Types[3].Description);
        Assert.Equal("Customer Price Group", SiteTypes.Types[2].Description);
        // A row of a type the site no longer defines shows its number.
        Assert.Equal("7", SiteTypes.Name(7));

        Assert.Equal(3, SiteTypes.Id("Member"));
        Assert.Equal(4, SiteTypes.Id(" 4 "));
        Assert.Equal(0, SiteTypes.Id(null));
        Assert.Equal(2, SiteTypes.Id("GROUP"));
        Assert.Equal(7, SiteTypes.Id("7", anyNumber: true));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("7")]
    [InlineData("Members")]
    public void A_price_type_the_site_doesnt_have_is_refused_with_the_site_s_types(string type)
    {
        var ex = Assert.Throws<AgentException>(() => SiteTypes.Id(type));

        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("it has all, user, group, member, outlet", ex.Message);
    }

    [Fact]
    public void A_key_that_would_clash_with_another_name_is_named_by_its_number()
    {
        var types = PriceTypeSet.Of([("All", 5, null), ("12", 6, "Twelve"), ("", 8, null), ("AllCustomers", 0, "Everyone")]);

        Assert.Equal(["all", "user", "group", "5", "6", "8"], types.Types.Select(t => t.Name));
        Assert.Equal("Everyone", types.Types[0].Description);
        Assert.Equal(0, types.Id("all"));
        Assert.Equal(5, types.Id("5"));
    }

    [Fact]
    public void The_base_price_never_matches_a_row_of_the_site_s_own_type()
    {
        // The same market, currency, quantity and start: before 0.37.0 all three read as `all`.
        var basePrice = Price(29, Today);
        var member = Price(25, Today, type: "member");
        var outlet = Price(19, Today, type: "outlet", code: "{\"rule\":\"clearance\"}");

        var key = PriceKeys.Key("DEFAULT", "USD", null, null, null, null, SiteTypes);
        Assert.Equal([basePrice], new[] { basePrice, member, outlet }.Where(p => PriceKeys.Matches(p, key)));

        var memberKey = PriceKeys.Key("DEFAULT", "USD", "MEMBER", null, null, null, SiteTypes);
        Assert.Equal(3, memberKey.PriceTypeId);
        Assert.Equal([member], new[] { basePrice, member, outlet }.Where(p => PriceKeys.Matches(p, memberKey)));

        var outletKey = PriceKeys.Key("DEFAULT", "USD", "4", " {\"rule\":\"clearance\"} ", null, null, SiteTypes);
        Assert.Equal("{\"rule\":\"clearance\"}", outletKey.PriceCode);
        Assert.Equal([outlet], new[] { basePrice, member, outlet }.Where(p => PriceKeys.Matches(p, outletKey)));
    }

    [Fact]
    public void A_site_s_own_type_takes_any_price_code_or_none_up_to_Commerce_s_length()
    {
        Assert.Equal("", PriceKeys.Key("DEFAULT", "USD", "member", null, null, null, SiteTypes).PriceCode);
        Assert.Equal("gold", PriceKeys.Key("DEFAULT", "USD", "member", "gold", null, null, SiteTypes).PriceCode);
        PriceKeys.Key("DEFAULT", "USD", "member", new string('x', PriceKeys.MaxPriceCode), null, null, SiteTypes);

        var ex = Assert.Throws<AgentException>(() => PriceKeys.Key("DEFAULT", "USD", "member", new string('x', PriceKeys.MaxPriceCode + 1), null, null, SiteTypes));
        Assert.Contains("at most 256", ex.Message);
        Assert.Throws<AgentException>(() => PriceKeys.Key("DEFAULT", "USD", "all", "gold", null, null, SiteTypes));
    }

    [Fact]
    public void A_removal_takes_the_number_of_a_type_the_site_no_longer_has()
    {
        Assert.Throws<AgentException>(() => PriceKeys.Key("DEFAULT", "USD", "7", null, null, null, SiteTypes));

        var key = PriceKeys.Key("DEFAULT", "USD", "7", null, null, null, SiteTypes, remove: true);
        Assert.Equal("7", key.PriceType);
        Assert.True(PriceKeys.Matches(Price(10, Today, type: "7"), key));
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
    public void A_market_written_without_lists_keeps_them_empty_until_given()
    {
        // As SQL writes a market: a default currency and language, no MarketCurrencies or MarketLanguages rows.
        var before = new CommerceMarket("BARE", "Bare", true, "USD", [], "en", [], [], null, false);

        var renamed = MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { Name = "Renamed" }, before);
        Assert.Empty(renamed.Currencies);
        Assert.Equal("USD", renamed.DefaultCurrency);
        Assert.Equal(["name"], MarketsEndpoint.Changes(before, renamed));
        // Commerce's save lists the defaults, which the result then says.
        Assert.Equal(["name", "currencies", "languages"], MarketsEndpoint.Changes(before, MarketsEndpoint.WithDefaultsListed(renamed)));
        Assert.Empty(MarketsEndpoint.Changes(before, MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE"), before)));

        // A default alone isn't checked against an empty list; with a list given it is.
        Assert.Equal("EUR", MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { DefaultCurrency = "EUR" }, before).DefaultCurrency);
        var listed = MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { Currencies = ["EUR", "USD"] }, before);
        Assert.Equal("USD", listed.DefaultCurrency);
        Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { Currencies = ["EUR"], DefaultCurrency = "SEK" }, before));
        Assert.Equal("EUR", MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { Currencies = ["EUR"] }, before).DefaultCurrency);

        var empty = Assert.Throws<AgentException>(() => MarketsEndpoint.Wanted("BARE", new MarketRequest("BARE") { Languages = [] }, before));
        Assert.Contains("at least one currency and one language", empty.Message);
    }

    [Fact]
    public void A_market_s_removal_takes_only_its_id()
    {
        MarketsEndpoint.RequireOnlyId(new MarketRequest("NORDIC") { Remove = true, DryRun = true });

        var ex = Assert.Throws<AgentException>(() => MarketsEndpoint.RequireOnlyId(new MarketRequest("NORDIC") { Remove = true, Name = "Nordic" }));
        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
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
