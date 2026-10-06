using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>
/// <c>trash</c> and <c>restore</c> on scratch pages below the edge-case site's language root: what was deleted comes back
/// below the parent the CMS stored, with its status, and what stands in the way is a conflict.
/// </summary>
public sealed class RestoreTests
{
    [SiteFact]
    public async Task Deleted_content_is_listed_with_its_original_parent_and_restored_there_as_it_was()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId);
        var name = $"opticli-it restore {Guid.NewGuid():N}";
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, name, Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);
        await writes.RunAsync(new CreateOperation(page, type, "child"), dryRun: false, cancellationToken);
        try
        {
            await writes.RunAsync(new DeleteOperation(page), dryRun: false, cancellationToken);

            var listed = (await new TrashReader(site.Session).ListAsync(DateTime.UtcNow.AddMinutes(-5), null, null, 0, 1000, cancellationToken)).Single(i => i.Ref == page);
            // The site answers as the database's copy of the CMS's store does.
            var throughSite = new TrashReader(site.Session, _ => Task.FromResult(site.Agent));
            var fromSite = (await throughSite.ListAsync(DateTime.UtcNow.AddMinutes(-5), null, null, 0, 1000, cancellationToken)).Single(i => i.Ref == page);
            Assert.Equal(("site", listed.OriginalParent), (throughSite.ParentsFrom, fromSite.OriginalParent));
            Assert.Equal((name, "opticli", 1), (listed.Name, listed.DeletedBy, listed.Descendants));
            Assert.Equal(WriteOutput.Id(root.Id), listed.OriginalParent!.Ref);
            Assert.EndsWith(" / Language settings root", listed.OriginalParent.Path);
            Assert.Null(listed.OriginalParent.Deleted);

            var dry = Assert.IsType<RestoreOutput>((await writes.RunAsync(new RestoreOperation(page), dryRun: true, cancellationToken)).Output);
            Assert.Equal((WriteOutput.Id(root.Id), "originalParent", false, 1), (dry.Parent, dry.From, dry.Restored, dry.Descendants));

            var restored = await writes.RunAsync(new RestoreOperation(page), dryRun: false, cancellationToken);
            var output = Assert.IsType<RestoreOutput>(restored.Output);
            Assert.True(output.Restored);
            Assert.Contains(restored.Warnings, w => w.Contains("live again", StringComparison.Ordinal));
            Assert.Equal($"opticli delete {page} (moves it back to the recycle bin)", UndoHints.For(new RestoreOperation(page), output));

            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.Equal((root.Id, false, VersionStatus.Published), (header!.ParentId, header.Deleted, header.LanguageRow(null)!.Status));

            var again = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new RestoreOperation(page), dryRun: false, cancellationToken));
            Assert.Contains("not in the recycle bin", again.Message);
        }
        finally
        {
            if (await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), cancellationToken) is { Deleted: false })
            {
                await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }

    [SiteFact]
    public async Task A_child_deleted_before_its_parent_waits_for_the_parent_or_goes_elsewhere()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId);
        var parent = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it restore parent {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var child = WriteOutput.Id((await writes.RunAsync(new CreateOperation(parent, type, "child"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var deleted = new List<string>();
        try
        {
            await writes.RunAsync(new DeleteOperation(child), dryRun: false, cancellationToken);
            await writes.RunAsync(new DeleteOperation(parent), dryRun: false, cancellationToken);
            deleted.AddRange([child, parent]);

            var listed = (await new TrashReader(site.Session).ListAsync(DateTime.UtcNow.AddMinutes(-5), null, null, 0, 1000, cancellationToken)).Single(i => i.Ref == child);
            Assert.Equal((parent, true), (listed.OriginalParent!.Ref, listed.OriginalParent.Deleted));

            var waiting = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new RestoreOperation(child), dryRun: true, cancellationToken));
            Assert.Contains("is in the recycle bin too", waiting.Message);
            Assert.Contains($"opticli restore {parent}", waiting.Hint);

            // Elsewhere, as --to says: the stored parent is still reported.
            var elsewhere = Assert.IsType<RestoreOutput>((await writes.RunAsync(new RestoreOperation(child, WriteOutput.Id(root.Id)), dryRun: false, cancellationToken)).Output);
            Assert.Equal((WriteOutput.Id(root.Id), "to", parent), (elsewhere.Parent, elsewhere.From, elsewhere.OriginalParent));
            deleted.Remove(child);

            // A plan that deletes and restores: the restore can only be checked when it runs.
            var plan = WritePlan.Parse($$"""{"operations": [{"op": "delete", "ref": "{{child}}"}, {"op": "restore", "ref": "{{child}}", "to": "{{root.Id}}"}]}""");
            var checkedPlan = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
            Assert.Equal(PlanStepStatus.Deferred, checkedPlan.Operations[1].Status);
            var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
            Assert.True(Assert.IsType<RestoreOutput>(run.Operations[1].Result).Restored);
        }
        finally
        {
            foreach (var reference in new[] { child, parent }.Except(deleted))
            {
                await writes.RunAsync(new DeleteOperation(reference, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }

    [SiteFact]
    public async Task A_plan_that_deletes_an_item_and_restores_it_under_another_ref_checks_the_restore_when_it_runs()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId);
        var id = (await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it restore ref {Guid.NewGuid():N}", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value;
        var page = WriteOutput.Id(id);
        try
        {
            var header = (await ContentHeaderReader.ByIdAsync(site.Session.Db, id, cancellationToken))!;
            await site.Session.Identities.LoadAsync([id], [], cancellationToken);
            var path = site.Session.Identities.Describe(header, null).Url!;
            // Deleted by id, restored by GUID: the dry run sees it is the same item, and the run restores it.
            var byGuid = WritePlan.Parse($$"""{"operations": [{"op": "delete", "ref": "{{page}}"}, {"op": "restore", "ref": "{{header.Guid:D}}"}]}""");
            var checkedPlan = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(byGuid, dryRun: true, publishAll: false, cancellationToken);
            Assert.Equal(PlanStepStatus.Deferred, checkedPlan.Operations[1].Status);
            var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(byGuid, dryRun: false, publishAll: false, cancellationToken);
            Assert.True(Assert.IsType<RestoreOutput>(run.Operations[1].Result).Restored);

            // By its path: the dry run matches it too, but content in the recycle bin has no URL, so the run can't find it.
            var byPath = WritePlan.Parse($$"""{"operations": [{"op": "delete", "ref": "{{header.Guid:D}}"}, {"op": "restore", "ref": "{{path}}"}]}""");
            checkedPlan = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(byPath, dryRun: true, publishAll: false, cancellationToken);
            Assert.Equal(PlanStepStatus.Deferred, checkedPlan.Operations[1].Status);
            var failed = await Assert.ThrowsAsync<NotFoundException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(byPath, dryRun: false, publishAll: false, cancellationToken));
            Assert.Contains("id or GUID", failed.Hint);
        }
        finally
        {
            if (await ContentHeaderReader.ByIdAsync(site.Session.Db, id, cancellationToken) is { Deleted: false })
            {
                await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }

    [SiteFact]
    public async Task Content_below_deleted_content_names_what_to_restore_instead()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId);
        var parent = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it restore below {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var child = WriteOutput.Id((await writes.RunAsync(new CreateOperation(parent, type, "child"), dryRun: false, cancellationToken)).CreatedId!.Value);
        await writes.RunAsync(new DeleteOperation(parent), dryRun: false, cancellationToken);

        var ex = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new RestoreOperation(child), dryRun: true, cancellationToken));

        Assert.Contains($"because {parent}", ex.Message);
        Assert.Contains($"opticli restore {parent}", ex.Hint);
    }
}
