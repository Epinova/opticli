using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class AgentLocatorTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Prefers_the_agent_next_to_the_cli()
    {
        var packaged = _root.Write("tool/agent/OptiCli.Agent.dll", "");
        _root.Write("src/OptiCli.Agent/bin/Debug/net8.0/OptiCli.Agent.dll", "");

        Assert.Equal(packaged, AgentLocator.Locate(_root.Combine("tool")));
    }

    [Fact]
    public void Falls_back_to_the_agent_project_build_in_a_checkout()
    {
        var release = _root.Write("src/OptiCli.Agent/bin/Release/net8.0/OptiCli.Agent.dll", "");

        Assert.Equal(release, AgentLocator.Locate(_root.Combine("src/OptiCli/bin/Release/net8.0")));
    }

    [Fact]
    public void Missing_agent_is_not_found()
    {
        Assert.Throws<NotFoundException>(() => AgentLocator.Locate(_root.Combine("tool")));
    }
}
