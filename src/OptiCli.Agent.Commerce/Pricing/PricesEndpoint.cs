using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Core;
using Mediachase.Commerce;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Markets;
using Mediachase.Commerce.Pricing;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Pricing;

/// <summary>
/// <see cref="AgentRoutes.CommercePrices"/>: an entry's price rows through Commerce's <see cref="IPriceDetailService"/>. A
/// write upserts (or removes) the one row its key names: market, currency, price type and code, minimum quantity and
/// valid-from. The entry's other rows stay as they are.
/// </summary>
internal static class PricesEndpoint
{
    public static PriceResult Write(AgentRequest request, PriceRequest body)
    {
        var services = request.Context.RequestServices;
        var entry = PricedEntry(new CatalogContentFinder(services), body.Entry);
        var market = Market(services, body.Market);
        var key = PriceKeys.Key(market, body.Currency, body.PriceType, body.PriceCode, body.MinQuantity, body.ValidFrom);
        if (body.ValidFrom is null && body.ValidUntil is not null && !body.Remove)
        {
            throw AgentException.Usage("A price with an end (validUntil) needs its start (validFrom) too: without validFrom, a price step names the entry's price that has no end.",
                "Give --valid-from (a plan's \"validFrom\") with --valid-until, as for a campaign price.");
        }
        var prices = services.GetRequiredService<IPriceDetailService>();
        var rows = prices.List(entry.ContentLink.ToReferenceWithoutVersion()).Select(r => (r.PriceValueId, Price: PriceKeys.ToPrice(r))).ToList();
        var matches = rows.Where(r => PriceKeys.Matches(r.Price, key)).ToList();
        if (matches.Count > 1)
        {
            throw AgentException.Usage(
                $"{entry.Code} has {matches.Count} {key.Market} {key.Currency} prices for that customer and quantity{(key.ValidFrom is null ? " without an end" : "")}, from {string.Join(", ", matches.Select(m => m.Price.ValidFrom.ToString("u", System.Globalization.CultureInfo.InvariantCulture)))}.",
                "Give validFrom (--valid-from) to name one.");
        }
        var existing = matches.Count == 1 ? matches[0] : ((long PriceValueId, CommercePrice Price)?)null;
        var entryRef = CatalogRefSyntax.Format(entry.ContentLink.ID);

        if (body.Remove)
        {
            if (existing is not { } removed)
            {
                return new PriceResult(entryRef, entry.Code, PriceKeys.Placeholder(key), CommerceWriteStatus.Absent, null, body.DryRun);
            }
            if (!body.DryRun)
            {
                prices.Delete([removed.PriceValueId]);
            }
            return new PriceResult(entryRef, entry.Code, removed.Price, CommerceWriteStatus.Removed, null, body.DryRun);
        }

        var amount = body.Amount ?? throw AgentException.Usage("amount is required (or remove the price).");
        if (amount < 0)
        {
            throw AgentException.Usage("amount can't be negative.");
        }
        var wanted = PriceKeys.Wanted(key, amount, body.ValidUntil, existing?.Price, DateTime.UtcNow);
        var status = existing is not { } before ? CommerceWriteStatus.Created
            : PriceKeys.Same(before.Price, wanted) ? CommerceWriteStatus.Unchanged
            : CommerceWriteStatus.Updated;
        if (!body.DryRun && status != CommerceWriteStatus.Unchanged)
        {
            prices.Save([new PriceDetailValue
            {
                PriceValueId = existing?.PriceValueId ?? 0,
                CatalogKey = new CatalogKey(entry.Code),
                MarketId = new MarketId(wanted.Market),
                CustomerPricing = new CustomerPricing(PriceKeys.ToCommerce(wanted.PriceType), wanted.PriceCode),
                ValidFrom = wanted.ValidFrom,
                ValidUntil = wanted.ValidUntil,
                MinQuantity = wanted.MinQuantity,
                UnitPrice = new Money(wanted.Amount, new Currency(wanted.Currency)),
            }]);
        }
        return new PriceResult(entryRef, entry.Code, wanted, status, status == CommerceWriteStatus.Updated ? existing?.Price : null, body.DryRun);
    }

