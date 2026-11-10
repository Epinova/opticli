using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Cms;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;
using OptiCli.Mcp.Tools;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// A site's write tools (<see cref="IOptiCliWriteTool{TArgs}"/>): registered and checked, offered with the module's
/// schema and annotations, and their plans dry-run, compared by the plan token, run step by step with planned content
/// filled in, stopped at the first failure, and audited. Without a CMS: <see cref="FakePlanSteps"/> stands in for the
/// operations, which the end-to-end tests run for real.
/// </summary>
public sealed class WriteToolTests
{
    private readonly FakePlanSteps _cms = new();

    private Task<TestSite> Site(Action<IOptiCliMcpBuilder>? module = null, Action<OptiCliMcpOptions>? configure = null) =>
        TestSite.StartAsync(configure,
            services: s =>
            {
                s.AddSingleton(_cms);
                s.AddSingleton<IContentPlanSteps>(_cms);
                s.AddSingleton<TestCounter>();
            },
            module: module ?? (m => m.WithWriteTool<RenameTool>().WithWriteTool<BuildTool>()));

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static async Task<CallToolResult> Call(McpClient client, string tool, object arguments) =>
        await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)));

    private static JsonObject Json(CallToolResult result) => JsonNode.Parse(Text(result))!.AsObject();

    private static JsonObject Ok(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return Json(result);
    }

    private static AgentError Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        Assert.StartsWith("{", Text(result), StringComparison.Ordinal);
        return JsonSerializer.Deserialize<AgentError>(Text(result), AgentJson.Options)!;
    }

    /// <summary>The dry run's token, after checking the dry run passed.</summary>
    private static async Task<string> DryRun(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = Ok(await client.CallToolAsync(tool, new Dictionary<string, object?>(arguments) { ["dryRun"] = true }));
        Assert.True(result["valid"]!.GetValue<bool>());
        return result["planToken"]!.GetValue<string>();
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> arguments, string token) =>
        new(arguments) { ["planToken"] = token };

    private static readonly Dictionary<string, object?> RenameBoth = new() { ["ids"] = "5,6", ["to"] = "Renamed" };

    [Fact]
    public async Task A_write_tool_is_offered_with_its_arguments_dryRun_and_planToken_flat()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        var rename = tools["test_rename"];
        Assert.Equal("Test: rename", rename.Title);
        Assert.Equal("Renames content. " + WriteToolType.Workflow, rename.Description);
        Assert.Equal((false, false, false, false), (rename.Annotations!.ReadOnlyHint, rename.Annotations.DestructiveHint, rename.Annotations.IdempotentHint, rename.Annotations.OpenWorldHint));
        Assert.Equal("Test: rename", rename.Annotations.Title);
        var schema = rename.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        Assert.Equal(["ids", "to", "count", "dryRun", "planToken"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Content ids, comma-separated.", properties.GetProperty("ids").GetProperty("description").GetString());
        Assert.Equal(WriteToolType.DryRunDescription, properties.GetProperty("dryRun").GetProperty("description").GetString());
        Assert.False(properties.GetProperty("dryRun").GetProperty("default").GetBoolean());
        Assert.Equal(WriteToolType.PlanTokenDescription, properties.GetProperty("planToken").GetProperty("description").GetString());
        Assert.Equal(["ids", "to"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        // Moves make it destructive, as move_content is.
        Assert.True(tools["test_build"].Annotations!.DestructiveHint);
        Assert.Null(tools["test_build"].Title);
    }

    [Fact]
    public async Task The_dry_run_checks_every_step_and_saves_nothing()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var result = Ok(await Call(client, "test_rename", new { ids = "5,6", to = "Renamed", dryRun = true }));

        Assert.Equal("test_rename", result["tool"]!.GetValue<string>());
        Assert.True(result["dryRun"]!.GetValue<bool>());
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(result["planToken"]!.GetValue<string>()));
        var steps = result["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        Assert.Equal((1, "update", "5_10"), (steps[0]!["step"]!.GetValue<int>(), steps[0]!["kind"]!.GetValue<string>(), steps[0]!["content"]!["ref"]!.GetValue<string>()));
        Assert.Equal("Renamed", steps[1]!["changes"]!["Heading"]!.GetValue<string>());
        Assert.Null(steps[0]!["dryRun"]);
        Assert.Empty(_cms.Ran);
        var updates = _cms.Checked.Cast<UpdateStep>().ToList();
        Assert.Equal(["5_10", "6_20"], updates.Select(u => u.Content.ToString()));
        Assert.All(updates, u => Assert.Equal(("Renamed", "en"), (u.Name, u.Language)));
        // Only the call is audited: no step was saved.
        Assert.DoesNotContain(site.Audit.Audit, m => m.Contains(" step ", StringComparison.Ordinal));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_rename ok: user editor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_real_run_with_the_dry_runs_token_checks_again_then_saves_every_step_in_order_and_audits_each()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_rename", RenameBoth);
        _cms.Calls.Clear();

        var result = Ok(await client.CallToolAsync("test_rename", With(RenameBoth, token)));

        Assert.False(result["dryRun"]!.GetValue<bool>());
        Assert.Equal((2, 2), (result["completed"]!.GetValue<int>(), result["total"]!.GetValue<int>()));
        var steps = result["steps"]!.AsArray();
        Assert.Equal(["saved", "saved"], steps.Select(s => s!["status"]!.GetValue<string>()));
        Assert.Equal("5_11", steps[0]!["content"]!["ref"]!.GetValue<string>());
        Assert.Equal("http://localhost/edit/6_21", steps[1]!["editUrl"]!.GetValue<string>());
        // Both checked before the first was saved.
        Assert.Equal(["check", "check", "run", "run"], _cms.Calls.Select(c => c.Phase));
        Assert.Equal(2, site.Audit.Audit.Count(m => m.StartsWith("MCP tool test_rename step ", StringComparison.Ordinal)));
        Assert.Contains("MCP tool test_rename step 1 update saved: user editor, client ", site.Audit.All);
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_rename step 2 update saved: user editor", StringComparison.Ordinal) && m.EndsWith("content 6_21", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_that_changed_since_the_dry_run_is_refused_and_nothing_is_saved()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_rename", RenameBoth);

        // Someone saved page 6 meanwhile: the tool plans from its newer version.
        _cms.Versions[6]++;
        var changed = Error(await client.CallToolAsync("test_rename", With(RenameBoth, token)));
        Assert.Equal(AgentErrorCodes.Conflict, changed.Code);
        Assert.Contains("isn't the one the dry run showed", changed.Message);
        Assert.Contains("dryRun: true again", changed.Hint);

        // Other arguments (also only a default one written out differently) or another tool's token.
        var token2 = await DryRun(client, "test_rename", RenameBoth);
        Assert.Equal(AgentErrorCodes.Conflict, Error(await client.CallToolAsync("test_rename", new Dictionary<string, object?>(With(RenameBoth, token2)) { ["to"] = "Other" })).Code);
        Assert.Equal(AgentErrorCodes.Conflict, Error(await client.CallToolAsync("test_rename", new Dictionary<string, object?>(With(RenameBoth, token2)) { ["count"] = 2 })).Code);
        Ok(await client.CallToolAsync("test_rename", new Dictionary<string, object?>(With(RenameBoth, token2)) { ["count"] = 1 }));

        // A token that isn't one: neither a token of this site's, nor this tool's.
        var forged = Error(await client.CallToolAsync("test_rename", With(RenameBoth, "abc")));
        Assert.Equal(AgentErrorCodes.Usage, forged.Code);
        var buildToken = await DryRun(client, "test_build", new Dictionary<string, object?>());
        Assert.Equal(AgentErrorCodes.Usage, Error(await client.CallToolAsync("test_rename", With(RenameBoth, buildToken))).Code);

        // No token at all.
        var none = Error(await client.CallToolAsync("test_rename", RenameBoth));
        Assert.Equal(AgentErrorCodes.Usage, none.Code);
        Assert.Contains("dryRun: true first", none.Hint);

        Assert.Single(_cms.Ran, s => s.Number == 1);
    }

    [Fact]
    public async Task A_token_is_the_editors_own()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_rename", RenameBoth);

        site.Roles.Set("other", "WebEditors");
        var tokens = await site.ConnectAsync("other");
        await using var other = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/episerver/opticli/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + tokens.Access },
        }, site.Client(), ownsHttpClient: true));

        Assert.Equal(AgentErrorCodes.Conflict, Error(await other.CallToolAsync("test_rename", With(RenameBoth, token))).Code);
        Assert.Empty(_cms.Ran);
    }

    [Fact]
    public async Task A_run_that_fails_at_a_step_stops_there_and_reports_what_was_saved()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var arguments = new Dictionary<string, object?> { ["ids"] = "5,6,7", ["to"] = "Renamed" };
        var token = await DryRun(client, "test_rename", arguments);
        _cms.FailRun = step => step.Number == 2
            ? new AgentException(AgentErrorCodes.Refused, "The site's validation refused it.", "Change the heading.")
            : null;

        var failed = await client.CallToolAsync("test_rename", With(arguments, token));

        var error = Error(failed);
        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Equal("Step 2 of 3 (update) failed: The site's validation refused it. Step 1 was saved and stays saved.", error.Message);
        Assert.Contains("which steps were saved", error.Hint);
        var report = Json(failed);
        Assert.Equal((1, 3), (report["completed"]!.GetValue<int>(), report["total"]!.GetValue<int>()));
        var steps = report["steps"]!.AsArray();
        Assert.Equal(["saved", "failed", "notRun"], steps.Select(s => s!["status"]!.GetValue<string>()));
        Assert.Equal("5_11", steps[0]!["content"]!["ref"]!.GetValue<string>());
        Assert.Equal("Change the heading.", steps[1]!["error"]!["hint"]!.GetValue<string>());
        Assert.Equal([1, 2], _cms.Ran.Select(s => s.Number));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_rename step 1 update saved", StringComparison.Ordinal));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_rename step 2 update failed", StringComparison.Ordinal) && m.EndsWith("content 6_20", StringComparison.Ordinal));
        Assert.DoesNotContain(site.Audit.Audit, m => m.Contains("step 3", StringComparison.Ordinal));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_rename failed: user editor", StringComparison.Ordinal));

        // An unexpected failure of the site's: internal, logged with the tool's name, the rest as before.
        _cms.FailRun = step => step.Number == 1 ? new InvalidOperationException("Server=secret") : null;
        var token2 = await DryRun(client, "test_rename", arguments);
        var internalError = Error(await client.CallToolAsync("test_rename", With(arguments, token2)));
        Assert.Equal(AgentErrorCodes.Internal, internalError.Code);
        Assert.DoesNotContain("secret", Text(await client.CallToolAsync("test_rename", With(arguments, await DryRun(client, "test_rename", arguments)))));
        Assert.StartsWith("Step 1 of 3 (update) failed: The site failed while running this tool", internalError.Message);
        Assert.EndsWith("No step was saved.", internalError.Message);
        Assert.Contains(site.Audit.Entries, e => e.Category == ToolErrors.LogCategory && e.Message == "The site's MCP tool test_rename failed.");
    }

    [Fact]
    public async Task A_step_the_check_refuses_makes_the_plan_unrunnable()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        _cms.RefuseCheck = step => step is UpdateStep { Content.ID: 6 } ? CmsCall.NotFound(new ContentReference(6)) : null;

        var result = Ok(await Call(client, "test_rename", new { ids = "5,6", to = "Renamed", dryRun = true }));

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Null(result["planToken"]);
        var steps = result["steps"]!.AsArray();
        Assert.Null(steps[0]!["error"]);
        Assert.Equal(AgentErrorCodes.NotFound, steps[1]!["error"]!["code"]!.GetValue<string>());

        // A gate of the module's, refused inside a step: its error, reason and all.
        _cms.RefuseCheck = step => step.Number == 1 ? ToolErrors.Refused("Deleting is off.", "Do it in the CMS.", McpErrorReasons.DeletingOff) : null;
        var gated = Ok(await Call(client, "test_rename", new { ids = "5,6", to = "Renamed", dryRun = true }));
        Assert.Equal((AgentErrorCodes.Refused, McpErrorReasons.DeletingOff), (gated["steps"]![0]!["error"]!["code"]!.GetValue<string>(), gated["steps"]![0]!["error"]!["reason"]!.GetValue<string>()));

        // A step with validation errors: the same.
        _cms.RefuseCheck = _ => null;
        _cms.Invalid = step => step.Number == 1;
        var invalid = Ok(await Call(client, "test_rename", new { ids = "5,6", to = "Renamed", dryRun = true }));
        Assert.False(invalid["valid"]!.GetValue<bool>());
        Assert.Null(invalid["planToken"]);

        // A plan that passed its dry run, refused when the real run checks it again: nothing is saved.
        _cms.Invalid = _ => false;
        var token = await DryRun(client, "test_rename", RenameBoth);
        _cms.RefuseCheck = step => step.Number == 2 ? new AgentException(AgentErrorCodes.Refused, "You don't have Edit access to 6 ('About us').") : null;
        var refused = Error(await client.CallToolAsync("test_rename", With(RenameBoth, token)));
        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Equal("Step 2 of 2 (update) can't run now: You don't have Edit access to 6 ('About us'). Nothing was saved.", refused.Message);
        Assert.Empty(_cms.Ran);
    }

    [Fact]
    public async Task Planned_content_is_checked_below_what_exists_and_filled_in_when_it_runs()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var dry = Ok(await Call(client, "test_build", new { dryRun = true }));
        var steps = dry["steps"]!.AsArray();
        Assert.Equal(["create", "create", "update", "move"], steps.Select(s => s!["kind"]!.GetValue<string>()));
        // The folder below page 5; the block below the folder, checked below page 5 as a ContentFolder's child.
        Assert.Equal(("5", "5"), (steps[0]!["parent"]!.GetValue<string>(), steps[0]!["checkedBelow"]!.GetValue<string>()));
        Assert.Equal(("step 1", "5"), (steps[1]!["parent"]!.GetValue<string>(), steps[1]!["checkedBelow"]!.GetValue<string>()));
        var checks = _cms.Checked.ToList();
        var block = (CreateStep)checks[1];
        Assert.Equal(("ContentFolder", new ContentReference(5)), (block.PlannedParentType, block.Parent.Existing));
        Assert.Null(((CreateStep)checks[0]).PlannedParentType);
        // The page's values that refer to the block are left out of its check, and listed.
        var update = (UpdateStep)checks[2];
        Assert.Equal(["Heading"], update.Properties.Select(p => p.Key));
        Assert.Empty(update.AreaOps);
        Assert.Equal("{\"properties\":{\"RelatedArea\":[{\"ref\":\"step 2\"}],\"Teaser\":\"step 2\"},\"areaOps\":[{\"op\":\"add\",\"property\":\"MainContentArea\",\"ref\":\"step 2\",\"at\":0,\"displayOption\":\"narrow\",\"ifMissing\":false}]}",
            steps[2]!["unchecked"]!.ToJsonString());
        Assert.NotNull(steps[2]!["uncheckedNote"]);
        Assert.Null(steps[1]!["unchecked"]);
        // The move into the folder: checked to page 5, as a move below a ContentFolder.
        var move = (MoveStep)checks[3];
        Assert.Equal(("ContentFolder", new ContentReference(5)), (move.PlannedDestinationType, move.Destination.Existing));
        Assert.Equal(("step 1", "step 1"), (steps[3]!["destination"]!.GetValue<string>(), steps[3]!["parent"]!.GetValue<string>()));

        _cms.Calls.Clear();
        var ran = Ok(await Call(client, "test_build", new { planToken = dry["planToken"]!.GetValue<string>() }));
        Assert.Equal(4, ran["completed"]!.GetValue<int>());
        var runs = _cms.Ran.ToList();
        var folder = (CreateStep)runs[0];
        Assert.Equal(new ContentReference(5), folder.Parent.Existing);
        var made = (CreateStep)runs[1];
        Assert.Equal((new ContentReference(1000), null), (made.Parent.Existing, made.PlannedParentType));
        Assert.Equal("{\"Heading\":\"Teaser\",\"Link\":\"6\"}", made.Properties.ToJsonString());
        var page = (UpdateStep)runs[2];
        Assert.Equal("{\"Heading\":\"Built\",\"RelatedArea\":[{\"ref\":\"1001\"}],\"Teaser\":\"1001\"}", page.Properties.ToJsonString());
        Assert.Equal((new ContentReference(1001), 0, "narrow"), (Assert.Single(page.AreaOps).Block.Existing, page.AreaOps[0].At, page.AreaOps[0].DisplayOption));
        Assert.Equal(new ContentReference(1000), ((MoveStep)runs[3]).Destination.Existing);
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_build step 2 create saved", StringComparison.Ordinal) && m.EndsWith("content 1001_1", StringComparison.Ordinal));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_build step 4 move moved", StringComparison.Ordinal) && m.EndsWith("content 7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_string_that_looks_like_planned_content_stays_a_string()
    {
        await using var site = await Site(m => m.WithWriteTool<EchoTool>());
        await using var client = await ToolTests.Connect(site);
        var marker = PlanValues.Marker("00000000000000000000000000000000", 1);

        var dry = Ok(await Call(client, "test_echo", new { text = marker, dryRun = true }));
        Assert.Null(dry["steps"]![1]!["unchecked"]);
        await Call(client, "test_echo", new { text = marker, planToken = dry["planToken"]!.GetValue<string>() });

        var update = (UpdateStep)_cms.Ran.Last();
        Assert.Equal(marker, update.Properties["Heading"]!.GetValue<string>());
        Assert.Equal("step 1", update.Properties["Other"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("update:6")]
    [InlineData("updatepublished:6")]
    [InlineData("create:5")]
    [InlineData("create:5,create:5")]
    [InlineData("move:7>8")]
    [InlineData("trash:7")]
    [InlineData("updatepublished:6,create:5,move:7>8,trash:8")]
    public async Task A_token_runs_its_plan_once_a_retry_with_it_is_refused_before_anything_is_saved(string ops)
    {
        await using var site = await Site(m => m.WithWriteTool<OpsTool>(), o => o.AllowDelete = true);
        await using var client = await ToolTests.Connect(site);
        var arguments = new Dictionary<string, object?> { ["ops"] = ops };
        var token = await DryRun(client, "test_ops", arguments);
        Ok(await client.CallToolAsync("test_ops", With(arguments, token)));
        var ran = _cms.Ran.Count();

        // A client that gave up on the call and tries again with the same token.
        var retry = Error(await client.CallToolAsync("test_ops", With(arguments, token)));

        Assert.Equal(AgentErrorCodes.Conflict, retry.Code);
        Assert.Equal(ran, _cms.Ran.Count());
        // A new dry run shows the plan as it is now.
        if (!ops.Contains("trash", StringComparison.Ordinal))
        {
            await DryRun(client, "test_ops", arguments);
        }
    }

    [Fact]
    public async Task A_step_on_content_below_content_an_earlier_step_trashes_is_refused()
    {
        await using var site = await Site(m => m.WithWriteTool<OpsTool>(), o => o.AllowDelete = true);
        await using var client = await ToolTests.Connect(site);
        _cms.Parents[9] = 7;
        _cms.Versions[9] = 90;

        var dry = Ok(await Call(client, "test_ops", new { ops = "trash:7,create:9,update:9,move:6>9,trash:9,create:6", dryRun = true }));

        Assert.False(dry["valid"]!.GetValue<bool>());
        var steps = dry["steps"]!.AsArray();
        Assert.Null(steps[0]!["error"]);
        foreach (var step in steps.Skip(1).Take(4))
        {
            Assert.Equal(AgentErrorCodes.Refused, step!["error"]!["code"]!.GetValue<string>());
            Assert.StartsWith("Step 1 trashes content 7, and content 9, which this step uses, is below it", step["error"]!["message"]!.GetValue<string>());
        }
        Assert.Null(steps[5]!["error"]);
    }

    [Fact]
    public async Task Arguments_are_read_strictly()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        foreach (var arguments in new object[]
        {
            new { to = "x", dryRun = true },
            new { ids = "5", to = (string?)null, dryRun = true },
            new { ids = "5", to = "x", colour = "red", dryRun = true },
            new { ids = 5, to = "x", dryRun = true },
            new { ids = "5", to = "x", dryRun = "yes" },
            new { ids = "5", to = "x", planToken = 5 },
        })
        {
            var error = Error(await Call(client, "test_rename", arguments));
            Assert.Equal(AgentErrorCodes.Usage, error.Code);
            Assert.StartsWith("The arguments don't fit the tool's schema", error.Message);
        }
        Assert.Empty(_cms.Calls);
        Assert.Equal(0, RenameTool.Made(site));

        // The tool's own refusal of an argument.
        var own = Error(await Call(client, "test_rename", new { ids = "five", to = "x", dryRun = true }));
        Assert.Equal((AgentErrorCodes.Usage, "'five' isn't a content id."), (own.Code, own.Message));
    }

    [Fact]
    public async Task The_tool_is_made_for_every_call_and_disposed_after_planning()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var token = await DryRun(client, "test_rename", RenameBoth);
        Ok(await client.CallToolAsync("test_rename", With(RenameBoth, token)));
        await Call(client, "test_rename", new { ids = "five", to = "x", dryRun = true });

        Assert.Equal(3, RenameTool.Made(site));
        Assert.Equal(3, RenameTool.Disposed(site));
    }

    [Fact]
    public async Task Writing_needs_the_write_scope()
    {
        await using var site = await Site();
        await using var reading = await ToolTests.Connect(site, scope: Scopes.Read);

        var refused = Error(await Call(reading, "test_rename", new { ids = "5", to = "x", dryRun = true }));

        Assert.Equal((AgentErrorCodes.Refused, McpErrorReasons.MissingScope), (refused.Code, refused.Reason));
        Assert.Empty(_cms.Calls);
    }

    [Fact]
    public async Task A_tool_that_may_trash_is_offered_only_where_the_site_allows_deleting()
    {
        await using (var site = await Site(m => m.WithWriteTool<TrashTool>()))
        {
            await using var client = await ToolTests.Connect(site);
            Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name == "test_trash");
            // Not there to call, as delete_content isn't.
            await Assert.ThrowsAnyAsync<McpException>(() => Call(client, "test_trash", new { id = 7, dryRun = true }));
            Assert.Empty(_cms.Calls);
        }

        await using var deleting = await Site(m => m.WithWriteTool<TrashTool>(), o => o.AllowDelete = true);
        await using var deletingClient = await ToolTests.Connect(deleting);
        var tool = Assert.Single(await deletingClient.ListToolsAsync(), t => t.Name == "test_trash");
        Assert.True(tool.ProtocolTool.Annotations!.DestructiveHint);
        var dry = Ok(await Call(deletingClient, "test_trash", new { id = 7, dryRun = true }));
        Assert.Equal(2, dry["steps"]![0]!["descendants"]!.GetValue<int>());
        var ran = Ok(await Call(deletingClient, "test_trash", new { id = 7, planToken = dry["planToken"]!.GetValue<string>() }));
        Assert.Equal("trashed", ran["steps"]![0]!["status"]!.GetValue<string>());
        Assert.Contains(deleting.Audit.Audit, m => m.StartsWith("MCP tool test_trash step 1 trash trashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_larger_than_the_site_allows_is_refused_at_the_dry_run()
    {
        await using var site = await Site(configure: o => o.MaxPlanSteps = 2);
        await using var client = await ToolTests.Connect(site);

        var refused = Error(await Call(client, "test_rename", new { ids = "5,6,7", to = "x", dryRun = true }));

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Equal("This would take 3 steps; the site lets a tool take at most 2 in one run. Nothing was changed.", refused.Message);
        Assert.Contains("Narrow the arguments", refused.Hint);
        Assert.Empty(_cms.Calls);
        Ok(await Call(client, "test_rename", new { ids = "5,6", to = "x", dryRun = true }));
        Assert.Contains("MaxPlanSteps", new OptiCliMcpOptions { MaxPlanSteps = 0 }.Problem());
        Assert.Contains("MaxPlanSteps", new OptiCliMcpOptions { MaxPlanSteps = 1001 }.Problem());
        Assert.Equal(100, new OptiCliMcpOptions().MaxPlanSteps);
    }

    [Fact]
    public async Task A_step_of_a_kind_the_tool_did_not_declare_is_the_sites_fault()
    {
        await using var site = await Site(m => m.WithWriteTool<SneakyTool>());
        await using var client = await ToolTests.Connect(site);

        var error = Error(await Call(client, "test_sneaky", new { dryRun = true }));

        Assert.Equal(AgentErrorCodes.Internal, error.Code);
        Assert.Contains(site.Audit.Entries, e => e.Category == ToolErrors.LogCategory && e.Message == "The site's MCP tool test_sneaky failed.");
        Assert.Empty(_cms.Calls);
    }

    [Fact]
    public async Task An_empty_plan_changes_nothing()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var token = await DryRun(client, "test_rename", new Dictionary<string, object?> { ["ids"] = "", ["to"] = "x" });
        var ran = Ok(await client.CallToolAsync("test_rename", new Dictionary<string, object?> { ["ids"] = "", ["to"] = "x", ["planToken"] = token }));

        Assert.Equal((0, 0), (ran["completed"]!.GetValue<int>(), ran["total"]!.GetValue<int>()));
        Assert.Empty(_cms.Calls);
    }

    [Fact]
    public async Task A_write_tool_never_reaches_another_mcp_server()
    {
        await using var site = await TestSite.StartAsync(
            services: s =>
            {
                s.AddSingleton(_cms);
                s.AddSingleton<IContentPlanSteps>(_cms);
                s.AddSingleton<TestCounter>();
            },
            module: m => m.WithWriteTool<RenameTool>(),
            after: services => services.AddMcpServer().WithHttpTransport(t => t.Stateless = true).WithTools<SiteServerTools>(),
            endpoints: app => app.MapMcp("/site-mcp"));
        await using var theirs = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/site-mcp") }, site.Client(), ownsHttpClient: true));

        Assert.Equal(["site_editor"], (await theirs.ListToolsAsync()).Select(t => t.Name));
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("test_rename", new Dictionary<string, object?> { ["ids"] = "5", ["to"] = "x", ["dryRun"] = true }).AsTask());
        Assert.Empty(_cms.Calls);
    }

    [Fact]
    public void Write_tools_are_checked_where_they_are_registered()
    {
        static string Refused(Action<IOptiCliMcpBuilder> register)
        {
            var module = new ServiceCollection().AddOptiCliMcp();
            return Assert.Throws<InvalidOperationException>(() => register(module)).Message;
        }

        Assert.Contains($"WithWriteTool<{typeof(NoAttributeTool).FullName}>(): the class has no [OptiCliWriteTool]", Refused(m => m.WithWriteTool<NoAttributeTool>()));
        Assert.Contains("has no [Description]", Refused(m => m.WithWriteTool<NoDescriptionTool>()));
        Assert.Contains("declares no kinds", Refused(m => m.WithWriteTool<NoKindsTool>()));
        Assert.Contains("isn't a valid MCP tool name", Refused(m => m.WithWriteTool<BadNameTool>()));
        Assert.Contains("'update_content' is the name of one of the opticli module's own tools", Refused(m => m.WithWriteTool<BuiltInNameTool>()));
        Assert.Contains("'custom_whoami' is already the name of the site's tool", Refused(m => m.WithTools<CustomReadTools>().WithWriteTool<ReadToolNameTool>()));
        Assert.Contains("'test_rename' is already the name of the site's tool", Refused(m => m.WithWriteTool<RenameTool>().WithWriteTool<SameNameTool>()));
        Assert.Contains("is registered twice", Refused(m => m.WithWriteTool<RenameTool>().WithWriteTool<RenameTool>()));
        Assert.Contains("doesn't implement IOptiCliWriteTool<TArgs>", Refused(m => m.WithWriteTool<NotAWriteTool>()));
        Assert.Contains("more than one arguments type", Refused(m => m.WithWriteTool<TwoArgsTool>()));
        Assert.Contains("has a property 'dryRun'", Refused(m => m.WithWriteTool<DryRunArgTool>()));
        Assert.Contains("isn't an object with properties", Refused(m => m.WithWriteTool<StringArgsTool>()));
        Assert.Contains("method Peek has [McpServerTool]", Refused(m => m.WithWriteTool<MixedTool>()));
        Assert.Contains("not abstract", Refused(m => m.WithWriteTool<AbstractWriteTool>()));

        // A refused class leaves nothing behind.
        var module = new ServiceCollection().AddOptiCliMcp();
        Assert.Throws<InvalidOperationException>(() => module.WithWriteTool<MixedTool>());
        module.WithWriteTool<RenameTool>();
        Assert.Equal("test_rename", Assert.Single(CustomTools.Of(module.Services).WriteTools).Name);
    }

    [Fact]
    public void A_plan_refuses_what_it_could_not_run()
    {
        var page = new TestContent(new ContentReference(5, 10));
        var plan = new ContentPlan().Update(page, p => p.Set("Heading", "x"));
        Assert.Contains("updates content 5 in 'en' twice", Assert.Throws<InvalidOperationException>(() => plan.Update(new TestContent(new ContentReference(5, 11)), p => p.SetName("y"))).Message);
        // Another language branch is another draft.
        plan.Update(new TestContent(new ContentReference(5, 12), "sv"), p => p.SetName("y"));

        plan.Trash(new TestContent(new ContentReference(7)));
        Assert.Contains("Step 3 of the plan trashes content 7", Assert.Throws<InvalidOperationException>(() => plan.Trash(new TestContent(new ContentReference(7)))).Message);
        Assert.Throws<InvalidOperationException>(() => plan.Move(page, new TestContent(new ContentReference(7))));
        Assert.Throws<InvalidOperationException>(() => plan.Create(new TestContent(new ContentReference(7)), "ArticlePage", "New"));

        var other = new ContentPlan().Create(page, "ContentFolder", "Elsewhere");
        Assert.Contains("belongs to another plan", Assert.Throws<ArgumentException>(() => plan.Create(other, "ArticlePage", "New")).Message);
        Assert.Throws<ArgumentException>(() => plan.Update(new TestContent(new ContentReference(6, 1)), p => p.Set("Teaser", other)));
        Assert.Throws<ArgumentException>(() => plan.Update(new TestContent(ContentReference.EmptyReference), p => p.Set("Heading", "x")));
        Assert.Throws<ArgumentException>(() => plan.Update(new TestContent(new ContentReference(8, 1)), p => p.Set(" ", "x")));
        Assert.Throws<ArgumentException>(() => plan.Update(new TestContent(new ContentReference(8, 1)), p => p.Set("Area", new PropertyString("x"))));
        Assert.Throws<InvalidOperationException>(() => plan.Create(page, "ArticlePage", "New", p => p.SetName("Other")));
        Assert.Throws<InvalidOperationException>(() => plan.Create(page, "ArticlePage", "New", p => p.AddToArea("MainContentArea", new ContentReference(6))));
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void Values_take_update_contents_forms()
    {
        var plan = new ContentPlan();
        var page = new TestContent(new ContentReference(5, 10));
        var folder = plan.Create(page, "ContentFolder", "Folder");
        plan.Update(new TestContent(new ContentReference(6, 20)), p => p
            .Set("Heading", "Text")
            .Set("Count", 3)
            .Set("Visible", true)
            .Set("Cleared", null)
            .Set("Link", new ContentReference(8))
            .Set("Page", page)
            .Set("Planned", folder)
            .Set("Area", new object[] { new { @ref = page, displayOption = "narrow" }, new Dictionary<string, object?> { ["ref"] = folder, ["Note"] = null } })
            .Set("Raw", JsonDocument.Parse("{\"href\":\"https://example.com\"}").RootElement)
            .Set("Heading", "Last"));
        var update = (UpdateStep)plan.Steps[1];
        var json = PlanValues.Replace(update.Properties, plan, step => $"step {step}")!.ToJsonString();
        Assert.Equal("{\"Heading\":\"Last\",\"Count\":3,\"Visible\":true,\"Cleared\":null,\"Link\":\"8\",\"Page\":\"5\",\"Planned\":\"step 1\",\"Area\":[{\"ref\":\"5\",\"displayOption\":\"narrow\"},{\"ref\":\"step 1\",\"Note\":null}],\"Raw\":{\"href\":\"https://example.com\"}}", json);
        Assert.Equal([1, 1], PlanValues.Steps(update.Properties, plan));
        Assert.Equal("step 1", folder.ToString());
    }
}

public sealed record RenameArgs(
    [property: Description("Content ids, comma-separated.")] string Ids,
    [property: Description("The new name.")] string To,
    [property: Description("Unused.")] int Count = 1);

[OptiCliWriteTool("test_rename", Title = "Test: rename", Kinds = ContentPlanKinds.Update)]
[Description("Renames content.")]
internal sealed class RenameTool : IOptiCliWriteTool<RenameArgs>, IDisposable
{
    private readonly FakePlanSteps _cms;
    private readonly TestCounter _counter;

    /// <summary>Takes the editor as a site's tool does: there for the call.</summary>
    public RenameTool(FakePlanSteps cms, IOptiCliEditor editor, TestCounter counter)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _cms = cms;
        _counter = counter;
        counter.Made++;
    }

    public static int Made(TestSite site) => site.Services.GetRequiredService<TestCounter>().Made;

    public static int Disposed(TestSite site) => site.Services.GetRequiredService<TestCounter>().Disposed;

    public Task<ContentPlan> PlanAsync(RenameArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        foreach (var id in args.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(id, out var number))
            {
                throw new OptiCliToolException(AgentErrorCodes.Usage, $"'{id}' isn't a content id.");
            }
            plan.Update(_cms.Latest(number), p => p.SetName(args.To).Set("Heading", args.To));
        }
        return Task.FromResult(plan);
    }

    public void Dispose() => _counter.Disposed++;
}

