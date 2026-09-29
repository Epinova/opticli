using OptiCli.Agent.Hosting;

namespace OptiCli.Agent.Tests.Hosting;

public class HostingStartupListTests
{
    [Theory]
    [InlineData(null, "OptiCli.Agent")]
    [InlineData("", "OptiCli.Agent")]
    [InlineData("Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation", "Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;OptiCli.Agent")]
    [InlineData("A; B ;", "A;B;OptiCli.Agent")]
    [InlineData("A;OptiCli.Agent", "A;OptiCli.Agent")]
    [InlineData("opticli.agent;A", "opticli.agent;A")]
    [InlineData("A;A;B", "A;B;OptiCli.Agent")]
    public void Appends_without_dropping_or_duplicating(string? existing, string expected) =>
        Assert.Equal(expected, HostingStartupList.Append(existing, "OptiCli.Agent"));
}
