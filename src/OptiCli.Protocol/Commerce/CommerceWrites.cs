namespace OptiCli.Protocol;

/// <summary>What a Commerce write did (or would do, for a dry run) to the one thing it names.</summary>
[ValueSetName("CommerceWriteStatus")]
public static class CommerceWriteStatus
{
    /// <summary>It didn't exist, and was made.</summary>
    public const string Created = "created";

    /// <summary>It existed with other values, which were changed.</summary>
    public const string Updated = "updated";

    /// <summary>It existed as asked: nothing was saved.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>A remove: it existed and was removed.</summary>
    public const string Removed = "removed";

    /// <summary>A remove: it didn't exist, so nothing was removed.</summary>
    public const string Absent = "absent";

    /// <summary>Whether the status is one that saved something.</summary>
    public static bool Changed(string status) => status is Created or Updated or Removed;
}

/// <summary>How catalog content is related (Commerce's <c>IRelationRepository</c>).</summary>
[ValueSetName("RelationKind")]
public static class RelationKinds
{
    /// <summary>A product's variant (<c>ProductVariation</c>).</summary>
    public const string Variant = "variant";

    /// <summary>An entry in a bundle (<c>BundleEntry</c>), with a quantity.</summary>
    public const string BundleEntry = "bundleEntry";

    /// <summary>An entry in a package (<c>PackageEntry</c>), with a quantity.</summary>
    public const string PackageEntry = "packageEntry";

    /// <summary>An entry in a node besides its own parent: an extra category (<c>NodeEntryRelation</c>).</summary>
    public const string Category = "category";
}

