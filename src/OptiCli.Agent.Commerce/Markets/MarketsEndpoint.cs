using System.Globalization;
using EPiServer.Framework.Cache;
using Mediachase.Commerce;
using Mediachase.Commerce.Catalog.Managers;
using Mediachase.Commerce.Markets;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Markets;

/// <summary><see cref="AgentRoutes.CommerceMarkets"/>: markets by id, through Commerce's <see cref="IMarketService"/>.</summary>
internal static class MarketsEndpoint
{
    public static MarketResult Write(AgentRequest request, MarketRequest body)
    {
        SharedRemovals.Check(request, body.Remove, body.AllowDestructive, $"market {body.Id} (with its prices)");
        var id = body.Id?.Trim() ?? "";
        if (id.Length == 0 || id.Length > 8 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            // Commerce's MarketId column is 8 characters.
            throw AgentException.Usage($"'{body.Id}' is not a market id: 1 to 8 letters, digits, '-' or '_' (DEFAULT, US, NORDIC).");
        }
        var markets = request.Context.RequestServices.GetRequiredService<IMarketService>();
        var existing = markets.GetMarket(new MarketId(id));
        var before = existing is null ? null : ToMarket(existing);
        if (body.Remove)
        {
            return Remove(markets, request.Context.RequestServices.GetRequiredService<ISynchronizedObjectInstanceCache>(), body, before);
        }
        var wanted = Wanted(id, body, before);
        // Before a dry run answers: Commerce refuses a currency it doesn't know only when saving (a foreign key).
        RequireKnownCurrencies(wanted, KnownCurrencies());
        // Commerce keeps market names unique, also only when saving.
        if (markets.GetAllMarkets().FirstOrDefault(m => !string.Equals(m.MarketId.Value, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.MarketName?.Trim(), wanted.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            throw AgentException.Usage($"The market {named.MarketId.Value} is named '{named.MarketName}' already: market names are unique.", "Give this market another name (--name).");
        }
        var changes = Changes(before, wanted);
        if (before is not null && changes.Count > 0)
        {
            // Commerce's save puts the defaults in the lists (a market written with SQL may lack them): say so.
            wanted = WithDefaultsListed(wanted);
            changes = Changes(before, wanted);
        }
        var status = before is null ? CommerceWriteStatus.Created : changes.Count == 0 ? CommerceWriteStatus.Unchanged : CommerceWriteStatus.Updated;
        if (!body.DryRun && status != CommerceWriteStatus.Unchanged)
        {
            var market = existing is null ? new MarketImpl(new MarketId(wanted.Id)) : new MarketImpl(existing);
            market.MarketName = wanted.Name;
            market.IsEnabled = wanted.Enabled;
            // Commerce refuses a market without a description (null); an empty one is none.
            market.MarketDescription = wanted.Description ?? "";
            market.PricesIncludeTax = wanted.PricesIncludeTax;
            market.DefaultCurrency = new Currency(wanted.DefaultCurrency);
            market.DefaultLanguage = CultureInfo.GetCultureInfo(wanted.DefaultLanguage);
            Replace(market.CurrenciesCollection, wanted.Currencies.Select(c => new Currency(c)));
            Replace(market.LanguagesCollection, wanted.Languages.Select(CultureInfo.GetCultureInfo));
            Replace(market.CountriesCollection, wanted.Countries);
            if (existing is null)
            {
                markets.CreateMarket(market);
            }
            else
            {
                markets.UpdateMarket(market);
            }
        }
        return new MarketResult(wanted, status, changes, body.DryRun);
    }

    /// <summary>
    /// A removal (<see cref="IMarketService.DeleteMarket"/>). Commerce's database removes the market's prices (in every
    /// catalog), and its place in payment and shipping methods, with it; orders and carts keep its id. Commerce's API can't
    /// count a market's prices short of reading every entry's (a catalog's list is refused, a node's has only its own
    /// entries), so the result doesn't say how many.
    /// </summary>
    private static MarketResult Remove(IMarketService markets, ISynchronizedObjectInstanceCache cache, MarketRequest body, CommerceMarket? before)
    {
        RequireOnlyId(body);
        if (string.Equals(body.Id?.Trim(), MarketId.Default.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentException.Usage($"{MarketId.Default.Value} is Commerce's own market, which it falls back to: it can't be removed.",
                $"Turn it off instead: opticli commerce market set {MarketId.Default.Value} --enabled false.");
        }
        if (before is null)
        {
            return new MarketResult(null, CommerceWriteStatus.Absent, [], body.DryRun);
        }
        if (!body.DryRun)
        {
            markets.DeleteMarket(new MarketId(before.Id));
            ForgetCachedPrices(cache);
        }
        return new MarketResult(before, CommerceWriteStatus.Removed, [], body.DryRun);
    }

    /// <summary>
    /// Commerce's price caches, every entry's, on every server. The database removed the market's prices with it, but
    /// Commerce's price services keep each entry's rows they read in the cache until a save of that entry's prices: without
    /// this, reads still show the removed market's rows, and price writes match rows that are gone. Commerce has no call to
    /// clear them (it only drops one entry's after saving its prices, and the removal can't name the entries), so this
    /// removes the master keys every cached entry depends on (<c>PriceDetailDatabase</c>'s and <c>PriceServiceDatabase</c>'s,
    /// the same on Commerce 14 and 15); the synchronized <see cref="IObjectInstanceCache.Remove"/> tells the other servers.
    /// </summary>
    internal static void ForgetCachedPrices(ISynchronizedObjectInstanceCache cache)
    {
        foreach (var key in PriceCacheMasterKeys)
        {
            cache.Remove(key);
        }
    }

    /// <summary>The master keys of Commerce's price caches: <c>IPriceDetailService</c>'s and <c>IPriceService</c>'s.</summary>
    internal static readonly IReadOnlyList<string> PriceCacheMasterKeys = ["EP:PriceDetailsMasterKey", "EP:CatalogKeyPricesMasterCacheKey"];

    /// <exception cref="AgentException">A removal that gives more than the id.</exception>
    internal static void RequireOnlyId(MarketRequest body)
    {
        if (body.Name is not null || body.Enabled is not null || body.Currencies is not null || body.DefaultCurrency is not null || body.Languages is not null
            || body.DefaultLanguage is not null || body.Countries is not null || body.Description is not null || body.PricesIncludeTax is not null)
        {
            throw AgentException.Usage("A market's removal takes only its id: give the other fields to market set, or remove the market.");
        }
    }

    public static MarketList List(AgentRequest request) =>
        new(request.Context.RequestServices.GetRequiredService<IMarketService>().GetAllMarkets()
            .Select(ToMarket).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>
    /// What the market should be: what the request gives, else what it has. A new market needs currencies and languages;
    /// an existing one keeps its lists as they are, empty too (a market written with SQL), unless the request gives them.
    /// </summary>
    /// <exception cref="AgentException">A value that isn't one, an empty list, or a default not in its list.</exception>
    internal static CommerceMarket Wanted(string id, MarketRequest body, CommerceMarket? before)
    {
        if (body.Currencies is { Count: 0 } || body.Languages is { Count: 0 })
        {
            throw AgentException.Usage("A market needs at least one currency and one language: give them, or leave them out to keep the market's.");
        }
        var currencies = body.Currencies is { } givenCurrencies ? Distinct(givenCurrencies.Select(Currency)) : before?.Currencies.ToList() ?? [];
        var languages = body.Languages is { } givenLanguages ? Distinct(givenLanguages.Select(Language)) : before?.Languages.ToList() ?? [];
        if (before is null && (currencies.Count == 0 || languages.Count == 0))
        {
            throw AgentException.Usage($"The market {id} doesn't exist yet: give its currencies and languages (--currencies USD,EUR --languages en,sv).");
        }
        // An empty list (a market written with SQL) has nothing to check a default against; Commerce's save adds it there.
        var defaultCurrency = body.DefaultCurrency is { } currency ? Currency(currency)
            : before is not null && (currencies.Count == 0 || currencies.Contains(before.DefaultCurrency)) ? before.DefaultCurrency : currencies[0];
        var defaultLanguage = body.DefaultLanguage is { } language ? Language(language)
            : before is not null && (languages.Count == 0 || languages.Contains(before.DefaultLanguage, StringComparer.OrdinalIgnoreCase)) ? before.DefaultLanguage : languages[0];
        if (currencies.Count > 0 && !currencies.Contains(defaultCurrency))
        {
            throw AgentException.Usage($"The default currency {defaultCurrency} isn't one of the market's currencies ({string.Join(", ", currencies)}).");
        }
        if (languages.Count > 0 && !languages.Contains(defaultLanguage, StringComparer.OrdinalIgnoreCase))
        {
            throw AgentException.Usage($"The default language {defaultLanguage} isn't one of the market's languages ({string.Join(", ", languages)}).");
        }
        var countries = body.Countries is { } givenCountries ? Distinct(givenCountries.Select(Country)) : before?.Countries.ToList() ?? [];
        return new CommerceMarket(
            before?.Id ?? id,
            string.IsNullOrWhiteSpace(body.Name) ? before?.Name ?? id : body.Name.Trim(),
            body.Enabled ?? before?.Enabled ?? true,
            defaultCurrency,
            currencies,
            defaultLanguage,
            languages,
            countries,
            body.Description is null ? before?.Description : body.Description.Trim() is { Length: > 0 } description ? description : null,
            body.PricesIncludeTax ?? before?.PricesIncludeTax ?? false);
    }

    /// <summary>
    /// The market as Commerce saves it: its default currency and language in its lists (Commerce's save adds them). Only a
    /// market written with SQL lacks them.
    /// </summary>
    internal static CommerceMarket WithDefaultsListed(CommerceMarket market) => market with
    {
        Currencies = market.Currencies.Contains(market.DefaultCurrency, StringComparer.OrdinalIgnoreCase) ? market.Currencies : [.. market.Currencies, market.DefaultCurrency],
        Languages = market.Languages.Contains(market.DefaultLanguage, StringComparer.OrdinalIgnoreCase) ? market.Languages : [.. market.Languages, market.DefaultLanguage],
    };

    /// <summary>The fields that differ (lists compared as sets); every field for a new market.</summary>
    internal static IReadOnlyList<string> Changes(CommerceMarket? before, CommerceMarket wanted)
    {
        if (before is null)
        {
            return ["name", "enabled", "defaultCurrency", "currencies", "defaultLanguage", "languages", "countries", "description", "pricesIncludeTax"];
        }
        var changes = new List<string>();
        void Check(string name, bool same)
        {
            if (!same)
            {
                changes.Add(name);
            }
        }
        Check("name", before.Name == wanted.Name);
        Check("enabled", before.Enabled == wanted.Enabled);
        Check("defaultCurrency", before.DefaultCurrency == wanted.DefaultCurrency);
        Check("currencies", SameSet(before.Currencies, wanted.Currencies));
        Check("defaultLanguage", string.Equals(before.DefaultLanguage, wanted.DefaultLanguage, StringComparison.OrdinalIgnoreCase));
        Check("languages", SameSet(before.Languages, wanted.Languages));
        Check("countries", SameSet(before.Countries, wanted.Countries));
        Check("description", (before.Description ?? "") == (wanted.Description ?? ""));
        Check("pricesIncludeTax", before.PricesIncludeTax == wanted.PricesIncludeTax);
        return changes;
    }

    internal static CommerceMarket ToMarket(IMarket market) => new(
        market.MarketId.Value,
        market.MarketName,
        market.IsEnabled,
        market.DefaultCurrency.CurrencyCode,
        market.Currencies.Select(c => c.CurrencyCode).ToList(),
        market.DefaultLanguage?.Name ?? "",
        market.Languages.Select(l => l.Name).ToList(),
        market.Countries.ToList(),
        string.IsNullOrWhiteSpace(market.MarketDescription) ? null : market.MarketDescription,
        market.PricesIncludeTax);

    /// <summary>The currencies Commerce knows (its Currency table); null when they can't be read, which leaves the check to Commerce.</summary>
    private static IReadOnlyCollection<string>? KnownCurrencies()
    {
        try
        {
            return CurrencyManager.GetCurrencyDto().Currency.Select(c => c.CurrencyCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opticli] Commerce's currencies couldn't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The currencies Commerce will save for the market are ones it knows: its list and its default, which Commerce's save
    /// adds to a list that lacks it (a market without lists, written with SQL).
    /// </summary>
    /// <exception cref="AgentException">A currency Commerce doesn't have.</exception>
    internal static void RequireKnownCurrencies(CommerceMarket wanted, IReadOnlyCollection<string>? known) =>
        RequireKnown(WithDefaultsListed(wanted).Currencies, known);

    /// <exception cref="AgentException">A currency Commerce doesn't have.</exception>
    internal static void RequireKnown(IEnumerable<string> currencies, IReadOnlyCollection<string>? known)
    {
        if (known is null)
        {
            return;
        }
        var unknown = currencies.Where(c => !known.Contains(c)).ToList();
        if (unknown.Count > 0)
        {
            throw AgentException.Usage($"Commerce has no currency {string.Join(", ", unknown)}.",
                $"Commerce's currencies: {string.Join(", ", known.Order(StringComparer.Ordinal))}; add one in Commerce's admin UI first.");
        }
    }

    private static string Currency(string code)
    {
        var value = code.Trim().ToUpperInvariant();
        return value.Length == 3 && value.All(char.IsAsciiLetterUpper) ? value : throw AgentException.Usage($"'{code}' is not a currency: give its ISO 4217 code (USD, EUR, SEK).");
    }

    private static string Language(string code)
    {
        try
        {
            // Only cultures .NET knows: an invented one (xx-YY) would save, and match no content's language.
            var culture = CultureInfo.GetCultureInfo(code.Trim(), predefinedOnly: true);
            return culture.Name.Length > 0 ? culture.Name : throw AgentException.Usage($"'{code}' is not a language.");
        }
        catch (CultureNotFoundException)
        {
            throw AgentException.Usage($"'{code}' is not a language: give a culture name (en, sv, en-US).");
        }
    }

    private static string Country(string code)
    {
        var value = code.Trim().ToUpperInvariant();
        return value.Length == 3 && value.All(char.IsAsciiLetterUpper) ? value : throw AgentException.Usage($"'{code}' is not a country: Commerce takes ISO 3166-1 alpha-3 codes (USA, SWE, NOR).");
    }

    private static List<string> Distinct(IEnumerable<string> values) => values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static void Replace<T>(ICollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }
}
