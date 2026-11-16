using System.Text.Json;
using ModelContextProtocol.Protocol;
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
    public void A_projects_items_and_the_project_are_logged_never_a_comment()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"project":"Spring campaign","items":["123","124_7",125,{"x":1}],"comment":"Please check the prices","requestApproval":true}""")!;

        Assert.Equal(new[] { "123", "124_7", "125" }, ToolAudit.References(arguments));
        Assert.Equal("Spring campaign", ToolAudit.Project(arguments));
        Assert.Null(ToolAudit.Project(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"reference":"123"}""")));
    }

    [Fact]
    public void The_project_is_logged_by_the_id_the_result_names_with_the_name_it_was_given()
    {
        static CallToolResult Result(string text, bool error = false) => new() { IsError = error ? true : null, Content = [new TextContentBlock { Text = text }] };

        Assert.Equal("12 (Spring campaign)", ToolAudit.Project("Spring campaign", Result("""{"project":{"id":12,"name":"Spring campaign"}}""")));
        Assert.Equal("12", ToolAudit.Project("12", Result("""{"project":{"id":12}}""")));
        // create_project names it in its result only; a save into a project in its result's project.
        Assert.Equal("13", ToolAudit.Project(null, Result("""{"project":{"id":13,"name":"New"},"created":true}""")));
        // A dry-run create has no id yet; a refusal names what was asked.
        Assert.Null(ToolAudit.Project(null, Result("""{"project":{"id":0,"name":"New"}}""")));
        Assert.Equal("Spring campaign", ToolAudit.Project("Spring campaign", Result("""{"code":"not_found","project":{"id":12}}""", error: true)));
        Assert.Null(ToolAudit.Project(null, Result("""{"content":{"ref":"5_6"},"projects":[{"id":12}]}""")));
    }

    [Fact]
    public void Caller_chosen_values_cannot_forge_log_lines()
    {
        Assert.Equal("Appfake entry", McpAudit.Clean("App\nfake entry"));
        Assert.Equal(201, McpAudit.Clean(new string('x', 500)).Length);
    }
}