    public static PriceList List(AgentRequest request)
    {
        var services = request.Context.RequestServices;
        var entry = PricedEntry(new CatalogContentFinder(services), request.Context.Request.Query["ref"].ToString(), reading: true);
        var prices = services.GetRequiredService<IPriceDetailService>().List(entry.ContentLink.ToReferenceWithoutVersion())
            .Select(PriceKeys.ToPrice)
            .OrderBy(p => p.Market, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Currency, StringComparer.Ordinal).ThenBy(p => p.PriceType, StringComparer.Ordinal)
            .ThenBy(p => p.PriceCode, StringComparer.Ordinal).ThenBy(p => p.MinQuantity).ThenBy(p => p.ValidFrom)
            .ToList();
        return new PriceList(CatalogRefSyntax.Format(entry.ContentLink.ID), entry.Code, prices);
    }

    /// <summary>The entry a price is for: a variant or package (what Commerce prices; a product or bundle has its variants' prices).</summary>
    internal static EntryContentBase PricedEntry(CatalogContentFinder finder, string? reference, bool reading = false)
    {
        var content = finder.Require(reference, "entry");
        var kind = CatalogContentFinder.KindOf(content);
        return content is EntryContentBase entry && (reading || kind is CatalogKinds.Variation or CatalogKinds.Package or CatalogKinds.DynamicPackage)
            ? entry
            : throw AgentException.Usage(
                $"'{content.Name}' is a {kind ?? "catalog item"}; Commerce keeps prices and stock on variants and packages.",
                kind is CatalogKinds.Product or CatalogKinds.Bundle ? "Give its variants (or the bundle's entries) prices and stock; a product shows theirs." : null);
    }

    /// <exception cref="AgentException"><c>not_found</c>, listing the markets.</exception>
    internal static IMarket Market(IServiceProvider services, string? id)
    {
        var markets = services.GetRequiredService<IMarketService>();
        var trimmed = id?.Trim() ?? "";
        return (trimmed.Length > 0 ? markets.GetMarket(new MarketId(trimmed)) : null)
            ?? throw AgentException.NotFound($"No market '{trimmed}'.", $"Markets: {string.Join(", ", markets.GetAllMarkets().Select(m => m.MarketId.Value))}; make one with `opticli commerce market set`.");
    }
}

/// <summary>A price row's key and values, apart from Commerce's types where they can be, so the rules can be tested.</summary>
internal static class PriceKeys
{
    /// <param name="Market">The market's id as Commerce has it.</param>
    internal sealed record PriceKey(string Market, string Currency, string PriceType, string PriceCode, decimal MinQuantity, DateTime? ValidFrom);

    public static PriceKey Key(IMarket market, string? currency, string? priceType, string? priceCode, decimal? minQuantity, DateTime? validFrom) =>
        Key(market.MarketId.Value, currency, priceType, priceCode, minQuantity, validFrom);

