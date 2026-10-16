using System.Globalization;
using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>apply</c> saves a plan's changes to one page and language into one draft (<see cref="PlanDrafts"/>), as the edit UI
/// does, and a version per operation with <see cref="PlanRunner.VersionPerStep"/>; after a publish, the next change
/// starts a new draft. On scratch pages below the edge-case language root.
/// </summary>
public sealed class PlanDraftTests
{
    [SiteFact]
    public async Task A_plans_changes_to_a_page_go_into_one_draft_and_after_a_publish_into_a_new_one()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var pages = new List<string>();
        async Task<(int Id, string Ref, int Live)> PageAsync()
        {
            var created = await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}",
                new JsonObject { ["MetaTitle"] = "Live title" }, Publish: true), dryRun: false, cancellationToken);
            var id = created.CreatedId!.Value;
            pages.Add(WriteOutput.Id(id));
            return (id, WriteOutput.Id(id), VersionId(Assert.IsType<WriteOutput>(created.Output).Version!));
        }
        static WritePlan Plan(string page, bool publishInTheMiddle = false)
        {
            var publish = publishInTheMiddle ? $$"""{"op": "publish", "ref": "{{page}}"},""" : "";
            return WritePlan.Parse($$$"""
                {"operations": [
                  {"op": "block", "id": "one", "type": "EditorialBlock", "name": "opticli-it one", "for": "{{{page}}}"},
                  {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "add", "item": "$one"},
                  {{{publish}}}
                  {"op": "block", "id": "two", "type": "EditorialBlock", "name": "opticli-it two", "for": "{{{page}}}"},
                  {"op": "area", "ref": "{{{page}}}", "property": "MainContentArea", "action": "add", "item": "$two"},
                  {"op": "set", "ref": "{{{page}}}", "properties": {"MetaTitle": "Plan title"}}]}
                """);
        }
        try
        {
            // One new draft with all of it; the later operations say they saved into it, and point to its undo.
            var (id, page, live) = await PageAsync();
            var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(Plan(page), dryRun: false, publishAll: false, cancellationToken);
            var versions = await VersionIdsAsync(site, id, cancellationToken);
            Assert.Equal(2, versions.Count);
            var draft = versions.Max();
            var draftRef = WriteOutput.VersionRef(id, draft);
            Assert.True(run.Operations.All(o => o.Status == PlanStepStatus.Saved), string.Join(", ", run.Operations.Select(o => o.Status)));
            Assert.Equal([draftRef, draftRef, draftRef], new[] { 1, 3, 4 }.Select(i => Assert.IsType<WriteOutput>(run.Operations[i].Result).Version));
            Assert.Null(run.Operations[1].SavedInto);
            Assert.Null(Assert.IsType<WriteOutput>(run.Operations[1].Result).IntoVersion);
            Assert.Equal([new SavedInto(draftRef, 1), new SavedInto(draftRef, 1)], new[] { 3, 4 }.Select(i => run.Operations[i].SavedInto));
            Assert.True(Assert.IsType<WriteOutput>(run.Operations[4].Result).IntoVersion);
            Assert.StartsWith($"{draftRef} is an unpublished draft", run.Operations[1].Undo, StringComparison.Ordinal);
            Assert.StartsWith($"Saved into {draftRef}, the draft operation 1 made, so nothing live changed; operation 1's undo covers this change too (", run.Operations[4].Undo, StringComparison.Ordinal);
            Assert.All(run.Operations, o => Assert.DoesNotContain(o.Warnings ?? [], w => w.StartsWith("Saved as a new version", StringComparison.Ordinal)));
            var latest = await GetAsync(site, id, draft, cancellationToken);
            Assert.Equal(2, Area(latest).Count);
            Assert.Equal("Plan title", latest.Properties["MetaTitle"]?["value"]?.GetValue<string>());
            Assert.Equal("Live title", (await GetAsync(site, id, live, cancellationToken)).Properties["MetaTitle"]?["value"]?.GetValue<string>());
            // The blocks each have one version, their own.
            foreach (var block in run.Created.Values)
            {
                Assert.Single(await VersionIdsAsync(site, int.Parse(block, CultureInfo.InvariantCulture), cancellationToken));
            }

            // Another run on the same page starts a draft of its own, and saves its later changes into that one.
            var again = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(
                WritePlan.Parse($$$"""{"operations": [{"op": "set", "ref": "{{{page}}}", "properties": {"MetaDescription": "Second run"}}, {"op": "set", "ref": "{{{page}}}", "properties": {"MetaKeywords": ["second"]}}]}"""),
                dryRun: false, publishAll: false, cancellationToken);
            Assert.Null(again.Operations[0].SavedInto);
            Assert.Equal(new SavedInto(WriteOutput.VersionRef(id, VersionId(Assert.IsType<WriteOutput>(again.Operations[0].Result).Version!)), 0), again.Operations[1].SavedInto);

            // --version-per-step: a version per operation on the page.
            (id, page, live) = await PageAsync();
            var perStep = await new PlanRunner(site.Session, writes, Path.GetTempPath()) { VersionPerStep = true }.RunAsync(Plan(page), dryRun: false, publishAll: false, cancellationToken);
            Assert.Equal(4, (await VersionIdsAsync(site, id, cancellationToken)).Count);
            Assert.All(perStep.Operations, o => Assert.Null(o.SavedInto));
            Assert.Equal(3, new[] { 1, 3, 4 }.Select(i => Assert.IsType<WriteOutput>(perStep.Operations[i].Result).Version).Distinct().Count());

            // A publish in the middle publishes the draft; the changes after it go into a new one.
            (id, page, live) = await PageAsync();
            var middle = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(Plan(page, publishInTheMiddle: true), dryRun: false, publishAll: false, cancellationToken);
            versions = await VersionIdsAsync(site, id, cancellationToken);
            Assert.Equal(3, versions.Count);
            var published = Assert.IsType<WriteOutput>(middle.Operations[2].Result);
            Assert.True(published.Published);
            Assert.Equal(Assert.IsType<WriteOutput>(middle.Operations[1].Result).Version, published.Version);
            var after = WriteOutput.VersionRef(id, versions.Max());
            Assert.Null(middle.Operations[4].SavedInto);
            Assert.Equal(after, Assert.IsType<WriteOutput>(middle.Operations[4].Result).Version);
            Assert.Equal(new SavedInto(after, 4), middle.Operations[5].SavedInto);
            var afterPublish = await GetAsync(site, id, versions.Max(), cancellationToken);
            Assert.Equal(2, Area(afterPublish).Count);
            Assert.Equal("Plan title", afterPublish.Properties["MetaTitle"]?["value"]?.GetValue<string>());
            Assert.Single(Area(await GetAsync(site, id, VersionId(published.Version!), cancellationToken)));

            // A draft someone else took over isn't saved into: the database check (and the agent's) makes a new version.
            var theirs = Assert.IsType<WriteOutput>(middle.Operations[5].Result).Version!;
            Assert.True(await site.HandOverAsync(theirs, cancellationToken), "The edge-case site's EdgeCasesFixture.cs has no /opticli-fixture/hand-over (run setup.sh again).");
            var notTheirs = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = "Not theirs" }) { IntoVersion = VersionId(theirs) }, dryRun: false, cancellationToken)).Output);
            Assert.Null(notTheirs.IntoVersion);
            Assert.True(VersionId(notTheirs.Version!) > VersionId(theirs));
        }
        finally
        {
            foreach (var page in pages)
            {
                await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }

    private static int VersionId(string versionRef) => int.Parse(versionRef.Split('_')[1], CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<int>> VersionIdsAsync(SiteUnderTest site, int id, CancellationToken cancellationToken) =>
        (await VersionReader.ListAsync(site.Session.Db, site.Session.Model, id, null, 0, 50, cancellationToken)).Select(v => v.Id).Order().ToList();

    private static Task<ContentDocument> GetAsync(SiteUnderTest site, int id, int version, CancellationToken cancellationToken) =>
        new ContentLoader(site.Session.Db, site.Session.Identities).GetAsync(id, new VersionSelector(VersionKind.Specific, version), null, new DecodeOptions(), cancellationToken);

    private static JsonArray Area(ContentDocument document) => document.Properties["MainContentArea"]?["value"]?.AsArray() ?? [];
}
