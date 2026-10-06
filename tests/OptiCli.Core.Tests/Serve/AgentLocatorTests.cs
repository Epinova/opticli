using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class AgentLocatorTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void Prefers_the_agent_next_to_the_cli(int major)
    {
        var packaged = _root.Write($"tool/agent/cms{major}/OptiCli.Agent.dll", "");
        _root.Write("src/OptiCli.Agent/bin/Debug/net8.0/OptiCli.Agent.dll", "");
        _root.Write("src/OptiCli.Agent/bin/Debug/net10.0/OptiCli.Agent.dll", "");

        Assert.Equal(packaged, AgentLocator.Locate(_root.Combine("tool"), major));
    }

    [Fact]
    public void Picks_the_build_for_the_cms_major()
    {
        var cms12 = _root.Write("tool/agent/cms12/OptiCli.Agent.dll", "");
        var cms13 = _root.Write("tool/agent/cms13/OptiCli.Agent.dll", "");

        Assert.Equal(cms12, AgentLocator.Locate(_root.Combine("tool"), 12));
        Assert.Equal(cms13, AgentLocator.Locate(_root.Combine("tool"), 13));
    }

    [Theory]
    [InlineData(12, "net8.0")]
    [InlineData(13, "net10.0")]
    public void Falls_back_to_the_agent_project_build_in_a_checkout(int major, string framework)
    {
        var release = _root.Write($"src/OptiCli.Agent/bin/Release/{framework}/OptiCli.Agent.dll", "");

        Assert.Equal(release, AgentLocator.Locate(_root.Combine("src/OptiCli/bin/Release/net8.0"), major));
    }

    [Fact]
    public void The_old_single_agent_layout_is_not_used()
    {
        // An agent/OptiCli.Agent.dll from an older build output is CMS 12's and would crash a CMS 13 site.
        _root.Write("tool/agent/OptiCli.Agent.dll", "");

        Assert.Throws<NotFoundException>(() => AgentLocator.Locate(_root.Combine("tool"), 13));
    }

    [Fact]
    public void Missing_agent_is_not_found()
    {
        var error = Assert.Throws<NotFoundException>(() => AgentLocator.Locate(_root.Combine("tool"), 13));
        Assert.Contains("CMS 13", error.Message);
    }
}
