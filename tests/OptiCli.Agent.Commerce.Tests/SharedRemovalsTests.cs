using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Tests;

/// <summary>A Commerce removal is for good, so on a shared database it needs --allow-destructive; locally it doesn't.</summary>
public class SharedRemovalsTests
{
    private static AgentRequest Request(bool shared)
    {
        var settings = new AgentSettings(AgentProtocol.DefaultConnectionName, null, "token",
            shared ? AgentProtocol.ParseRemote("tcp:dev.example.net,1433|ExampleDb") : null);
        var services = new ServiceCollection().AddSingleton(settings).BuildServiceProvider();
        return new AgentRequest(new DefaultHttpContext { RequestServices = services }, null);
    }

    [Fact]
    public void A_removal_on_a_shared_database_needs_allow_destructive()
    {
        var refused = Assert.Throws<AgentException>(() => SharedRemovals.Check(Request(shared: true), remove: true, allowDestructive: false, "a price"));
        Assert.Equal((AgentErrorCodes.Refused, SharedRemovals.Reason), (refused.Code, refused.Reason));
        Assert.Contains("--allow-destructive", refused.Hint);

        SharedRemovals.Check(Request(shared: true), remove: true, allowDestructive: true, "a price");
    }

    [Fact]
    public void Other_writes_and_local_databases_need_nothing()
    {
        SharedRemovals.Check(Request(shared: true), remove: false, allowDestructive: false, "a price");
        SharedRemovals.Check(Request(shared: false), remove: true, allowDestructive: false, "a price");
    }
}
