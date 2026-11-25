using System.Globalization;
using Mediachase.Commerce.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Pricing;

/// <summary>
/// The site's price types, by name: Commerce's three (<c>CustomerPricing.PriceType</c>: <c>all</c>, <c>user</c>,
/// <c>group</c>) and those its <c>CatalogOptions.SalePriceTypes</c> add (<c>EPiServer:Commerce:CatalogOptions</c>), each
/// named by its key in lower case. Commerce's <c>PriceTypeConfiguration</c> has the same types by number and description
/// only; the key is the setting's. A name stands for one number only: a key that would clash with another name (<c>all</c>,
/// a number) is named by its number, as is a number the site doesn't define (a row written before the site dropped it).
/// </summary>
internal sealed class PriceTypeSet
{
    public const int AllCustomers = 0;
    public const int UserName = 1;
    public const int PriceGroup = 2;

    /// <summary>Commerce's three, with the descriptions its PriceType table has for them.</summary>
    public static readonly PriceTypeSet Builtin = Of([]);

    private readonly Dictionary<int, CommercePriceType> _byId;
    private readonly Dictionary<string, CommercePriceType> _byName;

    private PriceTypeSet(IReadOnlyList<CommercePriceType> types)
    {
        Types = types;
        _byId = types.ToDictionary(t => t.Id);
        _byName = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>By number.</summary>
    public IReadOnlyList<CommercePriceType> Types { get; }

    /// <summary>The site's, from its <see cref="CatalogOptions"/>.</summary>
    public static PriceTypeSet From(IServiceProvider services) =>
        Of(services.GetService<IOptions<CatalogOptions>>()?.Value.SalePriceTypes?.Where(t => t is not null).Select(t => ((string?)t.Key, t.Value, (string?)t.Description)) ?? []);

    /// <param name="configured">The site's <c>SalePriceTypes</c>: key, number, description.</param>
    public static PriceTypeSet Of(IEnumerable<(string? Key, int Value, string? Description)> configured)
    {
        var types = new SortedDictionary<int, CommercePriceType>
        {
            [AllCustomers] = new(PriceTypes.All, AllCustomers, "All Customers"),
            [UserName] = new(PriceTypes.User, UserName, "Customer"),
            [PriceGroup] = new(PriceTypes.Group, PriceGroup, "Customer Price Group"),
        };
        var list = configured.ToList();
        foreach (var (key, value, description) in list)
        {
            if (types.TryGetValue(value, out var builtin) && value <= PriceGroup)
            {
                // Commerce lets the settings describe its own three again (by their enum names); the names stay.
                types[value] = builtin with { Description = string.IsNullOrWhiteSpace(description) ? builtin.Description : description.Trim() };
                continue;
            }
            var name = key?.Trim().ToLowerInvariant() ?? "";
            var number = value.ToString(CultureInfo.InvariantCulture);
            var clashes = name.Length == 0 || name is PriceTypes.All or PriceTypes.User or PriceTypes.Group
                || name.All(char.IsAsciiDigit)
                || list.Count(t => string.Equals(t.Key?.Trim(), key?.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
            types[value] = new CommercePriceType(clashes ? number : name, value, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
        }
        return new PriceTypeSet(types.Values.ToList());
    }

    /// <summary>The type's name; a number the site doesn't define, as a string.</summary>
    public string Name(int id) => _byId.TryGetValue(id, out var type) ? type.Name : id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The number of a type given by name (any case) or number; none is <c>all</c>.</summary>
    /// <param name="anyNumber">Take a number the site doesn't define too (a removal of a row of a type the site dropped).</param>
    /// <exception cref="AgentException"><c>usage</c>, listing the site's types.</exception>
    public int Id(string? given, bool anyNumber = false)
    {
        var value = given?.Trim() ?? "";
        if (value.Length == 0)
        {
            return AllCustomers;
        }
        if (_byName.TryGetValue(value, out var named))
        {
            return named.Id;
        }
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && (anyNumber || _byId.ContainsKey(id)))
        {
            return id;
        }
        throw AgentException.Usage(
            $"'{given}' is not a price type of this site: it has {string.Join(", ", Types.Select(t => t.Name))}.",
            "Give one of those by name or number. A site adds its own in its settings (EPiServer:Commerce:CatalogOptions:SalePriceTypes); `opticli commerce` lists them as priceTypes.");
    }
}
