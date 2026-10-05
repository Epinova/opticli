using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Drift;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Drift;

/// <summary>Every route that changes content passes the drift gate, so a new one can't skip it.</summary>
public class GatedRoutesTests
{
    /// <summary>Takes no content write, so it isn't gated: stopping the site changes nothing in the database.</summary>
    private static readonly AgentEndpoint[] UngatedWrites = [AgentEndpoint.Shutdown];

    [Fact]
    public void Every_endpoint_has_a_method_and_a_handler()
    {
        Assert.Equal(Enum.GetValues<AgentEndpoint>().Order(), AgentRouter.Methods.Keys.Order());
        Assert.Equal(Enum.GetValues<AgentEndpoint>().Order(), AgentMiddleware.Handlers.Keys.Order());
    }

    [Fact]
    public void Every_route_that_isnt_a_get_is_gated_and_no_get_is()
    {
        foreach (var (endpoint, method) in AgentRouter.Methods)
        {
            var gated = method != "GET" && !UngatedWrites.Contains(endpoint);
            Assert.True(gated == AgentMiddleware.Handlers[endpoint].Gated, $"{method} {endpoint} should {(gated ? "" : "not ")}pass the drift gate.");
        }
    }

    [Theory]
    [InlineData(nameof(AgentEndpoint.Delete), null)]
    [InlineData(nameof(AgentEndpoint.Publish), null)]
    [InlineData(nameof(AgentEndpoint.Draft), """{"name":"x"}""")]
    [InlineData(nameof(AgentEndpoint.Move), """{"parent":"5"}""")]
    [InlineData(nameof(AgentEndpoint.SiteHosts), """{"changes":[{"site":"Site A","host":"localhost:5001","action":"add"}]}""")]
    public async Task A_gated_write_stops_on_drift_before_its_endpoint_runs(string name, string? body)
    {
        var endpoint = Enum.Parse<AgentEndpoint>(name);
        var drifted = DriftReport.Create([new DriftItem("NewsPage", DriftAhead.Local, ContentModelComparison.OnlyInCode)], [], [], [], [], []);
        var services = new ServiceCollection()
            .AddSingleton(Settings(pinned: Remote, approvedRemote: RemoteApproval))
            .AddSingleton(new DriftCheck(() => drifted))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        }

        // Without CMS services the endpoint itself would fail differently: the drift error shows the gate came first.
        var ex = await Assert.ThrowsAsync<AgentException>(() => AgentMiddleware.Handlers[endpoint].Run(new AgentRequest(context, "123")));

        Assert.Equal(AgentErrorCodes.Drift, ex.Code);
    }
}