/// <summary>Counts a test tool's instances, per site.</summary>
internal sealed class TestCounter
{
    public int Made { get; set; }

    public int Disposed { get; set; }
}

public sealed record NoArgs;

[OptiCliWriteTool("test_build", Kinds = ContentPlanKinds.Create | ContentPlanKinds.Update | ContentPlanKinds.Move)]
[Description("Makes a folder with a block, puts the block on a page and moves another page into the folder.")]
internal sealed class BuildTool(FakePlanSteps cms) : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        var start = cms.Latest(5);
        var folder = plan.Create(start, "ContentFolder", "Teasers");
        var block = plan.Create(folder, "TeaserBlock", "Teaser", p => p.Set("Heading", "Teaser").Set("Link", cms.Latest(6)));
        plan.Update(start, p => p
            .Set("Heading", "Built")
            .Set("RelatedArea", new[] { new { @ref = block } })
            .Set("Teaser", block)
            .AddToArea("MainContentArea", block, at: 0, displayOption: "narrow"));
        plan.Move(cms.Latest(7), folder);
        return Task.FromResult(plan);
    }
}

public sealed record EchoArgs(string Text);

[OptiCliWriteTool("test_echo", Kinds = ContentPlanKinds.Create | ContentPlanKinds.Update)]
[Description("Writes the text it gets.")]
internal sealed class EchoTool(FakePlanSteps cms) : IOptiCliWriteTool<EchoArgs>
{
    public Task<ContentPlan> PlanAsync(EchoArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        plan.Create(cms.Latest(5), "ContentFolder", "Folder");
        plan.Update(cms.Latest(6), p => p.Set("Heading", args.Text).Set("Other", "step 1"));
        return Task.FromResult(plan);
    }
}

