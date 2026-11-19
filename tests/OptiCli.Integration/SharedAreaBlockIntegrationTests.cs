using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// New blocks in a ContentArea where the site's edit UI makes them shared (<c>UIOptions.InlineBlocksInContentAreaEnabled</c>
/// off, CMS 12's default; on CMS 13 serve the site with <c>EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled=false</c>):
/// shared blocks in the page's "For this page" folder, saved as drafts with the page, published along with it and rendered
/// by the site; inline blocks a page already has (another tool's, which the edge-case fixture adds through the CMS's API)
/// edited, moved, removed and kept inline; plans, the review refusal, CMS projects and another language. On the Alloy
/// edge-case site (tests/fixtures/edge-cases); every page goes to the recycle bin at the end. The tests marked inline run
/// where the site makes inline blocks, and check that nothing of this applies there.
/// </summary>
public sealed class SharedAreaBlockIntegrationTests
{
    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static int ContentId(string reference) => int.Parse(reference.Split('_')[0], CultureInfo.InvariantCulture);

    /// <summary>What a test needs of the site: Alloy's start page, an image, and the edge-case fixture's endpoints.</summary>
    private sealed class Alloy : IAsyncDisposable
    {
        private readonly List<string> _pages = [];

        private readonly List<string> _projects = [];

        private Alloy(SiteUnderTest site, int start, int image)
        {
            Site = site;
            Start = Id(start);
            Image = Id(image);
            Writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
            Projects = new ProjectWrites(Writes);
        }

        public SiteUnderTest Site { get; }

        public string Start { get; }

        public string Image { get; }

        public WriteExecutor Writes { get; }

        public ProjectWrites Projects { get; }

        public string Stamp { get; } = Guid.NewGuid().ToString("N")[..8];

        /// <returns>Null on a site that isn't the Alloy edge-case site (no TeaserBlock, no image, or no fixture endpoints).</returns>
        public static async Task<Alloy?> StartAsync()
        {
            var site = await SiteUnderTest.ConnectAsync(CancellationToken.None);
            if (site.SiteUrl is null || site.Session.Model.Types.All(t => t.Name != "TeaserBlock")
                || await FirstAsync(site, "StartPage") is not { } start || await FirstAsync(site, "ImageFile") is not { } image
                || await EdgeFixture.FindAsync(site, EdgeFixture.ApprovalRoot, CancellationToken.None) is null)
            {
                await site.DisposeAsync();
                return null;
            }
            return new Alloy(site, start, image);
        }

        private static async Task<int?> FirstAsync(SiteUnderTest site, string type)
        {
            if (site.Session.Model.Types.FirstOrDefault(t => t.Name == type) is not { } found)
            {
                return null;
            }
            var ids = await site.Session.Db.QueryAsync("SELECT TOP 1 pkID FROM tblContent WHERE fkContentTypeID = @type AND Deleted = 0 ORDER BY pkID",
                r => r.GetInt32(0), CancellationToken.None, new SqlParameter("@type", found.Id));
            return ids.Count == 0 ? null : ids[0];
        }

        public string Name(string what) => $"opticli-it shared {what} {Stamp}";

        /// <summary>A Teaser block's item for a whole-area write: <c>{type, name, properties}</c>.</summary>
        public JsonObject Teaser(string heading, string? name = null, string text = "t")
        {
            var item = new JsonObject
            {
                ["type"] = "TeaserBlock",
                ["properties"] = new JsonObject { ["Heading"] = heading, ["Text"] = text, ["Image"] = Image },
            };
            if (name is not null)
            {
                item["name"] = name;
            }
            return item;
        }

        public JsonObject TeaserValues(string heading, string text = "t") => new() { ["Heading"] = heading, ["Text"] = text, ["Image"] = Image };

        public static JsonObject Area(params JsonNode[] items) => new() { ["MainContentArea"] = new JsonArray(items) };

        public async Task<WriteOutput> RunAsync(WriteOperation<WriteOutput> operation, bool dryRun = false) =>
            (await Writes.RunAsync(operation, dryRun, CancellationToken.None)).Output;

        public async Task<string> PageAsync(string what, JsonObject? properties = null, string type = "StandardPage", string? parent = null, bool publish = false)
        {
            var made = await Writes.RunAsync(new CreateOperation(parent ?? Start, type, Name(what), properties, Publish: publish), dryRun: false, CancellationToken.None);
            var page = Id(made.CreatedId!.Value);
            _pages.Add(page);
            return page;
        }

        public void Track(string page) => _pages.Add(page);

        public async Task<string> ProjectAsync(string what)
        {
            var made = await Projects.CreateAsync(Name(what), dryRun: false, CancellationToken.None);
            var id = made.Project.Id.ToString(CultureInfo.InvariantCulture);
            _projects.Add(id);
            return id;
        }

        /// <summary>The content as <c>get</c> reads it (latest version, or <paramref name="version"/>), from a session of its own.</summary>
        public static async Task<ContentDocument> GetAsync(string id, VersionSelector? version = null)
        {
            await using var fresh = await SiteUnderTest.ConnectAsync(CancellationToken.None);
            return await new ContentLoader(fresh.Session.Db, fresh.Session.Identities).GetAsync(int.Parse(id, CultureInfo.InvariantCulture),
                version ?? new VersionSelector(VersionKind.Latest), null, new DecodeOptions(Full: true), CancellationToken.None);
        }

        public static JsonArray AreaOf(ContentDocument document, string property = "MainContentArea") =>
            document.Properties[property]?["value"]?.AsArray() ?? [];

        /// <summary>
        /// Adds an inline Teaser block to the page's area through the CMS's API (the edge-case fixture's
        /// <c>POST /opticli-fixture/inline-block</c>), as another tool may on a site whose edit UI makes none.
        /// </summary>
        public async Task<string> AddInlineAsync(string page, string name, JsonObject values, string type = "TeaserBlock", string property = "MainContentArea")
        {
            using var http = new HttpClient { BaseAddress = Site.SiteUrl };
            using var body = new StringContent(new JsonObject(values.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(v.Value!.ToString())))).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"opticli-fixture/inline-block?content={page}&property={property}&type={type}&name={Uri.EscapeDataString(name)}", body);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST /opticli-fixture/inline-block answered {(int)response.StatusCode}: is the site built with this checkout's tests/fixtures/edge-cases/setup.sh?");
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>The public page, as a visitor gets it.</summary>
        public async Task<string> RenderAsync(string page)
        {
            var document = await GetAsync(page, VersionSelector.Published);
            Assert.NotNull(document.Url);
            using var http = new HttpClient { BaseAddress = Site.SiteUrl };
            using var response = await http.GetAsync(document.Url);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {document.Url} answered {(int)response.StatusCode}: {html[..Math.Min(html.Length, 2000)]}");
            return html;
        }

