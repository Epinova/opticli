using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>--publish-at, on scratch pages below the edge-case site's language and approval roots.</summary>
public sealed class ScheduleTests
{
    [SiteFact]
    public async Task A_scheduled_publish_is_a_delayed_version_that_discard_cancels_and_approvals_refuse()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root
            || await EdgeFixture.FindAsync(site, EdgeFixture.ApprovalRoot, cancellationToken) is not { } approvalRoot)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);
        var reviewed = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(approvalRoot.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            var at = new DateTimeOffset(DateTime.UtcNow.AddDays(2).Date.AddHours(8), TimeSpan.Zero);
            var schedule = new SetOperation(page, Name: "scheduled") { PublishAt = at };
            Assert.Equal(at.UtcDateTime, Assert.IsType<WriteOutput>((await writes.RunAsync(schedule, dryRun: true, cancellationToken)).Output).ScheduledFor);

            var scheduled = Assert.IsType<WriteOutput>((await writes.RunAsync(schedule, dryRun: false, cancellationToken)).Output);
            Assert.Equal(("delayedPublish", false, at.UtcDateTime), (scheduled.Status, scheduled.Published, scheduled.ScheduledFor));
            var draft = (await new DraftReader(site.Session).ListAsync(null, null, null, null, 0, 20, cancellationToken)).First(d => d.Ref == page);
            Assert.Equal(("delayedPublish", at.UtcDateTime), (draft.Status, draft.PublishAt));
            Assert.Contains("opticli discard", UndoHints.For(schedule, scheduled));

            var cancelled = Assert.IsType<WriteOutput>((await writes.RunAsync(new DiscardOperation(scheduled.Version!), dryRun: false, cancellationToken)).Output);
            Assert.True(cancelled.Discarded);

            // An approval sequence applies to a scheduled publish as to one now.
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new SetOperation(reviewed, Name: "later") { PublishAt = at }, dryRun: true, cancellationToken));
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new PublishOperation(reviewed) { PublishAt = at }, dryRun: true, cancellationToken));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
            await writes.RunAsync(new DeleteOperation(reviewed, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
