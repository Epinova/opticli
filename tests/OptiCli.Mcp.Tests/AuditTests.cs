using System.Text.Json;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp.Tests;

public class AuditTests
{
    [Fact]
    public void Only_arguments_that_name_content_are_logged()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"reference":"123","parent":456,"properties":{"Heading":"Secret draft text"},"name":"A page","dryRun":true}""")!;
        Assert.Equal(new[] { "123", "456" }, ToolAudit.References(arguments));
    }

    [Fact]
    public void No_arguments_log_nothing() => Assert.Empty(ToolAudit.References(null));

    [Fact]
    public void Caller_chosen_values_cannot_forge_log_lines()
    {
        Assert.Equal("Appfake entry", McpAudit.Clean("App\nfake entry"));
        Assert.Equal(201, McpAudit.Clean(new string('x', 500)).Length);
    }
}
