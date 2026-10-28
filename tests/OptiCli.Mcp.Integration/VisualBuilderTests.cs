using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// CMS 13's Visual Builder as an editor reads it (tests/fixtures/mcp/McpVisualBuilderFixture.cs): a composition follows
/// the rules of every other read. Shared content the editor can't read is only its place, and the edit UI's hidden and
/// locked properties of an element are left out, in every tool that shows a composition or a change to one.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class VisualBuilderTests(SharedSessions sessions)
{
    private const string Experience = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b301";
    private const string SharedSection = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b302";
    private const string HiddenFolder = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b303";
    private const string HiddenElement = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b304";
    private const string HiddenSection = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b305";
    private const string SharedBanner = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b306";
    private const string HiddenBanner = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b307";
    private const string FieldsExperience = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b308";
    private const string FieldsSection = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b309";

    /// <summary>The CMS 13 fixture's shared element (tests/fixtures/cms13, VisualBuilderFixture.SharedElement).</summary>
    private const string SharedElement = "7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02";

    private const string FieldsElementKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c004";

    private const string MissingId = "987654";

    /// <summary>
    /// Text of the hidden shared content and of the names the composition gives it, which an editor must never see: the
    /// content's names and values, and the composition's names for it ("Hidden banner", ...).
    /// </summary>
    private static readonly string[] HiddenTexts =
    [
        "MCP VB hidden", "Hidden heading", "Only administrators", "Hidden section text", "Hidden banner title",
        "Hidden element", "Hidden banner", "Hidden section",
    ];

    /// <summary>The values of McpVbFieldsElement's properties the edit UI hides or locks for an editor, in any version.</summary>
    private static readonly string[] HiddenValues = ["Hidden value", "Locked value", "Admin scripts value", "Admin tab value"];

    [McpCms13SiteFact]
    public async Task Shared_content_an_editor_cannot_read_is_only_its_place_in_a_composition()
    {
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var hidden = await IdsAsync(admin, HiddenElement, HiddenBanner, HiddenSection);
        var readable = await IdsAsync(admin, SharedElement, SharedBanner, SharedSection);

        // The administrator reads all of it: the content is there, by reference, with what the editor mustn't see.
        var full = (await admin.OkAsync("get_content", new { reference = Experience })).GetProperty("composition");
        var shownToAdmin = Nodes(full).ToList();
        Assert.All(hidden, id => Assert.Contains(shownToAdmin, n => Ref(n.Node) == id && n.Node.TryGetProperty("type", out _)));
        Assert.Contains("Hidden section text", full.GetRawText());
        Assert.Contains("Hidden banner", full.GetRawText());

        foreach (var user in new[] { TestUsers.Editor, TestUsers.Product })
        {
            var session = await sessions.ForAsync(user);
            var published = await session.OkAsync("get_content", new { reference = Experience });
            var version = published.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture);
            foreach (var read in new object[] { new { reference = Experience }, new { reference = Experience, version = "latest" }, new { reference = Experience, version },
                new { reference = $"{published.GetProperty("id").GetInt32()}_{version}" }, new { reference = Experience, lang = "en" } })
            {
                var composition = (await session.OkAsync("get_content", read)).GetProperty("composition");
                var nodes = Nodes(composition).ToList();
                AssertNothingHidden(composition, user);
                foreach (var id in hidden)
                {
                    var (node, parent) = Assert.Single(nodes, n => Ref(n.Node) == id);
                    var place = parent == "experience" ? "section" : "component";
                    var fields = node.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                    Assert.Equal(place, node.GetProperty("nodeType").GetString());
                    // A component in a column keeps the composition's own display template and settings (this one has none);
                    // nothing else is shown.
                    Assert.Equal(["key", "nodeType", "ref"], fields);
                }
                // What the editor can read is shown: names, types and a shared section's own nodes.
                foreach (var id in readable)
                {
                    var (node, _) = Assert.Single(nodes, n => Ref(n.Node) == id);
                    Assert.True(node.TryGetProperty("name", out _) && node.TryGetProperty("type", out _), $"{user}: {node}");
                }
                Assert.Contains("In a shared section", composition.GetRawText());
            }
        }
    }

    [McpCms13SiteFact]
    public async Task An_elements_properties_the_edit_ui_hides_or_locks_are_left_out_for_an_editor_wherever_it_is()
    {
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var forAdmin = (await admin.OkAsync("get_content", new { reference = FieldsExperience })).GetProperty("composition");
        var adminFields = Fields(forAdmin, FieldsElementKey);
        // The administrator sees what an editor descriptor locks for others and the administrators' tab.
        Assert.Equal("Admin scripts value", adminFields.GetProperty("AdminScripts").GetProperty("value").GetString());
        Assert.Equal("Admin tab value", adminFields.GetProperty("AdminTab").GetProperty("value").GetString());

        var editor = await sessions.ForAsync(TestUsers.Editor);
        foreach (var read in new object[] { new { reference = FieldsExperience }, new { reference = FieldsExperience, version = "latest" } })
        {
            var composition = (await editor.OkAsync("get_content", read)).GetProperty("composition");
            AssertNothingHidden(composition, TestUsers.Editor);
            // The inline element in an inline section: Heading, without what the edit UI hides or locks.
            var fields = Fields(composition, FieldsElementKey);
            Assert.Equal(["Heading"], fields.EnumerateObject().Select(p => p.Name));
            // The element inside the shared section, by that section's access rights.
            var shared = Nodes(composition).Single(n => n.Node.TryGetProperty("name", out var name) && name.GetString() == "Shared fields").Node.GetProperty("properties");
            Assert.Equal(["Heading"], shared.EnumerateObject().Select(p => p.Name));
        }

        // The shared section read on its own.
        var section = (await editor.OkAsync("get_content", new { reference = FieldsSection })).GetProperty("composition");
        AssertNothingHidden(section, TestUsers.Editor);
        var inSection = Nodes(section).Single(n => n.Node.TryGetProperty("properties", out _)).Node.GetProperty("properties");
        Assert.Equal(["Heading"], inSection.EnumerateObject().Select(p => p.Name));
    }

    [McpCms13SiteFact]
    public async Task A_change_to_a_composition_shows_an_editor_only_what_a_read_shows_them()
    {
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var latest = await editor.OkAsync("get_content", new { reference = FieldsExperience, version = "latest" });
        Assert.Equal("checkedOut", latest.GetProperty("status").GetString());
        var draft = latest.GetProperty("version").GetInt32();

        // The administrator's view of the draft's changes: the hidden shared content and the values behind the admin tab.
        var forAdmin = await admin.OkAsync("discard_draft", new { reference = FieldsExperience, dryRun = true });
        var adminChange = Assert.Single(forAdmin.GetProperty("changes").EnumerateArray(), c => c.GetProperty("property").GetString() == "composition");
        Assert.Contains("Hidden banner", adminChange.GetRawText());
        Assert.Contains("Admin tab value, draft", adminChange.GetRawText());

        // The editor: a discard's dry run, a publish's dry run (the draft by nobody they'd put live), and the publish refused for it.
        var discard = await editor.OkAsync("discard_draft", new { reference = FieldsExperience, dryRun = true });
        var publish = await editor.OkAsync("update_content", new { reference = FieldsExperience, baseVersion = draft, properties = new { Summary = "x" }, publish = true, dryRun = true });
        var refused = await editor.ErrorAsync("publish_content", new { reference = FieldsExperience });
        Assert.Equal("conflict", refused.GetProperty("code").GetString());
        foreach (var changes in new[]
        {
            discard.GetProperty("changes"), discard.GetProperty("pendingDraft").GetProperty("changes"),
            publish.GetProperty("pendingDraft").GetProperty("changes"), refused.GetProperty("pendingDraft").GetProperty("changes"),
        })
        {
            var change = Assert.Single(changes.EnumerateArray(), c => c.GetProperty("property").GetString() == "composition");
            // What the editor may see of the change is there: the inline element's new heading, the hidden content's places.
            Assert.Equal("Fields heading", Fields(change.GetProperty("before"), FieldsElementKey).GetProperty("Heading").GetString());
            Assert.Equal("Fields heading, draft", Fields(change.GetProperty("after"), FieldsElementKey).GetProperty("Heading").GetString());
            AssertNothingHidden(change, TestUsers.Editor);
            var after = Nodes(change.GetProperty("after")).ToList();
            Assert.Equal(3, after.Count(n => n.Node.TryGetProperty("ref", out var r) && !n.Node.TryGetProperty("name", out _)));
            Assert.All(after.Where(n => n.Node.TryGetProperty("ref", out _) && !n.Node.TryGetProperty("name", out _)), n =>
                Assert.Equal(n.Parent == "column" ? ["displaySettings", "displayTemplate", "key", "nodeType", "ref"] : ["key", "nodeType", "ref"],
                    n.Node.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        }
        // The refusal's message names what changed, not more.
        Assert.DoesNotContain("Hidden", refused.GetProperty("message").GetString());

        // A change of the experience's own property: no composition in it.
        var own = await editor.OkAsync("update_content", new { reference = FieldsExperience, baseVersion = draft, properties = new { Summary = "x" }, dryRun = true });
        Assert.Equal(["Summary"], own.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("property").GetString()));

        // The versions, children and search: nothing of the hidden content.
        var versions = await editor.OkAsync("list_versions", new { reference = FieldsExperience });
        AssertNothingHidden(versions, TestUsers.Editor);
    }

    [McpCms13SiteFact]
    public async Task Shared_content_an_editor_cannot_read_is_the_same_not_found_in_every_tool()
    {
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var hidden = await IdsAsync(admin, HiddenElement, HiddenBanner, HiddenSection, HiddenFolder);
        var folder = await admin.OkAsync("get_content", new { reference = HiddenFolder });
        var assets = folder.GetProperty("parent").GetString()!;
        var site = McpSiteSettings.Url!.ToString().TrimEnd('/');

        foreach (var user in new[] { TestUsers.Editor, TestUsers.Product })
        {
            var session = await sessions.ForAsync(user);
            foreach (var (id, guid) in hidden.Zip(new[] { HiddenElement, HiddenBanner, HiddenSection, HiddenFolder }))
            {
                await AssertNotFoundAsync(session, "get_content", id, r => new { reference = r });
                await AssertNotFoundAsync(session, "get_content", guid, r => new { reference = r });
                await AssertNotFoundAsync(session, "get_content", id, r => new { reference = r, version = "latest" });
                await AssertNotFoundAsync(session, "list_versions", id, r => new { reference = r });
                await AssertNotFoundAsync(session, "list_children", id, r => new { reference = r });
                await AssertNotFoundAsync(session, "find_content", id, r => new { name = "a", root = r });
                await AssertNotFoundAsync(session, "resolve_url", id, r => new { url = $"{site}/{McpSiteSettings.EditUiPath}?language=en#context=epi.cms.contentdata:///{r}" });
                await AssertNotFoundAsync(session, "view_media", id, r => new { reference = r });
            }
            // Lists and searches leave it out.
            var children = await session.OkAsync("list_children", new { reference = assets, limit = 200 });
            AssertNothingHidden(children, user);
            Assert.DoesNotContain(children.GetProperty("items").EnumerateArray(), i => i.GetProperty("guid").GetString() == HiddenFolder);
            foreach (var name in new[] { "MCP VB hidden", "hidden" })
            {
                var found = await session.OkAsync("find_content", new { name });
                Assert.DoesNotContain(found.GetProperty("items").EnumerateArray(), i => hidden.Contains(i.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture)));
            }
            var byType = await session.OkAsync("find_content", new { name = "MCP VB", type = "VbSection" });
            AssertNothingHidden(byType, user);
        }
    }

    /// <summary>Every node of a composition (a read's or a change's), with its parent's node type (null at the root, "experience" below an experience's).</summary>
    private static IEnumerable<(JsonElement Node, string? Parent)> Nodes(JsonElement composition)
    {
        IEnumerable<(JsonElement, string?)> Walk(JsonElement node, string? parent, string? own)
        {
            yield return (node, parent);
            if (node.TryGetProperty("nodes", out var nodes))
            {
                foreach (var child in nodes.EnumerateArray())
                {
                    foreach (var item in Walk(child, own, child.TryGetProperty("nodeType", out var type) ? type.GetString() : null))
                    {
                        yield return item;
                    }
                }
            }
        }
        // A change's root has no node type: these are experiences.
        return Walk(composition, null, composition.TryGetProperty("nodeType", out var root) ? root.GetString() : "experience");
    }

    /// <summary>The properties of the node with <paramref name="key"/>: a read's (name to type and value) or a change's (name to value).</summary>
    private static JsonElement Fields(JsonElement composition, string key) =>
        Nodes(composition).Single(n => n.Node.TryGetProperty("key", out var k) && k.GetString() == key).Node.GetProperty("properties");

    private static string? Ref(JsonElement node) => node.TryGetProperty("ref", out var reference) ? reference.GetString() : null;

    private static void AssertNothingHidden(JsonElement json, string user)
    {
        var text = json.GetRawText();
        foreach (var hidden in HiddenTexts.Concat(HiddenValues))
        {
            Assert.False(text.Contains(hidden, StringComparison.OrdinalIgnoreCase), $"{user} was shown '{hidden}': {text}");
        }
    }

    private static async Task<List<string>> IdsAsync(McpSession admin, params string[] guids)
    {
        var ids = new List<string>();
        foreach (var guid in guids)
        {
            ids.Add((await admin.OkAsync("get_content", new { reference = guid })).GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture));
        }
        return ids;
    }

    /// <summary>The tool fails for <paramref name="hidden"/> exactly as for content that doesn't exist (its id or GUID swapped in).</summary>
    private static async Task AssertNotFoundAsync(McpSession session, string tool, string hidden, Func<string, object> arguments)
    {
        var missing = Guid.TryParse(hidden, out _) ? "0d9b5c7e-1f2a-4b3c-8d4e-5f6a7b8c9d0e" : MissingId;
        var forHidden = await session.CallAsync(tool, arguments(hidden));
        var forMissing = await session.CallAsync(tool, arguments(missing));
        Assert.True(forHidden.IsError, $"{tool} {hidden} should fail for {session.User}: {forHidden.Text}");
        Assert.Equal("not_found", forHidden.Json.GetProperty("code").GetString());
        string Normalize(string text, string token) => Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(token)}(?![\w-])", "<ref>");
        Assert.Equal(Normalize(forMissing.Text, missing), Normalize(forHidden.Text, hidden));
    }
}

/// <summary>A fact that needs the MCP test site on CMS 13, whose Visual Builder content it reads; skipped on CMS 12, which has none.</summary>
public sealed class McpCms13SiteFactAttribute : FactAttribute
{
    public McpCms13SiteFactAttribute()
    {
        if (McpSiteSettings.Url is null || McpSiteSettings.UsersFile is null)
        {
            Skip = $"Needs the MCP test site: build it with tests/fixtures/mcp/setup.sh, then set {McpSiteSettings.UrlVariable} and {McpSiteSettings.UsersVariable}.";
        }
        else if (McpSiteSettings.CmsMajor < 13)
        {
            Skip = $"Visual Builder is CMS 13's; the MCP test site runs CMS {McpSiteSettings.CmsMajor}.";
        }
    }
}
