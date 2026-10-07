using System.Globalization;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Writes;
using OptiCli.Integration.Comparison;
using OptiCli.Integration.Sampling;

namespace OptiCli.Integration;

/// <summary>
/// CMS 13's Visual Builder writes on a site with tests/fixtures/cms13/VisualBuilderFixture.cs: an experience built and
/// changed through the agent, node by node and whole, read back from the database the way <c>get</c> reads it, compared with
/// what the CMS loads, and rendered by the site. Everything it makes goes to the recycle bin at the end. On CMS 12 the
/// writes are refused.
/// </summary>
public sealed class VisualBuilderWriteTests
{
    private static readonly Guid Experience = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b01");

    private static readonly Guid SharedElement = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02");

    /// <summary>The fixture experience's parent (where experiences may go) and the shared element; null without the fixture or on CMS 12.</summary>
    private static async Task<(string Parent, string Shared)?> FixtureAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        if (!site.Session.Model.Schema.Compositions)
        {
            return null;
        }
        var ids = await ContentHeaderReader.IdsByGuidsAsync(site.Session.Db, [Experience, SharedElement], cancellationToken);
        if (ids.Count != 2)
        {
            return null;
        }
        var header = await site.Session.HeaderAsync(ids[Experience], cancellationToken);
        return (Id(header.ParentId!.Value), Id(ids[SharedElement]));
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The content as <c>get</c> reads it, from a session of its own (a write changes what the first one cached).</summary>
    private static async Task<ContentDocument> GetAsync(int id, VersionSelector? version = null, CancellationToken cancellationToken = default)
    {
        await using var fresh = await SiteUnderTest.ConnectAsync(cancellationToken);
        return await new ContentLoader(fresh.Session.Db, fresh.Session.Identities).GetAsync(id, version ?? new VersionSelector(VersionKind.Latest), null, new DecodeOptions(Full: true, Composition: true), cancellationToken);
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? array) => (array as JsonArray ?? []).OfType<JsonObject>();

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    private static IEnumerable<JsonObject> Elements(JsonObject section) =>
        Objects(section["rows"]).SelectMany(r => Objects(r["columns"])).SelectMany(c => Objects(c["elements"]));

