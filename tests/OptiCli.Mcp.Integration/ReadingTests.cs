using System.Text.Json;
using System.Text.RegularExpressions;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>Reading as the editor: content they can't read doesn't exist for them, in every tool.</summary>
[Collection(McpSiteCollection.Name)]
public sealed class ReadingTests(SharedSessions sessions)
{
    private const string MissingId = "987654";

    private const string MissingGuid = "0d9b5c7e-1f2a-4b3c-8d4e-5f6a7b8c9d0e";

    private const string MissingUrl = "/en/no-such-page-here/";

    [McpSiteFact]
    public async Task Content_the_editor_cannot_read_is_the_same_not_found_as_content_that_does_not_exist()
    {
        // The product editor may read the hidden page: what it is.
        var product = await sessions.ForAsync(TestUsers.Product);
        var hidden = await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage });
        Assert.Equal("Alloy Meet", hidden.GetProperty("name").GetString());
        var found = await product.OkAsync("find_content", new { name = "Alloy Meet" });
        Assert.Contains(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("guid").GetString() == TestUsers.HiddenPage);
        var id = hidden.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parent = hidden.GetProperty("parent").GetString()!;
        var url = hidden.GetProperty("url").GetString()!;

        var editor = await sessions.ForAsync(TestUsers.Editor);
        // Every way of naming it gives exactly the error a missing item gives.
        var same = new Comparer(editor, [(id, MissingId, "<id>"), (TestUsers.HiddenPage, MissingGuid, "<guid>"), (url, MissingUrl, "<url>")]);
        await same.ErrorAsync("get_content", id, MissingId, r => new { reference = r });
        await same.ErrorAsync("get_content", TestUsers.HiddenPage, MissingGuid, r => new { reference = r });
        await same.ErrorAsync("get_content", $"{id}_1", $"{MissingId}_1", r => new { reference = r });
        await same.ErrorAsync("list_versions", id, MissingId, r => new { reference = r });
        await same.ErrorAsync("list_children", id, MissingId, r => new { reference = r });
        await same.ErrorAsync("update_content", id, MissingId, r => new { reference = r, name = "changed", dryRun = true });
        await same.ErrorAsync("discard_draft", id, MissingId, r => new { reference = r, dryRun = true });
        await same.ErrorAsync("publish_content", id, MissingId, r => new { reference = r, requestApproval = true });
        await same.ErrorAsync("resolve_url", url, MissingUrl, r => new { url = r });
        await same.ErrorAsync("resolve_url", TestUsers.HiddenPage, MissingGuid, r => new { url = r });
        await same.ErrorAsync("find_content", id, MissingId, r => new { name = "Alloy", root = r });
        await same.ErrorAsync("create_content", id, MissingId, r => new { type = "StandardPage", name = "x", parent = r, dryRun = true });

        // Lists leave it out.
        var siblings = await editor.OkAsync("list_children", new { reference = parent, limit = 200 });
        Assert.NotEmpty(siblings.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(siblings.GetProperty("items").EnumerateArray(), i => i.GetProperty("guid").GetString() == TestUsers.HiddenPage);
        var search = await editor.OkAsync("find_content", new { name = "Alloy Meet" });
        Assert.DoesNotContain(search.GetProperty("items").EnumerateArray(), i => i.GetProperty("guid").GetString() == TestUsers.HiddenPage);
        // Its siblings are still there: Alloy Plan, say.
        var plan = await editor.OkAsync("find_content", new { name = "Alloy Plan" });
        Assert.NotEmpty(plan.GetProperty("items").EnumerateArray());
    }

    [McpSiteFact]
    public async Task Paging_through_children_counts_only_what_the_editor_can_read()
    {
        var product = await sessions.ForAsync(TestUsers.Product);
        var parent = (await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage })).GetProperty("parent").GetString()!;
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var all = (await editor.OkAsync("list_children", new { reference = parent, limit = 200 })).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("guid").GetString()).ToList();

        // One at a time: the cursor goes up by one per item the editor sees, so it tells nothing of the hidden page
        // between them.
        var paged = new List<string?>();
        int? cursor = null;
        do
        {
            var page = await editor.OkAsync("list_children", new { reference = parent, limit = 1, cursor });
            paged.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("guid").GetString()));
            int? next = page.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
            Assert.True(next is null || next == paged.Count, $"cursor {next} after {paged.Count} items");
            cursor = next;
        }
        while (cursor is not null);
        Assert.Equal(all, paged);
        Assert.DoesNotContain(TestUsers.HiddenPage, paged);
        var theirs = (await product.OkAsync("list_children", new { reference = parent, limit = 200 })).GetProperty("items").GetArrayLength();
        Assert.True(theirs > all.Count, "the product editor sees the hidden page among them");
    }

    [McpSiteFact]
    public async Task resolve_url_finds_the_content_of_a_public_path_a_full_URL_and_a_permanent_link()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = await editor.OkAsync("resolve_url", new { url = "/en/" });
        Assert.Equal("StartPage", start.GetProperty("content").GetProperty("type").GetString());
        var startId = start.GetProperty("content").GetProperty("id").GetInt32();

        var about = await editor.OkAsync("resolve_url", new { url = "/en/about-us/" });
        var aboutId = about.GetProperty("content").GetProperty("id").GetInt32();
        Assert.Equal("About us", about.GetProperty("content").GetProperty("name").GetString());
        Assert.EndsWith("/en/about-us/", about.GetProperty("url").GetString());

        var absolute = await editor.OkAsync("resolve_url", new { url = new Uri(McpSiteSettings.Url!, "en/about-us/").ToString() });
        Assert.Equal(aboutId, absolute.GetProperty("content").GetProperty("id").GetInt32());

        var read = await editor.OkAsync("get_content", new { reference = aboutId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var guid = Guid.Parse(read.GetProperty("guid").GetString()!);
        var permanent = await editor.OkAsync("resolve_url", new { url = $"/link/{guid:N}.aspx" });
        Assert.Equal(aboutId, permanent.GetProperty("content").GetProperty("id").GetInt32());
        Assert.Equal(startId.ToString(System.Globalization.CultureInfo.InvariantCulture), read.GetProperty("parent").GetString());
    }

    /// <summary>
    /// Compares the error for hidden content with the one for missing content, word for word once each id, GUID and URL
    /// is replaced by a placeholder: any other difference would tell the editor the hidden content exists.
    /// </summary>
    private sealed class Comparer(McpSession session, (string Hidden, string Missing, string Placeholder)[] tokens)
    {
        public async Task ErrorAsync(string tool, string hidden, string missing, Func<string, object> arguments)
        {
            var forHidden = await session.CallAsync(tool, arguments(hidden));
            var forMissing = await session.CallAsync(tool, arguments(missing));
            Assert.True(forHidden.IsError, $"{tool} {hidden} should fail for {session.User}: {forHidden.Text}");
            Assert.True(forMissing.IsError, $"{tool} {missing} should fail: {forMissing.Text}");
            Assert.Equal("not_found", forHidden.Json.GetProperty("code").GetString());
            Assert.Equal(Normalize(forMissing.Text, t => t.Missing), Normalize(forHidden.Text, t => t.Hidden));
        }

        private string Normalize(string text, Func<(string Hidden, string Missing, string Placeholder), string> pick) =>
            tokens.Aggregate(text, (current, token) =>
                Regex.Replace(current, $@"(?<![\w-]){Regex.Escape(pick(token))}(?![\w-])", token.Placeholder));
    }
}
