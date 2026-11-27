using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;
using OptiCli.Mcp.Tools;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// A site's action tools (<see cref="IOptiCliActionTool{TArgs}"/>): registered and checked, offered with the module's
/// schema and annotations, gated, previewed, run once per token (the run record), stopped at a failure, and audited.
/// The tools work on <see cref="FakeEvents"/>, a system outside the CMS's content.
/// </summary>
public sealed class ActionToolTests
{
    private readonly FakeEvents _events = new();

    private Task<TestSite> Site(Action<IOptiCliMcpBuilder>? module = null, Action<OptiCliMcpOptions>? configure = null) =>
        TestSite.StartAsync(configure,
            services: s => s.AddSingleton(_events),
            module: module ?? (m => m.WithActionTool<SaveEventTool>().WithActionTool<PublishEventsTool>()));

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static async Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        await client.CallToolAsync(tool, arguments);

    private static JsonObject Ok(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    private static AgentError Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return JsonSerializer.Deserialize<AgentError>(Text(result), AgentJson.Options)!;
    }

    private static JsonObject ErrorJson(CallToolResult result)
    {
        Assert.True(result.IsError);
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> arguments, string token) => new(arguments) { ["planToken"] = token };

    private static Dictionary<string, object?> Dry(Dictionary<string, object?> arguments) => new(arguments) { ["dryRun"] = true };

    private static async Task<string> DryRun(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = Ok(await Call(client, tool, Dry(arguments)));
        Assert.True(result["valid"]!.GetValue<bool>());
        return result["planToken"]!.GetValue<string>();
    }

    private static readonly Dictionary<string, object?> RenameOne = new() { ["id"] = 1, ["title"] = "Summer concert" };
    private static readonly Dictionary<string, object?> NewEvent = new() { ["title"] = "Autumn fair" };
    private static readonly Dictionary<string, object?> PublishTwo = new() { ["ids"] = "1,2" };