    /// <summary>Every node by name, all the way down.</summary>
    private static Dictionary<string, JsonObject> Named(JsonObject composition)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        void Walk(JsonObject node)
        {
            foreach (var list in new[] { "sections", "rows", "columns", "elements", "nodes" })
            {
                foreach (var child in Objects(node[list]))
                {
                    if (Text(child["name"]) is { } name)
                    {
                        result[name] = child;
                    }
                    Walk(child);
                }
            }
        }
        Walk(composition);
        return result;
    }

    private static CompositionEdit Edit(string id, string action, string? node = null, string? nodeType = null, string? parent = null, int? at = null, string? value = null) =>
        new(id, action, node, nodeType, parent, at, value is null ? null : JsonNode.Parse(value)!.AsObject());

    [SiteFact]
    public async Task An_experience_is_built_and_changed_node_by_node_and_whole_and_reads_back_as_the_cms_loads_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var name = $"VB write test {Guid.NewGuid():N}"[..26];
        var properties = JsonNode.Parse($$$"""
            {"Summary": "Made by the write test", "composition": {"sections": [
              {"type": "VbSection", "name": "Top", "displayTemplate": "vbSection", "displaySettings": {"background": "light"}, "rows": [
                {"name": "Row", "columns": [
                  {"name": "A", "elements": [{"type": "VbTextElement", "name": "Hello", "properties": {"Heading": "Hello from the test", "Body": "<p>Inline <strong>rich</strong> text.</p>"}}]},
                  {"name": "B", "elements": [{"ref": "{{{fixture.Shared}}}", "name": "Shared one"}, {"type": "VbLinkElement", "name": "Go", "properties": {"Heading": "Go", "Target": "{{{fixture.Parent}}}", "Link": "https://example.com/vb"}}]}]}]},
              {"type": "VbBanner", "name": "Banner", "properties": {"Title": "Banner from the test"}}]}}
            """)!.AsObject();

        var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(new CreateOperation(fixture.Parent, "VbExperience", name, properties), dryRun: true, cancellationToken)).Output);
        Assert.Equal(["added section Top", "added component Banner"], dry.Composition!.Select(c => $"{c.Change} {c.NodeType} {c.Name}"));
        Assert.Equal(6, dry.Composition![0].Holds);

        var created = await writes.RunAsync(new CreateOperation(fixture.Parent, "VbExperience", name, properties), dryRun: false, cancellationToken);
        var id = Id(created.CreatedId!.Value);
        try
        {
            var document = await GetAsync(created.CreatedId!.Value, cancellationToken: cancellationToken);
            var composition = document.Composition!;
            var top = Objects(composition["sections"]).First();
            Assert.Equal(("Top", "vbSection", "light"), (Text(top["name"]), Text(top["displayTemplate"]), Text(top["displaySettings"]!["background"])));
            Assert.Equal(["Hello", "Shared one", "Go"], Elements(top).Select(e => Text(e["name"])));
            Assert.Equal(fixture.Shared, Text(Elements(top).ElementAt(1)["content"]!["ref"]));
            Assert.Equal(fixture.Parent, Text(Elements(top).ElementAt(2)["properties"]!["Target"]!["value"]!["ref"]));
            Assert.Equal("component", Text(Objects(composition["sections"]).Last()["nodeType"]));

            // Node by node: add (by the column's name), set values and styles, move, remove.
            var added = Assert.IsType<WriteOutput>((await writes.RunAsync(
                Edit(id, "add", nodeType: "element", parent: "A", value: """{"type": "VbTextElement", "name": "Added", "properties": {"Heading": "Added"}}"""), false, cancellationToken)).Output);
            var addedKey = Assert.Single(added.Composition!).Key;
            await writes.RunAsync(Edit(id, "set", node: "Hello", value: """{"properties": {"Heading": "Hello again"}, "displayTemplate": "vbElement", "displaySettings": {"color": "accent"}}"""), false, cancellationToken);
            var moved = Assert.IsType<WriteOutput>((await writes.RunAsync(Edit(id, "move", node: addedKey, parent: "B", at: 0), false, cancellationToken)).Output);
            Assert.Equal(("moved", 0), (Assert.Single(moved.Composition!).Change, moved.Composition![0].At));
            await writes.RunAsync(Edit(id, "remove", node: "Banner"), false, cancellationToken);

            document = await GetAsync(created.CreatedId!.Value, cancellationToken: cancellationToken);
            var nodes = Named(document.Composition!);
            Assert.False(nodes.ContainsKey("Banner"));
            Assert.Equal(("Hello again", "vbElement", "accent"), (Text(nodes["Hello"]["properties"]!["Heading"]!["value"]), Text(nodes["Hello"]["displayTemplate"]), Text(nodes["Hello"]["displaySettings"]!["color"])));
            Assert.Equal(["Added", "Shared one", "Go"], Objects(nodes["B"]["elements"]).Select(e => Text(e["name"])));
            Assert.Equal(addedKey, Text(nodes["Added"]["key"]));

            // Refused before anything is saved: a template the site doesn't have, an element in an outline, a name two nodes have.
            Assert.Contains("no display template 'nope'", (await Assert.ThrowsAnyAsync<OptiCliException>(() =>
                writes.RunAsync(Edit(id, "set", node: "Hello", value: """{"displayTemplate": "nope"}"""), true, cancellationToken))).Message);
            Assert.Contains("can't stand in an experience's outline", (await Assert.ThrowsAnyAsync<OptiCliException>(() =>
                writes.RunAsync(Edit(id, "add", value: """{"type": "VbTextElement"}"""), true, cancellationToken))).Message);
            await writes.RunAsync(Edit(id, "add", nodeType: "element", parent: "A", value: """{"type": "VbTextElement", "name": "Go"}"""), false, cancellationToken);
            Assert.Contains("2 nodes are named 'Go'", (await Assert.ThrowsAnyAsync<OptiCliException>(() =>
                writes.RunAsync(Edit(id, "remove", node: "Go"), true, cancellationToken))).Message);

            // The whole composition, as get shows it: written back unchanged it changes nothing; reordered and renamed it does.
            document = await GetAsync(created.CreatedId!.Value, cancellationToken: cancellationToken);
            var whole = new JsonObject { ["composition"] = document.Composition!.DeepClone() };
            var same = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(id, whole), dryRun: true, cancellationToken)).Output);
            Assert.Null(same.Composition);
            var columnB = Named(whole["composition"]!.AsObject())["B"];
            var elements = columnB["elements"]!.AsArray();
            var first = elements[0]!;
            elements.RemoveAt(0);
            elements.Add(first);
            columnB["name"] = "Column B";
            var changed = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(id, whole), dryRun: false, cancellationToken)).Output);
            Assert.Contains(changed.Composition!, c => c.Change == "changed" && c.Name == "Column B");
            Assert.Contains(changed.Composition!, c => c.Change == "moved");
            nodes = Named((await GetAsync(created.CreatedId!.Value, cancellationToken: cancellationToken)).Composition!);
            Assert.Equal(["Shared one", "Go", "Added"], Objects(nodes["Column B"]["elements"]).Select(e => Text(e["name"])));

            // A section from the fixture's section blueprint, with its rows.
            var blueprint = (await site.Session.Db.QueryAsync("SELECT TOP 1 ContentGUID FROM tblContent WHERE Blueprint = 1 AND Deleted = 0 AND fkContentTypeID = @type",
                r => r.GetGuid(0), cancellationToken, new Microsoft.Data.SqlClient.SqlParameter("@type", site.Session.Model.RequireType("VbSection").Id))).Single();
            await writes.RunAsync(Edit(id, "add", nodeType: "section", value: $$"""{"blueprint": "{{blueprint}}", "name": "Copied section"}"""), false, cancellationToken);

            // Published, the site renders it; the CMS loads what the database read shows.
            await writes.RunAsync(new PublishOperation(id), dryRun: false, cancellationToken);
            document = await GetAsync(created.CreatedId!.Value, VersionSelector.Published, cancellationToken);
            Assert.Contains("Copied section", Named(document.Composition!).Keys);
            // From a session of its own: the first one has the content as it was before the writes.
            await using (var fresh = await SiteUnderTest.ConnectAsync(cancellationToken))
            {
                var result = await new ItemComparer(fresh).CompareAsync(
                    new SampleItem(created.CreatedId!.Value, fresh.Session.Model.RequireType("VbExperience").Id, ContentKind.Experience, fresh.Session.Language(document.Language)!.Id), cancellationToken);
                Assert.True(result.Mismatches.Count == 0, string.Join('\n', result.Mismatches.Select(m => $"{m.Field} {m.Path}: db {m.Db} / cms {m.Agent}")));
            }
            if (site.SiteUrl is { } url && document.Url is { } path)
            {
                using var http = new HttpClient { BaseAddress = url };
                var page = await http.GetStringAsync(path, cancellationToken);
                Assert.Contains("Hello again", page);
                Assert.Contains("From a blueprint", page);
                Assert.DoesNotContain("Banner from the test", page);
            }

            // A content variation: its own versions; the content itself and what visitors see stay as they are.
            var variation = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(id, new JsonObject { ["Summary"] = "In the variation" }) { Variation = "vbWriteTest" }, dryRun: false, cancellationToken)).Output);
            Assert.True(variation.Saved);
            // Its only version can be discarded: the content keeps its own (compared with its published version).
            var discard = Assert.IsType<WriteOutput>((await writes.RunAsync(new DiscardOperation(variation.Version!), dryRun: true, cancellationToken)).Output);
            Assert.Equal(document.Version, discard.BaseVersion);
            await writes.RunAsync(Edit(id, "set", node: "Hello", value: """{"properties": {"Heading": "Hello in the variation"}}""") with { Variation = "vbWriteTest", Publish = true }, false, cancellationToken);
            var own = await GetAsync(created.CreatedId!.Value, VersionSelector.Published, cancellationToken);
            Assert.Equal("Made by the write test", Text(own.Properties["Summary"]!["value"]));
            Assert.Equal("Hello again", Text(Named(own.Composition!)["Hello"]["properties"]!["Heading"]!["value"]));
            var varied = await GetAsync(created.CreatedId!.Value, VersionSelector.Published with { Variation = "vbWriteTest" }, cancellationToken);
            Assert.Equal(("vbWriteTest", "In the variation"), (varied.Variation, Text(varied.Properties["Summary"]!["value"])));
            Assert.Equal("Hello in the variation", Text(Named(varied.Composition!)["Hello"]["properties"]!["Heading"]!["value"]));
            // A change of the content itself is based on its own latest version, not the variation's.
            var next = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(id, new JsonObject { ["Summary"] = "Own again" }), dryRun: true, cancellationToken)).Output);
            Assert.Equal(own.Version, next.BaseVersion);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(id, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task An_experience_made_from_a_blueprint_has_its_values_and_composition()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }
        var blueprint = (await site.Session.Db.QueryAsync("SELECT TOP 1 pkID FROM tblContent WHERE Blueprint = 1 AND Deleted = 0 AND fkContentTypeID = @type ORDER BY pkID",
            r => r.GetInt32(0), cancellationToken, new Microsoft.Data.SqlClient.SqlParameter("@type", site.Session.Model.RequireType("VbExperience").Id))).Single();
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var create = new CreateOperation(fixture.Parent, "", "From the blueprint test") { Blueprint = Id(blueprint) };

        var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(create, dryRun: true, cancellationToken)).Output);
        Assert.Equal("VbExperience", dry.Type);
        Assert.NotEmpty(dry.Composition!);

        var created = await writes.RunAsync(create, dryRun: false, cancellationToken);
        try
        {
            var copy = await GetAsync(created.CreatedId!.Value, cancellationToken: cancellationToken);
            var source = await GetAsync(blueprint, VersionSelector.Published, cancellationToken);
            Assert.Equal(Text(source.Properties["Summary"]!["value"]), Text(copy.Properties["Summary"]!["value"]));
            Assert.Equal(Named(source.Composition!).Keys.Order(), Named(copy.Composition!).Keys.Order());
            Assert.Null(copy.Blueprint);
            Assert.Contains("is VbExperience, not StandardPage", (await Assert.ThrowsAnyAsync<OptiCliException>(() =>
                writes.RunAsync(new CreateOperation(fixture.Parent, "StandardPage", "x") { Blueprint = Id(blueprint) }, dryRun: true, cancellationToken))).Message);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(Id(created.CreatedId!.Value), IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task On_cms_12_compositions_and_variations_are_refused_before_the_site_is_asked()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (site.Session.Model.Schema.Compositions)
        {
            // CMS 13: the refusals here are CMS 12's; the CMS 13 writes are tested above.
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var start = Id((await site.Session.Db.QueryAsync("SELECT TOP 1 pkID FROM tblContent WHERE ContentType = 0 AND Deleted = 0 AND pkID > 4 ORDER BY pkID",
            r => r.GetInt32(0), cancellationToken)).Single());

        var composition = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(Edit(start, "add", value: """{"type": "TeaserBlock"}"""), true, cancellationToken));
        Assert.Contains("has no Visual Builder composition", composition.Message);
        var variation = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(new SetOperation(start, new JsonObject { ["MetaTitle"] = "x" }) { Variation = "campaign" }, true, cancellationToken));
        Assert.Contains("Content variations are CMS 13's", variation.Message);
        var blueprint = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(new CreateOperation(start, "", "x") { Blueprint = "Landing" }, true, cancellationToken));
        Assert.Contains("blueprints are CMS 13's", blueprint.Message);
    }
}
