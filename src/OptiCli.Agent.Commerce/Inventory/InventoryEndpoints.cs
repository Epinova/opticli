using Mediachase.Commerce.Inventory;
using Mediachase.Commerce.InventoryService;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Agent.Commerce.Pricing;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;
using InventoryRequest = OptiCli.Protocol.InventoryRequest;

namespace OptiCli.Agent.Commerce.Inventory;

/// <summary><see cref="AgentRoutes.CommerceWarehouses"/>: warehouses by code, through Commerce's <see cref="IWarehouseRepository"/>.</summary>
internal static class WarehousesEndpoint
{
    public static WarehouseResult Write(AgentRequest request, WarehouseRequest body)
    {
        var code = body.Code?.Trim() ?? "";
        if (code.Length == 0 || code.Length > 50)
        {
            throw AgentException.Usage("A warehouse code is 1 to 50 characters.");
        }
        var warehouses = request.Context.RequestServices.GetRequiredService<IWarehouseRepository>();
        var existing = warehouses.Get(code);
        var before = existing is null ? null : ToWarehouse(existing);
        var wanted = Wanted(code, body, before);
        var changes = Changes(before, wanted);
        var status = before is null ? CommerceWriteStatus.Created : changes.Count == 0 ? CommerceWriteStatus.Unchanged : CommerceWriteStatus.Updated;
        if (!body.DryRun && status != CommerceWriteStatus.Unchanged)
        {
            var warehouse = existing is null
                ? new Warehouse { Code = code, ContactInformation = new WarehouseContactInformation(), Created = DateTime.UtcNow, CreatorId = AgentProtocol.PrincipalName }
                : new Warehouse(existing);
            warehouse.Name = wanted.Name;
            warehouse.IsActive = wanted.Active;
            warehouse.IsPrimary = wanted.Primary;
            warehouse.IsFulfillmentCenter = wanted.FulfillmentCenter;
            warehouse.IsPickupLocation = wanted.PickupLocation;
            warehouse.IsDeliveryLocation = wanted.DeliveryLocation;
            warehouse.SortOrder = wanted.SortOrder;
            warehouse.Modified = DateTime.UtcNow;
            warehouse.ModifierId = AgentProtocol.PrincipalName;
            warehouses.Save(warehouse);
        }
        return new WarehouseResult(wanted, status, changes, body.DryRun);
    }