    [Fact]
    public async Task An_action_tool_is_offered_with_its_arguments_dryRun_and_planToken_flat_and_says_it_reaches_outside_the_cms()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        var save = tools["test_save_event"];
        Assert.Equal("Test: save event", save.Title);
        Assert.Equal("Saves an event in the calendar as a draft, or creates one. " + ActionToolType.Workflow(ActionEffects.Change), save.Description);
        Assert.Equal((false, false, false, true), (save.Annotations!.ReadOnlyHint, save.Annotations.DestructiveHint, save.Annotations.IdempotentHint, save.Annotations.OpenWorldHint));
        var properties = save.InputSchema.GetProperty("properties");
        Assert.Equal(["title", "id", "dryRun", "planToken"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["title"], save.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.False(save.InputSchema.GetProperty("additionalProperties").GetBoolean());

        // Changing what visitors see makes it destructive, as publish_content is.
        Assert.True(tools["test_publish_events"].Annotations!.DestructiveHint);
        Assert.EndsWith(ActionToolType.Workflow(ActionEffects.Live), tools["test_publish_events"].Description);
    }

    [Theory]
    [InlineData(ActionEffects.Change, "It changes nothing visitors see.", "delete")]
    [InlineData(ActionEffects.Live, "It can change what visitors see.", "delete")]
    [InlineData(ActionEffects.Change | ActionEffects.Live, "It can change what visitors see.", "nothing visitors")]
    [InlineData(ActionEffects.Delete, "It can delete things.", "visitors")]
    [InlineData(ActionEffects.Change | ActionEffects.Live | ActionEffects.Delete, "It can change what visitors see. It can delete things.", "nothing visitors")]
    public void An_action_tools_description_says_it_changes_things_outside_the_cms_and_what_its_effects_do(ActionEffects effects, string says, string notSaid)
    {
        var workflow = ActionToolType.Workflow(effects);

        Assert.StartsWith("It changes things outside the CMS's content, in the site's own systems, which the CMS's edit UI doesn't show. " + says, workflow, StringComparison.Ordinal);
        Assert.EndsWith(ActionToolType.DryRunWorkflow, workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(notSaid, workflow[..^ActionToolType.DryRunWorkflow.Length], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dry_run_shows_each_target_its_changes_version_notes_and_links_and_changes_nothing()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var dry = Ok(await Call(client, "test_save_event", Dry(RenameOne)));

        Assert.Equal("test_save_event", dry["tool"]!.GetValue<string>());
        Assert.True(dry["dryRun"]!.GetValue<bool>());
        Assert.True(dry["valid"]!.GetValue<bool>());
        Assert.Equal(site.Time.GetUtcNow().AddMinutes(30), dry["planTokenExpires"]!.GetValue<DateTimeOffset>());
        var target = Assert.Single(dry["targets"]!.AsArray())!.AsObject();
        Assert.Equal(
            """{"target":1,"kind":"event","id":"1","label":"Summer concert","effect":"change","version":"3","changes":[{"field":"title","before":"Spring concert","after":"Summer concert"}],"editUrl":"https://calendar.example.com/edit/1"}""",
            target.ToJsonString());
        Assert.Equal(["Visitors see it once it is published."], dry["notes"]!.AsArray().Select(n => n!.GetValue<string>()));

        // A new one has no id, and its fields change from nothing.
        var create = Ok(await Call(client, "test_save_event", Dry(NewEvent)))["targets"]![0]!;
        Assert.True(create["new"]!.GetValue<bool>());
        Assert.Null(create["id"]);
        Assert.Equal("""[{"field":"title","before":null,"after":"Autumn fair"}]""", create["changes"]!.ToJsonString());

        Assert.Equal(0, _events.Runs);
        Assert.Equal("Spring concert", _events.Items[1].Title);
    }

    [Fact]
    public async Task The_real_run_previews_again_runs_the_tool_and_audits_each_target_as_it_is_done()
    {
        await using var site = await Site(configure: o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_publish_events", PublishTwo);

        var ran = Ok(await Call(client, "test_publish_events", With(PublishTwo, token)));

        Assert.False(ran["dryRun"]!.GetValue<bool>());
        Assert.Equal((2, 2), (ran["completed"]!.GetValue<int>(), ran["total"]!.GetValue<int>()));
        Assert.Equal(
            """[{"target":1,"kind":"event","id":"1","label":"Spring concert","status":"published","url":"https://calendar.example.com/events/1","editUrl":"https://calendar.example.com/edit/1"},{"target":2,"kind":"event","id":"2","label":"Book fair","status":"published","url":"https://calendar.example.com/events/2","editUrl":"https://calendar.example.com/edit/2"}]""",
            ran["targets"]!.ToJsonString());
        Assert.Equal(["published", "published"], _events.Items.Values.Where(e => e.Id <= 2).Select(e => e.Status));
        Assert.Contains("MCP tool test_publish_events target 1 live published: user editor, client ", site.Audit.All);
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_publish_events target 2 live published: user editor", StringComparison.Ordinal) && m.EndsWith(", event:2", StringComparison.Ordinal));
        // The label is the site's text: never in the audit.
        Assert.DoesNotContain(site.Audit.Audit, m => m.Contains("Book fair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_runs_once_the_same_token_again_answers_with_the_first_runs_result()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        // A create: nothing in the preview changes after it ran, so only the record keeps it from running twice.
        var token = await DryRun(client, "test_save_event", NewEvent);

        var first = Ok(await Call(client, "test_save_event", With(NewEvent, token)));
        var again = Ok(await Call(client, "test_save_event", With(NewEvent, token)));

        Assert.Equal(1, _events.Runs);
        Assert.Single(_events.Items.Values, e => e.Title == "Autumn fair");
        Assert.Equal("4", first["targets"]![0]!["id"]!.GetValue<string>());
        Assert.True(again["repeated"]!.GetValue<bool>());
        Assert.Equal(site.Time.GetUtcNow(), again["ranAt"]!.GetValue<DateTimeOffset>());
        Assert.Contains("Nothing was run again.", again["repeatNote"]!.GetValue<string>());
        Assert.Equal(first["targets"]!.ToJsonString(), again["targets"]!.ToJsonString());
        Assert.Single(site.Audit.Audit, m => m.StartsWith("MCP tool test_save_event target 1 ", StringComparison.Ordinal));

        // Still after the token expired: the record outlives it, so a late retry gets the result too.
        site.Time.Advance(TimeSpan.FromMinutes(45));
        Assert.True(Ok(await Call(client, "test_save_event", With(NewEvent, token)))["repeated"]!.GetValue<bool>());
        Assert.Equal(1, _events.Runs);
    }

    [Fact]
    public async Task A_token_written_another_way_is_the_same_token_and_runs_once()
    {
        await using var site = await Site(m => m.WithActionTool<AddNoteTool>());
        await using var client = await ToolTests.Connect(site);
        // A create the preview can't see once it ran: only the record keeps it from running twice.
        var note = new Dictionary<string, object?> { ["text"] = "Doors open at 18:00." };
        var token = await DryRun(client, "test_add_note", note);
        Ok(await Call(client, "test_add_note", With(note, token)));

        // The same bytes: base64 decoding skips whitespace, so this unprotects to the same token.
        var again = Ok(await Call(client, "test_add_note", With(note, token + "    ")));

        Assert.True(again["repeated"]!.GetValue<bool>());
        Assert.Equal(1, _events.Runs);
        Assert.Equal(["Doors open at 18:00."], _events.Notes);
    }

    [Fact]
    public async Task A_run_cancelled_midway_records_its_end_so_the_same_token_says_what_was_done()
    {
        await using var site = await Site(m => m.WithActionTool<CutOffTool>());
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_cut_off", new());

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await client.CallToolAsync("test_cut_off", With(new(), token), cancellationToken: timeout.Token);
            }
            catch (Exception e) when (e is McpException or OperationCanceledException)
            {
                // The cancelled call has no result of its own.
            }
        }
        var again = ErrorJson(await Call(client, "test_cut_off", With(new(), token)));

        Assert.True(again["repeated"]!.GetValue<bool>());
        Assert.Equal(AgentErrorCodes.Conflict, again["code"]!.GetValue<string>());
        Assert.Equal("The run stopped at target 2 of 3 (setting) because the call was cancelled: whether that target was changed isn't known. One target was done and stays done.", again["message"]!.GetValue<string>());
        Assert.Equal(["done", "unknown", "notRun"], again["targets"]!.AsArray().Select(t => t!["status"]!.GetValue<string>()));
        Assert.Equal(1, _events.Runs);
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_cut_off target 2 change unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(site.Audit.Audit, m => m.StartsWith("MCP tool test_cut_off target 3 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_whose_run_started_and_never_ended_says_so_and_runs_nothing()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_save_event", RenameOne);
        var arguments = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("""{"title":"Summer concert","id":1}""")));
        site.Runs.TryStart(new ActionRunRecord(ActionRunner.RunKey(token), "editor", arguments, site.Time.GetUtcNow(), site.Time.GetUtcNow().AddHours(1)), site.Time.GetUtcNow());

        var error = Error(await Call(client, "test_save_event", With(RenameOne, token)));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("hasn't said how it ended: it may still be running, or the site stopped during it. Nothing was run again.", error.Message);
        Assert.Contains("check what was changed", error.Hint);
        Assert.Equal(0, _events.Runs);
    }

    [Fact]
    public async Task A_ran_token_with_other_arguments_or_of_another_editor_runs_nothing()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_save_event", RenameOne);
        Ok(await Call(client, "test_save_event", With(RenameOne, token)));

        var other = Error(await Call(client, "test_save_event", With(new() { ["id"] = 1, ["title"] = "Other" }, token)));
        Assert.Equal(AgentErrorCodes.Conflict, other.Code);
        Assert.Equal("This planToken ran already, with other arguments. Nothing was run again.", other.Message);

        site.Roles.Set("other", "WebEditors");
        var tokens = await site.ConnectAsync("other");
        await using var otherEditor = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/episerver/opticli/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + tokens.Access },
        }, site.Client(), ownsHttpClient: true));
        // Their own result isn't theirs to see: the token isn't one their dry run gave.
        Assert.Equal(AgentErrorCodes.Usage, Error(await Call(otherEditor, "test_save_event", With(RenameOne, token))).Code);
        Assert.Equal(1, _events.Runs);
    }

    [Fact]
    public async Task A_change_made_since_the_dry_run_is_a_conflict_with_or_without_a_version()
    {
        await using var site = await Site(m => m.WithActionTool<SaveEventTool>().WithActionTool<BumpCounterTool>());
        await using var client = await ToolTests.Connect(site);

        // With a version: someone saved the event meanwhile, without changing its title.
        var token = await DryRun(client, "test_save_event", RenameOne);
        _events.Items[1] = _events.Items[1] with { Version = 4 };
        var versioned = Error(await Call(client, "test_save_event", With(RenameOne, token)));
        Assert.Equal(AgentErrorCodes.Conflict, versioned.Code);
        Assert.Contains("aren't the ones the dry run showed", versioned.Message);

        // Without one: the counter's before value is what changed.
        var bump = await DryRun(client, "test_bump", new());
        _events.Counter = 7;
        Assert.Equal(AgentErrorCodes.Conflict, Error(await Call(client, "test_bump", With(new(), bump))).Code);
        var fresh = await DryRun(client, "test_bump", new());
        Assert.Equal("ok", Ok(await Call(client, "test_bump", With(new(), fresh)))["targets"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(8, _events.Counter);
        Assert.Equal(1, _events.Runs);
    }

    [Fact]
    public async Task A_token_works_for_30_minutes_then_the_run_asks_for_a_new_dry_run()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_save_event", RenameOne);

        site.Time.Advance(TimeSpan.FromMinutes(30));
        var expired = Error(await Call(client, "test_save_event", With(RenameOne, token)));

        Assert.Equal(AgentErrorCodes.Conflict, expired.Code);
        Assert.Equal("The planToken expired: a dry run's token works for 30 minutes. Nothing was changed.", expired.Message);
        Assert.Equal(0, _events.Runs);
    }

    [Fact]
    public async Task A_token_that_isnt_one_or_none_at_all_is_refused()
    {
        await using var site = await Site(configure: o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);

        Assert.Equal(AgentErrorCodes.Usage, Error(await Call(client, "test_save_event", With(RenameOne, "abc"))).Code);
        var publishToken = await DryRun(client, "test_publish_events", PublishTwo);
        Assert.Equal(AgentErrorCodes.Usage, Error(await Call(client, "test_save_event", With(RenameOne, publishToken))).Code);
        var none = Error(await Call(client, "test_save_event", RenameOne));
        Assert.Equal(AgentErrorCodes.Usage, none.Code);
        Assert.Contains("dryRun: true first", none.Hint);
        Assert.Equal(0, _events.Runs);
    }

    [Fact]
    public async Task Problems_make_the_dry_run_invalid_without_a_token_and_stop_a_run()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var taken = Ok(await Call(client, "test_save_event", Dry(new() { ["id"] = 1, ["title"] = "Book fair" })));
        Assert.False(taken["valid"]!.GetValue<bool>());
        Assert.Null(taken["planToken"]);
        Assert.Equal("""[{"message":"Another event is called Book fair.","field":"title"}]""", taken["targets"]![0]!["problems"]!.ToJsonString());

        // A problem that came up after the dry run: the run is refused, nothing run.
        var token = await DryRun(client, "test_save_event", RenameOne);
        _events.Items[2] = _events.Items[2] with { Title = "Summer concert" };
        var refused = ErrorJson(await Call(client, "test_save_event", With(RenameOne, token)));
        Assert.Equal(AgentErrorCodes.Validation, refused["code"]!.GetValue<string>());
        Assert.Equal("Target 1 of 1 (event) can't run now: Another event is called Summer concert. Nothing was changed.", refused["message"]!.GetValue<string>());
        Assert.Equal(0, _events.Runs);
    }

    [Fact]
    public async Task A_run_that_fails_at_a_target_stops_there_and_says_which_were_done()
    {
        await using var site = await Site(configure: o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);
        var three = new Dictionary<string, object?> { ["ids"] = "1,2,3" };
        var token = await DryRun(client, "test_publish_events", three);
        _events.FailAt = 2;

        var failed = ErrorJson(await Call(client, "test_publish_events", With(three, token)));

        Assert.Equal(AgentErrorCodes.Refused, failed["code"]!.GetValue<string>());
        Assert.Equal("Target 2 of 3 (event) failed: The calendar refused it: the event has no date. One target was done and stays done.", failed["message"]!.GetValue<string>());
        Assert.Contains("which targets were done", failed["hint"]!.GetValue<string>());
        Assert.Contains("Give it a date.", failed["hint"]!.GetValue<string>());
        Assert.Equal((1, 3), (failed["completed"]!.GetValue<int>(), failed["total"]!.GetValue<int>()));
        Assert.Equal(["published", "failed", "notRun"], failed["targets"]!.AsArray().Select(t => t!["status"]!.GetValue<string>()));
        Assert.Equal("refused", failed["targets"]![1]!["error"]!["code"]!.GetValue<string>());
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_publish_events target 2 live failed", StringComparison.Ordinal));
        Assert.DoesNotContain(site.Audit.Audit, m => m.StartsWith("MCP tool test_publish_events target 3 ", StringComparison.Ordinal));

        // The same token again: the same stopped result, nothing run again.
        _events.FailAt = null;
        var again = ErrorJson(await Call(client, "test_publish_events", With(three, token)));
        Assert.True(again["repeated"]!.GetValue<bool>());
        Assert.Equal(failed["targets"]!.ToJsonString(), again["targets"]!.ToJsonString());
        Assert.Equal("draft", _events.Items[2].Status);
    }

    [Fact]
    public async Task An_unexpected_failure_is_internal_and_logged_with_the_tools_name()
    {
        await using var site = await Site(configure: o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_publish_events", PublishTwo);
        _events.FailAt = 1;
        _events.FailWith = new HttpRequestException("calendar.example.com answered 503");

        var failed = ErrorJson(await Call(client, "test_publish_events", With(PublishTwo, token)));

        Assert.Equal(AgentErrorCodes.Internal, failed["code"]!.GetValue<string>());
        Assert.StartsWith("Target 1 of 2 (event) failed: The site failed while running this tool", failed["message"]!.GetValue<string>());
        Assert.EndsWith("Nothing was changed.", failed["message"]!.GetValue<string>());
        Assert.DoesNotContain("503", Text(await Call(client, "test_publish_events", With(PublishTwo, token))));
        Assert.Contains(site.Audit.Entries, e => e.Message == "The site's MCP tool test_publish_events failed.");
    }

    [Fact]
    public async Task A_run_that_doesnt_report_its_targets_is_the_sites_fault()
    {
        await using var site = await Site(m => m.WithActionTool<SilentTool>());
        await using var client = await ToolTests.Connect(site);
        var token = await DryRun(client, "test_silent", new());

        var result = ErrorJson(await Call(client, "test_silent", With(new(), token)));

        Assert.Equal(AgentErrorCodes.Internal, result["code"]!.GetValue<string>());
        Assert.Contains("without saying what it did with target(s) 2", result["message"]!.GetValue<string>());
        Assert.Equal(["done", "notReported"], result["targets"]!.AsArray().Select(t => t!["status"]!.GetValue<string>()));
        Assert.Contains(site.Audit.Entries, e => e.Message.Contains("ended its run without reporting target(s) 2", StringComparison.Ordinal));
        Assert.Contains(site.Audit.Audit, m => m.StartsWith("MCP tool test_silent target 2 change notReported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_target_of_an_effect_the_tool_didnt_declare_is_the_sites_fault()
    {
        await using var site = await Site(m => m.WithActionTool<SneakyActionTool>(), o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);

        var error = Error(await Call(client, "test_sneaky", Dry(new())));

        Assert.Equal(AgentErrorCodes.Internal, error.Code);
        Assert.Contains(site.Audit.Entries, e => e.Message == "The site's MCP tool test_sneaky failed.");
    }

    [Fact]
    public async Task Every_action_needs_the_write_scope()
    {
        await using var site = await Site();
        await using var reading = await ToolTests.Connect(site, scope: Scopes.Read);

        var refused = Error(await Call(reading, "test_save_event", Dry(RenameOne)));

        Assert.Equal((AgentErrorCodes.Refused, McpErrorReasons.MissingScope), (refused.Code, refused.Reason));
        Assert.Equal(0, _events.Previews);
    }

    [Fact]
    public async Task Changing_what_visitors_see_needs_publishing_allowed_and_the_publish_scope()
    {
        // A tool that only changes what visitors see is refused before it is asked for a preview.
        await using (var site = await Site())
        {
            await using var client = await ToolTests.Connect(site);
            Assert.Contains(await client.ListToolsAsync(), t => t.Name == "test_publish_events");
            var off = Error(await Call(client, "test_publish_events", Dry(PublishTwo)));
            Assert.Equal((AgentErrorCodes.Refused, McpErrorReasons.PublishingOff), (off.Code, off.Reason));
            Assert.Equal(ActionToolType.PublishingOffHint, off.Hint);
            Assert.Equal(0, _events.Previews);
        }

        // One that may: gated by its preview.
        await using (var site = await Site(m => m.WithActionTool<SaveOrPublishTool>()))
        {
            await using var client = await ToolTests.Connect(site);
            Assert.True(Ok(await Call(client, "test_save_or_publish", Dry(new() { ["publish"] = false })))["valid"]!.GetValue<bool>());
            Assert.Equal(McpErrorReasons.PublishingOff, Error(await Call(client, "test_save_or_publish", Dry(new() { ["publish"] = true }))).Reason);
        }

        await using (var site = await Site(configure: o => o.AllowPublish = true))
        {
            await using var client = await ToolTests.Connect(site, scope: $"{Scopes.Read} {Scopes.Write}");
            Assert.Equal(McpErrorReasons.MissingScope, Error(await Call(client, "test_publish_events", Dry(PublishTwo))).Reason);
        }
    }

    [Fact]
    public async Task A_tool_that_may_delete_is_offered_only_where_the_site_allows_deleting()
    {
        await using (var site = await Site(m => m.WithActionTool<DeleteEventTool>()))
        {
            await using var client = await ToolTests.Connect(site);
            Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name == "test_delete_event");
            await Assert.ThrowsAnyAsync<McpException>(() => Call(client, "test_delete_event", Dry(new() { ["id"] = 3 })));
        }

        await using var deleting = await Site(m => m.WithActionTool<DeleteEventTool>(), o => o.AllowDelete = true);
        await using var deletingClient = await ToolTests.Connect(deleting);
        var tool = Assert.Single(await deletingClient.ListToolsAsync(), t => t.Name == "test_delete_event");
        Assert.True(tool.ProtocolTool.Annotations!.DestructiveHint);
        var token = await DryRun(deletingClient, "test_delete_event", new() { ["id"] = 3 });
        Assert.Equal("deleted", Ok(await Call(deletingClient, "test_delete_event", With(new() { ["id"] = 3 }, token)))["targets"]![0]!["status"]!.GetValue<string>());
        Assert.False(_events.Items.ContainsKey(3));
        Assert.Contains(deleting.Audit.Audit, m => m.StartsWith("MCP tool test_delete_event target 1 delete deleted", StringComparison.Ordinal) && m.EndsWith("event:3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task More_targets_than_the_site_allows_are_refused_at_the_dry_run()
    {
        await using var site = await Site(configure: o => { o.AllowPublish = true; o.MaxPlanSteps = 2; });
        await using var client = await ToolTests.Connect(site);

        var error = Error(await Call(client, "test_publish_events", Dry(new() { ["ids"] = "1,2,3" })));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Equal("This would change 3 things; the site lets a tool change at most 2 in one run. Nothing was changed.", error.Message);
    }

    [Fact]
    public async Task An_empty_preview_is_valid_and_runs_nothing()
    {
        await using var site = await Site(configure: o => o.AllowPublish = true);
        await using var client = await ToolTests.Connect(site);

        var token = await DryRun(client, "test_publish_events", new() { ["ids"] = "" });
        var ran = Ok(await Call(client, "test_publish_events", With(new() { ["ids"] = "" }, token)));

        Assert.Equal((0, 0), (ran["completed"]!.GetValue<int>(), ran["total"]!.GetValue<int>()));
        Assert.Equal(0, _events.Runs);
    }

    [Fact]
    public async Task Arguments_are_read_strictly()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        foreach (var arguments in new Dictionary<string, object?>[]
        {
            new() { ["id"] = 1, ["dryRun"] = true },
            new() { ["title"] = "x", ["colour"] = "red", ["dryRun"] = true },
            new() { ["title"] = 5, ["dryRun"] = true },
        })
        {
            var error = Error(await Call(client, "test_save_event", arguments));
            Assert.Equal(AgentErrorCodes.Usage, error.Code);
            Assert.StartsWith("The arguments don't fit the tool's schema", error.Message);
        }
        Assert.Equal(0, _events.Previews);
    }

    [Fact]
    public async Task The_real_run_previews_and_runs_on_one_instance_made_for_the_call_and_disposed_after_it()
    {
        await using var site = await Site();
        await using var client = await ToolTests.Connect(site);

        var token = await DryRun(client, "test_save_event", RenameOne);
        Ok(await Call(client, "test_save_event", With(RenameOne, token)));

        Assert.Equal((2, 2), (_events.Made, _events.Disposed));
        Assert.True(_events.RanOnPreviewingInstance);
    }

    [Fact]
    public async Task The_instructions_tell_the_assistant_about_action_tools_only_where_the_site_has_them()
    {
        await using (var site = await Site())
        {
            await using var client = await ToolTests.Connect(site);
            Assert.Contains(McpInstructions.ActionTools, client.ServerInstructions);
        }
        await using (var site = await Site(m => m.WithTools<CustomReadTools>()))
        {
            await using var client = await ToolTests.Connect(site);
            Assert.DoesNotContain(McpInstructions.ActionTools, client.ServerInstructions);
        }
        // Only a tool that may delete, on a site that doesn't allow deleting: none is offered.
        await using (var site = await Site(m => m.WithActionTool<DeleteEventTool>()))
        {
            await using var client = await ToolTests.Connect(site);
            Assert.DoesNotContain(McpInstructions.ActionTools, client.ServerInstructions);
        }
    }

    [Fact]
    public async Task An_action_tool_never_reaches_another_mcp_server()
    {
        await using var site = await TestSite.StartAsync(
            services: s => s.AddSingleton(_events),
            module: m => m.WithActionTool<SaveEventTool>(),
            after: services => services.AddMcpServer().WithHttpTransport(t => t.Stateless = true).WithTools<SiteServerTools>(),
            endpoints: app => app.MapMcp("/site-mcp"));
        await using var theirs = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/site-mcp") }, site.Client(), ownsHttpClient: true));

        Assert.Equal(["site_editor"], (await theirs.ListToolsAsync()).Select(t => t.Name));
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("test_save_event", Dry(RenameOne)).AsTask());
        Assert.Equal(0, _events.Previews);
    }

    [Fact]
    public void Action_tools_are_checked_where_they_are_registered()
    {
        static string Refused(Action<IOptiCliMcpBuilder> register)
        {
            var module = new ServiceCollection().AddOptiCliMcp();
            return Assert.Throws<InvalidOperationException>(() => register(module)).Message;
        }

        Assert.Contains($"WithActionTool<{typeof(NoAttributeActionTool).FullName}>(): the class has no [OptiCliActionTool]", Refused(m => m.WithActionTool<NoAttributeActionTool>()));
        Assert.Contains("has no [Description]", Refused(m => m.WithActionTool<NoDescriptionActionTool>()));
        Assert.Contains("declares no effects", Refused(m => m.WithActionTool<NoEffectsActionTool>()));
        Assert.Contains("'update_content' is the name of one of the opticli module's own tools", Refused(m => m.WithActionTool<BuiltInNameActionTool>()));
        Assert.Contains("'test_rename' is already the name of the site's tool", Refused(m => m.WithWriteTool<RenameTool>().WithActionTool<RenameNamedActionTool>()));
        Assert.Contains("is registered twice", Refused(m => m.WithActionTool<SaveEventTool>().WithActionTool<SaveEventTool>()));
        Assert.Contains("doesn't implement IOptiCliActionTool<TArgs>", Refused(m => m.WithActionTool<RenameTool>()));
        Assert.Contains("is a write tool too", Refused(m => m.WithActionTool<BothTool>()));
        Assert.Contains("is an action tool too", Refused(m => m.WithWriteTool<BothTool>()));
        Assert.Contains("has a property 'dryRun'", Refused(m => m.WithActionTool<DryRunArgActionTool>()));
        Assert.Contains("[Authorize] restricts", Refused(m => m.WithActionTool<AuthorizedActionTool>()));
        Assert.Contains("method Peek has [McpServerTool]", Refused(m => m.WithActionTool<ToolMethodActionTool>()));

        var module = new ServiceCollection().AddOptiCliMcp();
        module.WithActionTool<SaveEventTool>();
        Assert.Equal("test_save_event", Assert.Single(CustomTools.Of(module.Services).ActionTools).Name);
    }

    [Fact]
    public void A_preview_refuses_what_it_could_not_show()
    {
        var preview = new ActionPreview();
        Assert.Throws<ArgumentException>(() => preview.Add("event", "1", "x", ActionEffects.Change | ActionEffects.Live));
        Assert.Throws<ArgumentException>(() => preview.Add("event", "1", "x", ActionEffects.None));
        Assert.Throws<ArgumentException>(() => preview.Add(" ", "1", "x", ActionEffects.Change));
        var target = preview.Add("event", "1", "x", ActionEffects.Change).Change("title", "a", "b");
        Assert.Throws<ArgumentException>(() => target.Change("title", "b", "c"));
        Assert.Throws<NotSupportedException>(() => target.Change("handle", null, new IntPtr(1)));

        // A run reports the targets of its own preview, each once.
        var run = new ActionRun(preview, "key", (_, _) => { });
        Assert.Throws<ArgumentException>(() => run.Done(new ActionPreview().Add("event", "2", "y", ActionEffects.Change), "saved"));
        run.Done(target, "saved");
        Assert.Throws<InvalidOperationException>(() => run.Done(target, "saved"));

        // Not after the run ended: the result is made by then.
        var later = preview.Add("event", "3", "z", ActionEffects.Change);
        run.End();
        Assert.Throws<InvalidOperationException>(() => run.Done(later, "saved"));
        Assert.Single(run.Outcomes);
    }
}

/// <summary>A calendar outside the CMS, for the test tools.</summary>
public sealed class FakeEvents
{
    public sealed record Event(int Id, string Title, string Status, int Version);

    public Dictionary<int, Event> Items { get; } = new()
    {
        [1] = new(1, "Spring concert", "draft", 3),
        [2] = new(2, "Book fair", "draft", 1),
        [3] = new(3, "Film night", "draft", 5),
    };

    public List<string> Notes { get; } = [];
    public int Counter { get; set; } = 5;
    public int Previews { get; set; }
    public int Runs { get; set; }
    public int Made { get; set; }
    public int Disposed { get; set; }
    public bool RanOnPreviewingInstance { get; set; }
    public int? FailAt { get; set; }
    public Exception? FailWith { get; set; }

    public void Fail(int id)
    {
        if (FailAt == id)
        {
            throw FailWith ?? new OptiCliToolException(AgentErrorCodes.Refused, "The calendar refused it: the event has no date.", "Give it a date.");
        }
    }

    public static string EditUrl(int id) => $"https://calendar.example.com/edit/{id}";

    public static string Url(int id) => $"https://calendar.example.com/events/{id}";
}

public sealed record SaveEventArgs([property: Description("Its title.")] string Title, [property: Description("The event's id; leave out to create one.")] int? Id = null);

[OptiCliActionTool("test_save_event", Title = "Test: save event", Effects = ActionEffects.Change)]
[Description("Saves an event in the calendar as a draft, or creates one.")]
internal sealed class SaveEventTool : IOptiCliActionTool<SaveEventArgs>, IDisposable
{
    private readonly FakeEvents _events;
    private bool _previewed;

    public SaveEventTool(IOptiCliEditor editor, FakeEvents events)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _events = events;
        events.Made++;
    }

    public Task<ActionPreview> PreviewAsync(SaveEventArgs args, CancellationToken cancellationToken)
    {
        _events.Previews++;
        _previewed = true;
        var preview = new ActionPreview().Note("Visitors see it once it is published.");
        var current = args.Id is { } id ? _events.Items[id] : null;
        var target = preview.Add("event", current?.Id.ToString(), args.Title, ActionEffects.Change);
        if (current is not null)
        {
            target.Version(current.Version.ToString()).Links(editUrl: FakeEvents.EditUrl(current.Id));
        }
        if (args.Title != current?.Title)
        {
            target.Change("title", current?.Title, args.Title);
        }
        if (_events.Items.Values.Any(e => e.Title == args.Title && e.Id != args.Id))
        {
            target.Problem($"Another event is called {args.Title}.", "title");
        }
        return Task.FromResult(preview);
    }

    public Task RunAsync(SaveEventArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        _events.Runs++;
        _events.RanOnPreviewingInstance = _previewed;
        var id = args.Id ?? _events.Items.Keys.Max() + 1;
        _events.Items[id] = new FakeEvents.Event(id, args.Title, "draft", (args.Id is null ? 0 : _events.Items[id].Version) + 1);
        run.Done(preview.Targets[0], "saved", id.ToString(), editUrl: FakeEvents.EditUrl(id));
        return Task.CompletedTask;
    }

    public void Dispose() => _events.Disposed++;
}

public sealed record PublishEventsArgs([property: Description("Event ids, comma-separated.")] string Ids);

[OptiCliActionTool("test_publish_events", Effects = ActionEffects.Live)]
[Description("Publishes events in the calendar.")]
internal sealed class PublishEventsTool(FakeEvents events) : IOptiCliActionTool<PublishEventsArgs>
{
    public Task<ActionPreview> PreviewAsync(PublishEventsArgs args, CancellationToken cancellationToken)
    {
        events.Previews++;
        var preview = new ActionPreview();
        foreach (var id in args.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
        {
            var item = events.Items[id];
            preview.Add("event", id.ToString(), item.Title, ActionEffects.Live)
                .Version(item.Version.ToString())
                .Change("status", item.Status, "published")
                .Links(editUrl: FakeEvents.EditUrl(id));
        }
        return Task.FromResult(preview);
    }

    public Task RunAsync(PublishEventsArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        events.Runs++;
        foreach (var target in preview.Targets)
        {
            var id = int.Parse(target.Id!);
            events.Fail(id);
            events.Items[id] = events.Items[id] with { Status = "published", Version = events.Items[id].Version + 1 };
            run.Done(target, "published", url: FakeEvents.Url(id));
        }
        return Task.CompletedTask;
    }
}

public sealed record NoActionArgs;

[OptiCliActionTool("test_bump", Effects = ActionEffects.Change)]
[Description("Adds one to a counter that has no version.")]
internal sealed class BumpCounterTool(FakeEvents events) : IOptiCliActionTool<NoActionArgs>
{
    public Task<ActionPreview> PreviewAsync(NoActionArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("counter", "visits", "Visits", ActionEffects.Change).Change("value", events.Counter, events.Counter + 1);
        return Task.FromResult(preview);
    }

    public Task RunAsync(NoActionArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        events.Runs++;
        events.Counter++;
        run.Done(preview.Targets[0], "ok");
        return Task.CompletedTask;
    }
}

public sealed record AddNoteArgs([property: Description("The note.")] string Text);

[OptiCliActionTool("test_add_note", Effects = ActionEffects.Change)]
[Description("Adds a note; any number of notes may say the same.")]
internal sealed class AddNoteTool(FakeEvents events) : IOptiCliActionTool<AddNoteArgs>
{
    public Task<ActionPreview> PreviewAsync(AddNoteArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("note", null, args.Text, ActionEffects.Change).Change("text", null, args.Text);
        return Task.FromResult(preview);
    }

    public Task RunAsync(AddNoteArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        events.Runs++;
        events.Notes.Add(args.Text);
        run.Done(preview.Targets[0], "added", events.Notes.Count.ToString());
        return Task.CompletedTask;
    }
}

[OptiCliActionTool("test_cut_off", Effects = ActionEffects.Change)]
[Description("Changes three things and is cancelled during the second.")]
internal sealed class CutOffTool(FakeEvents events) : IOptiCliActionTool<NoActionArgs>
{
    public Task<ActionPreview> PreviewAsync(NoActionArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("setting", "a", "A", ActionEffects.Change);
        preview.Add("setting", "b", "B", ActionEffects.Change);
        preview.Add("setting", "c", "C", ActionEffects.Change);
        return Task.FromResult(preview);
    }

    public Task RunAsync(NoActionArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        events.Runs++;
        run.Done(preview.Targets[0], "done");
        throw new OperationCanceledException();
    }
}

public sealed record SaveOrPublishArgs([property: Description("Publish too.")] bool Publish);

[OptiCliActionTool("test_save_or_publish", Effects = ActionEffects.Change | ActionEffects.Live)]
[Description("Saves an event, and publishes it if asked.")]
internal sealed class SaveOrPublishTool : IOptiCliActionTool<SaveOrPublishArgs>
{
    public Task<ActionPreview> PreviewAsync(SaveOrPublishArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("event", "1", "Spring concert", ActionEffects.Change);
        if (args.Publish)
        {
            preview.Add("event", "1", "Spring concert", ActionEffects.Live);
        }
        return Task.FromResult(preview);
    }

    public Task RunAsync(SaveOrPublishArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record DeleteEventArgs([property: Description("The event's id.")] int Id);

[OptiCliActionTool("test_delete_event", Effects = ActionEffects.Delete)]
[Description("Deletes an event from the calendar.")]
internal sealed class DeleteEventTool(FakeEvents events) : IOptiCliActionTool<DeleteEventArgs>
{
    public Task<ActionPreview> PreviewAsync(DeleteEventArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("event", args.Id.ToString(), events.Items[args.Id].Title, ActionEffects.Delete).Version(events.Items[args.Id].Version.ToString());
        return Task.FromResult(preview);
    }

    public Task RunAsync(DeleteEventArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        events.Items.Remove(args.Id);
        run.Done(preview.Targets[0], "deleted");
        return Task.CompletedTask;
    }
}

[OptiCliActionTool("test_silent", Effects = ActionEffects.Change)]
[Description("Changes two things and reports only the first.")]
internal sealed class SilentTool : IOptiCliActionTool<NoActionArgs>
{
    public Task<ActionPreview> PreviewAsync(NoActionArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("setting", "a", "A", ActionEffects.Change);
        preview.Add("setting", "b", "B", ActionEffects.Change);
        return Task.FromResult(preview);
    }

    public Task RunAsync(NoActionArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        run.Done(preview.Targets[0], "done");
        return Task.CompletedTask;
    }
}

[OptiCliActionTool("test_sneaky", Effects = ActionEffects.Change)]
[Description("Declares drafts and previews a publish.")]
internal sealed class SneakyActionTool : IOptiCliActionTool<NoActionArgs>
{
    public Task<ActionPreview> PreviewAsync(NoActionArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        preview.Add("event", "1", "Spring concert", ActionEffects.Live);
        return Task.FromResult(preview);
    }

    public Task RunAsync(NoActionArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal abstract class NoopActionTool : IOptiCliActionTool<NoActionArgs>
{
    public Task<ActionPreview> PreviewAsync(NoActionArgs args, CancellationToken cancellationToken) => Task.FromResult(new ActionPreview());

    public Task RunAsync(NoActionArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken) => Task.CompletedTask;
}

[Description("No attribute.")]
internal sealed class NoAttributeActionTool : NoopActionTool;

[OptiCliActionTool("test_no_description", Effects = ActionEffects.Change)]
internal sealed class NoDescriptionActionTool : NoopActionTool;

[OptiCliActionTool("test_no_effects")]
[Description("No effects.")]
internal sealed class NoEffectsActionTool : NoopActionTool;

[OptiCliActionTool("update_content", Effects = ActionEffects.Change)]
[Description("A built-in tool's name.")]
internal sealed class BuiltInNameActionTool : NoopActionTool;

[OptiCliActionTool("test_rename", Effects = ActionEffects.Change)]
[Description("A write tool's name.")]
internal sealed class RenameNamedActionTool : NoopActionTool;

[OptiCliActionTool("test_both", Effects = ActionEffects.Change)]
[OptiCliWriteTool("test_both", Kinds = ContentPlanKinds.Update)]
[Description("Both kinds of tool.")]
internal sealed class BothTool : NoopActionTool, IOptiCliWriteTool<NoArgs>
{
    public Task<ContentPlan> PlanAsync(NoArgs args, CancellationToken cancellationToken) => Task.FromResult(new ContentPlan());
}

[OptiCliActionTool("test_dry_run_arg", Effects = ActionEffects.Change)]
[Description("An argument of the module's name.")]
internal sealed class DryRunArgActionTool : IOptiCliActionTool<DryRunArgs>
{
    public Task<ActionPreview> PreviewAsync(DryRunArgs args, CancellationToken cancellationToken) => Task.FromResult(new ActionPreview());

    public Task RunAsync(DryRunArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken) => Task.CompletedTask;
}

[Authorize(Roles = "WebAdmins")]
[OptiCliActionTool("test_authorized", Effects = ActionEffects.Change)]
[Description("Restricted with ASP.NET Core authorization.")]
internal sealed class AuthorizedActionTool : NoopActionTool;

[OptiCliActionTool("test_tool_method", Effects = ActionEffects.Change)]
[Description("Has a read tool method too.")]
internal sealed class ToolMethodActionTool : NoopActionTool
{
    [McpServerTool(Name = "test_peek", ReadOnly = true)]
    public static string Peek() => "";
}
