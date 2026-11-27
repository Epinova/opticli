using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// Optimizely Commerce's services, through the site's Commerce agent: relations, prices, warehouses, stock and markets.
/// Catalog refs are resolved first (a step's <c>$id</c> is already the content's <c>id__CatalogContent</c>), so the agent
/// is told which content is meant, and a ref that names none fails before anything is sent.
/// </summary>
public sealed partial class WriteExecutor
{
    internal async Task<WriteOutcome<RelationResult>> RelationAsync(RelationOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var request = new RelationRequest(await CatalogAsync(op.Parent, "parent", cancellationToken), await CatalogAsync(op.Child, "child", cancellationToken), op.Remove)
        {
            Quantity = op.Quantity,
            SortOrder = op.SortOrder,
            DryRun = dryRun,
            AllowDestructive = op.AllowDestructive,
        };
        return await CommerceAsync<RelationRequest, RelationResult>(AgentRoutes.CommerceRelations, request, op, cancellationToken);
    }

    internal async Task<WriteOutcome<PriceResult>> PriceAsync(PriceOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        if (!op.Remove && op.Amount is null)
        {
            throw new UsageException("A price needs its amount (--amount, a plan's \"amount\"), or remove it (--remove).");
        }
        var request = new PriceRequest(await CatalogAsync(op.Entry, "entry", cancellationToken), op.Market, op.Currency, op.Remove ? null : op.Amount)
        {
            PriceType = op.PriceType,
            PriceCode = op.PriceCode,
            MinQuantity = op.MinQuantity,
            ValidFrom = op.ValidFrom?.UtcDateTime,
            ValidUntil = op.ValidUntil?.UtcDateTime,
            Remove = op.Remove,
            DryRun = dryRun,
            AllowDestructive = op.AllowDestructive,
            PlannedCurrencies = dryRun ? op.PlannedCurrencies : null,
        };
        return await CommerceAsync<PriceRequest, PriceResult>(AgentRoutes.CommercePrices, request, op, cancellationToken);
    }

    internal Task<WriteOutcome<WarehouseResult>> WarehouseAsync(WarehouseOperation op, bool dryRun, CancellationToken cancellationToken) =>
        CommerceAsync<WarehouseRequest, WarehouseResult>(AgentRoutes.CommerceWarehouses, new WarehouseRequest(op.Code)
        {
            Name = op.Name,
            Active = op.Active,
            Primary = op.Primary,
            FulfillmentCenter = op.FulfillmentCenter,
            PickupLocation = op.PickupLocation,
            DeliveryLocation = op.DeliveryLocation,
            SortOrder = op.SortOrder,
            DryRun = dryRun,
        }, op, cancellationToken);

    internal async Task<WriteOutcome<InventoryResult>> InventoryAsync(InventoryOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var request = new InventoryRequest(await CatalogAsync(op.Entry, "entry", cancellationToken), op.Warehouse)
        {
            InStock = op.InStock,
            Backorder = op.Backorder,
            Preorder = op.Preorder,
            Tracked = op.Tracked,
            DryRun = dryRun,
            PlannedWarehouse = dryRun && op.PlannedWarehouse,
        };
        return await CommerceAsync<InventoryRequest, InventoryResult>(AgentRoutes.CommerceInventory, request, op, cancellationToken);
    }

    internal async Task<WriteOutcome<MarketResult>> MarketAsync(MarketOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var outcome = await MarketCoreAsync(op, dryRun, cancellationToken);
        if (op.Remove || outcome.Output.Market is not { } market)
        {
            return outcome;
        }
        // The agent takes any culture .NET knows; one the CMS hasn't enabled matches no content's language yet.
        var disabled = market.Languages.Where(code => session.Model.LanguageByCode(code) is not { Enabled: true }).ToList();
        return disabled.Count == 0 ? outcome : outcome with
        {
            Warnings = [.. outcome.Warnings, $"The CMS hasn't enabled {string.Join(", ", disabled)}: the market's content in that language shows only once it is (opticli languages enable {string.Join(" ", disabled)})."],
        };
    }

    private Task<WriteOutcome<MarketResult>> MarketCoreAsync(MarketOperation op, bool dryRun, CancellationToken cancellationToken) =>
        CommerceAsync<MarketRequest, MarketResult>(AgentRoutes.CommerceMarkets, new MarketRequest(op.Market)
        {
            Name = op.Name,
            Enabled = op.Enabled,
            Currencies = op.Currencies,
            DefaultCurrency = op.DefaultCurrency,
            Languages = op.Languages,
            DefaultLanguage = op.DefaultLanguage,
            Countries = op.Countries,
            Description = op.Description,
            PricesIncludeTax = op.PricesIncludeTax,
            Remove = op.Remove,
            DryRun = dryRun,
            AllowDestructive = op.AllowDestructive,
        }, op, cancellationToken);

    /// <summary>The ref the agent takes for the catalog content a ref names.</summary>
    /// <exception cref="NotFoundException">It names no catalog content.</exception>
    private async Task<string> CatalogAsync(string reference, string what, CancellationToken cancellationToken)
    {
        if (reference.StartsWith('$'))
        {
            throw new UsageException($"{what} '{reference}' refers to plan content; $ids only work inside `opticli apply`.");
        }
        return (await CatalogRefAsync(reference, cancellationToken) ?? throw CatalogNotFound(reference, what)).Ref!;
    }

    /// <summary>A Commerce write (the drift check comes first, as for every write: <see cref="RunCoreAsync{T}"/>): the agent's answer, and its notes.</summary>
    private async Task<WriteOutcome<TResult>> CommerceAsync<TRequest, TResult>(string route, TRequest request, WriteOperation op, CancellationToken cancellationToken)
        where TRequest : notnull
        where TResult : notnull
    {
        TResult result;
        try
        {
            result = await (await AgentAsync(cancellationToken)).SendAsync<TResult>(HttpMethod.Post, route, request, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            throw NoCommerceAgent(null);
        }
        return new WriteOutcome<TResult>(result, AgentSource, null, CommerceWarnings.For(op, result));
    }
}

/// <summary>What a Commerce write says beside its result.</summary>
internal static class CommerceWarnings
{
    public static IReadOnlyList<string> For(WriteOperation op, object result) => result switch
    {
        RelationResult { Status: CommerceWriteStatus.Unchanged } or PriceResult { Status: CommerceWriteStatus.Unchanged } or WarehouseResult { Status: CommerceWriteStatus.Unchanged }
            or InventoryResult { Status: CommerceWriteStatus.Unchanged } or MarketResult { Status: CommerceWriteStatus.Unchanged } => ["Nothing changed: it is already as asked."],
        RelationResult { Status: CommerceWriteStatus.Absent } or PriceResult { Status: CommerceWriteStatus.Absent } or MarketResult { Status: CommerceWriteStatus.Absent } => ["Nothing to remove: there is none."],
        MarketResult { Status: CommerceWriteStatus.Removed, Market: { } market } =>
            [$"Commerce removes a market's prices with it, in every catalog, and takes it off the payment and shipping methods that name it; orders and carts keep naming {market.Id}."],
        _ => [],
    };
}
