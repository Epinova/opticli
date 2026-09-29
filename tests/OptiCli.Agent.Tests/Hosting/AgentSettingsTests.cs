using OptiCli.Agent.Hosting;
using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Hosting;

public class AgentSettingsTests
{
    private const string Remote = "Server=tcp:dev.example.net,1433;Database=ExampleDb;User Id=app;Password=secret";

    private static AgentSettings Settings(string? approved) =>
        new("EPiServerDB", null, "token", AgentProtocol.ParseRemote(approved));

    [Fact]
    public void Without_an_approval_only_local_databases_are_allowed()
    {
        var settings = Settings(null);

        Assert.False(settings.SharedDatabase);
        Assert.True(settings.Allows(ConnectionGuard.Check("Server=localhost;Database=ExampleDb")));
        Assert.False(settings.Allows(ConnectionGuard.Check(Remote)));
    }

    [Fact]
    public void The_approved_remote_database_is_allowed_and_nothing_else_remote()
    {
        var settings = Settings("tcp:dev.example.net,1433|ExampleDb");

        Assert.True(settings.SharedDatabase);
        Assert.True(settings.Allows(ConnectionGuard.Check(Remote)));
        Assert.True(settings.Allows(ConnectionGuard.Check(Remote.Replace("ExampleDb", "exampledb", StringComparison.Ordinal))));
        Assert.False(settings.Allows(ConnectionGuard.Check(Remote.Replace("ExampleDb", "OtherDb", StringComparison.Ordinal))));
        Assert.False(settings.Allows(ConnectionGuard.Check(Remote.Replace("dev.example.net", "prod.example.net", StringComparison.Ordinal))));
        Assert.True(settings.Allows(ConnectionGuard.Check("Server=localhost;Database=Anything")));
    }

    [Fact]
    public void A_failover_partner_elsewhere_or_a_missing_database_is_not_the_approved_remote()
    {
        var settings = Settings("tcp:dev.example.net,1433|ExampleDb");

        Assert.False(settings.Allows(ConnectionGuard.Check(Remote + ";Failover Partner=prod.example.net")));
        Assert.True(settings.Allows(ConnectionGuard.Check(Remote + ";Failover Partner=tcp:dev.example.net,1433")));
        Assert.False(Settings("tcp:dev.example.net,1433|").Allows(ConnectionGuard.Check(Remote.Replace("Database=ExampleDb;", "", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("|ExampleDb")]
    [InlineData("no-separator")]
    public void Malformed_approvals_approve_nothing(string value)
    {
        Assert.Null(AgentProtocol.ParseRemote(value));
    }
}
