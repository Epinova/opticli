using OptiCli.Agent.Commerce.Catalog;
using OptiCli.Agent.Extensions;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce;

/// <summary>
/// The Commerce agent: the routes below <c>/_opticli/v1/commerce</c>, which the CMS agent passes on to it in a Commerce
/// site (<see cref="AgentExtensions"/>). Catalog content itself is written by the CMS agent's content routes (it is
/// <c>IContent</c>); this is for what isn't: refs by code, and Commerce's own services.
/// </summary>
internal sealed class CommerceExtension : AgentExtension
{
    public override string Segment => "commerce";

    public override string RouteList => $"GET {AgentRoutes.Commerce}, POST {AgentRoutes.CommerceRefs}";

    public override void Start(IServiceProvider services) => CatalogGuids.Start(services);

    public override ExtensionRoute? Route(string method, IReadOnlyList<string> segments) => segments switch
    {
        [] => Only(method, "GET", AgentMiddleware.Read(CommerceStatusEndpoint.Handle)),
        ["refs"] => Only(method, "POST", new AgentMiddleware.EndpointHandler(false, async request =>
            (200, (object)CatalogRefsEndpoint.Handle(request, await request.ReadBodyAsync<CatalogRefsRequest>())))),
        _ => null,
    };

    /// <exception cref="AgentException">The route takes another method.</exception>
    private ExtensionRoute Only(string method, string allowed, AgentMiddleware.EndpointHandler handler, string? argument = null) =>
        string.Equals(method, allowed, StringComparison.OrdinalIgnoreCase)
            ? new ExtensionRoute(handler, argument)
            : throw AgentException.Usage($"This route does not accept {method}; use {allowed}.", $"Routes: {RouteList}.");
}
