using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Tests;

/// <summary>The Commerce agent's routes, as the CMS agent's router hands them on.</summary>
public class CommerceRoutesTests
{
    private readonly CommerceExtension _commerce = new();

    [Theory]
    [InlineData("GET", "", false)]
    [InlineData("POST", "refs", false)]
    [InlineData("GET", "children", false)]
    [InlineData("GET", "relations", false)]
    [InlineData("POST", "relations", true)]
    [InlineData("GET", "prices", false)]
    [InlineData("POST", "prices", true)]
    [InlineData("GET", "warehouses", false)]
    [InlineData("POST", "warehouses", true)]
    [InlineData("GET", "inventory", false)]
    [InlineData("POST", "inventory", true)]
    [InlineData("GET", "markets", false)]
    [InlineData("POST", "markets", true)]
    public void Every_route_has_its_method_and_only_writes_pass_the_drift_gate(string method, string path, bool gated)
    {
        var route = _commerce.Route(method, Segments(path));

        Assert.NotNull(route);
        Assert.Equal(gated, route.Handler.Gated);
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "refs")]
    [InlineData("DELETE", "prices")]
    [InlineData("PUT", "markets")]
    public void Another_method_is_a_usage_error_naming_the_routes(string method, string path)
    {
        var ex = Assert.Throws<AgentException>(() => _commerce.Route(method, Segments(path)));

        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains(AgentRoutes.CommerceRefs, ex.Hint);
    }

    [Fact]
    public void An_unknown_route_is_none()
    {
        Assert.Null(_commerce.Route("GET", ["nope"]));
        Assert.Equal("commerce", _commerce.Segment);
    }

    internal static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
