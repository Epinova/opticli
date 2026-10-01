using EPiServer.Data;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Hosting;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Hosting;

[Collection(HostingCollection.Name)]
public class DataAccessOptionsGuardTests
{
    [Fact]
    public void Local_cms_connections_pass()
    {
        var result = Validate(Settings(), ("EPiServerDB", Local), ("Other", OtherLocal));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Every_cms_connection_must_be_allowed_not_only_the_one_used()
    {
        var result = Validate(Settings(), ("EPiServerDB", Local), ("Reporting", Remote));

        Assert.True(result.Failed);
        Assert.StartsWith("[opticli] CMS connection 'Reporting' is not local.", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void The_approved_remote_database_passes_and_no_other_remote_one()
    {
        var settings = Settings(approvedRemote: RemoteApproval);

        Assert.True(Validate(settings, ("EPiServerDB", Remote)).Succeeded);
        var result = Validate(settings, ("EPiServerDB", OtherRemote));
        Assert.True(result.Failed);
        Assert.Contains("is not local nor the approved development database", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_pin_the_connection_the_cms_uses_must_be_the_pin()
    {
        var settings = Settings(pinned: Local);

        Assert.True(Validate(settings, ("Other", OtherLocal), ("EPiServerDB", Local)).Succeeded);

        var result = Validate(settings, ("EPiServerDB", OtherLocal), ("Pinned", Local));
        Assert.True(result.Failed);
        Assert.Equal(["[opticli] The CMS would connect with 'EPiServerDB', which is not the pinned connection string."], result.Failures);
    }

    [Fact]
    public void With_a_pin_and_no_cms_connection_at_all_the_guard_fails()
    {
        var result = Validate(Settings(pinned: Local));

        Assert.True(result.Failed);
        Assert.Contains("would connect with '<none>'", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_are_also_written_to_stderr_for_the_serve_log()
    {
        var stderr = Stderr(() => Validate(Settings(), ("EPiServerDB", Remote)));

        Assert.StartsWith("[opticli] Refusing to start: CMS connection 'EPiServerDB' is not local.", stderr, StringComparison.Ordinal);
    }

    private static ValidateOptionsResult Validate(AgentSettings settings, params (string Name, string ConnectionString)[] connections)
    {
        var options = new DataAccessOptions();
        foreach (var (name, connectionString) in connections)
        {
            options.ConnectionStrings.Add(new ConnectionStringOptions { Name = name, ConnectionString = connectionString });
        }
        return new DataAccessOptionsGuard(settings).Validate(Options.DefaultName, options);
    }
}
