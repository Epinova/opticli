using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>unpublish and discard, on scratch pages below the edge-case site's language root.</summary>
public sealed class UnpublishDiscardTests
{
    [SiteFact]
    public async Task Unpublish_takes_a_page_offline_keeps_the_draft_and_is_undone_by_publishing_the_version_that_was_live()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await ScratchParentAsync(site, cancellationToken) is not { } parent)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var page = await CreateAsync(writes, parent, publish: true, cancellationToken);
        try
        {
            var draft = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, Name: "draft"), dryRun: false, cancellationToken)).Output);
            var offline = Assert.IsType<WriteOutput>((await writes.RunAsync(new UnpublishOperation(page), dryRun: false, cancellationToken)).Output);
            Assert.Equal((true, false), (offline.Unpublished, offline.Published));
            var undo = UndoHints.For(new UnpublishOperation(page), offline);
            Assert.Equal($"{page} is offline in 'en'; to put it back, publish the version that was live: opticli publish {page} --version {offline.PreviouslyPublished!.Split('_')[1]}", undo);

            var shown = await GetAsync(site, page, VersionSelector.Published, cancellationToken);
            Assert.True(shown.StopPublish <= DateTime.UtcNow);
            Assert.Contains(shown.Notes!, n => n.StartsWith("Offline", StringComparison.Ordinal));
            Assert.Equal(draft.Version, await CommonDraftAsync(site, page, cancellationToken));
            await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new UnpublishOperation(page), dryRun: false, cancellationToken));

            var back = Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(page, int.Parse(offline.PreviouslyPublished.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture)), dryRun: false, cancellationToken)).Output);
            Assert.True(back.Published);
            Assert.Null((await GetAsync(site, page, VersionSelector.Published, cancellationToken)).StopPublish);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Discard_deletes_a_draft_but_not_the_published_or_only_version_and_asks_for_someone_elses()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await ScratchParentAsync(site, cancellationToken) is not { } parent)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var page = await CreateAsync(writes, parent, publish: false, cancellationToken);
        try
        {
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new DiscardOperation(page), dryRun: true, cancellationToken));
            await writes.RunAsync(new PublishOperation(page), dryRun: false, cancellationToken);
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new DiscardOperation(page), dryRun: true, cancellationToken));

            // The agent saves only as opticli; the draft becomes someone else's before the agent lists the versions again
            // (the CMS caches the list until the next save).
            var draft = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, Name: "someone else's"), dryRun: false, cancellationToken)).Output);
            await site.Session.Db.QueryAsync("UPDATE tblWorkContent SET ChangedByName = 'someone-else@example.com' WHERE pkID = @version; SELECT @@ROWCOUNT",
                r => r.GetInt32(0), cancellationToken, new SqlParameter("@version", int.Parse(draft.Version!.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture)));

            var dry = await writes.RunAsync(new DiscardOperation(page), dryRun: true, cancellationToken);
            var output = Assert.IsType<WriteOutput>(dry.Output);
            Assert.Equal((draft.Version, "someone-else@example.com"), (output.PendingDraft?.Version, output.PendingDraft?.SavedBy));
            Assert.Contains(output.Changes, c => c.Property == "Name");
            Assert.Contains(dry.Warnings, w => w.Contains("can't be undone", StringComparison.Ordinal));
            var stopped = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(new DiscardOperation(page), dryRun: false, cancellationToken));
            Assert.Equal(PendingDraft.Reason, Assert.IsType<AgentErrorDetails>(stopped.Details).Reason);

            var discarded = Assert.IsType<WriteOutput>((await writes.RunAsync(new DiscardOperation(page) { IncludeDraft = true }, dryRun: false, cancellationToken)).Output);
            Assert.True(discarded.Discarded);
            Assert.StartsWith("None:", UndoHints.For(new DiscardOperation(page), discarded));
            var versions = await VersionReader.ListAsync(site.Session.Db, site.Session.Model, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), null, 0, 50, cancellationToken);
            Assert.DoesNotContain(versions, v => v.Ref == draft.Version);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    private static async Task<string?> ScratchParentAsync(SiteUnderTest site, CancellationToken cancellationToken) =>
        await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is { } root ? WriteOutput.Id(root.Id) : null;

    private static async Task<string> CreateAsync(WriteExecutor writes, string parent, bool publish, CancellationToken cancellationToken) =>
        WriteOutput.Id((await writes.RunAsync(new CreateOperation(parent, "StandardPage", $"opticli-it {Guid.NewGuid():N}", Publish: publish), dryRun: false, cancellationToken)).CreatedId!.Value);

    private static Task<ContentDocument> GetAsync(SiteUnderTest site, string id, VersionSelector version, CancellationToken cancellationToken) =>
        new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model))
            .GetAsync(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), version, null, new Core.Properties.DecodeOptions(), cancellationToken);

    private static async Task<string?> CommonDraftAsync(SiteUnderTest site, string id, CancellationToken cancellationToken)
    {
        var rows = await site.Session.Db.QueryAsync("SELECT pkID FROM tblWorkContent WHERE fkContentID = @id AND CommonDraft = 1", r => r.GetInt32(0), cancellationToken, new SqlParameter("@id", int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)));
        return rows.Count == 1 ? $"{id}_{rows[0]}" : null;
    }
}
