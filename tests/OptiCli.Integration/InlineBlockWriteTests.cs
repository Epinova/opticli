using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Writes;
using OptiCli.Integration.Comparison;
using OptiCli.Integration.Sampling;

namespace OptiCli.Integration;

/// <summary>
/// Inline blocks in a ContentArea (CMS 12.20+ and CMS 13), on an Alloy site (a StandardPage's MainContentArea, Alloy's
/// TeaserBlock): added, changed by position, written back whole, moved and removed through the agent, read back from the
/// database the way <c>get</c> reads them, compared with what the CMS loads, and rendered by the site. The page it makes
/// goes to the recycle bin at the end. On a CMS before 12.20 the write is refused with the version it needs.
/// </summary>
public sealed class InlineBlockWriteTests
{
    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The first content of a type that isn't deleted; null when the site has none (or no such type).</summary>
    private static async Task<int?> FirstAsync(SiteUnderTest site, string type, CancellationToken cancellationToken)
    {
        if (site.Session.Model.Types.FirstOrDefault(t => t.Name == type) is not { } found)
        {
            return null;
        }
        var ids = await site.Session.Db.QueryAsync("SELECT TOP 1 pkID FROM tblContent WHERE fkContentTypeID = @type AND Deleted = 0 ORDER BY pkID",
            r => r.GetInt32(0), cancellationToken, new SqlParameter("@type", found.Id));
        return ids.Count == 0 ? null : ids[0];
    }

    /// <summary>The content as <c>get</c> reads it, from a session of its own (a write changes what the first one cached).</summary>
    private static async Task<ContentDocument> GetAsync(int id, VersionSelector? version = null, CancellationToken cancellationToken = default)
    {
        await using var fresh = await SiteUnderTest.ConnectAsync(cancellationToken);
        return await new ContentLoader(fresh.Session.Db, fresh.Session.Identities).GetAsync(id, version ?? new VersionSelector(VersionKind.Latest), null, new DecodeOptions(Full: true), cancellationToken);
    }

    private static JsonArray Area(ContentDocument document) => document.Properties["MainContentArea"]!["value"]!.AsArray();

    private static string? Value(JsonNode? item, string property) => item?["properties"]?[property]?["value"]?.GetValue<string>();

    /// <summary>The area as <c>set --values</c> gets it from what <c>get</c> printed (its <c>{type, value}</c> unwrapped).</summary>
    private static JsonObject AsValues(JsonArray area) =>
        GetShape.UnwrapAll(new JsonObject { ["MainContentArea"] = new JsonObject { ["type"] = "ContentArea", ["value"] = area.DeepClone() } });

    private static AreaEdit AddInline(string page, string values, string? name = null, int? at = null) =>
        new(page, "MainContentArea", "add", At: at) { Type = "TeaserBlock", Values = JsonNode.Parse(values)!.AsObject(), Name = name };