/// <summary>Body of <see cref="AgentRoutes.CommerceRelations"/>.</summary>
/// <param name="Parent">The product, bundle, package or node (a catalog ref).</param>
/// <param name="Child">The variant or entry (a catalog ref).</param>
/// <param name="Remove">Remove the relation instead of adding (or updating) it.</param>
public sealed record RelationRequest(string Parent, string Child, bool Remove = false)
{
    /// <summary>For a bundle or package entry: how many (default 1 for a new one).</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Where it sorts among the parent's children (default 0 for a new one).</summary>
    public int? SortOrder { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Result of a relation write.</summary>
/// <param name="Parent">The parent's ref (<c>id__CatalogContent</c>).</param>
/// <param name="Child">The child's ref.</param>
/// <param name="Kind">One of <see cref="RelationKinds"/>.</param>
/// <param name="Status">One of <see cref="CommerceWriteStatus"/>.</param>
/// <param name="Quantity">The quantity it has (had, for a removal); null for variants and categories.</param>
public sealed record RelationResult(
    string Parent,
    string Child,
    [property: OutputValues(typeof(RelationKinds))] string Kind,
    [property: OutputValues(typeof(CommerceWriteStatus))] string Status,
    decimal? Quantity,
    int SortOrder,
    bool DryRun);

/// <summary>One price row of a catalog entry (Commerce's <c>IPriceDetailValue</c>).</summary>
/// <param name="Market">The market's id (<c>DEFAULT</c>).</param>
/// <param name="Currency">ISO 4217 code (<c>USD</c>).</param>
/// <param name="PriceType">
/// One of <see cref="PriceTypes"/>, or a price type of the site's own: its key in lower case (<c>member</c>), or its number
/// (<c>"7"</c>) when the site no longer defines it (<see cref="CommerceInfo.PriceTypes"/> lists the site's).
/// </param>
/// <param name="PriceCode">The user name or price group for those types, what the site keeps there for its own; empty for <c>all</c>.</param>
/// <param name="MinQuantity">The price applies from this quantity on.</param>
/// <param name="ValidFrom">UTC.</param>
/// <param name="ValidUntil">UTC; null: no end.</param>
public sealed record CommercePrice(
    string Market,
    string Currency,
    decimal Amount,
    [property: OutputValues(typeof(PriceTypes), Open = true)] string PriceType,
    string PriceCode,
    decimal MinQuantity,
    DateTime ValidFrom,
    DateTime? ValidUntil);

/// <summary>Who a price is for (Commerce's <c>CustomerPricing.PriceType</c>); a site may add its own (<see cref="CommercePriceType"/>).</summary>
[ValueSetName("PriceType")]
public static class PriceTypes
{
    public const string All = "all";

    /// <summary>One customer, by user name (the price code).</summary>
    public const string User = "user";

    /// <summary>A customer price group (the price code).</summary>
    public const string Group = "group";
}

/// <summary>Body of <see cref="AgentRoutes.CommercePrices"/>: one price row, upserted (or removed) by its key.</summary>
/// <param name="Entry">The variant or package (a catalog ref).</param>
/// <param name="Amount">The unit price; required unless <see cref="Remove"/>.</param>
public sealed record PriceRequest(string Entry, string Market, string Currency, decimal? Amount = null)
{
    /// <summary>One of <see cref="PriceTypes"/> (default <c>all</c>), or a type of the site's own by name or number.</summary>
    public string? PriceType { get; init; }

    /// <summary>None for <c>all</c>; the user name or price group for <c>user</c> and <c>group</c>; anything or none for the site's own types.</summary>
    public string? PriceCode { get; init; }

    public decimal? MinQuantity { get; init; }

    /// <summary>Part of the key. Null: the row with the rest of the key that has no end (the base price); a new row starts today (UTC).</summary>
    public DateTime? ValidFrom { get; init; }

    /// <summary>When the price ends (needs <see cref="ValidFrom"/>); null: no end.</summary>
    public DateTime? ValidUntil { get; init; }

    public bool Remove { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// Dry run only (ignored otherwise): an earlier step of the plan makes this market, or gives it these currencies. The
    /// price is checked against them instead of the market as the site has it now, and the market need not exist yet.
    /// </summary>
    public IReadOnlyList<string>? PlannedCurrencies { get; init; }
}

/// <summary>Result of a price write.</summary>
/// <param name="Entry">The entry's ref.</param>
/// <param name="Code">The entry's code, which Commerce keys prices by.</param>
/// <param name="Price">The row as it is now (as it was, for a removal); null when a removal found none (<c>absent</c>).</param>
/// <param name="Before">The row before an update.</param>
public sealed record PriceResult(string Entry, string Code, CommercePrice? Price, [property: OutputValues(typeof(CommerceWriteStatus))] string Status, CommercePrice? Before, bool DryRun);

/// <summary>A warehouse (Commerce's <c>IWarehouse</c>).</summary>
public sealed record CommerceWarehouse(string Code, string Name, bool Active, bool Primary, bool FulfillmentCenter, bool PickupLocation, bool DeliveryLocation, int SortOrder);

/// <summary>Body of <see cref="AgentRoutes.CommerceWarehouses"/>: a warehouse upserted by its code. What is left out keeps its value (a new one's defaults: active, not primary).</summary>
public sealed record WarehouseRequest(string Code)
{
    public string? Name { get; init; }

    public bool? Active { get; init; }

    public bool? Primary { get; init; }

    public bool? FulfillmentCenter { get; init; }

    public bool? PickupLocation { get; init; }

    public bool? DeliveryLocation { get; init; }

    public int? SortOrder { get; init; }

    public bool DryRun { get; init; }
}

/// <param name="Changes">The fields that changed (all of them for a new one).</param>
public sealed record WarehouseResult(CommerceWarehouse Warehouse, [property: OutputValues(typeof(CommerceWriteStatus))] string Status, IReadOnlyList<string> Changes, bool DryRun);

/// <summary>An entry's stock in one warehouse (Commerce's <c>InventoryRecord</c>).</summary>
/// <param name="InStock">What can be bought now (<c>PurchaseAvailableQuantity</c>).</param>
/// <param name="Tracked">Whether stock is tracked (an untracked entry is always available).</param>
public sealed record CommerceStock(string Warehouse, decimal InStock, decimal Backorder, decimal Preorder, bool Tracked);

/// <summary>Body of <see cref="AgentRoutes.CommerceInventory"/>: an entry's stock in a warehouse, upserted. What is left out keeps its value (a new record's: 0, tracked).</summary>
/// <param name="Entry">The variant or package (a catalog ref).</param>
/// <param name="Warehouse">The warehouse's code.</param>
public sealed record InventoryRequest(string Entry, string Warehouse)
{
    public decimal? InStock { get; init; }

    public decimal? Backorder { get; init; }

    public decimal? Preorder { get; init; }

    public bool? Tracked { get; init; }

    public bool DryRun { get; init; }

    /// <summary>Dry run only (ignored otherwise): an earlier step of the plan makes this warehouse, so it need not exist yet.</summary>
    public bool PlannedWarehouse { get; init; }
}

/// <param name="Before">The record before an update.</param>
public sealed record InventoryResult(string Entry, string Code, CommerceStock Stock, [property: OutputValues(typeof(CommerceWriteStatus))] string Status, CommerceStock? Before, bool DryRun);

/// <summary>A market (Commerce's <c>IMarket</c>).</summary>
/// <param name="Countries">ISO 3166-1 alpha-3 codes (<c>USA</c>, <c>SWE</c>).</param>
public sealed record CommerceMarket(
    string Id,
    string Name,
    bool Enabled,
    string DefaultCurrency,
    IReadOnlyList<string> Currencies,
    string DefaultLanguage,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Countries,
    string? Description,
    bool PricesIncludeTax);

/// <summary>Body of <see cref="AgentRoutes.CommerceMarkets"/>: a market upserted by its id. What is left out keeps its value; a new market needs currencies and languages.</summary>
public sealed record MarketRequest(string Id)
{
    public string? Name { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>The market's currencies, all of them (replacing its list).</summary>
    public IReadOnlyList<string>? Currencies { get; init; }

    /// <summary>Default: the first of <see cref="Currencies"/>.</summary>
    public string? DefaultCurrency { get; init; }

    /// <summary>The market's languages, all of them (replacing its list).</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>Default: the first of <see cref="Languages"/>.</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>The market's countries, all of them (replacing its list).</summary>
    public IReadOnlyList<string>? Countries { get; init; }

    public string? Description { get; init; }

    public bool? PricesIncludeTax { get; init; }

    public bool DryRun { get; init; }
}

/// <param name="Changes">The fields that changed (all of them for a new one).</param>
public sealed record MarketResult(CommerceMarket Market, [property: OutputValues(typeof(CommerceWriteStatus))] string Status, IReadOnlyList<string> Changes, bool DryRun);
