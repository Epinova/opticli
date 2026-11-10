using System.Text.Json;
using McpException = ModelContextProtocol.McpException;
using McpSession = OptiCli.Mcp.Integration.Support.McpSession;
using ModelContextProtocol.Client;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// The site's own tools (tests/fixtures/mcp/McpCustomToolsFixture.cs, registered with <c>AddOptiCliMcp().WithTools…</c>
/// by setup.sh) through the real endpoint, as the test users: read tools that never show what the editor can't read,
/// write tools whose plans the module dry-runs and runs as the editor, step by step, with a plan token that runs its plan
/// once and expires. Each test makes its own scratch pages below the start page and moves them to the recycle bin
/// afterwards.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class CustomToolsTests(SharedSessions sessions)
{
    private const string Missing = "987654321";

    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    [McpSiteFact]
    public async Task The_sites_tools_are_offered_beside_the_built_in_ones_with_its_instructions_and_on_no_other_endpoint()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);

        var tools = (await editor.Client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        foreach (var name in new[] { "fixture_find_pages", "fixture_describe", "fixture_describe_unchecked", "fixture_readable", "fixture_set_teaser", "fixture_build_section", "fixture_move", "fixture_trash", "get_content", "delete_content" })
        {
            Assert.True(tools.ContainsKey(name), name);
        }
        Assert.True(tools["fixture_find_pages"].Annotations!.ReadOnlyHint);
        var teaser = tools["fixture_set_teaser"];
        Assert.Equal("Fixture: set teaser text", teaser.Title);
        Assert.StartsWith("Test-only: sets the teaser text of the pages given. It saves drafts only", teaser.Description);
        Assert.Equal(["ids", "text", "from", "lang", "dryRun", "planToken"], teaser.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.False(teaser.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal((false, false), (teaser.Annotations!.ReadOnlyHint, teaser.Annotations.DestructiveHint));
        Assert.True(tools["fixture_build_section"].Annotations!.DestructiveHint);
        Assert.True(tools["fixture_trash"].Annotations!.DestructiveHint);

        var instructions = editor.Client.ServerInstructions!;
        Assert.Contains("From this site's developers, about its own tools and content:", instructions);
        Assert.EndsWith("Test-only tools of the opticli MCP fixture start with fixture_; they work on Alloy's pages.", instructions.TrimEnd());

        // An argument the tool doesn't have, and a missing one: usage, for the site's tools and the built-in ones alike.
        Assert.Equal("usage", (await editor.ErrorAsync("fixture_set_teaser", new { ids = "1", text = "x", colour = "red", dryRun = true })).GetProperty("code").GetString());
        Assert.Equal("usage", (await editor.ErrorAsync("fixture_set_teaser", new { ids = "1", dryRun = true })).GetProperty("code").GetString());
        Assert.Equal("usage", (await editor.ErrorAsync("fixture_describe", new { lang = "en" })).GetProperty("code").GetString());
        Assert.Equal("usage", (await editor.ErrorAsync("get_content", new { })).GetProperty("code").GetString());

        // The site's own MCP server, which registers the read tools' class too, has its own tool only.
        await using var theirs = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(McpSiteSettings.Url!, "site-mcp") }));
        Assert.Equal(["site_ping"], (await theirs.ListToolsAsync()).Select(t => t.Name));
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("fixture_readable", new Dictionary<string, object?> { ["ids"] = "1" }).AsTask());
        await Assert.ThrowsAnyAsync<McpException>(() => theirs.CallToolAsync("fixture_set_teaser", new Dictionary<string, object?> { ["ids"] = "1", ["text"] = "x", ["dryRun"] = true }).AsTask());
    }

    [McpSiteFact]
    public async Task A_read_tool_shows_nothing_the_editor_cant_read_and_hidden_content_looks_missing()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var product = await sessions.ForAsync(TestUsers.Product);
        var start = await StartAsync(editor);
        var hidden = await HiddenAsync(product);
        var hiddenVersion = (await product.OkAsync("get_content", new { reference = hidden })).GetProperty("version").GetInt32();

        // A search over the site's own index: the hidden page matches (with news about it), and only the product editor
        // sees it.
        var editorFound = (await editor.OkAsync("fixture_find_pages", new { text = "Alloy Meet", root = int.Parse(start) })).EnumerateArray().ToList();
        Assert.NotEmpty(editorFound);
        Assert.DoesNotContain(editorFound, f => f.GetProperty("id").GetInt32() == int.Parse(hidden));
        var productFound = (await product.OkAsync("fixture_find_pages", new { text = "Alloy Meet", root = int.Parse(start) })).EnumerateArray().ToList();
        var found = Assert.Single(productFound, f => f.GetProperty("id").GetInt32() == int.Parse(hidden));
        Assert.Equal(editorFound.Count + 1, productFound.Count);
        Assert.Equal("Alloy Meet", found.GetProperty("name").GetString());
        Assert.Contains("#context=epi.cms.contentdata:///", found.GetProperty("editUrl").GetString());
        Assert.False(string.IsNullOrEmpty(found.GetProperty("url").GetString()));

        // Readable keeps order and duplicates, and leaves out what is missing or hidden.
        Assert.Equal([start, start], Strings(await editor.OkAsync("fixture_readable", new { ids = $"{start},{hidden},{Missing},{start}" })));
        Assert.Equal([start, hidden, start], Strings(await product.OkAsync("fixture_readable", new { ids = $"{start},{hidden},{Missing},{start}" })));

        // Load: hidden content is not_found, word for word as content that doesn't exist, however it is asked for.
        var missing = await editor.ErrorAsync("fixture_describe", new { reference = Missing });
        Assert.Equal("not_found", missing.GetProperty("code").GetString());
        foreach (var arguments in new object[]
        {
            new { reference = hidden },
            new { reference = hidden, latest = true },
            new { reference = hidden, lang = "sv" },
            new { reference = hidden, lang = "xx" },
            new { reference = $"{hidden}_{hiddenVersion}" },
        })
        {
            var refused = await editor.ErrorAsync("fixture_describe", arguments);
            Assert.Equal(missing.GetProperty("code").GetString(), refused.GetProperty("code").GetString());
            Assert.Equal(missing.GetProperty("message").GetString()!.Replace(Missing, hidden, StringComparison.Ordinal), refused.GetProperty("message").GetString());
            Assert.Equal(Hint(missing), Hint(refused));
            Assert.DoesNotContain("Alloy Meet", refused.GetRawText());
        }
        Assert.Equal("Alloy Meet", (await product.OkAsync("fixture_describe", new { reference = hidden })).GetProperty("name").GetString());

        // Describe of content the site's code loaded unchecked: not_found, without even its id.
        var described = await editor.ErrorAsync("fixture_describe_unchecked", new { id = int.Parse(hidden) });
        Assert.Equal(("not_found", "No such content."), (described.GetProperty("code").GetString(), described.GetProperty("message").GetString()));
        Assert.DoesNotContain(hidden, described.GetRawText());
        Assert.Equal("Alloy Meet", (await product.OkAsync("fixture_describe_unchecked", new { id = int.Parse(hidden) })).GetProperty("name").GetString());
    }

    [McpSiteFact]
    public async Task Hidden_content_in_a_plan_is_a_not_found_step_and_nothing_of_it_shows()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var product = await sessions.ForAsync(TestUsers.Product);
        var start = await StartAsync(editor);
        var hidden = await HiddenAsync(product);
        var missing = (await editor.ErrorAsync("fixture_describe", new { reference = hidden })).GetProperty("message").GetString();
        var sandbox = await CreateAsync(editor, start);
        try
        {
            // Planned from content the site loaded without an access check: each step is refused as not_found.
            foreach (var (tool, arguments) in new (string, object)[]
            {
                ("fixture_set_teaser", new { ids = hidden, text = "x", from = "loader", dryRun = true }),
                ("fixture_move", new { ids = hidden, destination = int.Parse(sandbox), @unchecked = true, dryRun = true }),
                ("fixture_move", new { ids = sandbox, destination = int.Parse(hidden), @unchecked = true, dryRun = true }),
                ("fixture_trash", new { ids = hidden, @unchecked = true, dryRun = true }),
            })
            {
                var dry = await editor.OkAsync(tool, arguments);
                Assert.False(dry.GetProperty("valid").GetBoolean());
                Assert.False(dry.TryGetProperty("planToken", out _));
                var error = Assert.Single(dry.GetProperty("steps").EnumerateArray()).GetProperty("error");
                Assert.Equal(("not_found", missing), (error.GetProperty("code").GetString(), error.GetProperty("message").GetString()));
                Assert.DoesNotContain("Alloy Meet", dry.GetRawText());
                Assert.DoesNotContain("ProductPage", dry.GetRawText());
            }
            // Loaded through the editor: the whole call is not_found.
            var loaded = await editor.ErrorAsync("fixture_set_teaser", new { ids = hidden, text = "x", dryRun = true });
            Assert.Equal(("not_found", missing), (loaded.GetProperty("code").GetString(), loaded.GetProperty("message").GetString()));

            // The product editor may read and edit it, but not move it to the recycle bin.
            Assert.True((await product.OkAsync("fixture_set_teaser", new { ids = hidden, text = "x", dryRun = true })).GetProperty("valid").GetBoolean());
            var trash = await product.OkAsync("fixture_trash", new { ids = hidden, dryRun = true });
            Assert.False(trash.GetProperty("valid").GetBoolean());
            var refused = Assert.Single(trash.GetProperty("steps").EnumerateArray()).GetProperty("error");
            Assert.Equal("refused", refused.GetProperty("code").GetString());
            Assert.Contains("Delete access", refused.GetProperty("message").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task A_plan_runs_after_its_dry_run_step_by_step_as_drafts_with_planned_content_filled_in_and_audited()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        var mover = await CreateAsync(editor, sandbox);
        var name = ScratchName();
        try
        {
            var arguments = new Dictionary<string, object?> { ["page"] = int.Parse(sandbox), ["name"] = name, ["movePage"] = int.Parse(mover) };
            var dry = await editor.OkAsync("fixture_build_section", With(arguments, dryRun: true));
            Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
            var expires = dry.GetProperty("planTokenExpires").GetDateTimeOffset();
            Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(31));
            var steps = dry.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(["create", "create", "create", "update", "create", "move"], steps.Select(s => s.GetProperty("kind").GetString()));
            Assert.Equal(sandbox, steps[0].GetProperty("parent").GetString());
            // The folder in the page's own "For this page" folder, the block in the folder, the child below the section.
            Assert.Equal(sandbox, steps[1].GetProperty("forContent").GetString());
            Assert.Equal("step 2", steps[2].GetProperty("parent").GetString());
            Assert.Equal("step 1", steps[4].GetProperty("parent").GetString());
            Assert.Equal("step 1", steps[5].GetProperty("destination").GetString());
            // What refers to planned content is checked when it runs, and the dry run says so.
            Assert.Equal("step 3", steps[3].GetProperty("unchecked").GetProperty("areaOps")[0].GetProperty("ref").GetString());
            Assert.Contains("step 3", steps[4].GetProperty("unchecked").GetProperty("properties").GetProperty("MainContentArea").GetRawText());
            Assert.All(steps, s => Assert.False(s.TryGetProperty("error", out _)));
            // Nothing saved.
            Assert.Empty((await editor.OkAsync("find_content", new { name, root = sandbox })).GetProperty("items").EnumerateArray());

            var token = dry.GetProperty("planToken").GetString()!;
            var run = await editor.OkAsync("fixture_build_section", With(arguments, token: token));
            Assert.Equal((6, 6), (run.GetProperty("completed").GetInt32(), run.GetProperty("total").GetInt32()));
            var ran = run.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(["saved", "saved", "saved", "saved", "saved", "moved"], ran.Select(s => s.GetProperty("status").GetString()));
            var section = WritingTests.Id(ran[0].GetProperty("content"));
            var folder = WritingTests.Id(ran[1].GetProperty("content"));
            var block = WritingTests.Id(ran[2].GetProperty("content"));
            var child = WritingTests.Id(ran[4].GetProperty("content"));
            WritingTests.EditUrl(ran[0], ran[0].GetProperty("content").GetProperty("ref").GetString()!);

            // Drafts only, each where the plan put it, with the planned content filled in.
            var sectionRead = await editor.OkAsync("get_content", new { reference = section, version = "latest" });
            Assert.Equal((sandbox, "checkedOut"), (sectionRead.GetProperty("parent").GetString(), sectionRead.GetProperty("status").GetString()));
            Assert.Equal(folder, (await editor.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("parent").GetString());
            var childRead = await editor.OkAsync("get_content", new { reference = child, version = "latest" });
            Assert.Equal(section, childRead.GetProperty("parent").GetString());
            Assert.Contains($"\"{block}\"", childRead.GetProperty("properties").GetProperty("MainContentArea").GetRawText());
            var sandboxRead = await editor.OkAsync("get_content", new { reference = sandbox, version = "latest" });
            Assert.Contains($"\"{block}\"", sandboxRead.GetProperty("properties").GetProperty("MainContentArea").GetRawText());
            Assert.Equal(section, (await editor.OkAsync("get_content", new { reference = mover, version = "latest" })).GetProperty("parent").GetString());

            var audit = await AuditAsync("fixture_build_section", 6, $"content {mover}");
            Assert.Equal(
                ["step 1 create saved", "step 2 create saved", "step 3 create saved", "step 4 update saved", "step 5 create saved", "step 6 move moved"],
                audit.Select(l => l[(l.IndexOf("step ", StringComparison.Ordinal))..l.IndexOf(':', StringComparison.Ordinal)]));
            Assert.All(audit, l => Assert.Contains("user mcp-editor", l));
            Assert.EndsWith($"content {mover}", audit[5]);

            // The same token again (a client that retries): refused, nothing made twice.
            var made = (await editor.OkAsync("find_content", new { name, root = sandbox })).GetProperty("items").GetArrayLength();
            var again = await editor.ErrorAsync("fixture_build_section", With(arguments, token: token));
            Assert.Equal("conflict", again.GetProperty("code").GetString());
            Assert.Equal(made, (await editor.OkAsync("find_content", new { name, root = sandbox })).GetProperty("items").GetArrayLength());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task A_run_that_fails_midway_keeps_the_steps_before_it_and_says_which_were_saved()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        var mover = await CreateAsync(editor, sandbox);
        var name = ScratchName();
        try
        {
            var arguments = new Dictionary<string, object?> { ["page"] = int.Parse(sandbox), ["name"] = name, ["movePage"] = int.Parse(mover), ["fail"] = true };
            var dry = await editor.OkAsync("fixture_build_section", With(arguments, dryRun: true));
            // A value that refers to planned content can't be checked before it exists.
            Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
            Assert.True(dry.GetProperty("steps")[4].GetProperty("unchecked").GetProperty("properties").TryGetProperty("NoSuchProperty", out _));

            var failed = await editor.ErrorAsync("fixture_build_section", With(arguments, token: dry.GetProperty("planToken").GetString()));
            Assert.Equal("usage", failed.GetProperty("code").GetString());
            Assert.StartsWith("Step 5 of 6 (create) failed: ", failed.GetProperty("message").GetString());
            Assert.EndsWith("Steps 1 to 4 were saved and stay saved.", failed.GetProperty("message").GetString());
            Assert.Contains("which steps were saved", failed.GetProperty("hint").GetString());
            Assert.Equal((4, 6), (failed.GetProperty("completed").GetInt32(), failed.GetProperty("total").GetInt32()));
            var steps = failed.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(["saved", "saved", "saved", "saved", "failed", "notRun"], steps.Select(s => s.GetProperty("status").GetString()));
            Assert.Contains("NoSuchProperty", steps[4].GetProperty("error").GetProperty("message").GetString());

            // The section was made and stays; nothing below it, and the page to move is where it was.
            var section = WritingTests.Id(steps[0].GetProperty("content"));
            Assert.Equal(sandbox, (await editor.OkAsync("get_content", new { reference = section, version = "latest" })).GetProperty("parent").GetString());
            Assert.Empty((await editor.OkAsync("list_children", new { reference = section })).GetProperty("items").EnumerateArray());
            Assert.Equal(sandbox, (await editor.OkAsync("get_content", new { reference = mover, version = "latest" })).GetProperty("parent").GetString());

            var audit = await AuditAsync("fixture_build_section", 5, $"new below {section}");
            Assert.Contains(audit, l => l.Contains("step 5 create failed:", StringComparison.Ordinal) && l.EndsWith($"content new below {section}", StringComparison.Ordinal));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task A_restricted_editors_plan_is_refused_step_by_step_for_their_access_rights()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var product = await sessions.ForAsync(TestUsers.Product);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        var other = await CreateAsync(editor, sandbox);
        try
        {
            // The product editor may read the sandbox, but not create, edit or delete there.
            var build = await product.OkAsync("fixture_build_section", new { page = int.Parse(sandbox), name = ScratchName(), movePage = int.Parse(other), dryRun = true });
            Assert.False(build.GetProperty("valid").GetBoolean());
            var errors = build.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("error")).ToList();
            Assert.All(errors, e => Assert.Equal("refused", e.GetProperty("code").GetString()));
            Assert.All(errors, e => Assert.Contains(" access to ", e.GetProperty("message").GetString()));
            Assert.Contains("Create access", errors[0].GetProperty("message").GetString());
            Assert.Contains("Edit access", errors[3].GetProperty("message").GetString());
            var teaser = await product.OkAsync("fixture_set_teaser", new { ids = other, text = "x", dryRun = true });
            Assert.Contains("Edit access", Assert.Single(teaser.GetProperty("steps").EnumerateArray()).GetProperty("error").GetProperty("message").GetString());
            var trash = await product.OkAsync("fixture_trash", new { ids = other, dryRun = true });
            Assert.Contains("Delete access", Assert.Single(trash.GetProperty("steps").EnumerateArray()).GetProperty("error").GetProperty("message").GetString());

            // Writing needs content:write: a read-only connection is refused before the tool plans anything.
            await using var reader = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Allow = [] });
            var missingScope = await reader.ErrorAsync("fixture_set_teaser", new { ids = other, text = "x", dryRun = true });
            Assert.Equal(("refused", "missingScope"), (missingScope.GetProperty("code").GetString(), missingScope.GetProperty("reason").GetString()));
            // Reading doesn't.
            Assert.Equal([other], Strings(await reader.OkAsync("fixture_readable", new { ids = other })));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task A_page_saved_between_the_dry_run_and_the_run_refuses_the_run_and_a_token_runs_once()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        try
        {
            var arguments = new Dictionary<string, object?> { ["ids"] = sandbox, ["text"] = "from the tool" };
            var token = await DryRunAsync(editor, "fixture_set_teaser", arguments);
            var latest = (await editor.OkAsync("get_content", new { reference = sandbox, version = "latest" })).GetProperty("version").GetInt32();
            await editor.OkAsync("update_content", new { reference = sandbox, properties = new { TeaserText = "saved meanwhile" }, baseVersion = latest });

            var changed = await editor.ErrorAsync("fixture_set_teaser", With(arguments, token: token));
            Assert.Equal("conflict", changed.GetProperty("code").GetString());
            Assert.Contains("isn't the one the dry run showed", changed.GetProperty("message").GetString());
            Assert.Equal("saved meanwhile", WritingTests.Property(await editor.OkAsync("get_content", new { reference = sandbox, version = "latest" }), "TeaserText"));

            token = await DryRunAsync(editor, "fixture_set_teaser", arguments);
            var run = await editor.OkAsync("fixture_set_teaser", With(arguments, token: token));
            Assert.Equal("saved", Assert.Single(run.GetProperty("steps").EnumerateArray()).GetProperty("status").GetString());
            Assert.Equal("from the tool", WritingTests.Property(await editor.OkAsync("get_content", new { reference = sandbox, version = "latest" }), "TeaserText"));
            Assert.Contains(await AuditAsync("fixture_set_teaser", 1, $"content {sandbox}_"), l => l.Contains("step 1 update saved: user mcp-editor", StringComparison.Ordinal));
            // Run once: the same token again is refused.
            Assert.Equal("conflict", (await editor.ErrorAsync("fixture_set_teaser", With(arguments, token: token))).GetProperty("code").GetString());

            // A token is the editor's own.
            var admin = await sessions.ForAsync(TestUsers.Admin);
            token = await DryRunAsync(editor, "fixture_set_teaser", arguments);
            Assert.Equal("conflict", (await admin.ErrorAsync("fixture_set_teaser", With(arguments, token: token))).GetProperty("code").GetString());
            // And this tool's: another's, or one that isn't a token, is usage.
            var moveToken = await DryRunAsync(editor, "fixture_move", new Dictionary<string, object?> { ["ids"] = "", ["destination"] = int.Parse(sandbox) });
            Assert.Equal("usage", (await editor.ErrorAsync("fixture_set_teaser", With(arguments, token: moveToken))).GetProperty("code").GetString());
            Assert.Equal("usage", (await editor.ErrorAsync("fixture_set_teaser", With(arguments, token: "abc"))).GetProperty("code").GetString());
            Assert.Equal("usage", (await editor.ErrorAsync("fixture_set_teaser", arguments)).GetProperty("code").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task A_plan_token_works_for_30_minutes()
    {
        // A connection of its own, signed in just now: the site's clock moves forward for its requests only
        // (FixtureClock), and its access token lasts an hour.
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        try
        {
            var arguments = new Dictionary<string, object?> { ["ids"] = sandbox, ["text"] = "in time" };
            var token = await DryRunAsync(editor, "fixture_set_teaser", arguments);

            editor.Headers["X-Opticli-Fixture-Clock"] = (31 * 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var expired = await editor.ErrorAsync("fixture_set_teaser", With(arguments, token: token));
            Assert.Equal(("conflict", "The planToken expired: a dry run's token works for 30 minutes. Nothing was saved."),
                (expired.GetProperty("code").GetString(), expired.GetProperty("message").GetString()));
            Assert.Contains("dryRun: true again", expired.GetProperty("hint").GetString());

            editor.Headers["X-Opticli-Fixture-Clock"] = (28 * 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("saved", Assert.Single((await editor.OkAsync("fixture_set_teaser", With(arguments, token: token))).GetProperty("steps").EnumerateArray()).GetProperty("status").GetString());
        }
        finally
        {
            editor.Headers.Clear();
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [McpSiteFact]
    public async Task An_update_planned_from_the_published_version_is_refused_over_a_newer_draft_in_its_language()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = await StartAsync(editor);
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, publish = true })).GetProperty("content"));
        var draftOnly = await CreateAsync(editor, start);
        try
        {
            await editor.OkAsync("add_language", new { reference = page, lang = "sv", publish = true });
            var published = (await editor.OkAsync("get_content", new { reference = page })).GetProperty("version").GetInt32();
            // The administrator's newer draft, in English only.
            var theirs = (await admin.OkAsync("update_content", new { reference = page, properties = new { TeaserText = "the administrator's" }, baseVersion = published })).GetProperty("content").GetProperty("version").GetInt32();

            foreach (var from in new[] { "published", "loader" })
            {
                var dry = await editor.OkAsync("fixture_set_teaser", new { ids = page, text = "x", from, dryRun = true });
                Assert.False(dry.GetProperty("valid").GetBoolean());
                var error = Assert.Single(dry.GetProperty("steps").EnumerateArray()).GetProperty("error");
                Assert.Equal("conflict", error.GetProperty("code").GetString());
                Assert.Contains($"The tool planned from version {published}, the one visitors see", error.GetProperty("message").GetString());
                Assert.Contains("it may be the user's own", error.GetProperty("hint").GetString());
                Assert.Equal(theirs, error.GetProperty("currentVersion").GetInt32());
            }
            // Planned from the newest version, it is based on the draft.
            var latest = await editor.OkAsync("fixture_set_teaser", new { ids = page, text = "x", dryRun = true });
            Assert.Equal(theirs, Assert.Single(latest.GetProperty("steps").EnumerateArray()).GetProperty("baseVersion").GetInt32());

            // Swedish has no draft: planned from its published version, it runs; then its own draft is newer.
            var sv = new Dictionary<string, object?> { ["ids"] = page, ["text"] = "svensk", ["from"] = "published", ["lang"] = "sv" };
            var svPublished = (await editor.OkAsync("get_content", new { reference = page, lang = "sv" })).GetProperty("version").GetInt32();
            var svDry = await editor.OkAsync("fixture_set_teaser", With(sv, dryRun: true));
            Assert.Equal(svPublished, Assert.Single(svDry.GetProperty("steps").EnumerateArray()).GetProperty("baseVersion").GetInt32());
            var svRun = await editor.OkAsync("fixture_set_teaser", With(sv, token: svDry.GetProperty("planToken").GetString()));
            Assert.Equal("sv", Assert.Single(svRun.GetProperty("steps").EnumerateArray()).GetProperty("content").GetProperty("language").GetString());
            var again = Assert.Single((await editor.OkAsync("fixture_set_teaser", With(sv, dryRun: true))).GetProperty("steps").EnumerateArray()).GetProperty("error");
            Assert.Equal("conflict", again.GetProperty("code").GetString());

            // Never published: based on its primary draft, and after the run on the draft the run saved.
            var draftArguments = new Dictionary<string, object?> { ["ids"] = draftOnly, ["text"] = "first", ["from"] = "published" };
            var primary = (await editor.OkAsync("get_content", new { reference = draftOnly })).GetProperty("version").GetInt32();
            var draftDry = await editor.OkAsync("fixture_set_teaser", With(draftArguments, dryRun: true));
            Assert.Equal(primary, Assert.Single(draftDry.GetProperty("steps").EnumerateArray()).GetProperty("baseVersion").GetInt32());
            var saved = Assert.Single((await editor.OkAsync("fixture_set_teaser", With(draftArguments, token: draftDry.GetProperty("planToken").GetString()))).GetProperty("steps").EnumerateArray());
            var next = await editor.OkAsync("fixture_set_teaser", new { ids = draftOnly, text = "second", from = "published", dryRun = true });
            Assert.True(next.GetProperty("valid").GetBoolean(), next.GetRawText());
            Assert.Equal(saved.GetProperty("content").GetProperty("version").GetInt32(), Assert.Single(next.GetProperty("steps").EnumerateArray()).GetProperty("baseVersion").GetInt32());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
            await WritingTests.DeleteAsync(editor, draftOnly);
        }
    }

    [McpSiteFact]
    public async Task Moves_and_trash_steps_report_what_goes_with_them_and_run_once()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = await StartAsync(editor);
        var sandbox = await CreateAsync(editor, start);
        try
        {
            var a = await CreateAsync(editor, sandbox);
            var b = await CreateAsync(editor, sandbox);
            var move = new Dictionary<string, object?> { ["ids"] = a, ["destination"] = int.Parse(b) };
            var moveToken = await DryRunAsync(editor, "fixture_move", move);
            var moved = Assert.Single((await editor.OkAsync("fixture_move", With(move, token: moveToken))).GetProperty("steps").EnumerateArray());
            Assert.Equal(("moved", b), (moved.GetProperty("status").GetString(), moved.GetProperty("parent").GetString()));
            Assert.Equal("conflict", (await editor.ErrorAsync("fixture_move", With(move, token: moveToken))).GetProperty("code").GetString());

            // b has a below it now, and a published page refers to b in its ContentArea.
            var referrer = WritingTests.Id((await editor.OkAsync("create_content", new
            {
                type = "StandardPage", name = ScratchName(), parent = sandbox, publish = true,
                properties = new { MainContentArea = new[] { new { @ref = b } } },
            })).GetProperty("content"));
            var trash = new Dictionary<string, object?> { ["ids"] = b };
            var dry = await editor.OkAsync("fixture_trash", With(trash, dryRun: true));
            var step = Assert.Single(dry.GetProperty("steps").EnumerateArray());
            Assert.Equal(1, step.GetProperty("descendants").GetInt32());
            Assert.Equal(int.Parse(referrer), Assert.Single(step.GetProperty("referencedBy").EnumerateArray()).GetProperty("id").GetInt32());
            Assert.Contains("unpublished drafts", step.GetProperty("referencedByNote").GetString());

            // A step on content below what an earlier step trashes is refused, naming both.
            var below = await editor.OkAsync("fixture_trash", new { ids = $"{b},{a}", dryRun = true });
            Assert.False(below.GetProperty("valid").GetBoolean());
            Assert.StartsWith($"Step 1 trashes content {b}, and content {a}, which this step uses, is below it",
                below.GetProperty("steps")[1].GetProperty("error").GetProperty("message").GetString());

            var trashed = await editor.OkAsync("fixture_trash", With(trash, token: dry.GetProperty("planToken").GetString()));
            Assert.Equal("trashed", Assert.Single(trashed.GetProperty("steps").EnumerateArray()).GetProperty("status").GetString());
            Assert.True((await editor.OkAsync("get_content", new { reference = b })).GetProperty("deleted").GetBoolean());
            var replay = await editor.ErrorAsync("fixture_trash", With(trash, token: dry.GetProperty("planToken").GetString()));
            Assert.Equal(("conflict", 0), (replay.GetProperty("code").GetString(), replay.GetProperty("completed").GetInt32()));
            Assert.Contains("already in the recycle bin", replay.GetProperty("message").GetString());

            var audit = await AuditAsync("fixture_trash", 1, $"content {b}");
            Assert.Contains(audit, l => l.Contains("step 1 trash trashed: user mcp-editor", StringComparison.Ordinal));
            Assert.Contains(await AuditAsync("fixture_move", 1, $"content {a}"), l => l.Contains("step 1 move moved: user mcp-editor", StringComparison.Ordinal));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, sandbox);
        }
    }

    [NoPublishSiteFact]
    public async Task Without_publishing_and_deleting_a_plan_moves_only_drafts_and_no_tool_may_trash()
    {
        // Made on the first site, which publishes and deletes; planned on the second, which does neither.
        var main = await sessions.ForAsync(TestUsers.Editor);
        var start = await StartAsync(main);
        var sandbox = await CreateAsync(main, start);
        var published = WritingTests.Id((await main.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, publish = true })).GetProperty("content"));
        var draft = await CreateAsync(main, start);
        try
        {
            await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = McpSiteSettings.NoPublishUrl });
            var tools = (await editor.Client.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("fixture_move", tools);
            Assert.DoesNotContain("fixture_trash", tools);
            Assert.DoesNotContain("delete_content", tools);
            await Assert.ThrowsAnyAsync<McpException>(() => editor.CallAsync("fixture_trash", new { ids = draft, dryRun = true }));

            var live = await editor.OkAsync("fixture_move", new { ids = published, destination = int.Parse(sandbox), dryRun = true });
            Assert.False(live.GetProperty("valid").GetBoolean());
            var error = Assert.Single(live.GetProperty("steps").EnumerateArray()).GetProperty("error");
            Assert.Equal(("refused", "publishingOff"), (error.GetProperty("code").GetString(), error.GetProperty("reason").GetString()));

            var arguments = new Dictionary<string, object?> { ["ids"] = draft, ["destination"] = int.Parse(sandbox) };
            var token = await DryRunAsync(editor, "fixture_move", arguments);
            var moved = Assert.Single((await editor.OkAsync("fixture_move", With(arguments, token: token))).GetProperty("steps").EnumerateArray());
            Assert.Equal(("moved", sandbox), (moved.GetProperty("status").GetString(), moved.GetProperty("parent").GetString()));
        }
        finally
        {
            await WritingTests.DeleteAsync(main, published);
            await WritingTests.DeleteAsync(main, sandbox);
        }
    }

    private static async Task<string> StartAsync(McpSession session) =>
        WritingTests.Id((await session.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));

    /// <summary>Alloy Meet's id, which only the product editor (or an administrator) may read.</summary>
    private static async Task<string> HiddenAsync(McpSession product) =>
        (await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage })).GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A scratch page, a draft, below <paramref name="parent"/>.</summary>
    private static async Task<string> CreateAsync(McpSession session, string parent) =>
        WritingTests.Id((await session.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent })).GetProperty("content"));

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

    /// <summary>A dry run that must pass: its plan token.</summary>
    private static async Task<string> DryRunAsync(McpSession session, string tool, Dictionary<string, object?> arguments)
    {
        var dry = await session.OkAsync(tool, With(arguments, dryRun: true));
        Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
        return dry.GetProperty("planToken").GetString()!;
    }

    private static string? Hint(JsonElement error) => error.TryGetProperty("hint", out var hint) ? hint.GetString() : null;

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>
    /// The site's audit lines for the steps of <paramref name="tool"/>, from its log (serve.sh's <c>mcp-site-&lt;port&gt;.log</c>),
    /// the last <paramref name="count"/> of them once one ends with <paramref name="lastContent"/> (when given): the log is
    /// written a moment after the call.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AuditAsync(string tool, int count, string? lastContent = null)
    {
        var log = Path.Combine(McpSiteSettings.SiteDirectory, "App_Data", $"mcp-site-{McpSiteSettings.Url!.Port}.log");
        var prefix = $"MCP tool {tool} step ";
        List<string> lines = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                lines = [.. (await reader.ReadToEndAsync()).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];
            }
            if (lines.Count >= count && (lastContent is null || lines.Any(l => l.Contains(lastContent, StringComparison.Ordinal))))
            {
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(lines.Count >= count, $"{log} has {lines.Count} audit lines for {tool}'s steps, not {count}.");
        return lines[^count..];
    }
}