    [SiteFact]
    public async Task An_inline_block_is_added_changed_written_back_moved_and_removed_and_reads_back_as_the_cms_loads_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var image = await FirstAsync(site, "ImageFile", cancellationToken);
        if (start is null || image is null || site.Session.Model.Types.All(t => t.Name is not ("TeaserBlock" or "StandardPage")))
        {
            // Not an Alloy site.
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var cms = Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]);
        var created = await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline test {Guid.NewGuid():N}"[..24]), dryRun: false, cancellationToken);
        var id = created.CreatedId!.Value;
        var page = Id(id);
        var teaser = $$"""{"Heading": "A", "Text": "First", "Image": "{{image}}"}""";
        try
        {
            if (cms < new Version(12, 20))
            {
                var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(AddInline(page, teaser), dryRun: true, cancellationToken));
                Assert.Contains("Inline blocks in a ContentArea need CMS 12.20 or later", refused.Message);
                return;
            }

            // The CMS validates the new block as it validates one the edit UI makes: Image is required on Alloy's teaser.
            var invalid = await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(AddInline(page, """{"Heading": "A"}"""), dryRun: true, cancellationToken));
            Assert.Contains("Image", invalid.Message);

            var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(AddInline(page, teaser, name: "Intro"), dryRun: true, cancellationToken)).Output);
            Assert.Equal("""[{"inline":true,"type":"TeaserBlock","name":"Intro","properties":{"Heading":"A","Text":"First","Image":"IMAGE"}}]""".Replace("IMAGE", Id(image.Value)),
                Assert.Single(dry.Changes).After!.Value.GetRawText());
            await writes.RunAsync(AddInline(page, teaser, name: "Intro"), dryRun: false, cancellationToken);

            var document = await GetAsync(id, cancellationToken: cancellationToken);
            var item = Assert.Single(Area(document));
            Assert.Equal((true, "TeaserBlock", "Intro", "A", Id(image.Value)), (item!["inline"]!.GetValue<bool>(), item["type"]!.GetValue<string>(), item["name"]!.GetValue<string>(),
                Value(item, "Heading"), item["properties"]!["Image"]!["value"]!["ref"]!.GetValue<string>()));
            // Stored as the CMS stores one the edit UI made: the item's markup names the type, the CMS indexes the block.
            var version = int.Parse(document.Version!.Split('_')[1], CultureInfo.InvariantCulture);
            var usages = await site.Session.Db.QueryAsync("SELECT fkContentTypeID FROM tblInlineBlockUsage WHERE fkWorkContentID = @version",
                r => r.GetInt32(0), cancellationToken, new SqlParameter("@version", version));
            Assert.Equal([site.Session.Model.RequireType("TeaserBlock").Id], usages);

            // Its values by position; then two more of the type, and one at the start.
            await writes.RunAsync(new SetOperation(page, JsonNode.Parse("""{"MainContentArea[0]": {"Heading": "A changed"}}""")!.AsObject()), dryRun: false, cancellationToken);
            await writes.RunAsync(AddInline(page, $$"""{"Heading": "B", "Text": "Second", "Image": "{{image}}", "Link": "{{start}}"}"""), dryRun: false, cancellationToken);
            await writes.RunAsync(AddInline(page, $$"""{"Heading": "C", "Text": "Third", "Image": "{{image}}"}"""), dryRun: false, cancellationToken);
            await writes.RunAsync(AddInline(page, $$"""{"Heading": "Top", "Text": "Zero", "Image": "{{image}}"}""", at: 0), dryRun: false, cancellationToken);
            document = await GetAsync(id, cancellationToken: cancellationToken);
            Assert.Equal(["Top", "A changed", "B", "C"], Area(document).Select(i => Value(i, "Heading")));
            Assert.Equal("Intro", Area(document)[1]!["name"]!.GetValue<string>());
            // Its name (and values) by position, the rest of it staying; "" removes the name.
            await writes.RunAsync(new AreaEdit(page, "MainContentArea", "set", Index: 1) { Name = "Renamed", Values = new JsonObject { ["Text"] = "First again" } }, dryRun: false, cancellationToken);
            var renamed = Area(await GetAsync(id, cancellationToken: cancellationToken))[1];
            Assert.Equal(("Renamed", "A changed", "First again"), (renamed!["name"]!.GetValue<string>(), Value(renamed, "Heading"), Value(renamed, "Text")));
            await writes.RunAsync(new AreaEdit(page, "MainContentArea", "set", Index: 1) { Name = "" }, dryRun: false, cancellationToken);
            document = await GetAsync(id, cancellationToken: cancellationToken);
            Assert.Null(Area(document)[1]!["name"]);

            // Written back as get shows it, the area doesn't change; without B, C keeps its own values (not B's link).
            var same = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, AsValues(Area(document))), dryRun: false, cancellationToken)).Output);
            Assert.False(same.Saved);
            Assert.Empty(same.Changes);
            var withoutB = Area(document).DeepClone().AsArray();
            withoutB.RemoveAt(2);
            await writes.RunAsync(new SetOperation(page, AsValues(withoutB)), dryRun: false, cancellationToken);
            document = await GetAsync(id, cancellationToken: cancellationToken);
            Assert.Equal(["Top", "A changed", "C"], Area(document).Select(i => Value(i, "Heading")));
            Assert.Null(Area(document)[2]!["properties"]!["Link"]);

            // Moved and removed by position; a ref names no inline block.
            await writes.RunAsync(new AreaEdit(page, "MainContentArea", "move", Index: 0, To: 2), dryRun: false, cancellationToken);
            await writes.RunAsync(new AreaEdit(page, "MainContentArea", "remove", Index: 1), dryRun: false, cancellationToken);
            var byRef = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(new AreaEdit(page, "MainContentArea", "remove", Item: Id(image.Value)), dryRun: true, cancellationToken));
            Assert.Contains("An inline block has no ref: name it by its position", byRef.Hint);
            var outOfRange = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(new SetOperation(page, JsonNode.Parse("""{"MainContentArea[5]": {"Heading": "x"}}""")!.AsObject()), dryRun: true, cancellationToken));
            Assert.Contains("there is no MainContentArea[5]", outOfRange.Message);

            // Published, the site renders it; the CMS loads what the database read shows.
            await writes.RunAsync(new PublishOperation(page), dryRun: false, cancellationToken);
            document = await GetAsync(id, VersionSelector.Published, cancellationToken);
            Assert.Equal(["A changed", "Top"], Area(document).Select(i => Value(i, "Heading")));
            await using (var fresh = await SiteUnderTest.ConnectAsync(cancellationToken))
            {
                var result = await new ItemComparer(fresh).CompareAsync(
                    new SampleItem(id, fresh.Session.Model.RequireType("StandardPage").Id, ContentKind.Page, fresh.Session.Language(document.Language)!.Id), cancellationToken);
                Assert.True(result.Mismatches.Count == 0, string.Join('\n', result.Mismatches.Select(m => $"{m.Field} {m.Path}: db {m.Db} / cms {m.Agent}")));
            }
            if (site.SiteUrl is { } url && document.Url is { } path)
            {
                using var http = new HttpClient { BaseAddress = url };
                var html = await http.GetStringAsync(path, cancellationToken);
                Assert.Contains("A changed", html);
            }
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    /// <summary>
    /// The item of an area as <c>get</c> shows it, with one value changed (in <c>get</c>'s shape), and without its
    /// personalization, so what it keeps is what it takes over.
    /// </summary>
    private static JsonNode Changed(JsonNode? item, string property, string value)
    {
        var copy = item!.DeepClone().AsObject();
        copy["properties"]![property] = new JsonObject { ["type"] = "LongString", ["value"] = value };
        copy.Remove("group");
        copy.Remove("visitorGroups");
        copy.Remove("visitorGroupNames");
        return copy;
    }

    [SiteFact]
    public async Task Changed_inline_blocks_in_a_whole_area_are_built_from_what_they_give_and_keep_settings_only_when_unambiguous()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var image = await FirstAsync(site, "ImageFile", cancellationToken);
        if (start is null || image is null || site.Session.Model.Types.All(t => t.Name != "TeaserBlock")
            || Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]) < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var id = (await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline whole {Guid.NewGuid():N}"[..25]), dryRun: false, cancellationToken)).CreatedId!.Value;
        var page = Id(id);
        string Teaser(string heading, string? link = null, string? group = null)
        {
            var item = new JsonObject
            {
                ["type"] = "TeaserBlock",
                ["properties"] = new JsonObject { ["Heading"] = heading, ["Text"] = "t", ["Image"] = Id(image.Value) },
            };
            if (link is not null)
            {
                item["properties"]!["Link"] = link;
            }
            if (group is not null)
            {
                item["group"] = group;
            }
            return item.ToJsonString();
        }
        async Task<JsonArray> WriteAreaAsync(string items)
        {
            await writes.RunAsync(new SetOperation(page, JsonNode.Parse($$"""{"MainContentArea": [{{items}}]}""")!.AsObject()), dryRun: false, cancellationToken);
            return Area(await GetAsync(id, cancellationToken: cancellationToken));
        }
        try
        {
            // Removed and edited: [A, B (a link), C] written back as [A, C changed]. C's item can't be told from B's, so the
            // changed block is built from what it gives: no link of B's, and neither item's personalization.
            var area = await WriteAreaAsync($"{Teaser("A")}, {Teaser("B", Id(start.Value), group: "b")}, {Teaser("C")}");
            area = await WriteAreaAsync($"{area[0]!.ToJsonString()}, {Changed(area[2], "Heading", "C changed").ToJsonString()}");
            Assert.Equal(["A", "C changed"], area.Select(i => Value(i, "Heading")));
            Assert.Null(area[1]!["properties"]!["Link"]);
            Assert.Null(area[1]!["group"]);

            // Moved and edited: [X (personalized), Y] written as [Y as it is, X changed]: X is the only one left on both
            // sides, so it keeps X's personalization, and its values are what it gives.
            area = await WriteAreaAsync($"{Teaser("X", Id(start.Value), group: "x")}, {Teaser("Y")}");
            var x = Changed(area[0], "Heading", "X changed").AsObject();
            x["properties"]!.AsObject().Remove("Link");
            area = await WriteAreaAsync($"{area[1]!.ToJsonString()}, {x.ToJsonString()}");
            Assert.Equal([("Y", null), ("X changed", "x")], area.Select(i => (Value(i, "Heading"), (string?)i!["group"])));
            Assert.Null(area[1]!["properties"]!["Link"]);

            // Two of the type, both changed: neither takes over a block's settings.
            area = await WriteAreaAsync($"{Teaser("P", group: "p")}, {Teaser("Q")}");
            area = await WriteAreaAsync($"{Changed(area[0], "Heading", "P changed").ToJsonString()}, {Changed(area[1], "Heading", "Q changed").ToJsonString()}");
            Assert.Equal([("P changed", null), ("Q changed", null)], area.Select(i => (Value(i, "Heading"), (string?)i!["group"])));

            // In an inline block's own area (by its position), written as get shows it: the same rule.
            if (site.Session.Model.Types.Any(t => t.Name == "EdgeContainerBlock"))
            {
                area = await WriteAreaAsync("{\"type\": \"EdgeContainerBlock\", \"properties\": {\"Heading\": \"Box\", \"Items\": [" + Teaser("Inner A", Id(start.Value)) + ", " + Teaser("Inner B") + "]}}");
                var inner = area[0]!["properties"]!["Items"]!.DeepClone().AsObject();
                var items = inner["value"]!.AsArray();
                items[1] = Changed(items[1], "Heading", "Inner B changed");
                items.RemoveAt(0);
                await writes.RunAsync(new SetOperation(page, new JsonObject { ["MainContentArea[0]"] = new JsonObject { ["Items"] = inner } }), dryRun: false, cancellationToken);
                var box = Area(await GetAsync(id, cancellationToken: cancellationToken))[0]!;
                var only = Assert.Single(box["properties"]!["Items"]!["value"]!.AsArray());
                Assert.Equal(("Box", "Inner B changed"), (Value(box, "Heading"), Value(only, "Heading")));
                Assert.Null(only!["properties"]!["Link"]);
            }
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Area_edits_in_another_language_than_the_master_are_refused_like_set_and_named_adds_rerun_unchanged()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var image = await FirstAsync(site, "ImageFile", cancellationToken);
        if (start is null || image is null || site.Session.Model.Types.All(t => t.Name != "TeaserBlock") || site.Session.Language("sv") is null
            || Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]) < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var rerun = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true);
        var id = (await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline lang {Guid.NewGuid():N}"[..24]), dryRun: false, cancellationToken)).CreatedId!.Value;
        var page = Id(id);
        try
        {
            // A named add, then a change by position: run again with --update-existing, both find what they want there.
            var add = AddInline(page, $$"""{"Heading": "Named", "Text": "t", "Image": "{{image}}"}""", name: "Intro", at: 0);
            var change = new SetOperation(page, JsonNode.Parse("""{"MainContentArea[0]": {"Text": "Z"}}""")!.AsObject());
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(change, dryRun: false, cancellationToken)).Output).Saved);
            Assert.False(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            Assert.False(Assert.IsType<WriteOutput>((await rerun.RunAsync(change, dryRun: false, cancellationToken)).Output).Saved);
            Assert.Equal(["Named"], Area(await GetAsync(id, cancellationToken: cancellationToken)).Select(i => Value(i, "Heading")));

            // Alloy's MainContentArea isn't culture-specific: a Swedish branch can't change it, by area edit or by set.
            await writes.RunAsync(new TranslateOperation(page, "sv"), dryRun: false, cancellationToken);
            foreach (var edit in new WriteOperation[]
            {
                new AreaEdit(page, "MainContentArea", "set", Index: 0, Lang: "sv") { Values = new JsonObject { ["Heading"] = "SV" } },
                new AreaEdit(page, "MainContentArea", "remove", Index: 0, Lang: "sv"),
                AddInline(page, """{"Heading": "SV"}""") with { Lang = "sv" },
                new SetOperation(page, JsonNode.Parse("""{"MainContentArea[0]": {"Heading": "SV"}}""")!.AsObject(), Lang: "sv"),
            })
            {
                var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => writes.RunAsync(edit, dryRun: false, cancellationToken));
                Assert.Contains("'MainContentArea' is not culture-specific, so it can only be changed on the master language", refused.Message);
            }
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_changed_block_with_an_area_of_its_own_keeps_what_its_area_pairs_with()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var image = await FirstAsync(site, "ImageFile", cancellationToken);
        if (start is null || image is null || site.Session.Model.Types.All(t => t.Name != "EdgeContainerBlock")
            || Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]) < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var id = (await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline box {Guid.NewGuid():N}"[..23]), dryRun: false, cancellationToken)).CreatedId!.Value;
        var page = Id(id);
        string Teaser(string heading, string group) =>
            new JsonObject { ["type"] = "TeaserBlock", ["group"] = group, ["properties"] = new JsonObject { ["Heading"] = heading, ["Text"] = "t", ["Image"] = Id(image.Value) } }.ToJsonString();
        try
        {
            await writes.RunAsync(new SetOperation(page, JsonNode.Parse("{\"MainContentArea\": [{\"type\": \"EdgeContainerBlock\", \"properties\": {\"Heading\": \"Box\", \"Items\": [" + Teaser("First", "a") + ", " + Teaser("Second", "b") + "]}}]}")!.AsObject()), dryRun: false, cancellationToken);

            // Written back as get shows it, the box's heading and one of its blocks changed (their own personalization left
            // out): the box is the only one of its type, so its area pairs with the area it had.
            var area = Area(await GetAsync(id, cancellationToken: cancellationToken));
            var box = Changed(area[0], "Heading", "Box changed").AsObject();
            var items = box["properties"]!["Items"]!["value"]!.AsArray();
            items[1] = Changed(items[1], "Heading", "Second changed");
            await writes.RunAsync(new SetOperation(page, new JsonObject { ["MainContentArea"] = new JsonArray(box) }), dryRun: false, cancellationToken);

            var inside = Area(await GetAsync(id, cancellationToken: cancellationToken))[0]!["properties"]!["Items"]!["value"]!.AsArray();
            Assert.Equal([("First", "a"), ("Second changed", "b")], inside.Select(i => (Value(i, "Heading"), (string?)i!["group"])));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_shared_block_by_ref_alone_in_an_inline_blocks_own_area_is_taken_by_create_area_add_and_set()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var shared = await FirstAsync(site, "TeaserBlock", cancellationToken);
        if (start is null || shared is null || site.Session.Model.Types.All(t => t.Name != "EdgeContainerBlock")
            || Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]) < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        JsonObject Box(string heading) => JsonNode.Parse($$"""{"Heading": "{{heading}}", "Items": [{"ref": "{{shared}}"}]}""")!.AsObject();
        JsonObject WholeArea(string heading) => new()
        {
            ["MainContentArea"] = new JsonArray(new JsonObject { ["type"] = "EdgeContainerBlock", ["properties"] = Box(heading) }),
        };
        // The shared block inside each box, by ref.
        async Task<IEnumerable<(string?, string?)>> BoxesAsync(int id) =>
            Area(await GetAsync(id, cancellationToken: cancellationToken)).Select(box =>
                (Value(box, "Heading"), (string?)Assert.Single(box!["properties"]!["Items"]!["value"]!.AsArray())!["ref"]));

        var id = (await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline ref {Guid.NewGuid():N}"[..23], WholeArea("Created")), dryRun: false, cancellationToken)).CreatedId!.Value;
        var page = Id(id);
        try
        {
            Assert.Equal([("Created", Id(shared.Value))], await BoxesAsync(id));

            var add = new AreaEdit(page, "MainContentArea", "add") { Type = "EdgeContainerBlock", Values = Box("Added") };
            await writes.RunAsync(add, dryRun: true, cancellationToken);
            await writes.RunAsync(add, dryRun: false, cancellationToken);
            Assert.Equal([("Created", Id(shared.Value)), ("Added", Id(shared.Value))], await BoxesAsync(id));

            await writes.RunAsync(new SetOperation(page, WholeArea("Set")), dryRun: false, cancellationToken);
            Assert.Equal([("Set", Id(shared.Value))], await BoxesAsync(id));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task An_inline_add_of_a_type_the_area_doesnt_allow_fails_the_cms_validation()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        // Alloy's product page leaves the jumbotron out of its related area ([AllowedTypes]).
        if (await FirstAsync(site, "ProductPage", cancellationToken) is not { } product || site.Session.Model.Types.All(t => t.Name != "JumbotronBlock"))
        {
            return;
        }
        var cms = Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]);
        if (cms < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));

        var refused = await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(
            new AreaEdit(Id(product), "RelatedContentArea", "add") { Type = "JumbotronBlock", Values = new JsonObject { ["Heading"] = "Not here" } }, dryRun: true, cancellationToken));

        Assert.Contains("is not allowed in", refused.Message);
    }

    [SiteFact]
    public async Task With_update_existing_an_inline_block_with_its_name_or_its_values_counts_as_already_there()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var start = await FirstAsync(site, "StartPage", cancellationToken);
        var image = await FirstAsync(site, "ImageFile", cancellationToken);
        if (start is null || image is null || site.Session.Model.Types.All(t => t.Name != "TeaserBlock")
            || Version.Parse((await site.Agent.PingAsync(cancellationToken)).CmsVersion.Split('-', '+')[0]) < new Version(12, 20))
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var rerun = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true);
        var page = Id((await writes.RunAsync(new CreateOperation(Id(start.Value), "StandardPage", $"Inline rerun {Guid.NewGuid():N}"[..25]), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            // Without a name, an inline block of its type with its values is "already there"; other values add another.
            var add = AddInline(page, $$"""{"Heading": "Once", "Text": "Only once", "Image": "{{image}}"}""");
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            Assert.False(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            var other = add with { Values = JsonNode.Parse($$"""{"Heading": "Twice", "Text": "Only once", "Image": "{{image}}"}""")!.AsObject() };
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(other, dryRun: false, cancellationToken)).Output).Saved);
            // With a name, one of its type with that name is, whatever its values now; a plain run adds another.
            var named = other with { Name = "Named", Values = JsonNode.Parse($$"""{"Heading": "Named", "Text": "t", "Image": "{{image}}"}""")!.AsObject() };
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(named, dryRun: false, cancellationToken)).Output).Saved);
            Assert.False(Assert.IsType<WriteOutput>((await rerun.RunAsync(named with { Values = JsonNode.Parse("""{"Heading": "Changed since"}""")!.AsObject() }, dryRun: false, cancellationToken)).Output).Saved);
            Assert.True(Assert.IsType<WriteOutput>((await writes.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            Assert.Equal(["Once", "Twice", "Named", "Once"], Area(await GetAsync(int.Parse(page, CultureInfo.InvariantCulture), cancellationToken: cancellationToken)).Select(i => Value(i, "Heading")));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
