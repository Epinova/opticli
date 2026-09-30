using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Http;

public class AgentRouterTests
{
    [Theory]
    [InlineData("GET", "/v1/ping", "Ping", null)]
    [InlineData("GET", "/v1/types/ArticlePage", "Type", "ArticlePage")]
    [InlineData("GET", "/v1/types/0b1c2d3e-0000-4000-8000-000000000001", "Type", "0b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("POST", "/v1/content", "Create", null)]
    [InlineData("POST", "/v1/content/", "Create", null)]
    [InlineData("POST", "/v1/content/123/draft", "Draft", "123")]
    [InlineData("POST", "/v1/content/123_456/draft", "Draft", "123_456")]
    [InlineData("POST", "/v1/content/123/languages", "Languages", "123")]
    [InlineData("POST", "/v1/content/123/publish", "Publish", "123")]
    [InlineData("POST", "/v1/content/123/move", "Move", "123")]
    [InlineData("POST", "/v1/content/123/access", "Access", "123")]
    [InlineData("DELETE", "/v1/content/123", "Delete", "123")]
    [InlineData("GET", "/v1/content/123", "Read", "123")]
    [InlineData("GET", "/v1/content/123_456", "Read", "123_456")]
    [InlineData("get", "/v1/content/0b1c2d3e-0000-4000-8000-000000000001", "Read", "0b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("get", "/v1/ping", "Ping", null)]
    public void Matches_every_route(string method, string path, string endpoint, string? argument)
    {
        var match = AgentRouter.Match(method, path);

        Assert.Equal(Enum.Parse<AgentEndpoint>(endpoint), match.Endpoint);
        Assert.Equal(argument, match.Argument);
    }

    [Theory]
    [InlineData("/v1/nope")]
    [InlineData("/v1/content/123/draft/extra")]
    [InlineData("/v1/types")]
    [InlineData("")]
    [InlineData("/")]
    // Unversioned routes are unknown, with a hint naming the protocol.
    [InlineData("/ping")]
    [InlineData("/content/123/draft")]
    public void Unknown_routes_are_not_found_and_mention_the_protocol(string path)
    {
        var error = Assert.Throws<AgentException>(() => AgentRouter.Match("GET", path));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Equal(404, error.Status);
        Assert.Contains("protocol v1", error.Message);
        Assert.Contains("/v1/ping", error.Hint);
    }

    [Theory]
    [InlineData("/v2/ping")]
    [InlineData("/v0/content/123/draft")]
    [InlineData("/v12/types/ArticlePage")]
    public void Other_protocol_versions_are_reported_as_such(string path)
    {
        var error = Assert.Throws<AgentException>(() => AgentRouter.Match("GET", path));

        Assert.Equal(AgentErrorCodes.UnsupportedProtocol, error.Code);
        Assert.Equal(404, error.Status);
        Assert.Contains("protocol v1", error.Message);
    }

    [Theory]
    [InlineData("POST", "/v1/ping", "GET")]
    [InlineData("GET", "/v1/content/123/draft", "POST")]
    [InlineData("POST", "/v1/content/123", "GET or DELETE")]
    [InlineData("PUT", "/v1/content/123", "GET or DELETE")]
    [InlineData("DELETE", "/v1/content", "POST")]
    public void Wrong_method_is_a_usage_error_naming_the_right_one(string method, string path, string allowed)
    {
        var error = Assert.Throws<AgentException>(() => AgentRouter.Match(method, path));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Equal(400, error.Status);
        Assert.Contains($"use {allowed}", error.Message);
    }

    [Fact]
    public void Protocol_route_builders_produce_routes_the_router_matches()
    {
        Assert.Equal(AgentEndpoint.Ping, AgentRouter.Match("GET", AgentRoutes.Ping).Endpoint);
        Assert.Equal(new RouteMatch(AgentEndpoint.Type, "ArticlePage"), AgentRouter.Match("GET", AgentRoutes.Type("ArticlePage")));
        Assert.Equal(AgentEndpoint.Create, AgentRouter.Match("POST", AgentRoutes.Create).Endpoint);
        Assert.Equal(new RouteMatch(AgentEndpoint.Draft, "123_456"), AgentRouter.Match("POST", AgentRoutes.Draft("123_456")));
        Assert.Equal(new RouteMatch(AgentEndpoint.Languages, "123"), AgentRouter.Match("POST", AgentRoutes.Languages("123")));
        Assert.Equal(new RouteMatch(AgentEndpoint.Publish, "123"), AgentRouter.Match("POST", AgentRoutes.Publish("123")));
        Assert.Equal(new RouteMatch(AgentEndpoint.Move, "123"), AgentRouter.Match("POST", AgentRoutes.Move("123")));
        Assert.Equal(new RouteMatch(AgentEndpoint.Delete, "123"), AgentRouter.Match("DELETE", AgentRoutes.Delete("123")));
        Assert.Equal(new RouteMatch(AgentEndpoint.Read, "123"), AgentRouter.Match("GET", AgentRoutes.Read("123")));
    }

    [Theory]
    [InlineData(null, null, "/v1/content/123")]
    [InlineData("en", null, "/v1/content/123?lang=en")]
    [InlineData(null, "456", "/v1/content/123?version=456")]
    [InlineData("pt-BR", "latest", "/v1/content/123?lang=pt-BR&version=latest")]
    public void Read_route_carries_language_and_version_as_query_parameters(string? lang, string? version, string expected)
    {
        var route = AgentRoutes.Read("123", lang, version);

        Assert.Equal(expected, route);
        // The router sees the path only; ASP.NET Core splits the query off before the agent does.
        Assert.Equal(new RouteMatch(AgentEndpoint.Read, "123"), AgentRouter.Match("GET", route.Split('?')[0]));
    }

    [Theory]
    [InlineData(AgentErrorCodes.Usage, 400)]
    [InlineData(AgentErrorCodes.Unauthorized, 401)]
    [InlineData(AgentErrorCodes.Refused, 403)]
    [InlineData(AgentErrorCodes.NotFound, 404)]
    [InlineData(AgentErrorCodes.UnsupportedProtocol, 404)]
    [InlineData(AgentErrorCodes.Conflict, 409)]
    [InlineData(AgentErrorCodes.Validation, 422)]
    [InlineData(AgentErrorCodes.Internal, 500)]
    public void Error_codes_map_to_http_statuses(string code, int status) => Assert.Equal(status, AgentErrorCodes.HttpStatus(code));
}
