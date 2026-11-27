using System.Globalization;
using System.Text.Json;
using McpException = ModelContextProtocol.McpException;
using McpSession = OptiCli.Mcp.Integration.Support.McpSession;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// The site's action tools (tests/fixtures/mcp/McpCustomToolsFixture.cs: notes on pages, kept outside the CMS's content)
/// through the real endpoint, as the test users: a dry run, a token that runs once (the run record in the Dynamic Data
/// Store), the publish and delete gates, access rights checked on the page a note belongs to, a run that stops at a
/// failure, and the audit. Each test makes its own scratch pages and moves them to the recycle bin afterwards.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class ActionToolsTests(SharedSessions sessions)
{
    private const string Clock = "X-Opticli-Fixture-Clock";

    [McpSiteFact]
    public async Task Action_tools_are_offered_as_reaching_outside_the_cms_and_the_instructions_say_how_to_run_them()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);

        var tools = (await editor.Client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        var save = tools["fixture_save_note"];
        Assert.Equal("Fixture: save note", save.Title);
        Assert.StartsWith("Test-only: saves the draft of a page's note, kept outside the CMS's content. It changes things outside the CMS's content", save.Description);
        Assert.Equal(["page", "text", "dryRun", "planToken"], save.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal((false, false, true), (save.Annotations!.ReadOnlyHint, save.Annotations.DestructiveHint, save.Annotations.OpenWorldHint));
        Assert.True(tools["fixture_publish_note"].Annotations!.DestructiveHint);
        Assert.Contains("Some of the site's own tools are action tools", editor.Client.ServerInstructions);
    }

    [McpSiteFact]
    public async Task A_note_is_saved_and_published_after_dry_runs_each_token_runs_once_and_each_target_is_audited()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var page = await CreateAsync(editor);
        try
        {
            var save = new Dictionary<string, object?> { ["page"] = int.Parse(page, CultureInfo.InvariantCulture), ["text"] = "Doors open at six." };
            var dry = await editor.OkAsync("fixture_save_note", With(save, dryRun: true));
            Assert.True(dry.GetProperty("valid").GetBoolean());
            var target = Assert.Single(dry.GetProperty("targets").EnumerateArray());
            Assert.Equal(("note", page, "change", "0"), (target.GetProperty("kind").GetString(), target.GetProperty("id").GetString(), target.GetProperty("effect").GetString(), target.GetProperty("version").GetString()));
            Assert.StartsWith("opticli-mcp-it ", target.GetProperty("label").GetString());
            Assert.Equal("""[{"field":"draft","before":null,"after":"Doors open at six."}]""", target.GetProperty("changes").GetRawText());
            Assert.Contains("#context=epi.cms.contentdata:///", target.GetProperty("editUrl").GetString());
            var token = dry.GetProperty("planToken").GetString()!;

            var saved = await editor.OkAsync("fixture_save_note", With(save, token: token));
            Assert.Equal("saved", Assert.Single(saved.GetProperty("targets").EnumerateArray()).GetProperty("status").GetString());
            // The same token again: the first run's result, from its record; nothing saved again.
            var again = await editor.OkAsync("fixture_save_note", With(save, token: token));
            Assert.True(again.GetProperty("repeated").GetBoolean());
            Assert.Equal(saved.GetProperty("targets").GetRawText(), again.GetProperty("targets").GetRawText());
            var version = Assert.Single((await editor.OkAsync("fixture_save_note", With(save, dryRun: true))).GetProperty("targets").EnumerateArray()).GetProperty("version").GetString();
            Assert.Equal("1", version);

            var publish = new Dictionary<string, object?> { ["pages"] = page };
            var publishDry = await editor.OkAsync("fixture_publish_note", With(publish, dryRun: true));
            Assert.Equal("live", Assert.Single(publishDry.GetProperty("targets").EnumerateArray()).GetProperty("effect").GetString());
            Assert.Equal(["Visitors see a published note under the page's heading."], publishDry.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
            var published = await editor.OkAsync("fixture_publish_note", With(publish, token: publishDry.GetProperty("planToken").GetString()));
            Assert.Equal("published", Assert.Single(published.GetProperty("targets").EnumerateArray()).GetProperty("status").GetString());

            var lines = await AuditAsync(2, $"note:{page}");
            Assert.Contains(lines, l => l.StartsWith($"MCP tool fixture_save_note target 1 change saved: user mcp-editor", StringComparison.Ordinal) && l.EndsWith($"note:{page}", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith($"MCP tool fixture_publish_note target 1 live published: user mcp-editor", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("opticli-mcp-it", StringComparison.Ordinal));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task A_note_changed_since_the_dry_run_is_a_conflict_and_a_run_that_fails_stops_at_that_note()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var first = await CreateAsync(editor);
        var second = await CreateAsync(editor);
        try
        {
            await SaveAsync(editor, first, "First.");
            await SaveAsync(editor, second, "Second.");
            var save = new Dictionary<string, object?> { ["page"] = int.Parse(first, CultureInfo.InvariantCulture), ["text"] = "Mine." };
            var token = await DryRunAsync(editor, "fixture_save_note", save);
            await SaveAsync(editor, first, "Someone else's.");
            var changed = await editor.ErrorAsync("fixture_save_note", With(save, token: token));
            Assert.Equal("conflict", changed.GetProperty("code").GetString());
            Assert.Contains("aren't the ones the dry run showed", changed.GetProperty("message").GetString());

            // The second note fails to publish: the first stays published, and the same token gives that result again.
            await SaveAsync(editor, second, "fail on publish");
            var publish = new Dictionary<string, object?> { ["pages"] = $"{first},{second}" };
            var publishToken = await DryRunAsync(editor, "fixture_publish_note", publish);
            var failed = await editor.ErrorAsync("fixture_publish_note", With(publish, token: publishToken));
            Assert.Equal("refused", failed.GetProperty("code").GetString());
            Assert.Equal("Target 2 of 2 (note) failed: The note service refused the note. One target was done and stays done.", failed.GetProperty("message").GetString());
            Assert.Equal(["published", "failed"], failed.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("status").GetString()));
            var again = await editor.ErrorAsync("fixture_publish_note", With(publish, token: publishToken));
            Assert.True(again.GetProperty("repeated").GetBoolean());
            Assert.Equal(failed.GetProperty("targets").GetRawText(), again.GetProperty("targets").GetRawText());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, first);
            await WritingTests.DeleteAsync(editor, second);
        }
    }

    [McpSiteFact]
    public async Task The_tool_checks_the_editors_access_on_the_page_and_the_module_checks_the_scopes()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var product = await sessions.ForAsync(TestUsers.Product);
        var page = await CreateAsync(editor);
        try
        {
            // The product editor may read the page, but not edit it.
            var refused = await product.ErrorAsync("fixture_save_note", new { page = int.Parse(page, CultureInfo.InvariantCulture), text = "x", dryRun = true });
            Assert.Equal("refused", refused.GetProperty("code").GetString());
            Assert.Contains("Edit access", refused.GetProperty("message").GetString());

            // A page the editor can't read is not_found, as one that doesn't exist.
            var hidden = (await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage })).GetProperty("id").GetInt32();
            var missing = await editor.ErrorAsync("fixture_save_note", new { page = 987654321, text = "x", dryRun = true });
            var hiddenError = await editor.ErrorAsync("fixture_save_note", new { page = hidden, text = "x", dryRun = true });
            Assert.Equal("not_found", hiddenError.GetProperty("code").GetString());
            Assert.Equal(missing.GetProperty("message").GetString()!.Replace("987654321", hidden.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), hiddenError.GetProperty("message").GetString());

            // Every action needs content:write.
            await using var reader = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Allow = [] });
            var missingScope = await reader.ErrorAsync("fixture_save_note", new { page = int.Parse(page, CultureInfo.InvariantCulture), text = "x", dryRun = true });
            Assert.Equal(("refused", "missingScope"), (missingScope.GetProperty("code").GetString(), missingScope.GetProperty("reason").GetString()));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task A_token_works_for_30_minutes_and_its_record_answers_a_retry_after_that()
    {
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor);
        var page = await CreateAsync(editor);
        try
        {
            var save = new Dictionary<string, object?> { ["page"] = int.Parse(page, CultureInfo.InvariantCulture), ["text"] = "In time." };
            var token = await DryRunAsync(editor, "fixture_save_note", save);
            editor.Headers[Clock] = (31 * 60).ToString(CultureInfo.InvariantCulture);
            var expired = await editor.ErrorAsync("fixture_save_note", With(save, token: token));
            Assert.Equal(("conflict", "The planToken expired: a dry run's token works for 30 minutes. Nothing was changed."),
                (expired.GetProperty("code").GetString(), expired.GetProperty("message").GetString()));

            editor.Headers[Clock] = (28 * 60).ToString(CultureInfo.InvariantCulture);
            Assert.Equal("saved", Assert.Single((await editor.OkAsync("fixture_save_note", With(save, token: token))).GetProperty("targets").EnumerateArray()).GetProperty("status").GetString());
            editor.Headers[Clock] = (45 * 60).ToString(CultureInfo.InvariantCulture);
            Assert.True((await editor.OkAsync("fixture_save_note", With(save, token: token))).GetProperty("repeated").GetBoolean());
        }
        finally
        {
            editor.Headers.Clear();
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [NoPublishSiteFact]
    public async Task Without_publishing_and_deleting_only_the_tool_that_saves_drafts_is_offered()
    {
        var main = await sessions.ForAsync(TestUsers.Editor);
        var page = await CreateAsync(main);
        try
        {
            await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = McpSiteSettings.NoPublishUrl });
            var tools = (await editor.Client.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("fixture_save_note", tools);
            // It may delete: not offered, as delete_content isn't.
            Assert.DoesNotContain("fixture_publish_note", tools);
            await Assert.ThrowsAnyAsync<McpException>(() => editor.CallAsync("fixture_publish_note", new { pages = page, dryRun = true }));
            await SaveAsync(editor, page, "A draft is fine.");
        }
        finally
        {
            await WritingTests.DeleteAsync(main, page);
        }
    }

    /// <summary>A scratch page, a draft, below the start page.</summary>
    private static async Task<string> CreateAsync(McpSession session)
    {
        var start = WritingTests.Id((await session.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        return WritingTests.Id((await session.OkAsync("create_content", new { type = "StandardPage", name = $"opticli-mcp-it {Guid.NewGuid():N}", parent = start })).GetProperty("content"));
    }

    private static async Task SaveAsync(McpSession session, string page, string text)
    {
        var save = new Dictionary<string, object?> { ["page"] = int.Parse(page, CultureInfo.InvariantCulture), ["text"] = text };
        var token = await DryRunAsync(session, "fixture_save_note", save);
        await session.OkAsync("fixture_save_note", With(save, token: token));
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> arguments, bool dryRun = false, string? token = null)
    {
        var result = new Dictionary<string, object?>(arguments);
        if (dryRun)
        {
            result["dryRun"] = true;
        }
        if (token is not null)
        {
            result["planToken"] = token;
        }
        return result;
    }

    private static async Task<string> DryRunAsync(McpSession session, string tool, Dictionary<string, object?> arguments)
    {
        var dry = await session.OkAsync(tool, With(arguments, dryRun: true));
        Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
        return dry.GetProperty("planToken").GetString()!;
    }

    /// <summary>The site's audit lines for action targets, the last <paramref name="count"/> once one names <paramref name="what"/>.</summary>
    private static async Task<IReadOnlyList<string>> AuditAsync(int count, string what)
    {
        var log = Path.Combine(McpSiteSettings.SiteDirectory, "App_Data", $"mcp-site-{McpSiteSettings.Url!.Port}.log");
        List<string> lines = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                lines = [.. (await reader.ReadToEndAsync()).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("MCP tool fixture_", StringComparison.Ordinal) && l.Contains(" target ", StringComparison.Ordinal))];
            }
            if (lines.Count(l => l.EndsWith(what, StringComparison.Ordinal)) >= count)
            {
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(lines.Count >= count, $"{log} has {lines.Count} audit lines for action targets, not {count}.");
        return lines[^count..];
    }
}
