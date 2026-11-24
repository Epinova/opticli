using System.Globalization;
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
        var id = body.Id?.Trim() ?? "";
        if (id.Length == 0 || id.Length > 8 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            // Commerce's MarketId column is 8 characters.
            throw AgentException.Usage($"'{body.Id}' is not a market id: 1 to 8 letters, digits, '-' or '_' (DEFAULT, US, NORDIC).");
        }
        var markets = request.Context.RequestServices.GetRequiredService<IMarketService>();
        var existing = markets.GetMarket(new MarketId(id));
        var before = existing is null ? null : ToMarket(existing);
        var wanted = Wanted(id, body, before);
        // Before a dry run answers: Commerce refuses a currency it doesn't know only when saving (a foreign key).
        RequireKnown(wanted.Currencies, KnownCurrencies());
        var changes = Changes(before, wanted);
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

    public static MarketList List(AgentRequest request) =>
        new(request.Context.RequestServices.GetRequiredService<IMarketService>().GetAllMarkets()
            .Select(ToMarket).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>What the market should be: what the request gives, else what it has. A new market needs currencies and languages.</summary>
    /// <exception cref="AgentException">A value that isn't one, or a default not in its list.</exception>
    internal static CommerceMarket Wanted(string id, MarketRequest body, CommerceMarket? before)
    {
        var currencies = body.Currencies is { } givenCurrencies ? Distinct(givenCurrencies.Select(Currency)) : before?.Currencies.ToList();
        var languages = body.Languages is { } givenLanguages ? Distinct(givenLanguages.Select(Language)) : before?.Languages.ToList();
        if (currencies is not { Count: > 0 } || languages is not { Count: > 0 })
        {
            throw AgentException.Usage(before is null
                ? $"The market {id} doesn't exist yet: give its currencies and languages (--currencies USD,EUR --languages en,sv)."
                : "A market needs at least one currency and one language.");
        }
        var defaultCurrency = body.DefaultCurrency is { } currency ? Currency(currency)
            : before is not null && currencies.Contains(before.DefaultCurrency) ? before.DefaultCurrency : currencies[0];
        var defaultLanguage = body.DefaultLanguage is { } language ? Language(language)
            : before is not null && languages.Contains(before.DefaultLanguage, StringComparer.OrdinalIgnoreCase) ? before.DefaultLanguage : languages[0];
        if (!currencies.Contains(defaultCurrency))
        {
            throw AgentException.Usage($"The default currency {defaultCurrency} isn't one of the market's currencies ({string.Join(", ", currencies)}).");
        }
        if (!languages.Contains(defaultLanguage, StringComparer.OrdinalIgnoreCase))
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
