using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary><c>set --from</c>: a change based on the published version while someone else's draft is newer, on a scratch page.</summary>
public sealed class FromVersionTests
{
    [SiteFact]
    public async Task A_change_from_the_published_version_leaves_someone_elses_draft_out_also_when_published()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var created = await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}",
            new JsonObject { ["MetaTitle"] = "Live title" }, Publish: true), dryRun: false, cancellationToken);
        var page = WriteOutput.Id(created.CreatedId!.Value);
        var live = Assert.IsType<WriteOutput>(created.Output).Version!;
        try
        {
            // The agent saves only as opticli; the draft becomes someone else's before the agent lists the versions again
            // (the CMS caches the list until the next save).
            var draft = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaTitle"] = "Their draft" }), dryRun: false, cancellationToken)).Output).Version!;
            await site.Session.Db.QueryAsync("UPDATE tblWorkContent SET ChangedByName = 'someone-else@example.com' WHERE pkID = @version; SELECT @@ROWCOUNT",
                r => r.GetInt32(0), cancellationToken, new SqlParameter("@version", VersionId(draft)));

            // Based on the latest version, a publish would put their draft live.
            var change = new SetOperation(page, new JsonObject { ["MetaDescription"] = "Our change" }) { From = FromVersion.Published };
            Assert.Equal(draft, Assert.IsType<WriteOutput>((await writes.RunAsync(change with { From = null, Publish = true }, dryRun: true, cancellationToken)).Output).PendingDraft?.Version);

            var dry = await writes.RunAsync(change with { Publish = true }, dryRun: true, cancellationToken);
            var dryOutput = Assert.IsType<WriteOutput>(dry.Output);
            Assert.Equal(live, dryOutput.BaseVersion);
            Assert.Null(dryOutput.PendingDraft);
            Assert.Equal(["MetaDescription"], dryOutput.Changes.Select(c => c.Property));
            Assert.Equal((draft, "someone-else@example.com", true), (dryOutput.LeftOut?[0].Version, dryOutput.LeftOut?[0].SavedBy, dryOutput.LeftOut?[0].Primary));
            Assert.Contains(dry.Warnings, w => w.StartsWith($"Based on {live} (the published version), this would leave out the newer version {draft}", StringComparison.Ordinal));

            // The concurrency check stays: --base-version names a version that isn't the latest any more.
            var stale = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(change with { BaseVersion = VersionId(live) }, dryRun: false, cancellationToken));
            Assert.Contains("--from still bases the change on the published version", stale.Hint);
            // A ref that names a version still has to be the latest.
            await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new SetOperation(live, new JsonObject { ["MetaDescription"] = "x" }), dryRun: false, cancellationToken));
            await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(change with { Ref = live }, dryRun: true, cancellationToken));

            var saved = await writes.RunAsync(change, dryRun: false, cancellationToken);
            var ours = Assert.IsType<WriteOutput>(saved.Output);
            Assert.Equal(live, ours.BaseVersion);
            Assert.Equal(ours.Version, await CommonDraftAsync(site, page, cancellationToken));
            Assert.Contains(saved.Warnings, w => w.Contains($"Edit mode now opens {ours.Version} instead of {draft}", StringComparison.Ordinal));
            var ourValues = await GetAsync(site, page, new VersionSelector(VersionKind.Specific, VersionId(ours.Version!)), cancellationToken);
            Assert.Equal("Live title", ourValues.Properties["MetaTitle"]?["value"]?.GetValue<string>());

            // Publishing what is based on the published version needs no --include-draft, plain or after it.
            Assert.Null(Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(page), dryRun: true, cancellationToken)).Output).PendingDraft);
            var published = await writes.RunAsync(change with { Properties = new JsonObject { ["MetaDescription"] = "Live change" }, Publish = true }, dryRun: false, cancellationToken);
            var publishedOutput = Assert.IsType<WriteOutput>(published.Output);
            Assert.True(publishedOutput.Published);
            Assert.Null(publishedOutput.PendingDraft);
            Assert.Equal([ours.Version, draft], publishedOutput.LeftOut!.Select(v => v.Version));
            Assert.Contains(published.Warnings, w => w.StartsWith($"{publishedOutput.Version} is published, based on {live} (the published version)", StringComparison.Ordinal));
            Assert.Equal(publishedOutput.Version, await CommonDraftAsync(site, page, cancellationToken));
            var nowLive = await GetAsync(site, page, VersionSelector.Published, cancellationToken);
            Assert.Equal(("Live title", "Live change"), (nowLive.Properties["MetaTitle"]?["value"]?.GetValue<string>(), nowLive.Properties["MetaDescription"]?["value"]?.GetValue<string>()));
            var versions = await VersionReader.ListAsync(site.Session.Db, site.Session.Model, created.CreatedId.Value, null, 0, 50, cancellationToken);
            Assert.Equal("checkedOut", versions.Single(v => v.Ref == draft).Status);

            // Another draft of theirs, on top of what is live now.
            var second = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaTitle"] = "Their second draft" }), dryRun: false, cancellationToken)).Output).Version!;
            await site.Session.Db.QueryAsync("UPDATE tblWorkContent SET ChangedByName = 'someone-else@example.com' WHERE pkID = @version; SELECT @@ROWCOUNT",
                r => r.GetInt32(0), cancellationToken, new SqlParameter("@version", VersionId(second)));

            // In a plan, a later set builds on the change from the published version, and the publish is dry-run on it too;
            // without "from" the publish would put their draft live.
            var runner = new PlanRunner(site.Session, writes, Path.GetTempPath());
            var plain = WritePlan.Parse($$$"""{"operations": [{"op": "set", "ref": "{{{page}}}", "properties": {"MetaKeywords": ["plan"]}}, {"op": "publish", "ref": "{{{page}}}"}]}""");
            Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAnyAsync<OptiCliException>(() => runner.RunAsync(plain, dryRun: true, publishAll: false, cancellationToken))).Code);
            var plan = WritePlan.Parse($$$"""{"operations": [{"op": "set", "ref": "{{{page}}}", "from": "published", "properties": {"MetaDescription": "Plan"}}, {"op": "set", "ref": "{{{page}}}", "properties": {"MetaKeywords": ["plan"]}}, {"op": "publish", "ref": "{{{page}}}"}]}""");
            var dryPlan = await runner.RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
            Assert.Equal([PlanStepStatus.Valid, PlanStepStatus.Simulated, PlanStepStatus.Simulated], dryPlan.Operations.Select(o => o.Status));
            Assert.Equal(["MetaDescription", "MetaKeywords"], Assert.IsType<WriteOutput>(dryPlan.Operations[1].Result).Changes.Select(c => c.Property).Order(StringComparer.Ordinal));
            var run = await runner.RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
            Assert.True(Assert.IsType<WriteOutput>(run.Operations[2].Result).Published);
            var afterPlan = await GetAsync(site, page, VersionSelector.Published, cancellationToken);
            Assert.Equal(("Live title", "Plan"), (afterPlan.Properties["MetaTitle"]?["value"]?.GetValue<string>(), afterPlan.Properties["MetaDescription"]?["value"]?.GetValue<string>()));
            Assert.Equal("plan", afterPlan.Properties["MetaKeywords"]?["value"]?[0]?.GetValue<string>());
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    private static int VersionId(string versionRef) => int.Parse(versionRef.Split('_')[1], CultureInfo.InvariantCulture);

    private static Task<ContentDocument> GetAsync(SiteUnderTest site, string id, VersionSelector version, CancellationToken cancellationToken) =>
        new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model))
            .GetAsync(int.Parse(id, CultureInfo.InvariantCulture), version, null, new Core.Properties.DecodeOptions(), cancellationToken);

    private static async Task<string?> CommonDraftAsync(SiteUnderTest site, string id, CancellationToken cancellationToken)
    {
        var rows = await site.Session.Db.QueryAsync("SELECT pkID FROM tblWorkContent WHERE fkContentID = @id AND CommonDraft = 1", r => r.GetInt32(0), cancellationToken, new SqlParameter("@id", int.Parse(id, CultureInfo.InvariantCulture)));
        return rows.Count == 1 ? $"{id}_{rows[0]}" : null;
    }
}
