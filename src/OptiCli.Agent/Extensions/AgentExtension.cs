using OptiCli.Agent.Http;

namespace OptiCli.Agent.Extensions;

/// <summary>
/// The agent's part for something only some sites have (Commerce), in an assembly of its own next to this one, which
/// <see cref="AgentExtensions"/> loads only into a site that has it. The CMS agent never references what an extension
/// compiles against; an extension uses the agent's own request, error and write helpers (it sees its internals).
/// </summary>
internal abstract class AgentExtension
{
    /// <summary>Its first route segment after the protocol version: <c>commerce</c> for <c>/_opticli/v1/commerce/...</c>.</summary>
    public abstract string Segment { get; }

    /// <summary>Its routes, for the message about a route that doesn't exist.</summary>
    public abstract string RouteList { get; }

    /// <summary>Called once the site's services are built, before the first request: subscribe to content events and the like.</summary>
    public virtual void Start(IServiceProvider services)
    {
    }

    /// <summary>The endpoint for a request below <see cref="Segment"/>; null when there is no such route.</summary>
    /// <param name="segments">The path's segments after <see cref="Segment"/>.</param>
    /// <exception cref="OptiCli.Cms.AgentException">The route exists, but not for this method.</exception>
    public abstract ExtensionRoute? Route(string method, IReadOnlyList<string> segments);
}

/// <param name="Handler">What runs it: <see cref="AgentMiddleware.Read"/> or <see cref="AgentMiddleware.Write{T}(Func{AgentRequest, Task{T}}, Func{T, bool}, Func{AgentRequest, T, Task{ValueTuple{int, object}}})"/>.</param>
/// <param name="Argument">The route's <c>{ref}</c> segment, when it has one.</param>
internal sealed record ExtensionRoute(AgentMiddleware.EndpointHandler Handler, string? Argument = null);
