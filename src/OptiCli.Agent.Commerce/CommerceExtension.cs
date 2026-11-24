using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Agent.Commerce.Inventory;
using OptiCli.Agent.Commerce.Markets;
using OptiCli.Agent.Commerce.Pricing;
using OptiCli.Agent.Extensions;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce;

/// <summary>
/// The Commerce agent: the routes below <c>/_opticli/v1/commerce</c>, which the CMS agent passes on to it in a Commerce
/// site (<see cref="AgentExtensions"/>). Catalog content itself is written by the CMS agent's content routes (it is
/// <c>IContent</c>); this is for what isn't: refs by code, relations, and Commerce's own services. Every write goes
/// through <see cref="AgentMiddleware.Write{T}(Func{AgentRequest, Task{T}}, Func{T, bool}, Func{AgentRequest, T, ValueTuple{int, object}})"/>,
/// so it passes the drift gate as the CMS agent's writes do.
/// </summary>
internal sealed class CommerceExtension : AgentExtension
{
    public override string Segment => "commerce";

    public override string RouteList =>
        $"GET {AgentRoutes.Commerce}, POST {AgentRoutes.CommerceRefs}, GET {AgentRoutes.CommerceChildren}?ref=, GET {AgentRoutes.CommerceResolve}?url=, GET|POST {AgentRoutes.CommerceRelations}, GET|POST {AgentRoutes.CommercePrices}, " +
        $"GET|POST {AgentRoutes.CommerceWarehouses}, GET|POST {AgentRoutes.CommerceInventory}, GET|POST {AgentRoutes.CommerceMarkets}";

    public override void Start(IServiceProvider services)
    {
        CatalogGuids.Start(services);
        // CommerceMediaCollection: written and shown by create, set and reads as a list of CMS media.
        Cms.Content.PropertyValueConverters.Register(new CommerceMediaConverter());
    }

    public override ExtensionRoute? Route(string method, IReadOnlyList<string> segments) => segments switch
    {
        [] => Only(method, "GET", AgentMiddleware.Read(CommerceStatusEndpoint.Handle)),
        ["refs"] => Only(method, "POST", new AgentMiddleware.EndpointHandler(false, async request =>
            (200, (object)CatalogRefsEndpoint.Handle(request, await request.ReadBodyAsync<CatalogRefsRequest>())))),
        ["children"] => Only(method, "GET", AgentMiddleware.Read(CatalogChildrenEndpoint.Handle)),
        ["resolve"] => Only(method, "GET", AgentMiddleware.Read(CatalogUrlEndpoint.Handle)),
        ["relations"] => ReadOrWrite(method, RelationsEndpoint.List, Write<RelationRequest>(b => b.DryRun, RelationsEndpoint.Write)),
        ["prices"] => ReadOrWrite(method, PricesEndpoint.List, Write<PriceRequest>(b => b.DryRun, PricesEndpoint.Write)),
        ["warehouses"] => ReadOrWrite(method, WarehousesEndpoint.List, Write<WarehouseRequest>(b => b.DryRun, WarehousesEndpoint.Write)),
        ["inventory"] => ReadOrWrite(method, InventoryEndpoint.List, Write<InventoryRequest>(b => b.DryRun, InventoryEndpoint.Write)),
        ["markets"] => ReadOrWrite(method, MarketsEndpoint.List, Write<MarketRequest>(b => b.DryRun, MarketsEndpoint.Write)),
        _ => null,
    };

    /// <summary>A write: its body read, the drift gate passed (dry runs pass), then run; 200 whatever it did.</summary>
    private static AgentMiddleware.EndpointHandler Write<T>(Func<T, bool> dryRun, Func<AgentRequest, T, object> handle) where T : class =>
        AgentMiddleware.Write(r => r.ReadBodyAsync<T>(), dryRun, (r, b) => (200, handle(r, b)));

    /// <summary>A route whose GET reads and whose POST writes.</summary>
    private ExtensionRoute ReadOrWrite(string method, Func<AgentRequest, object> read, AgentMiddleware.EndpointHandler write) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ? new ExtensionRoute(AgentMiddleware.Read(read))
        : Only(method, "POST", write);

    /// <exception cref="AgentException">The route takes another method.</exception>
    private ExtensionRoute Only(string method, string allowed, AgentMiddleware.EndpointHandler handler, string? argument = null) =>
        string.Equals(method, allowed, StringComparison.OrdinalIgnoreCase)
            ? new ExtensionRoute(handler, argument)
            : throw AgentException.Usage($"This route does not accept {method}; use {allowed}.", $"Routes: {RouteList}.");
}
