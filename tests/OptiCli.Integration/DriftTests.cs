using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// Drift between the site's build and its database. Against a local database nothing is compared; against a shared one
/// (a copy of the edge-case database reached by a host name, chosen with `opticli db use`) writes stop while anything
/// differs, until the fingerprint confirms it.
/// </summary>
public sealed class DriftTests
{
    /// <summary>Content that doesn't exist: a write to it that gets past the drift check fails with not_found, and saves nothing.</summary>
    private const string Missing = "2147483646";

    [SiteFact]
    public async Task Writes_stop_on_drift_until_its_fingerprint_confirms_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var write = new DraftRequest { Name = "opticli-it drift" };
        if (site.Session.Db.ConnectionString.IsLocal)
        {
            // Nothing to compare, so the agent isn't even asked (one from an older opticli couldn't answer).
            await new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent)).RequireDriftAcceptedAsync(cancellationToken);
            await Assert.ThrowsAsync<NotFoundException>(() => site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(Missing), write, cancellationToken));
            return;
        }
        var report = await site.Agent.DriftAsync(cancellationToken);
        Assert.True(report.Checked);
        if (report.Fingerprint is null)
        {
            await new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent)).RequireDriftAcceptedAsync(cancellationToken);
            return;
        }

        // The site agent stops it on its own, and a dry run passes.
        var refused = await Assert.ThrowsAsync<DriftException>(() => site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(Missing), write, cancellationToken));
        Assert.Equal(report.Fingerprint, Assert.IsType<DriftReport>(refused.Details).Fingerprint);
        await Assert.ThrowsAsync<NotFoundException>(() => site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(Missing), write with { DryRun = true }, cancellationToken));

        // The CLI stops it before sending anything, also with a fingerprint of other differences.
        await Assert.ThrowsAsync<DriftException>(() => new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent)).RequireDriftAcceptedAsync(cancellationToken));
        await Assert.ThrowsAsync<DriftException>(() => new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), acceptDrift: "0123456789ab").RequireDriftAcceptedAsync(cancellationToken));

        await new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), acceptDrift: report.Fingerprint).RequireDriftAcceptedAsync(cancellationToken);
        await Assert.ThrowsAsync<NotFoundException>(() => site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(Missing), write, cancellationToken));
    }
}
