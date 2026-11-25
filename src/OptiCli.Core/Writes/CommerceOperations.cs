using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// <c>commerce relation add|remove</c> (a plan's <c>relation</c>): a product's variant, a bundle's or package's entry (with
/// a quantity), or an entry's extra category, through the site's Commerce agent.
/// </summary>
/// <param name="Parent">The product, bundle, package or node: a catalog ref (<c>code:...</c>, <c>$id</c>, ...).</param>
/// <param name="Child">The variant or entry.</param>
/// <param name="Remove">Remove the relation instead of adding (or updating) it.</param>
public sealed record RelationOperation(string Parent, string Child, bool Remove = false) : WriteOperation<RelationResult>
{
    public override string Kind => "relation";

    /// <summary>For a bundle's or package's entry: how many.</summary>
    public decimal? Quantity { get; init; }

    public int? SortOrder { get; init; }

    internal override Task<WriteOutcome<RelationResult>> RunOnAsync(WriteExecutor executor, bool dryRun, CancellationToken cancellationToken) =>
        executor.RelationAsync(this, dryRun, cancellationToken);

    public override IEnumerable<string?> Refs => [Parent, Child];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Parent = map(Parent), Child = map(Child) };
}

/// <summary>
/// <c>commerce price set|remove</c> (a plan's <c>price</c>): one price row of a variant or package, upserted (or removed) by
/// its key: market, currency, price type and code, minimum quantity and valid-from. The entry's other prices stay.
/// </summary>
/// <param name="Amount">The unit price; required unless <see cref="Remove"/>.</param>
public sealed record PriceOperation(string Entry, string Market, string Currency, decimal? Amount = null) : WriteOperation<PriceResult>
{
    public override string Kind => "price";

    /// <summary><c>all</c> (default), <c>user</c>, <c>group</c>, or a price type of the site's own by name or number.</summary>
    public string? PriceType { get; init; }

    /// <summary>The user name or price group of a <c>user</c> or <c>group</c> price; any text or none for the site's own types.</summary>
    public string? PriceCode { get; init; }

    public decimal? MinQuantity { get; init; }

    /// <summary>Part of the key: null names the row with the rest of the key that has no end, the base price (a new one starts today, UTC).</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>When the price ends (with <see cref="ValidFrom"/>: a campaign price); null: no end.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    public bool Remove { get; init; }

    /// <summary>
    /// A plan's dry run only: an earlier step makes the market or gives it these currencies, so the dry run checks the
    /// price against them (<see cref="PriceRequest.PlannedCurrencies"/>).
    /// </summary>
    internal IReadOnlyList<string>? PlannedCurrencies { get; init; }

    internal override Task<WriteOutcome<PriceResult>> RunOnAsync(WriteExecutor executor, bool dryRun, CancellationToken cancellationToken) =>
        executor.PriceAsync(this, dryRun, cancellationToken);

    public override IEnumerable<string?> Refs => [Entry];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Entry = map(Entry) };
}

/// <summary><c>commerce warehouse set</c> (a plan's <c>warehouse</c>): a warehouse, upserted by its code.</summary>
public sealed record WarehouseOperation(string Code) : WriteOperation<WarehouseResult>
{
    public override string Kind => "warehouse";

    public string? Name { get; init; }

    public bool? Active { get; init; }

    public bool? Primary { get; init; }

    public bool? FulfillmentCenter { get; init; }

    public bool? PickupLocation { get; init; }

    public bool? DeliveryLocation { get; init; }

    public int? SortOrder { get; init; }

    internal override Task<WriteOutcome<WarehouseResult>> RunOnAsync(WriteExecutor executor, bool dryRun, CancellationToken cancellationToken) =>
        executor.WarehouseAsync(this, dryRun, cancellationToken);

    public override IEnumerable<string?> Refs => [];

    public override WriteOperation MapRefs(Func<string, string> map) => this;
}

/// <summary><c>commerce inventory set</c> (a plan's <c>inventory</c>): a variant's or package's stock in a warehouse, upserted.</summary>
public sealed record InventoryOperation(string Entry, string Warehouse) : WriteOperation<InventoryResult>
{
    public override string Kind => "inventory";

    /// <summary>What can be bought now (Commerce's purchase available quantity).</summary>
    public decimal? InStock { get; init; }

    public decimal? Backorder { get; init; }

    public decimal? Preorder { get; init; }

    public bool? Tracked { get; init; }

    /// <summary>A plan's dry run only: an earlier step makes the warehouse (<see cref="InventoryRequest.PlannedWarehouse"/>).</summary>
    internal bool PlannedWarehouse { get; init; }

    internal override Task<WriteOutcome<InventoryResult>> RunOnAsync(WriteExecutor executor, bool dryRun, CancellationToken cancellationToken) =>
        executor.InventoryAsync(this, dryRun, cancellationToken);

    public override IEnumerable<string?> Refs => [Entry];

    public override WriteOperation MapRefs(Func<string, string> map) => this with { Entry = map(Entry) };
}

/// <summary><c>commerce market set|remove</c> (a plan's <c>market</c>): a market, upserted (or removed) by its id.</summary>
/// <param name="Market">The market's id (<c>DEFAULT</c>, <c>NORDIC</c>).</param>
public sealed record MarketOperation(string Market) : WriteOperation<MarketResult>
{
    public override string Kind => "market";

    public string? Name { get; init; }

    public bool? Enabled { get; init; }

    public IReadOnlyList<string>? Currencies { get; init; }

    public string? DefaultCurrency { get; init; }

    public IReadOnlyList<string>? Languages { get; init; }

    public string? DefaultLanguage { get; init; }

    public IReadOnlyList<string>? Countries { get; init; }

    public string? Description { get; init; }

    public bool? PricesIncludeTax { get; init; }

    /// <summary>Remove the market instead (only the id may be given); Commerce removes its prices with it.</summary>
    public bool Remove { get; init; }

    internal override Task<WriteOutcome<MarketResult>> RunOnAsync(WriteExecutor executor, bool dryRun, CancellationToken cancellationToken) =>
        executor.MarketAsync(this, dryRun, cancellationToken);

    public override IEnumerable<string?> Refs => [];

    public override WriteOperation MapRefs(Func<string, string> map) => this;
}