    /// <exception cref="AgentException">A value that isn't one.</exception>
    public static PriceKey Key(string market, string? currency, string? priceType, string? priceCode, decimal? minQuantity, DateTime? validFrom)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? "";
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw AgentException.Usage($"'{currency}' is not a currency: give its ISO 4217 code (USD, EUR, SEK).");
        }
        var type = string.IsNullOrWhiteSpace(priceType) ? PriceTypes.All : priceType.Trim().ToLowerInvariant();
        if (type is not (PriceTypes.All or PriceTypes.User or PriceTypes.Group))
        {
            throw AgentException.Usage($"'{priceType}' is not a price type: all, user or group.");
        }
        var priceCodeValue = priceCode?.Trim() ?? "";
        if ((type == PriceTypes.All) != (priceCodeValue.Length == 0))
        {
            throw AgentException.Usage(type == PriceTypes.All
                ? "A price for all customers has no price code; give --price-type user or group with it."
                : $"A {type} price needs the {(type == PriceTypes.User ? "user name" : "price group")} as its price code.");
        }
        if (minQuantity is < 0)
        {
            throw AgentException.Usage("minQuantity can't be negative.");
        }
        return new PriceKey(market, code, type, priceCodeValue, minQuantity ?? 0, validFrom is { } from ? Second(from) : null);
    }

    /// <summary>Whether a row has the key; without a valid-from, the key names the row with no end (the base price).</summary>
    public static bool Matches(CommercePrice price, PriceKey key) =>
        string.Equals(price.Market, key.Market, StringComparison.OrdinalIgnoreCase)
        && string.Equals(price.Currency, key.Currency, StringComparison.OrdinalIgnoreCase)
        && price.PriceType == key.PriceType
        && string.Equals(price.PriceCode, key.PriceCode, StringComparison.Ordinal)
        && price.MinQuantity == key.MinQuantity
        && (key.ValidFrom is null ? price.ValidUntil is null : Second(price.ValidFrom) == key.ValidFrom);

    /// <summary>The row as asked for: an existing row keeps its valid-from; a new one starts at <paramref name="key"/>'s, else today.</summary>
    public static CommercePrice Wanted(PriceKey key, decimal amount, DateTime? validUntil, CommercePrice? existing, DateTime now) =>
        new(key.Market, key.Currency, amount, key.PriceType, key.PriceCode, key.MinQuantity,
            key.ValidFrom ?? existing?.ValidFrom ?? DateTime.SpecifyKind(now.Date, DateTimeKind.Utc),
            validUntil is { } until ? Second(until) : null);

    /// <summary>Whether a row already has what is asked: the amount and the end (the key matched already).</summary>
    public static bool Same(CommercePrice existing, CommercePrice wanted) =>
        existing.Amount == wanted.Amount && (existing.ValidUntil is { } a ? Second(a) : (DateTime?)null) == wanted.ValidUntil;

    /// <summary>What a removal that found nothing reports.</summary>
    public static CommercePrice Placeholder(PriceKey key) =>
        new(key.Market, key.Currency, 0, key.PriceType, key.PriceCode, key.MinQuantity, key.ValidFrom ?? default, null);

    public static CommercePrice ToPrice(IPriceValue value) => new(
        value.MarketId.Value,
        value.UnitPrice.Currency.CurrencyCode,
        Decimals.Plain(value.UnitPrice.Amount),
        FromCommerce(value.CustomerPricing.PriceTypeId),
        value.CustomerPricing.PriceCode ?? "",
        Decimals.Plain(value.MinQuantity),
        DateTime.SpecifyKind(value.ValidFrom, DateTimeKind.Utc),
        value.ValidUntil is { } until ? DateTime.SpecifyKind(until, DateTimeKind.Utc) : null);

    public static CustomerPricing.PriceType ToCommerce(string type) => type switch
    {
        PriceTypes.User => CustomerPricing.PriceType.UserName,
        PriceTypes.Group => CustomerPricing.PriceType.PriceGroup,
        _ => CustomerPricing.PriceType.AllCustomers,
    };

    public static string FromCommerce(CustomerPricing.PriceType type) => type switch
    {
        CustomerPricing.PriceType.UserName => PriceTypes.User,
        CustomerPricing.PriceType.PriceGroup => PriceTypes.Group,
        _ => PriceTypes.All,
    };

    /// <summary>In UTC, to the second: what the database keeps of a time, so a run compares what it wrote.</summary>
    public static DateTime Second(DateTime time)
    {
        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}

internal static class Decimals
{
    /// <summary>Without the trailing zeros of the database's scale: 49.9, not 49.900000000 (or 0, not 0E-9).</summary>
    public static decimal Plain(decimal value) => value / 1.000000000000000000000000000000000m;
}
