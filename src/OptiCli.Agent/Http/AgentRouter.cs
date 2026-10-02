using System.Text.RegularExpressions;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

internal enum AgentEndpoint
{
    Ping,
    Shutdown,
    Type,
    Create,
    Upload,
    Draft,
    Languages,
    Publish,
    Move,
    Access,
    Delete,
    Read,
    Unpublish,
    Discard,
    RemoveLanguage,
    Drift,
}

/// <param name="Argument">The <c>{name}</c> or <c>{ref}</c> segment, when the route has one.</param>
internal sealed record RouteMatch(AgentEndpoint Endpoint, string? Argument = null);

/// <summary>
/// Maps a method and a path below <see cref="AgentProtocol.BasePath"/> to an endpoint. Hand-rolled
/// rather than endpoint routing, so the agent works whatever the site did to its routing.
/// </summary>
internal static partial class AgentRouter
{
    private static readonly string VersionSegment = AgentRoutes.Prefix.TrimStart('/');

    private static readonly string RouteList =
        $"GET {AgentRoutes.Prefix}/ping, POST {AgentRoutes.Prefix}/shutdown, GET {AgentRoutes.Prefix}/drift, GET {AgentRoutes.Prefix}/types/{{name}}, POST {AgentRoutes.Prefix}/content, POST {AgentRoutes.Prefix}/media, " +
        $"POST {AgentRoutes.Prefix}/content/{{ref}}/draft|languages|remove-language|publish|unpublish|discard|move|access, GET|DELETE {AgentRoutes.Prefix}/content/{{ref}}";

    /// <exception cref="AgentException">No route matches, or it belongs to another protocol version.</exception>
    public static RouteMatch Match(string method, string? path)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = $"{AgentProtocol.BasePath}/{string.Join('/', segments)}";
        var shown = $"{method} {route}";

        if (segments.Length == 0 || !string.Equals(segments[0], VersionSegment, StringComparison.Ordinal))
        {
            if (segments.Length > 0 && VersionPattern().IsMatch(segments[0]))
            {
                throw new AgentException(
                    AgentErrorCodes.UnsupportedProtocol,
                    $"This agent speaks opticli protocol v{AgentProtocol.Version}, but the request was for {segments[0]}.",
                    "The CLI and the agent injected into the site are different versions. Restart the site with the same opticli version (opticli serve --stop, then serve).");
            }
            throw NotFound(shown);
        }

        var (endpoint, argument) = segments[1..] switch
        {
            ["ping"] => (AgentEndpoint.Ping, (string?)null),
            ["shutdown"] => (AgentEndpoint.Shutdown, (string?)null),
            ["drift"] => (AgentEndpoint.Drift, (string?)null),
            ["types", var name] => (AgentEndpoint.Type, name),
            ["content"] => (AgentEndpoint.Create, (string?)null),
            ["media"] => (AgentEndpoint.Upload, (string?)null),
            ["content", var reference] when IsMethod(method, "GET") => (AgentEndpoint.Read, reference),
            ["content", var reference] => (AgentEndpoint.Delete, reference),
            ["content", var reference, "draft"] => (AgentEndpoint.Draft, reference),
            ["content", var reference, "languages"] => (AgentEndpoint.Languages, reference),
            ["content", var reference, "publish"] => (AgentEndpoint.Publish, reference),
            ["content", var reference, "remove-language"] => (AgentEndpoint.RemoveLanguage, reference),
            ["content", var reference, "unpublish"] => (AgentEndpoint.Unpublish, reference),
            ["content", var reference, "discard"] => (AgentEndpoint.Discard, reference),
            ["content", var reference, "move"] => (AgentEndpoint.Move, reference),
            ["content", var reference, "access"] => (AgentEndpoint.Access, reference),
            _ => throw NotFound(shown),
        };
        var allowed = Methods[endpoint];

        if (!IsMethod(method, allowed))
        {
            var accepted = endpoint == AgentEndpoint.Delete ? "GET or DELETE" : allowed;
            throw AgentException.Usage($"{route} does not accept {method}; use {accepted}.", $"Routes: {RouteList}.");
        }

        // ASP.NET Core has already percent-decoded the path.
        return new RouteMatch(endpoint, argument);
    }

    /// <summary>The HTTP method each endpoint takes.</summary>
    internal static readonly IReadOnlyDictionary<AgentEndpoint, string> Methods = new Dictionary<AgentEndpoint, string>
    {
        [AgentEndpoint.Ping] = "GET",
        [AgentEndpoint.Shutdown] = "POST",
        [AgentEndpoint.Drift] = "GET",
        [AgentEndpoint.Type] = "GET",
        [AgentEndpoint.Create] = "POST",
        [AgentEndpoint.Upload] = "POST",
        [AgentEndpoint.Read] = "GET",
        [AgentEndpoint.Delete] = "DELETE",
        [AgentEndpoint.Draft] = "POST",
        [AgentEndpoint.Languages] = "POST",
        [AgentEndpoint.Publish] = "POST",
        [AgentEndpoint.RemoveLanguage] = "POST",
        [AgentEndpoint.Unpublish] = "POST",
        [AgentEndpoint.Discard] = "POST",
        [AgentEndpoint.Move] = "POST",
        [AgentEndpoint.Access] = "POST",
    };

    private static bool IsMethod(string method, string expected) => string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    private static AgentException NotFound(string shown) => AgentException.NotFound(
        $"No agent route for {shown}. This agent speaks opticli protocol v{AgentProtocol.Version}.",
        $"Routes: {RouteList}.");

    [GeneratedRegex("^v[0-9]+$")]
    private static partial Regex VersionPattern();
}
