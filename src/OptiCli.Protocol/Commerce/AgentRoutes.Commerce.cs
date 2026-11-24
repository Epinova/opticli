namespace OptiCli.Protocol;

/// <summary>The Commerce agent's routes, below <c>/v1/commerce</c>. Not compiled into the MCP module, which has no Commerce.</summary>
public static partial class AgentRoutes
{
    /// <summary>
    /// <c>GET /v1/commerce</c>. Response: <see cref="CommerceInfo"/>. Only on a Commerce site whose Commerce agent loaded
    /// (<see cref="PingResponse.Commerce"/>); elsewhere every <c>/v1/commerce</c> route is <c>not_found</c>.
    /// </summary>
    public static string Commerce => $"{Prefix}/commerce";

    /// <summary>
    /// <c>POST /v1/commerce/refs</c>, body <see cref="CatalogRefsRequest"/>. Response: <see cref="CatalogRefsResponse"/>:
    /// each ref resolved to catalog content, or not found. Read-only. Catalog content isn't in the CMS database, so the CLI
    /// resolves catalog refs here, then passes the agent's content routes their <c>id__CatalogContent</c> ref.
    /// </summary>
    public static string CommerceRefs => $"{Commerce}/refs";

    /// <summary>
    /// <c>POST /v1/commerce/relations</c>, body <see cref="RelationRequest"/>. Response: <see cref="RelationResult"/>. Adds,
    /// updates or removes a product's variant, a bundle's or package's entry, or an entry's extra category
    /// (<c>IRelationRepository</c>). <c>GET /v1/commerce/relations?ref=</c>: <see cref="RelationList"/>.
    /// </summary>
    public static string CommerceRelations => $"{Commerce}/relations";

    /// <summary>
    /// <c>POST /v1/commerce/prices</c>, body <see cref="PriceRequest"/>. Response: <see cref="PriceResult"/>: one price row
    /// upserted or removed by its key (<c>IPriceDetailService</c>); the entry's other prices stay. <c>GET
    /// /v1/commerce/prices?ref=</c>: <see cref="PriceList"/>.
    /// </summary>
    public static string CommercePrices => $"{Commerce}/prices";

    /// <summary>
    /// <c>POST /v1/commerce/warehouses</c>, body <see cref="WarehouseRequest"/>. Response: <see cref="WarehouseResult"/>
    /// (<c>IWarehouseRepository</c>). <c>GET</c>: <see cref="WarehouseList"/>.
    /// </summary>
    public static string CommerceWarehouses => $"{Commerce}/warehouses";

    /// <summary>
    /// <c>POST /v1/commerce/inventory</c>, body <see cref="InventoryRequest"/>. Response: <see cref="InventoryResult"/>
    /// (<c>IInventoryService</c>). <c>GET /v1/commerce/inventory?ref=</c>: <see cref="InventoryList"/>.
    /// </summary>
    public static string CommerceInventory => $"{Commerce}/inventory";

    /// <summary>
    /// <c>POST /v1/commerce/markets</c>, body <see cref="MarketRequest"/>. Response: <see cref="MarketResult"/>
    /// (<c>IMarketService</c>). <c>GET</c>: <see cref="MarketList"/>.
    /// </summary>
    public static string CommerceMarkets => $"{Commerce}/markets";

    /// <summary>
    /// <c>GET /v1/commerce/children?ref=&amp;limit=</c>. Response: <see cref="CatalogChildren"/>: the catalogs below the
    /// root, the nodes and entries below a catalog or node (an entry's own parent only). Read-only.
    /// </summary>
    public static string CommerceChildren => $"{Commerce}/children";

    /// <summary>A read route with the catalog content as <c>?ref=</c>.</summary>
    public static string WithRef(string route, string reference) => $"{route}?ref={Uri.EscapeDataString(reference)}";
}