public sealed record TrashArgs(int Id);

[OptiCliWriteTool("test_trash", Kinds = ContentPlanKinds.Trash)]
[Description("Trashes content.")]
internal sealed class TrashTool(FakePlanSteps cms) : IOptiCliWriteTool<TrashArgs>
{
    public Task<ContentPlan> PlanAsync(TrashArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan().Trash(cms.Latest(args.Id)));
}

[OptiCliWriteTool("test_sneaky", Kinds = ContentPlanKinds.Update)]
[Description("Declares updates, plans a trash.")]
internal sealed class SneakyTool(FakePlanSteps cms) : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan().Trash(cms.Latest(7)));
}

[Description("No attribute.")]
internal sealed class NoAttributeTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_nodescription", Kinds = ContentPlanKinds.Update)]
internal sealed class NoDescriptionTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_nokinds"), Description("No kinds.")]
internal sealed class NoKindsTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test bad", Kinds = ContentPlanKinds.Update), Description("Bad name.")]
internal sealed class BadNameTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("update_content", Kinds = ContentPlanKinds.Update), Description("A built-in name.")]
internal sealed class BuiltInNameTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("custom_whoami", Kinds = ContentPlanKinds.Update), Description("A read tool's name.")]
internal sealed class ReadToolNameTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_rename", Kinds = ContentPlanKinds.Update), Description("Another tool's name.")]
internal sealed class SameNameTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_not", Kinds = ContentPlanKinds.Update), Description("Not a write tool.")]
internal sealed class NotAWriteTool;

