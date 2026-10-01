using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>Content approval sequences, below the edge-case site's approval root.</summary>
public sealed class ApprovalTests
{
    [SiteFact]
    public async Task A_publish_under_an_approval_sequence_is_refused_and_a_review_request_starts_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.ApprovalRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId)!;
        var rootRef = WriteOutput.Id(root.Id);
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(rootRef, type, $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            var sequence = await ApprovalReader.ResolveAsync(site.Session.Db, site.Session.Model, header!, cancellationToken);
            Assert.Equal((rootRef, true), (sequence?.DefinedOn, sequence?.Inherited));

            // Refused as a dry run, for real, as a publish, and as a plan step that creates published content below it.
            var publish = new SetOperation(page, Name: "published", Publish: true);
            var refused = await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(publish, dryRun: true, cancellationToken));
            Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
            Assert.Contains("--request-approval", refused.Hint);
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(publish, dryRun: false, cancellationToken));
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new PublishOperation(page), dryRun: true, cancellationToken));
            var plan = WritePlan.Parse($$"""
                {"operations": [
                  {"op": "create", "id": "a", "parent": "{{rootRef}}", "type": "{{type}}", "name": "opticli-it a"},
                  {"op": "create", "id": "b", "parent": "$a", "type": "{{type}}", "name": "opticli-it b", "publish": true}]}
                """);
            var invalid = await Assert.ThrowsAnyAsync<OptiCliException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
            Assert.Equal(ErrorCode.Refused, invalid.Code);
            var confirmed = await new PlanRunner(site.Session, writes, Path.GetTempPath(), requestApproval: true).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
            Assert.All(confirmed.Operations, o => Assert.NotEqual(PlanStepStatus.Invalid, o.Status));

            var review = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, Name: "for review") { RequestApproval = true }, dryRun: false, cancellationToken)).Output);
            Assert.Equal((true, false, "awaitingApproval"), (review.ApprovalRequested, review.Published, review.Status));
            Assert.Contains("sent for review", UndoHints.For(new SetOperation(page) { RequestApproval = true }, review));
            var drafts = await new DraftReader(site.Session).ListAsync(null, null, null, null, 0, 20, cancellationToken);
            Assert.Equal("awaitingApproval", drafts.First(d => d.Ref == page).Status);

            // In review, it can't be changed until a reviewer decides.
            var inReview = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new SetOperation(page, Name: "changed meanwhile"), dryRun: false, cancellationToken));
            Assert.Equal(AgentErrorReasons.InReview, Assert.IsType<AgentErrorDetails>(inReview.Details).Reason);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page), dryRun: false, cancellationToken);
        }
    }
}
