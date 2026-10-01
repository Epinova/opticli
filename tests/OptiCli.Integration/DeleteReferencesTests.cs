using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>A delete of content other content references, on scratch pages below the edge-case site's language root.</summary>
public sealed class DeleteReferencesTests
{
    [SiteFact]
    public async Task A_delete_of_referenced_content_stops_unless_confirmed_or_a_plan_removes_the_reference_first()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        // An EdgePage, which has a ContentArea and allows pages below it.
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId)!;
        var scratch = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            var target = WriteOutput.Id((await writes.RunAsync(new CreateOperation(scratch, type, "referenced"), dryRun: false, cancellationToken)).CreatedId!.Value);
            var owner = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it owner {Guid.NewGuid():N}",
                new JsonObject { ["MainContentArea"] = new JsonArray(new JsonObject { ["ref"] = target }) }), dryRun: false, cancellationToken)).CreatedId!.Value);
            try
            {
                // The scratch page's own child doesn't count; the owner outside it does, also when deleting the parent.
                var dry = Assert.IsType<MoveOutput>((await writes.RunAsync(new DeleteOperation(scratch), dryRun: true, cancellationToken)).Output);
                var reference = Assert.Single(dry.References!);
                Assert.Equal((owner, target, "MainContentArea", "contentArea"), (reference.From, reference.To, reference.Property, reference.Kind));

                var stopped = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new DeleteOperation(target), dryRun: false, cancellationToken));
                Assert.Equal(IncomingReferences.Reason, Assert.IsType<ReferencedDetails>(stopped.Details).Reason);
                Assert.Contains("--ignore-references", stopped.Hint);

                var plan = WritePlan.Parse($$"""{"operations": [{"op": "delete", "ref": "{{target}}"}]}""");
                var invalid = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
                Assert.Equal(ErrorCode.Conflict, invalid.Code);

                // Taking it out of the area first: the plan validates with a warning, and the delete finds nothing left.
                var removing = WritePlan.Parse($$"""
                    {"operations": [
                      {"op": "area", "ref": "{{owner}}", "property": "MainContentArea", "action": "remove", "item": "{{target}}"},
                      {"op": "delete", "ref": "{{target}}"}]}
                    """);
                var checkedPlan = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(removing, dryRun: true, publishAll: false, cancellationToken);
                Assert.Contains(checkedPlan.Operations[1].Warnings!, w => w.Contains("earlier operations change", StringComparison.Ordinal));
                var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(removing, dryRun: false, publishAll: false, cancellationToken);
                Assert.True(Assert.IsType<MoveOutput>(run.Operations[1].Result).Moved);
            }
            finally
            {
                await writes.RunAsync(new DeleteOperation(owner, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(scratch, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
