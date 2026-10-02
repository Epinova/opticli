using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>translate --with-blocks and --remove, and publishing branches before the master, on scratch pages below the edge-case site's language root.</summary>
public sealed class LanguageBranchTests
{
    [SiteFact]
    public async Task A_branch_is_not_published_before_its_master_branch_has_been_published()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        // Master branch en, never published.
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(new TranslateOperation(page, "sv", "opticli-it sv", Publish: true), dryRun: true, cancellationToken));
            await writes.RunAsync(new TranslateOperation(page, "sv", "opticli-it sv"), dryRun: false, cancellationToken);
            var versions = await VersionCountAsync(site, page, cancellationToken);

            var publish = await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(new PublishOperation(page, Lang: "sv"), dryRun: true, cancellationToken));
            Assert.StartsWith($"Dry run: the 'sv' branch of {page} ", publish.Message);
            Assert.Contains("before its master language ('en') is published", publish.Message);
            Assert.Contains($"opticli publish {page}", publish.Hint);
            var set = new SetOperation(page, Name: "opticli-it sv 2", Lang: "sv", Publish: true);
            await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(set, dryRun: true, cancellationToken));

            // The real run is refused before anything is saved, not halfway by the CMS.
            var refused = await Assert.ThrowsAsync<ContentValidationException>(() => writes.RunAsync(set, dryRun: false, cancellationToken));
            Assert.Contains("Nothing was saved", refused.Message);
            Assert.Equal(versions, await VersionCountAsync(site, page, cancellationToken));

            // A scheduled publish is saved by the CMS, and fails only when it comes due: a warning.
            var scheduled = await writes.RunAsync(new PublishOperation(page, Lang: "sv") { PublishAt = DateTimeOffset.UtcNow.AddDays(2) }, dryRun: true, cancellationToken);
            Assert.Contains(scheduled.Warnings, w => w.Contains("scheduled publish fails when it comes due", StringComparison.Ordinal));

            await writes.RunAsync(new PublishOperation(page), dryRun: false, cancellationToken);
            var published = Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(page, Lang: "sv"), dryRun: false, cancellationToken)).Output);
            Assert.True(published.Published);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_plan_that_publishes_a_branch_before_its_master_is_rejected_by_its_dry_run()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var name = $"opticli-it {Guid.NewGuid():N}";
        WritePlan Plan(params string[] publishes) => WritePlan.Parse($$"""
            {"operations": [
              {"op": "create", "id": "p", "parent": "{{WriteOutput.Id(root.Id)}}", "type": "StandardPage", "name": "{{name}}"},
              {"op": "translate", "ref": "$p", "lang": "sv", "name": "{{name}} sv"},
              {{string.Join(", ", publishes)}}
            ]}
            """);
        const string Branch = """{"op": "publish", "ref": "$p", "lang": "sv"}""";
        const string Master = """{"op": "publish", "ref": "$p"}""";

        var rejected = await Assert.ThrowsAnyAsync<OptiCliException>(() =>
            new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(Plan(Branch, Master), dryRun: true, publishAll: false, cancellationToken));
        Assert.Equal(ErrorCode.Validation, rejected.Code);
        Assert.Contains("operation 2 publishes the 'sv' branch of $p before its master language ('en')", rejected.Message);
        Assert.Equal("Move operation 3, which publishes 'en', before operation 2.", rejected.Hint);
        Assert.Equal(PlanStepStatus.Invalid, Assert.IsType<PlanRun>(rejected.Details).Operations[2].Status);
        Assert.Empty(await NamedAsync(site, name, cancellationToken));

        var reordered = Plan(Master, Branch);
        var created = new List<string>();
        try
        {
            var dry = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(reordered, dryRun: true, publishAll: false, cancellationToken);
            Assert.Contains(dry.Operations[3].Warnings!, w => w.Contains("operation 2 publishes it first", StringComparison.Ordinal));

            var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(reordered, dryRun: false, publishAll: false, cancellationToken);
            created.AddRange(run.Created.Values);
            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(run.Created["p"], System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.All(header!.Languages.Values, l => Assert.Equal(VersionStatus.Published, l.Status));
            Assert.Equal(2, header.Languages.Count);
        }
        finally
        {
            foreach (var id in created)
            {
                await writes.RunAsync(new DeleteOperation(id, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }

    private static async Task<int> VersionCountAsync(SiteUnderTest site, string id, CancellationToken cancellationToken) =>
        (await VersionReader.ListAsync(site.Session.Db, site.Session.Model, int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), null, 0, 100, cancellationToken)).Count;

    private static Task<IReadOnlyList<int>> NamedAsync(SiteUnderTest site, string name, CancellationToken cancellationToken) =>
        site.Session.Db.QueryAsync("SELECT fkContentID FROM tblContentLanguage WHERE Name = @name", r => r.GetInt32(0), cancellationToken,
            new Microsoft.Data.SqlClient.SqlParameter("@name", name));

    [SiteFact]
    public async Task A_translation_with_blocks_translates_the_for_this_page_blocks_and_a_branch_can_be_removed_again()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            var block = WriteOutput.Id((await writes.RunAsync(new BlockCreateOperation("EditorialBlock", "opticli-it block", For: page, Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);

            var translate = new TranslateOperation(page, "sv", "opticli-it sv", Publish: true) { WithBlocks = true };
            var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(translate, dryRun: true, cancellationToken)).Output);
            Assert.Equal((block, "translated"), (Assert.Single(dry.Blocks!).Ref, dry.Blocks![0].Status));
            var saved = await writes.RunAsync(translate, dryRun: false, cancellationToken);
            var output = Assert.IsType<WriteOutput>(saved.Output);
            Assert.NotNull(Assert.Single(output.Blocks!).Version);
            var blockHeader = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(block, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.Contains(site.Session.Model.LanguageByCode("sv")!.Id, blockHeader!.Languages.Keys);
            Assert.Contains($"opticli translate {page} --lang sv --remove --confirm", UndoHints.For(translate, output));

            // Removing needs confirming, never the master language.
            var remove = new TranslateOperation(page, "sv") { Remove = true };
            var plan = Assert.IsType<RemoveLanguageOutput>((await writes.RunAsync(remove, dryRun: true, cancellationToken)).Output);
            Assert.Equal((true, false), (plan.Published, plan.Removed));
            var stopped = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(remove, dryRun: false, cancellationToken));
            Assert.Contains("--confirm", stopped.Hint);
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new TranslateOperation(page, "en") { Remove = true, Confirm = true }, dryRun: true, cancellationToken));

            var removed = Assert.IsType<RemoveLanguageOutput>((await writes.RunAsync(remove with { Confirm = true }, dryRun: false, cancellationToken)).Output);
            Assert.True(removed.Removed);
            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.DoesNotContain(site.Session.Model.LanguageByCode("sv")!.Id, header!.Languages.Keys);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