        /// <summary>The block's folder and that folder's owner, from the database.</summary>
        public async Task<(int Folder, Guid Owner)> FolderAsync(string block) =>
            (await Site.Session.Db.QueryAsync("SELECT f.pkID, f.ContentOwnerID FROM tblContent b JOIN tblContent f ON f.pkID = b.fkParentID WHERE b.pkID = @block",
                r => (r.GetInt32(0), r.GetGuid(1)), CancellationToken.None, new SqlParameter("@block", ContentId(block)))).Single();

        public async Task<Guid> GuidAsync(string content) =>
            (await Site.Session.Db.QueryAsync("SELECT ContentGUID FROM tblContent WHERE pkID = @id", r => r.GetGuid(0), CancellationToken.None,
                new SqlParameter("@id", ContentId(content)))).Single();

        /// <summary>The content's master language, from the database.</summary>
        public async Task<string> MasterLanguageAsync(string content) =>
            (await Site.Session.Db.QueryAsync("SELECT l.LanguageID FROM tblContent c JOIN tblLanguageBranch l ON l.pkID = c.fkMasterLanguageBranchID WHERE c.pkID = @id",
                r => r.GetString(0).Trim(), CancellationToken.None, new SqlParameter("@id", ContentId(content)))).Single();

        /// <summary>The highest content id: nothing new was made while it stays the same.</summary>
        public async Task<int> NewestIdAsync() =>
            (await Site.Session.Db.QueryAsync("SELECT MAX(pkID) FROM tblContent", r => r.GetInt32(0), CancellationToken.None)).Single();

