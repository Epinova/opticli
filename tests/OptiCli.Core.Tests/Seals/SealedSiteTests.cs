using OptiCli.Core.Discovery;
using OptiCli.Core.Safety;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// The site a sealed apply starts against production: nothing secret in its environment (the token and the write login go
/// on its stdin), the agent as its only startup hook, shared mode, and loopback addresses only.
/// </summary>
public sealed class SealedSiteTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Its_environment_holds_no_secret_and_drops_what_would_compete_with_the_pin()
    {
        var path = _folder.Write("plan.json", """{"operations": [{"op": "set", "ref": "61104228-962d-4dd2-925c-b5b12e2373ee", "properties": {"TeaserText": "x"}}]}""");
        var plan = await PlanSnapshot.ReadAsync(path, CancellationToken.None);
        var verified = SealCheck.Verify(SealFixtures.Seal(planHash: plan.Hash) with { Plan = path }, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, DateTimeOffset.UtcNow);
        var write = ProductionWrite.Authorize(verified, ConnectionSafety.Approve("Server=sql.example.net;Database=cms-prod;User ID=reader;Password=read-secret", production: true), "cms-prod", new WriteCredentials("writer", "write-secret"));
        var project = new ProjectInfo(new CsprojFile(Path.Combine(_folder.Path, "Site.csproj"), "Microsoft.NET.Sdk.Web", null, "net8.0", "Site", new Dictionary<string, string?>()), _folder.Path, null, _folder.Path, "test");
        var launch = new SealedLaunch(project, write, "EPiServerDB", new SiteOutput(Path.Combine(_folder.Path, "Site.dll"), "test"), "/opticli/agent/cms12/OptiCli.Agent.dll", 12, 5299, TimeSpan.FromSeconds(1), "/state/drift.json", "/state/run.log");

        var variables = SealedSite.Environment(launch).ToDictionary(v => v.Key, v => v.Value);

        Assert.DoesNotContain(variables.Values, v => v is not null && (v.Contains("secret", StringComparison.Ordinal) || v.Contains("Password", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(AgentProtocol.SecretsFromStdin, variables[AgentProtocol.SecretsVariable]);
        Assert.Equal("/opticli/agent/cms12/OptiCli.Agent.dll", variables[SiteEnvironment.StartupHooksVariable]);
        Assert.Equal("sql.example.net|cms-prod", variables[AgentProtocol.RemoteDatabaseVariable]);
        Assert.Equal("http://127.0.0.1:5299", variables["ASPNETCORE_URLS"]);
        Assert.Equal("Development", variables["ASPNETCORE_ENVIRONMENT"]);
        // Removed from what the site inherits (an exported pin or token of an earlier `opticli env` included).
        foreach (var removed in new[] { AgentProtocol.TokenVariable, AgentProtocol.DatabaseVariable, "ConnectionStrings__EPiServerDB", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES" })
        {
            Assert.True(variables.TryGetValue(removed, out var value) && value is null, removed);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5299", true)]
    [InlineData("http://localhost:5299", true)]
    [InlineData("http://[::1]:5299", true)]
    [InlineData("http://0.0.0.0:80", false)]
    [InlineData("http://[::]:80", false)]
    [InlineData("https://intranet.example.net:443", false)]
    [InlineData("http://192.168.1.20:5000", false)]
    public void Only_loopback_addresses_are_this_machines(string address, bool loopback)
    {
        Assert.Equal(loopback, SealedSite.Loopback(address, port: null));
        Assert.Equal(loopback ? [] : [address], SealedSite.Elsewhere([address, "http://127.0.0.1:5299"]));
    }
}