    public static WarehouseList List(AgentRequest request) =>
        new(request.Context.RequestServices.GetRequiredService<IWarehouseRepository>().List()
            .Select(ToWarehouse).OrderBy(w => w.SortOrder).ThenBy(w => w.Code, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>What the warehouse should be: what the request gives, else what it has, else a new one's defaults.</summary>
    internal static CommerceWarehouse Wanted(string code, WarehouseRequest body, CommerceWarehouse? before) => new(
        before?.Code ?? code,
        string.IsNullOrWhiteSpace(body.Name) ? before?.Name ?? code : body.Name.Trim(),
        body.Active ?? before?.Active ?? true,
        body.Primary ?? before?.Primary ?? false,
        body.FulfillmentCenter ?? before?.FulfillmentCenter ?? false,
        body.PickupLocation ?? before?.PickupLocation ?? false,
        body.DeliveryLocation ?? before?.DeliveryLocation ?? false,
        body.SortOrder ?? before?.SortOrder ?? 0);

    /// <summary>The fields that differ; every field for a new one.</summary>
    internal static IReadOnlyList<string> Changes(CommerceWarehouse? before, CommerceWarehouse wanted)
    {
        var changes = new List<string>();
        void Check<T>(string name, T? a, T b)
        {
            if (before is null || !EqualityComparer<T>.Default.Equals(a, b))
            {
                changes.Add(name);
            }
        }
        Check("name", before?.Name, wanted.Name);
        Check("active", before?.Active ?? default, wanted.Active);
        Check("primary", before?.Primary ?? default, wanted.Primary);
        Check("fulfillmentCenter", before?.FulfillmentCenter ?? default, wanted.FulfillmentCenter);
        Check("pickupLocation", before?.PickupLocation ?? default, wanted.PickupLocation);
        Check("deliveryLocation", before?.DeliveryLocation ?? default, wanted.DeliveryLocation);
        Check("sortOrder", before?.SortOrder ?? default, wanted.SortOrder);
        return changes;
    }

    internal static CommerceWarehouse ToWarehouse(IWarehouse warehouse) => new(
        warehouse.Code, warehouse.Name, warehouse.IsActive, warehouse.IsPrimary, warehouse.IsFulfillmentCenter, warehouse.IsPickupLocation, warehouse.IsDeliveryLocation, warehouse.SortOrder);
}

/// <summary>
/// <see cref="AgentRoutes.CommerceInventory"/>: an entry's stock per warehouse, through Commerce's <see cref="IInventoryService"/>.
/// A write upserts the one record of its entry and warehouse.
/// </summary>
internal static class InventoryEndpoint
{
    public static InventoryResult Write(AgentRequest request, InventoryRequest body)
    {
        var services = request.Context.RequestServices;
        var entry = PricesEndpoint.PricedEntry(new CatalogContentFinder(services), body.Entry);
        var warehouse = services.GetRequiredService<IWarehouseRepository>().Get(body.Warehouse?.Trim() ?? "")
            ?? throw AgentException.NotFound($"No warehouse '{body.Warehouse?.Trim()}'.",
                $"Warehouses: {string.Join(", ", services.GetRequiredService<IWarehouseRepository>().List().Select(w => w.Code))}; make one with `opticli commerce warehouse set`.");
        foreach (var (name, value) in new[] { ("inStock", body.InStock), ("backorder", body.Backorder), ("preorder", body.Preorder) })
        {
            if (value is < 0)
            {
                throw AgentException.Usage($"{name} can't be negative.");
            }
        }
        var inventory = services.GetRequiredService<IInventoryService>();
        var existing = inventory.Get(entry.Code, warehouse.Code);
        var before = existing is null ? null : ToStock(existing);
        var wanted = Wanted(warehouse.Code, body, before);
        var status = before is null ? CommerceWriteStatus.Created : before == wanted ? CommerceWriteStatus.Unchanged : CommerceWriteStatus.Updated;
        if (!body.DryRun && status != CommerceWriteStatus.Unchanged)
        {
            var now = DateTime.UtcNow;
            var record = existing is null
                ? new InventoryRecord { CatalogEntryCode = entry.Code, WarehouseCode = warehouse.Code, PurchaseAvailableUtc = now, PreorderAvailableUtc = now, BackorderAvailableUtc = now }
                : new InventoryRecord(existing);
            record.PurchaseAvailableQuantity = wanted.InStock;
            record.BackorderAvailableQuantity = wanted.Backorder;
            record.PreorderAvailableQuantity = wanted.Preorder;
            record.IsTracked = wanted.Tracked;
            inventory.Save([record]);
        }
        return new InventoryResult(CatalogRefSyntax.Format(entry.ContentLink.ID), entry.Code, wanted, status, status == CommerceWriteStatus.Updated ? before : null, body.DryRun);
    }

    public static InventoryList List(AgentRequest request)
    {
        var services = request.Context.RequestServices;
        var entry = PricesEndpoint.PricedEntry(new CatalogContentFinder(services), request.Context.Request.Query["ref"].ToString(), reading: true);
        var stock = services.GetRequiredService<IInventoryService>().QueryByEntry([entry.Code])
            .Select(ToStock).OrderBy(s => s.Warehouse, StringComparer.OrdinalIgnoreCase).ToList();
        return new InventoryList(CatalogRefSyntax.Format(entry.ContentLink.ID), entry.Code, stock);
    }

    /// <summary>What the record should be: what the request gives, else what it has, else a new record's defaults (0, tracked).</summary>
    internal static CommerceStock Wanted(string warehouse, InventoryRequest body, CommerceStock? before) => new(
        warehouse,
        body.InStock ?? before?.InStock ?? 0,
        body.Backorder ?? before?.Backorder ?? 0,
        body.Preorder ?? before?.Preorder ?? 0,
        body.Tracked ?? before?.Tracked ?? true);

    internal static CommerceStock ToStock(InventoryRecord record) => new(
        record.WarehouseCode, Pricing.Decimals.Plain(record.PurchaseAvailableQuantity), Pricing.Decimals.Plain(record.BackorderAvailableQuantity), Pricing.Decimals.Plain(record.PreorderAvailableQuantity), record.IsTracked);
}