[OptiCliWriteTool("test_two", Kinds = ContentPlanKinds.Update), Description("Two kinds of arguments.")]
internal sealed class TwoArgsTool : IOptiCliWriteTool<NoArgs>, IOptiCliWriteTool<EchoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());

    public Task<ContentPlan> PlanAsync(EchoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

public sealed record DryRunArgs(bool DryRun);

[OptiCliWriteTool("test_dryrun", Kinds = ContentPlanKinds.Update), Description("An argument of the module's name.")]
internal sealed class DryRunArgTool : IOptiCliWriteTool<DryRunArgs>
{
    public Task<ContentPlan> PlanAsync(DryRunArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_string", Kinds = ContentPlanKinds.Update), Description("A string as its arguments.")]
internal sealed class StringArgsTool : IOptiCliWriteTool<string>
{
    public Task<ContentPlan> PlanAsync(string args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliWriteTool("test_mixed", Kinds = ContentPlanKinds.Update), Description("A read tool inside.")]
internal sealed class MixedTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());

    [McpServerTool(Name = "test_peek", ReadOnly = true)]
    public static string Peek() => "";
}

[OptiCliWriteTool("test_abstract", Kinds = ContentPlanKinds.Update), Description("Abstract.")]
internal abstract class AbstractWriteTool : IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

public sealed record OpsArgs([property: Description("Steps: update:6, updatepublished:6, create:5, move:7>8, trash:7, comma-separated.")] string Ops);

[OptiCliWriteTool("test_ops", Kinds = ContentPlanKinds.Update | ContentPlanKinds.Create | ContentPlanKinds.Move | ContentPlanKinds.Trash)]
[Description("Plans the steps it is given.")]
internal sealed class OpsTool(FakePlanSteps cms) : IOptiCliWriteTool<OpsArgs>
{
    public Task<ContentPlan> PlanAsync(OpsArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        foreach (var op in args.Ops.Split(','))
        {
            var (kind, target) = (op.Split(':')[0], op.Split(':')[1]);
            var ids = target.Split('>').Select(int.Parse).ToArray();
            _ = kind switch
            {
                "update" => plan.Update(cms.Latest(ids[0]), p => p.Set("Heading", "x")),
                // Loaded without a version, as IContentLoader.Get gives the published version.
                "updatepublished" => plan.Update(new TestContent(new ContentReference(ids[0])), p => p.Set("Heading", "x")),
                "create" => plan.Create(new TestContent(new ContentReference(ids[0])), "ArticlePage", "New").Plan,
                "move" => plan.Move(new TestContent(new ContentReference(ids[0])), new TestContent(new ContentReference(ids[1]))),
                _ => plan.Trash(new TestContent(new ContentReference(ids[0]))),
            };
        }
        return Task.FromResult(plan);
    }
}
