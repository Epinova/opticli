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
    public async Task With_update_existing_an_inline_block_with_the_same_values_counts_as_already_there()
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
            var add = AddInline(page, $$"""{"Heading": "Once", "Text": "Only once", "Image": "{{image}}"}""", name: "Once");
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);

            Assert.False(Assert.IsType<WriteOutput>((await rerun.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            // Other values (or a plain run) add another.
            var other = add with { Values = JsonNode.Parse($$"""{"Heading": "Twice", "Text": "Only once", "Image": "{{image}}"}""")!.AsObject() };
            Assert.True(Assert.IsType<WriteOutput>((await rerun.RunAsync(other, dryRun: false, cancellationToken)).Output).Saved);
            Assert.True(Assert.IsType<WriteOutput>((await writes.RunAsync(add, dryRun: false, cancellationToken)).Output).Saved);
            Assert.Equal(["Once", "Twice", "Once"], Area(await GetAsync(int.Parse(page, CultureInfo.InvariantCulture), cancellationToken: cancellationToken)).Select(i => Value(i, "Heading")));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
