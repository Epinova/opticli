using OptiCli.Agent.Hosting;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// The sealed apply gives the site its token and connection string as one line on stdin, never in its environment block:
/// the agent takes the token, the pinned connection and connection strings, and nothing else, and refuses to start
/// without them.
/// </summary>
public class StdinSecretsTests
{
    [Fact]
    public void The_line_holds_the_token_the_pin_and_connection_strings()
    {
        var variables = StdinSecrets.Parse("""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": "Server=sql;Database=cms;User ID=w;Password=p", "ConnectionStrings__EPiServerDB": "Server=sql;Database=cms;User ID=w;Password=p"}""");

        Assert.Equal(
            [AgentProtocol.TokenVariable, AgentProtocol.DatabaseVariable, "ConnectionStrings__EPiServerDB"],
            variables.Select(v => v.Key).ToList());
    }

    [Fact]
    public void The_line_may_hold_productions_event_bus_and_blob_storage()
    {
        var variables = StdinSecrets.Parse("""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": "x", "ConnectionStrings__EPiServerAzureEvents": "e", "OPTICLI_EVENTS_TOPIC": "t", "ConnectionStrings__EPiServerAzureBlobs": "b", "OPTICLI_BLOBS_CONTAINER": "c"}""");

        Assert.Equal(6, variables.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"OPTICLI_DB": "Server=sql;Database=cms"}""")]
    [InlineData("""{"OPTICLI_TOKEN": "t0k"}""")]
    [InlineData("""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": "x", "OPTICLI_REMOTE_DB": "other|db"}""")]
    [InlineData("""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": "x", "DOTNET_STARTUP_HOOKS": "/tmp/evil.dll"}""")]
    [InlineData("""{"OPTICLI_TOKEN": "t0k", "OPTICLI_DB": 5}""")]
    public void Anything_else_stops_the_site_without_echoing_the_line(string? line)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => StdinSecrets.Parse(line));

        Assert.StartsWith("[opticli] Refusing to start", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t0k", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", refused.Message, StringComparison.Ordinal);
    }
}