        /// <summary>A version's status as the CLI names it, from the database.</summary>
        public async Task<string> StatusAsync(string version) =>
            (await Site.Session.Db.QueryAsync("SELECT Status FROM tblWorkContent WHERE pkID = @version", r => r.GetInt32(0), CancellationToken.None,
                new SqlParameter("@version", int.Parse(version.Split('_')[1], CultureInfo.InvariantCulture)))).Single() switch
            {
                2 => "checkedOut",
                3 => "checkedIn",
                4 => "published",
                5 => "previouslyPublished",
                7 => "awaitingApproval",
                var other => other.ToString(CultureInfo.InvariantCulture),
            };

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var project in _projects)
                {
                    try
                    {
                        await Projects.DeleteAsync(project, CancellationToken.None);
                    }
                    catch (OptiCliException)
                    {
                        // Published already, or gone: the pages go regardless.
                    }
                }
                foreach (var page in Enumerable.Reverse(_pages))
                {
                    try
                    {
                        await Writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, CancellationToken.None);
                    }
                    catch (OptiCliException)
                    {
                        // Below another page of the test, which goes to the recycle bin with it.
                    }
                }
            }
            finally
            {
                await Site.DisposeAsync();
            }
        }
    }

    private static string? Value(JsonNode? item, string property) => item?["properties"]?[property]?["value"]?.GetValue<string>();

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task New_blocks_of_a_pages_area_are_drafts_in_its_folder_updated_by_name_published_along_and_rendered()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var newest = await alloy.NewestIdAsync();
        var area = Alloy.Area(alloy.Teaser("Shared intro", name: "Intro"), alloy.Teaser("Shared second"));
        var create = new CreateOperation(alloy.Start, "StandardPage", alloy.Name("page"), area);

        // The dry run shows what each block will be, and makes nothing.
        var dry = await alloy.RunAsync(create, dryRun: true);
        Assert.Equal([(null, SharedBlockActions.Created, "Intro"), (null, SharedBlockActions.Created, null)],
            dry.SharedBlocks!.Select(b => (b.Ref, b.Action, b.Name == "Intro" ? b.Name : null)));
        var planned = Assert.Single(dry.Changes, c => c.Property == "MainContentArea").After!.Value.GetRawText();
        Assert.Contains("\"shared\":\"planned\"", planned);
        Assert.Contains(dry.Validation!, v => v.Severity == "info" && v.Message.Contains("are shared blocks in the page's \"For this page\" folder"));
        Assert.Equal(newest, await alloy.NewestIdAsync());

        var made = await alloy.Writes.RunAsync(create, dryRun: false, CancellationToken.None);
        var page = Id(made.CreatedId!.Value);
        alloy.Track(page);
        var blocks = made.Output.SharedBlocks!;
        Assert.Equal([("MainContentArea[0]", "Intro", "checkedOut"), ("MainContentArea[1]", $"{made.Output.Name} TeaserBlock 1", "checkedOut")],
            blocks.Select(b => (b.Path, b.Name, b.Status)));
        // In the page's own "For this page" folder, which the write made; the area shows them by their refs.
        var pageGuid = await alloy.GuidAsync(page);
        foreach (var block in blocks)
        {
            var (folder, owner) = await alloy.FolderAsync(block.Ref!);
            Assert.Equal((Id(folder), pageGuid), (block.Folder, owner));
        }
        Assert.Equal(blocks.Select(b => Id(ContentId(b.Ref!))), Alloy.AreaOf(await Alloy.GetAsync(page)).Select(i => (string?)i!["ref"]));

        // Written whole again as {type, name, properties}: the block named "Intro" is updated by its name, the other (an
        // exact copy) left as it is; nothing new is made.
        newest = await alloy.NewestIdAsync();
        var again = await alloy.RunAsync(new SetOperation(page, Alloy.Area(alloy.Teaser("Shared intro changed", name: "Intro"), alloy.Teaser("Shared second", name: blocks[1].Name))));
        var updated = Assert.Single(again.SharedBlocks!);
        Assert.Equal((SharedBlockActions.Updated, ContentId(blocks[0].Ref!)), (updated.Action, ContentId(updated.Ref!)));
        Assert.Equal(newest, await alloy.NewestIdAsync());

        // Published, the page takes its blocks' drafts along, and the site renders them.
        var published = await alloy.RunAsync(new PublishOperation(page));
        Assert.True(published.Published);
        Assert.Equal([SharedBlockActions.Published, SharedBlockActions.Published], published.SharedBlocks!.Select(b => b.Action));
        foreach (var block in published.SharedBlocks!)
        {
            Assert.Equal("published", await alloy.StatusAsync(block.Ref!));
        }
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Shared intro changed", html);
        Assert.Contains("Shared second", html);

        // A write that publishes (--publish) takes a block it adds along too.
        var add = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add, Publish: true) { Type = "TeaserBlock", Name = "Third", Values = alloy.TeaserValues("Shared third") });
        Assert.True(add.Published);
        var third = Assert.Single(add.SharedBlocks!);
        Assert.Equal((SharedBlockActions.Created, "published"), (third.Action, third.Status));
        Assert.Contains("Shared third", await alloy.RenderAsync(page));
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task Inline_blocks_a_page_already_has_are_edited_moved_removed_and_kept_inline_beside_new_shared_ones()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null || alloy.Site.Session.Model.Types.All(t => t.Name != "EdgeContainerBlock"))
        {
            return;
        }
        var page = await alloy.PageAsync("inline");
        await alloy.AddInlineAsync(page, "Kept A", alloy.TeaserValues("Inline A"));
        await alloy.AddInlineAsync(page, "Kept B", alloy.TeaserValues("Inline B"));
        var area = Alloy.AreaOf(await Alloy.GetAsync(page));
        Assert.All(area, i => Assert.True(i!["inline"]!.GetValue<bool>()));

        // Changed and moved by position: still inline.
        await alloy.RunAsync(new SetOperation(page, JsonNode.Parse("""{"MainContentArea[0]": {"Heading": "Inline A changed"}}""")!.AsObject()));
        await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Move, Index: 1, To: 0));
        area = Alloy.AreaOf(await Alloy.GetAsync(page));
        Assert.Equal([("Kept B", true, "Inline B"), ("Kept A", true, "Inline A changed")], area.Select(i => ((string?)i!["name"], i["inline"]?.GetValue<bool>() == true, Value(i, "Heading"))));

        // Written whole, "Kept A" changed by its name, "Kept B" as get shows it, and a new item: the two stay inline (in the
        // order given), only the new one becomes a shared block.
        var b = area[0]!.DeepClone().AsObject();
        b["properties"] = alloy.TeaserValues("Inline B");
        var whole = await alloy.RunAsync(new SetOperation(page, Alloy.Area(alloy.Teaser("Inline A again", name: "Kept A"), b, alloy.Teaser("Shared new", name: "New"))));
        var made = Assert.Single(whole.SharedBlocks!);
        Assert.Equal(("MainContentArea[2]", SharedBlockActions.Created, "New"), (made.Path, made.Action, made.Name));
        area = Alloy.AreaOf(await Alloy.GetAsync(page));
        Assert.Equal([("Kept A", true), ("Kept B", true), ("New", false)], area.Select(i => ((string?)i!["name"], i["inline"]?.GetValue<bool>() == true)));
        Assert.Equal(Id(ContentId(made.Ref!)), (string?)area[2]!["ref"]);

        // A new block in an inline block's own area goes in the page's folder too.
        await alloy.AddInlineAsync(page, "Box", new JsonObject { ["Heading"] = "Inline box" }, type: "EdgeContainerBlock");
        var inner = await alloy.RunAsync(new AreaEdit(page, "MainContentArea[3].Items", AreaOps.Add) { Type = "TeaserBlock", Values = alloy.TeaserValues("Shared inner") });
        var innerBlock = Assert.Single(inner.SharedBlocks!);
        Assert.Equal("MainContentArea[3].Items[0]", innerBlock.Path);
        Assert.Equal(await alloy.GuidAsync(page), (await alloy.FolderAsync(innerBlock.Ref!)).Owner);

        // Removed by position; then published, with both shared blocks (the one inside the inline block too): the site
        // renders the inline block and the shared one (the fixture's container block has no template of its own).
        await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Remove, Index: 1));
        var published = await alloy.RunAsync(new PublishOperation(page));
        Assert.Equal([ContentId(made.Ref!), ContentId(innerBlock.Ref!)], published.SharedBlocks!.Where(s => s.Action == SharedBlockActions.Published).Select(s => ContentId(s.Ref!)).Order());
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Inline A again", html);
        Assert.DoesNotContain("Inline B", html);
        Assert.Contains("Shared new", html);

        // Alloy's MainContentArea isn't culture-specific: a Swedish branch changes neither its inline block nor adds a shared
        // one, by area edit or by set, and nothing is made.
        if (alloy.Site.Session.Language("sv") is null)
        {
            return;
        }
        await alloy.Writes.RunAsync(new TranslateOperation(page, "sv"), dryRun: false, CancellationToken.None);
        var newest = await alloy.NewestIdAsync();
        foreach (var edit in new WriteOperation<WriteOutput>[]
        {
            new AreaEdit(page, "MainContentArea", AreaOps.Set, Index: 0, Lang: "sv") { Values = new JsonObject { ["Heading"] = "SV" } },
            new AreaEdit(page, "MainContentArea", AreaOps.Remove, Index: 0, Lang: "sv"),
            new AreaEdit(page, "MainContentArea", AreaOps.Add, Lang: "sv") { Type = "TeaserBlock", Values = alloy.TeaserValues("SV") },
            new SetOperation(page, JsonNode.Parse("""{"MainContentArea[0]": {"Heading": "SV"}}""")!.AsObject(), Lang: "sv"),
        })
        {
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => alloy.Writes.RunAsync(edit, dryRun, CancellationToken.None));
                Assert.Contains("'MainContentArea' is not culture-specific, so it can only be changed on the master language", refused.Message);
            }
        }
        Assert.Equal(newest, await alloy.NewestIdAsync());
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_plans_area_add_names_its_block_for_later_steps_and_finds_it_again_run_with_update_existing()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var guid = Guid.NewGuid();
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "create", "id": "page", "guid": "{{{guid}}}", "parent": "{{{alloy.Start}}}", "type": "StandardPage", "name": "{{{alloy.Name("plan")}}}"},
              {"op": "area", "ref": "$page", "property": "MainContentArea", "action": "add", "type": "TeaserBlock", "id": "intro", "name": "Intro",
               "values": {"Heading": "Planned", "Text": "t", "Image": "{{{alloy.Image}}}"}},
              {"op": "set", "ref": "$intro", "properties": {"Text": "Set on the block by its id"}},
              {"op": "publish", "ref": "$page"}
            ]}
            """);
        var rerun = new WriteExecutor(alloy.Site.Session, _ => Task.FromResult(alloy.Site.Agent), updateExisting: true);
        PlanRun Run(WriteExecutor writes, bool dryRun) => new PlanRunner(alloy.Site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun, publishAll: false, CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(Run(alloy.Writes, dryRun: true).Operations.All(o => o.Status != PlanStepStatus.Invalid));
        var first = Run(alloy.Writes, dryRun: false);
        var page = first.Created["page"];
        alloy.Track(page);
        var block = first.Created["intro"];
        Assert.Equal(Id(ContentId(block)), (string?)Alloy.AreaOf(await Alloy.GetAsync(page))[0]!["ref"]);
        Assert.Contains("Set on the block by its id", await alloy.RenderAsync(page));

        // Run again: the add finds the block it made, $intro reaches it again, and nothing changes.
        var dry = Run(rerun, dryRun: true);
        Assert.True(dry.Operations.All(o => o.Status != PlanStepStatus.Invalid), string.Join(", ", dry.Operations.Select(o => o.Status)));
        var second = Run(rerun, dryRun: false);
        Assert.Equal(block, second.Created["intro"]);
        Assert.All(second.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));
        Assert.Single(Alloy.AreaOf(await Alloy.GetAsync(page)));

        // A plan that names the new block by its position instead is refused in the dry run.
        var positional = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "add", "type": "TeaserBlock", "values": {"Heading": "Second", "Text": "t", "Image": "{{{alloy.Image}}}"}},
              {"op": "set", "ref": "{{{page}}}", "properties": {"MainContentArea[1]": {"Heading": "By position"} } }
            ]}
            """);
        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath()).RunAsync(positional, dryRun: true, publishAll: false, CancellationToken.None));
        Assert.Contains("names by its position the new block an earlier operation puts at MainContentArea[1]", refused.Message);

    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_run_again_whose_add_finds_an_inline_block_like_it_is_refused_in_the_dry_run_when_its_id_is_used()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var page = await alloy.PageAsync("plan inline");
        await alloy.AddInlineAsync(page, "Intro", alloy.TeaserValues("Planned"));
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "add", "type": "TeaserBlock", "id": "intro", "name": "Intro",
               "values": {"Heading": "Planned", "Text": "t", "Image": "{{{alloy.Image}}}"}},
              {"op": "set", "ref": "$intro", "properties": {"Text": "Set on the block by its id"}}
            ]}
            """);
        var rerun = new WriteExecutor(alloy.Site.Session, _ => Task.FromResult(alloy.Site.Agent), updateExisting: true);

        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(alloy.Site.Session, rerun, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None));

        Assert.Contains("Operation(s) 1 reach the block this add makes as '$intro', but MainContentArea already has an inline block like it", refused.Message);
        Assert.Single(Alloy.AreaOf(await Alloy.GetAsync(page)));
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_review_request_with_unpublished_blocks_is_refused_and_a_project_sends_the_page_and_its_blocks_for_review_together()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        // Below the approval root, whose sequence its pages and their folders' blocks inherit.
        var root = Id((await EdgeFixture.FindAsync(alloy.Site, EdgeFixture.ApprovalRoot, CancellationToken.None))!.Id);
        var newest = await alloy.NewestIdAsync();
        var create = new CreateOperation(root, "StandardPage", alloy.Name("review"), Alloy.Area(alloy.Teaser("Reviewed block"))) { RequestApproval = true };
        foreach (var dryRun in new[] { true, false })
        {
            var refused = await Assert.ThrowsAsync<UsageException>(() => alloy.Writes.RunAsync(create, dryRun, CancellationToken.None));
            Assert.Contains("MainContentArea[0] (a new block)", refused.Message);
            Assert.StartsWith("Save the change into a CMS project instead (--in-project <project> --ready --request-approval)", refused.Hint);
        }
        Assert.Equal(newest, await alloy.NewestIdAsync());

        // Saved as a draft, the page's review request is refused while its block is a draft: in the dry run too.
        var page = await alloy.PageAsync("review draft", Alloy.Area(alloy.Teaser("Draft block")), parent: root);
        foreach (var dryRun in new[] { true, false })
        {
            var refused = await Assert.ThrowsAsync<UsageException>(() => alloy.Writes.RunAsync(new PublishOperation(page) { RequestApproval = true }, dryRun, CancellationToken.None));
            Assert.Contains("(block ", refused.Message);
            Assert.StartsWith("Publish those blocks first", refused.Hint);
        }

        // In a project with ready and requestApproval, the page and its new block go for review together.
        var project = await alloy.ProjectAsync("review");
        var inProject = await alloy.Writes.RunAsync(create with { Name = alloy.Name("review project"), Project = project, Ready = true }, dryRun: false, CancellationToken.None);
        alloy.Track(Id(inProject.CreatedId!.Value));
        var block = Assert.Single(inProject.Output.SharedBlocks!);
        Assert.Equal("awaitingApproval", block.Status);
        Assert.Equal(("awaitingApproval", "awaitingApproval"), (await alloy.StatusAsync(inProject.Output.Version!), await alloy.StatusAsync(block.Ref!)));
        Assert.Equal(2, (await alloy.Site.Session.Db.QueryAsync("SELECT COUNT(*) FROM tblProjectItem WHERE fkProjectID = @project", r => r.GetInt32(0), CancellationToken.None,
            new SqlParameter("@project", int.Parse(project, CultureInfo.InvariantCulture)))).Single());
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_live_blocks_newer_draft_stays_a_draft_when_its_page_is_published_and_goes_along_when_named()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        // A page published with its block; then a newer draft of that block (an editor's change in progress), a new block
        // never published, and a new draft of the page.
        var page = await alloy.PageAsync("live block", Alloy.Area(alloy.Teaser("Live original", name: "Live")));
        var first = await alloy.RunAsync(new PublishOperation(page));
        var live = Id(ContentId(Assert.Single(first.SharedBlocks!).Ref!));
        await alloy.RunAsync(new SetOperation(live, alloy.TeaserValues("Live changed")));
        var added = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add) { Type = "TeaserBlock", Name = "Fresh", Values = alloy.TeaserValues("Fresh block") });
        var fresh = Id(ContentId(Assert.Single(added.SharedBlocks!).Ref!));
        var liveDraft = (await Alloy.GetAsync(live)).Version;

        // The dry run asks the site: the new block goes along, the live one stays behind with a warning naming it.
        var dry = await alloy.RunAsync(new PublishOperation(page), dryRun: true);
        Assert.True(dry.Valid);
        Assert.Equal([(fresh, SharedBlockActions.Published)], dry.SharedBlocks!.Select(b => (Id(ContentId(b.Ref!)), b.Action)));
        var warning = Assert.Single(dry.Validation!, v => v.Severity == "warning");
        Assert.Equal("MainContentArea[0]", warning.Property);
        Assert.Contains($"('Live') at MainContentArea[0] is published already", warning.Message);
        Assert.Contains($"--with-block {live}", warning.Message);
        Assert.Equal(("checkedOut", "checkedOut"), (await alloy.StatusAsync(liveDraft), await alloy.StatusAsync(Assert.Single(added.SharedBlocks!).Ref!)));

        // A block that isn't one of the page's own folder that it shows can't be named.
        var elsewhere = await Assert.ThrowsAsync<UsageException>(() => alloy.Writes.RunAsync(new PublishOperation(page) { WithBlocks = [alloy.Start] }, dryRun: false, CancellationToken.None));
        Assert.Contains("isn't a block of the content's own \"For this page\" folder that its ContentAreas show", elsewhere.Message);

        // Published: the page goes live with the new block and the live block as it was published.
        var published = await alloy.RunAsync(new PublishOperation(page));
        Assert.Equal([fresh], published.SharedBlocks!.Select(b => Id(ContentId(b.Ref!))));
        Assert.Equal("checkedOut", await alloy.StatusAsync(liveDraft));
        Assert.Contains(published.Validation!, v => v.Severity == "warning" && v.Message.Contains("is published already"));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Live original", html);
        Assert.DoesNotContain("Live changed", html);
        Assert.Contains("Fresh block", html);

        // Named, in a plan's publish step after a change of the page, the live block's draft goes along.
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "name": "{{{alloy.Name("live block again")}}}"},
              {"op": "publish", "ref": "{{{page}}}", "withBlocks": [{{{live}}}]}
            ]}
            """);
        var run = await new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath()).RunAsync(plan, dryRun: false, publishAll: false, CancellationToken.None);
        var along = Assert.IsType<WriteOutput>(run.Operations[1].Result);
        Assert.Equal([live], along.SharedBlocks!.Select(b => Id(ContentId(b.Ref!))));
        Assert.Equal("published", await alloy.StatusAsync(liveDraft));
        Assert.Contains("Live changed", await alloy.RenderAsync(page));
    }

    /// <summary>The ids of the blocks a step's result lists as going (or gone) live, in order.</summary>
    private static List<string> Live(PlanStepResult step) =>
        (Assert.IsType<WriteOutput>(step.Result).SharedBlocks ?? []).Where(b => b.Status == "published").Select(b => b.Ref is null ? "new" : Id(ContentId(b.Ref))).Order().ToList();

    /// <summary>
    /// The warnings of a step's result about blocks of the folder its publish leaves behind (a dry run's also has the
    /// write's own validation warnings, such as CMS 13's about an image in another content's assets folder).
    /// </summary>
    private static List<string> Warnings(PlanStepResult step) =>
        (Assert.IsType<WriteOutput>(step.Result).Validation ?? []).Where(v => v.Severity == "warning" && v.Message.StartsWith("The block ", StringComparison.Ordinal))
            .Select(v => v.Message).ToList();

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_plans_publish_after_an_area_rewrite_that_takes_over_a_live_block_takes_it_along_in_the_dry_run_and_the_run()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var page = await alloy.PageAsync("plan rewrite", Alloy.Area(alloy.Teaser("Intro original", name: "Intro")));
        var intro = Id(ContentId(Assert.Single((await alloy.RunAsync(new PublishOperation(page))).SharedBlocks!).Ref!));
        // The area written whole: the live block taken over by its name, and a new block.
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{Alloy.Area(alloy.Teaser("Intro rewritten", name: "Intro"), alloy.Teaser("Added by the plan", name: "Added")).ToJsonString()}}}},
              {"op": "publish", "ref": "{{{page}}}"}
            ]}
            """);
        var runner = new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath());

        var dry = await runner.RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None);
        var run = await runner.RunAsync(plan, dryRun: false, publishAll: false, CancellationToken.None);

        // The dry run takes the live block along as the run does (and lists the new one, which has no ref yet).
        Assert.Equal(new[] { intro, "new" }.Order(), Live(dry.Operations[1]));
        Assert.Equal([PlanStepStatus.Saved, PlanStepStatus.Saved], run.Operations.Select(o => o.Status));
        var published = Assert.IsType<WriteOutput>(run.Operations[1].Result);
        Assert.True(published.Published);
        Assert.Contains(intro, Live(run.Operations[1]));
        Assert.Equal(2, Live(run.Operations[1]).Count);
        Assert.Equal(Warnings(dry.Operations[1]), Warnings(run.Operations[1]));
        Assert.Empty(Warnings(run.Operations[1]));
        Assert.Equal("published", await alloy.StatusAsync((await Alloy.GetAsync(intro)).Version!));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Intro rewritten", html);
        Assert.Contains("Added by the plan", html);
        Assert.DoesNotContain("Intro original", html);

        // A block a later step takes out of the area again isn't published: the run leaves its draft, as the dry run says.
        var removing = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{Alloy.Area(alloy.Teaser("Intro dropped", name: "Intro"), alloy.Teaser("Added by the plan", name: "Added")).ToJsonString()}}}},
              {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "remove", "item": "{{{intro}}}"},
              {"op": "publish", "ref": "{{{page}}}"}
            ]}
            """);
        var dryRemoving = await runner.RunAsync(removing, dryRun: true, publishAll: false, CancellationToken.None);
        Assert.DoesNotContain(intro, Live(dryRemoving.Operations[2]));
        var runRemoving = await runner.RunAsync(removing, dryRun: false, publishAll: false, CancellationToken.None);
        Assert.All(runRemoving.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        Assert.DoesNotContain(intro, Live(runRemoving.Operations[2]));
        Assert.Equal("checkedOut", await alloy.StatusAsync((await Alloy.GetAsync(intro)).Version!));
        Assert.DoesNotContain("Intro dropped", await alloy.RenderAsync(page));
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_plans_publish_after_a_step_that_changed_only_a_block_publishes_that_block_and_the_named_one()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var page = await alloy.PageAsync("plan block only", Alloy.Area(alloy.Teaser("Only original", name: "Only"), alloy.Teaser("Other original", name: "Other")));
        var blocks = (await alloy.RunAsync(new PublishOperation(page))).SharedBlocks!.Select(b => Id(ContentId(b.Ref!))).ToList();
        var (only, other) = (blocks[0], blocks[1]);
        // An editor's change of the other block in progress, which the plan's publish names.
        await alloy.RunAsync(new SetOperation(other, alloy.TeaserValues("Other changed")));
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{Alloy.Area(alloy.Teaser("Only changed", name: "Only"), new JsonObject { ["ref"] = other }).ToJsonString()}}}},
              {"op": "publish", "ref": "{{{page}}}", "withBlocks": [{{{other}}}]}
            ]}
            """);
        var runner = new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath());

        var dry = await runner.RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None);
        var run = await runner.RunAsync(plan, dryRun: false, publishAll: false, CancellationToken.None);

        Assert.Equal(new[] { only, other }.Order(), Live(dry.Operations[1]));
        // The area step saved only the block's draft, and says so; the publish puts both blocks live, the page as it was.
        Assert.Equal([PlanStepStatus.Saved, PlanStepStatus.Saved], run.Operations.Select(o => o.Status));
        var set = Assert.IsType<WriteOutput>(run.Operations[0].Result);
        Assert.False(set.Saved);
        Assert.Equal((only, SharedBlockActions.Updated), (Id(ContentId(Assert.Single(set.SharedBlocks!).Ref!)), Assert.Single(set.SharedBlocks!).Action));
        Assert.Contains(run.Operations[0].Warnings!, w => w.StartsWith("The content itself was left as it was"));
        Assert.Contains("is a new draft of a block", run.Operations[0].Undo);
        var published = Assert.IsType<WriteOutput>(run.Operations[1].Result);
        Assert.False(published.Published);
        Assert.Equal(new[] { only, other }.Order(), Live(run.Operations[1]));
        Assert.Contains(published.Validation!, v => v.Severity == "info" && v.Message.Contains("is already the published version. Only the drafts of the blocks"));
        Assert.Equal(Warnings(dry.Operations[1]), Warnings(run.Operations[1]));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Only changed", html);
        Assert.Contains("Other changed", html);

        // Run again, there is nothing to publish: the dry run and the run both say so.
        var again = await Assert.ThrowsAnyAsync<OptiCliException>(() => alloy.Writes.RunAsync(new PublishOperation(page), dryRun: true, CancellationToken.None));
        Assert.Equal(ErrorCode.Conflict, again.Code);
        Assert.Contains("No draft of a block", again.Message);
        // Blocks named for a publish that goes for review are refused, as a review publishes none along: dry run too.
        var root = Id((await EdgeFixture.FindAsync(alloy.Site, EdgeFixture.ApprovalRoot, CancellationToken.None))!.Id);
        var reviewed = await alloy.PageAsync("plan block only review", Alloy.Area(alloy.Teaser("Reviewed")), parent: root);
        var block = Id(ContentId((string)Alloy.AreaOf(await Alloy.GetAsync(reviewed))[0]!["ref"]!));
        foreach (var dryRun in new[] { true, false })
        {
            var review = await Assert.ThrowsAsync<UsageException>(() => alloy.Writes.RunAsync(new PublishOperation(reviewed) { WithBlocks = [block], RequestApproval = true }, dryRun, CancellationToken.None));
            Assert.Contains("goes for review instead", review.Message);
        }
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_publish_of_a_live_page_publishes_only_the_blocks_it_names_and_run_again_stays_unchanged()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        // A live page showing a block kept back when it was published (never published) and a live block with a newer draft.
        var page = await alloy.PageAsync("live page names", Alloy.Area(alloy.Teaser("Live original", name: "Live")));
        var live = Id(ContentId(Assert.Single((await alloy.RunAsync(new PublishOperation(page))).SharedBlocks!).Ref!));
        await alloy.RunAsync(new SetOperation(live, alloy.TeaserValues("Live changed")));
        var added = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add) { Type = "TeaserBlock", Name = "Kept", Values = alloy.TeaserValues("Kept back block") });
        var kept = Id(ContentId(Assert.Single(added.SharedBlocks!).Ref!));
        var keptBack = await alloy.RunAsync(new PublishOperation(page) { WithoutBlocks = [kept] });
        Assert.True(keptBack.Published);
        Assert.Null(keptBack.SharedBlocks);
        var keptDraft = (await Alloy.GetAsync(kept)).Version!;
        var liveDraft = (await Alloy.GetAsync(live)).Version!;

        // Published again, unnamed: neither block goes along, so there is nothing to publish (dry run alike); the hint lists
        // both, and how to name them.
        foreach (var dryRun in new[] { true, false })
        {
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => alloy.Writes.RunAsync(new PublishOperation(page), dryRun, CancellationToken.None));
            Assert.Contains("No draft of a block", conflict.Message);
            Assert.Contains($"{kept} ('Kept') at MainContentArea[1], never published", conflict.Hint);
            Assert.Contains($"{live} ('Live') at MainContentArea[0], published, with a newer draft", conflict.Hint);
            Assert.Contains($"opticli publish {page} --with-block {live} --with-block {kept}", conflict.Hint);
        }
        // A plan run again with --update-existing: its publish has nothing named or written, so it stays unchanged.
        var plan = WritePlan.Parse($$$"""{"operations": [{"op": "publish", "ref": "{{{page}}}"}]}""");
        var again = new WriteExecutor(alloy.Site.Session, _ => Task.FromResult(alloy.Site.Agent), updateExisting: true);
        var runner = new PlanRunner(alloy.Site.Session, again, Path.GetTempPath());
        var dryRerun = Assert.Single((await runner.RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None)).Operations);
        Assert.NotEqual(PlanStepStatus.Invalid, dryRerun.Status);
        Assert.Contains(dryRerun.Warnings!, w => w.Contains("is already published; nothing to do"));
        Assert.Equal(PlanStepStatus.Unchanged, Assert.Single((await runner.RunAsync(plan, dryRun: false, publishAll: false, CancellationToken.None)).Operations).Status);
        Assert.Equal(("checkedOut", "checkedOut"), (await alloy.StatusAsync(keptDraft), await alloy.StatusAsync(liveDraft)));

        // Named, the block kept back goes live on its own, the live block's draft staying behind; dry run alike.
        var dry = await alloy.RunAsync(new PublishOperation(page) { WithBlocks = [kept] }, dryRun: true);
        var named = await alloy.RunAsync(new PublishOperation(page) { WithBlocks = [kept] });
        foreach (var shown in new[] { dry, named })
        {
            Assert.Equal([kept], shown.SharedBlocks!.Select(b => Id(ContentId(b.Ref!))));
            Assert.Contains(shown.Validation!, v => v.Severity == "warning" && v.Property == "MainContentArea[0]" && v.Message.Contains("is published already"));
        }
        Assert.False(named.Published);
        Assert.Equal(("published", "checkedOut"), (await alloy.StatusAsync(keptDraft), await alloy.StatusAsync(liveDraft)));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Kept back block", html);
        Assert.DoesNotContain("Live changed", html);
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_whole_area_write_over_someone_elses_draft_of_a_folder_block_is_refused_until_include_draft()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var page = await alloy.PageAsync("their draft", Alloy.Area(alloy.Teaser("Intro original", name: "Intro")));
        var intro = Id(ContentId(Assert.Single((await alloy.RunAsync(new PublishOperation(page))).SharedBlocks!).Ref!));
        // Another editor's change of the block in progress.
        var theirs = (await alloy.RunAsync(new SetOperation(intro, alloy.TeaserValues("Their change")))).Version!;
        if (!await alloy.Site.HandOverAsync(theirs, CancellationToken.None))
        {
            return;
        }
        var whole = Alloy.Area(alloy.Teaser("Ours", name: "Intro"));

        // The area written whole would save the block's new version over their draft: refused, dry run too, a plan as well.
        foreach (var dryRun in new[] { true, false })
        {
            var refused = await Assert.ThrowsAsync<ConflictException>(() => alloy.Writes.RunAsync(new SetOperation(page, whole), dryRun, CancellationToken.None));
            var draft = Assert.IsType<AgentErrorDetails>(refused.Details).Draft!;
            Assert.Equal((theirs, SiteUnderTest.SomeoneElse, "MainContentArea[0]"), (draft.Version, draft.SavedBy, draft.Path));
            Assert.Contains("Heading", draft.Changes.Select(c => c.Property));
            Assert.Contains($"the block {intro} ('Intro')", refused.Message);
            Assert.Contains("--include-draft", refused.Hint);
        }
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{whole.ToJsonString()}}}},
              {"op": "publish", "ref": "{{{page}}}"}
            ]}
            """);
        var runner = new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath());
        var refusedPlan = await Assert.ThrowsAsync<ConflictException>(() => runner.RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None));
        Assert.Contains("operation(s) 0, 1", refusedPlan.Message);
        Assert.Contains($"over changes saved by {SiteUnderTest.SomeoneElse} in {theirs}", refusedPlan.Message);
        Assert.Equal(theirs, (await Alloy.GetAsync(intro)).Version);

        // With includeDraft on the step that writes the area (not on the publish), the dry run and the run agree: the
        // block's new version replaces their draft, the result says whose, and the publish puts it live.
        var confirmed = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{whole.ToJsonString()}}}, "includeDraft": true},
              {"op": "publish", "ref": "{{{page}}}"}
            ]}
            """);
        var dry = await runner.RunAsync(confirmed, dryRun: true, publishAll: false, CancellationToken.None);
        Assert.All(dry.Operations, o => Assert.NotEqual(PlanStepStatus.Invalid, o.Status));
        Assert.Contains(intro, Live(dry.Operations[1]));
        var run = await runner.RunAsync(confirmed, dryRun: false, publishAll: false, CancellationToken.None);
        Assert.All(run.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        Assert.Contains(Assert.IsType<WriteOutput>(run.Operations[0].Result).Validation!,
            v => v.Severity == "warning" && v.Message.Contains($"over changes saved by {SiteUnderTest.SomeoneElse} in {theirs}") && v.Message.Contains("as includeDraft confirmed"));
        Assert.Contains(intro, Live(run.Operations[1]));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Ours", html);
        Assert.DoesNotContain("Their change", html);
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task Replacing_one_colleagues_block_draft_doesnt_publish_another_colleagues_block_draft_until_it_is_named()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        const string ColleagueB = "colleague-b@example.com";
        var page = await alloy.PageAsync("two colleagues", Alloy.Area(alloy.Teaser("Intro original", name: "Intro")));
        var intro = Id(ContentId(Assert.Single((await alloy.RunAsync(new PublishOperation(page))).SharedBlocks!).Ref!));
        // Colleague A's change of the live block Intro, and colleague B's new block Extra, never published.
        var theirs = (await alloy.RunAsync(new SetOperation(intro, alloy.TeaserValues("Change by A")))).Version!;
        var added = await alloy.RunAsync(new SetOperation(page, Alloy.Area(new JsonObject { ["ref"] = intro }, alloy.Teaser("Block by B", name: "Extra"))));
        var extraDraft = Assert.Single(added.SharedBlocks!, b => b.Action == SharedBlockActions.Created).Ref!;
        var extra = Id(ContentId(extraDraft));
        if (!await alloy.Site.HandOverAsync(theirs, CancellationToken.None) || !await alloy.Site.HandOverAsync(extraDraft, CancellationToken.None, ColleagueB))
        {
            return;
        }

        // The editor agrees to replace A's draft of Intro (includeDraft on the step that writes the area, and on the publish
        // too): Intro goes live as the run's own version, while B's draft of Extra stays a draft, dry run and run alike, with
        // a warning that says whose it is and how to name it.
        var whole = Alloy.Area(alloy.Teaser("Ours", name: "Intro"), new JsonObject { ["ref"] = extra });
        whole["MetaDescription"] = "Changed by the editor";
        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "set", "ref": "{{{page}}}", "properties": {{{whole.ToJsonString()}}}, "includeDraft": true},
              {"op": "publish", "ref": "{{{page}}}", "includeDraft": true}
            ]}
            """);
        var runner = new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath());
        var dry = await runner.RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None);
        Assert.All(dry.Operations, o => Assert.NotEqual(PlanStepStatus.Invalid, o.Status));
        Assert.Equal(extraDraft, (await Alloy.GetAsync(extra)).Version);
        var run = await runner.RunAsync(plan, dryRun: false, publishAll: false, CancellationToken.None);
        Assert.All(run.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        foreach (var step in new[] { dry.Operations[1], run.Operations[1] })
        {
            Assert.Equal([intro], Live(step));
            var left = Assert.Single(Warnings(step), w => w.Contains($"('Extra')"));
            Assert.Contains($"is a draft {ColleagueB} saved, so it stays a draft", left);
            Assert.Contains($"--with-block {extra}", left);
        }
        Assert.Equal(("published", "checkedOut"), (await alloy.StatusAsync((await Alloy.GetAsync(intro)).Version!), await alloy.StatusAsync(extraDraft)));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Ours", html);
        Assert.DoesNotContain("Block by B", html);

        // Named, B's draft goes live with the page (published already, so it alone); dry run alike.
        var namedDry = await alloy.RunAsync(new PublishOperation(page) { WithBlocks = [extra] }, dryRun: true);
        Assert.Equal("checkedOut", await alloy.StatusAsync(extraDraft));
        var named = await alloy.RunAsync(new PublishOperation(page) { WithBlocks = [extra] });
        foreach (var shown in new[] { namedDry, named })
        {
            Assert.Equal([extra], shown.SharedBlocks!.Select(b => Id(ContentId(b.Ref!))));
        }
        Assert.False(named.Published);
        Assert.Equal("published", await alloy.StatusAsync(extraDraft));
        Assert.Contains("Block by B", await alloy.RenderAsync(page));
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_project_write_marked_ready_takes_its_blocks_along_and_the_projects_publish_puts_them_live()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var page = await alloy.PageAsync("project");
        var project = await alloy.ProjectAsync("project");

        var written = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add) { Type = "TeaserBlock", Name = "Project block", Values = alloy.TeaserValues("Project block"), Project = project, Ready = true });

        var block = Assert.Single(written.SharedBlocks!);
        Assert.Equal(("checkedIn", "checkedIn"), (await alloy.StatusAsync(written.Version!), await alloy.StatusAsync(block.Ref!)));
        var published = await alloy.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, CancellationToken.None);
        Assert.Equal(2, published.Published!.Count);
        Assert.Equal("published", await alloy.StatusAsync(block.Ref!));
        Assert.Contains("Project block", await alloy.RenderAsync(page));
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_projects_publish_takes_a_named_live_blocks_draft_along_and_keeps_a_named_new_block_back()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        // A page published with its block; then a newer draft of that block, a new block never published (neither in the
        // project), and the page's new draft in the project, marked ready.
        var page = await alloy.PageAsync("project named", Alloy.Area(alloy.Teaser("Project live original", name: "Live")));
        var first = await alloy.RunAsync(new PublishOperation(page));
        var live = Id(ContentId(Assert.Single(first.SharedBlocks!).Ref!));
        await alloy.RunAsync(new SetOperation(live, alloy.TeaserValues("Project live changed")));
        var liveDraft = (await Alloy.GetAsync(live)).Version;
        var added = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add) { Type = "TeaserBlock", Name = "Fresh", Values = alloy.TeaserValues("Project fresh block") });
        var fresh = Assert.Single(added.SharedBlocks!).Ref!;
        var project = await alloy.ProjectAsync("project named");
        await alloy.RunAsync(new SetOperation(page, null, alloy.Name("project named again")) { Project = project, Ready = true });

        // Unnamed (dry run): the new block goes along, the live one stays behind with a warning naming the flag.
        var dry = await alloy.Projects.PublishAsync(project, null, includeDraft: false, dryRun: true, confirm: null, CancellationToken.None);
        var item = Assert.Single(dry.Published!);
        Assert.Equal([ContentId(fresh)], item.SharedBlocks!.Select(b => ContentId(b.Ref!)));
        Assert.Contains(item.Validation!, v => v.Severity == "warning" && v.Message.Contains("is published already")
            && v.Message.Contains($"projects publish {project} --with-block {live}"));

        // A block that isn't one of a ready item's own folder that it shows can't be named; nothing goes live.
        var elsewhere = await Assert.ThrowsAsync<UsageException>(() => alloy.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null,
            CancellationToken.None, withBlocks: [alloy.Start]));
        Assert.Contains("isn't a block of the content's own \"For this page\" folder", elsewhere.Message);
        Assert.Equal("checkedOut", await alloy.StatusAsync(fresh));

        // Named: the live block's draft goes live with the project, the new block stays a draft, as asked.
        var published = await alloy.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, CancellationToken.None,
            withBlocks: [live], withoutBlocks: [fresh]);
        item = Assert.Single(published.Published!);
        Assert.Equal([ContentId(live)], item.SharedBlocks!.Select(b => ContentId(b.Ref!)));
        Assert.Contains(item.Validation!, v => v.Severity == "warning" && v.Message.Contains("stays a draft, as asked"));
        Assert.Equal(("published", "checkedOut"), (await alloy.StatusAsync(liveDraft), await alloy.StatusAsync(fresh)));
        var html = await alloy.RenderAsync(page);
        Assert.Contains("Project live changed", html);
        Assert.DoesNotContain("Project fresh block", html);
    }

    [InlineBlockModeFact(InlineBlockModes.Shared)]
    public async Task A_block_added_to_a_culture_specific_area_of_another_language_is_made_in_that_language()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null || alloy.Site.Session.Language("sv") is null || alloy.Site.Session.Model.Types.All(t => t.Name != "LandingPage"))
        {
            return;
        }
        // Alloy's landing page has a culture-specific MainContentArea.
        var page = await alloy.PageAsync("language", type: "LandingPage", publish: true);
        await alloy.Writes.RunAsync(new TranslateOperation(page, "sv", Name: alloy.Name("sprak")), dryRun: false, CancellationToken.None);

        var added = await alloy.RunAsync(new AreaEdit(page, "MainContentArea", AreaOps.Add, Lang: "sv", Publish: true) { Type = "TeaserBlock", Values = alloy.TeaserValues("Svensk block") });

        var block = Assert.Single(added.SharedBlocks!);
        Assert.Equal("sv", await alloy.MasterLanguageAsync(block.Ref!));
        Assert.Equal(await alloy.GuidAsync(page), (await alloy.FolderAsync(block.Ref!)).Owner);
        Assert.Equal("published", await alloy.StatusAsync(block.Ref!));
        // The English branch's area is its own, without it.
        Assert.Empty(Alloy.AreaOf(await Alloy.GetAsync(page)));
    }

    [InlineBlockModeFact(InlineBlockModes.Inline)]
    public async Task Where_the_site_makes_inline_blocks_a_new_area_block_is_inline_and_a_plans_id_on_it_is_refused()
    {
        await using var alloy = await Alloy.StartAsync();
        if (alloy is null)
        {
            return;
        }
        var newest = await alloy.NewestIdAsync();
        var made = await alloy.Writes.RunAsync(new CreateOperation(alloy.Start, "StandardPage", alloy.Name("inline"), Alloy.Area(alloy.Teaser("Inline made", name: "Intro"))), dryRun: false, CancellationToken.None);
        var page = Id(made.CreatedId!.Value);
        alloy.Track(page);

        Assert.Null(made.Output.SharedBlocks);
        Assert.Equal(newest + 1, await alloy.NewestIdAsync());
        Assert.True(Alloy.AreaOf(await Alloy.GetAsync(page))[0]!["inline"]!.GetValue<bool>());
        await alloy.RunAsync(new PublishOperation(page));
        Assert.Contains("Inline made", await alloy.RenderAsync(page));

        var plan = WritePlan.Parse($$$"""
            {"operations": [
              {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "add", "type": "TeaserBlock", "id": "intro", "values": {"Heading": "x", "Text": "t", "Image": "{{{alloy.Image}}}"}},
              {"op": "set", "ref": "$intro", "properties": {"Text": "y"}}
            ]}
            """);
        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(alloy.Site.Session, alloy.Writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, CancellationToken.None));
        Assert.Contains("the site makes new blocks in a ContentArea inline", refused.Message);
    }
}
