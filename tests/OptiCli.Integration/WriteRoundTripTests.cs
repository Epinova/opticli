using OptiCli.Core.Content;
using OptiCli.Core.Queries;
using OptiCli.Core.Refs;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// Writes that the DB read path must agree with, run against scratch content in the site's development database: a
/// folder in the global assets that is moved to the recycle bin afterwards.
/// </summary>
public sealed class WriteRoundTripTests
{
    private const string FolderType = "SysContentFolder";

    [SiteFact]
    public async Task Access_changes_read_back_the_same_from_the_database_and_the_cms()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));

        var folder = await ScratchFolderAsync(site, writes, cancellationToken);
        try
        {
            await AssertSameAsync(site, folder, inherited: true, cancellationToken);

            var granted = await writes.RunAsync(new AccessOperation(folder,
                Grant: new Dictionary<string, string> { ["Everyone"] = "Read,Edit" }, BreakInheritance: true), dryRun: false, cancellationToken);
            var output = Assert.IsType<AccessOutput>(granted.Output);
            Assert.True(output.Saved);
            Assert.Contains(output.After.Entries, e => e.Name == "Everyone" && e.Mask == (AccessLevels.Read | AccessLevels.Edit));
            await AssertSameAsync(site, folder, inherited: false, cancellationToken);

            var revoked = await writes.RunAsync(new AccessOperation(folder, Revoke: ["Everyone"]), dryRun: false, cancellationToken);
            Assert.DoesNotContain(((AccessOutput)revoked.Output).After.Entries, e => e.Name == "Everyone");
            await AssertSameAsync(site, folder, inherited: false, cancellationToken);

            await writes.RunAsync(new AccessOperation(folder, Inherit: true), dryRun: false, cancellationToken);
            await AssertSameAsync(site, folder, inherited: true, cancellationToken);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(folder), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_provider_ref_resolves_to_the_same_content_as_its_guid()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var mapped = await site.Session.Db.QueryAsync("SELECT TOP 1 pkID, Provider, ContentGuid FROM tblMappedIdentity ORDER BY pkID",
            r => (Id: r.GetInt32(0), Provider: r.GetString(1), Guid: r.GetGuid(2)), cancellationToken);
        if (mapped.Count == 0)
        {
            return; // The site has no content provider content.
        }

        var reference = ContentRefParser.Parse($"{mapped[0].Id}__{mapped[0].Provider}");
        Assert.Equal(ContentRefKind.Provider, reference.Kind);
        Assert.Equal(mapped[0].Guid, await ContentHeaderReader.ProviderGuidAsync(site.Session.Db, reference.Id, reference.Provider!, cancellationToken));

        // The agent accepts the same ref and loads the same item.
        var loaded = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(reference.ToString()), null, cancellationToken);
        Assert.Equal(mapped[0].Guid, loaded.Guid);
    }

    private static async Task<string> ScratchFolderAsync(SiteUnderTest site, WriteExecutor writes, CancellationToken cancellationToken)
    {
        var parent = site.Session.Model.Sites.GlobalAssetsRoot ?? throw new InvalidOperationException("The site has no global assets folder.");
        var created = await writes.RunAsync(new CreateOperation(WriteOutput.Id(parent), FolderType, $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken);
        return WriteOutput.Id(created.CreatedId ?? throw new InvalidOperationException("The folder was not created."));
    }

    /// <summary>The ACL as <see cref="AccessReader"/> reads it from the database equals the CMS's (a no-op dry run).</summary>
    private static async Task AssertSameAsync(SiteUnderTest site, string reference, bool inherited, CancellationToken cancellationToken)
    {
        var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(reference, System.Globalization.CultureInfo.InvariantCulture), cancellationToken)
            ?? throw new InvalidOperationException($"No content {reference}.");
        var fromDb = await AccessReader.ReadAsync(site.Session.Db, header, cancellationToken);
        var fromCms = (await site.Agent.SendAsync<AccessResult>(HttpMethod.Post, AgentRoutes.Access(reference), new AccessRequest { DryRun = true }, cancellationToken)).Before;

        Assert.Equal(inherited, fromDb.Inherited);
        Assert.Equal((fromCms.Inherited, fromCms.From), (fromDb.Inherited, fromDb.From));
        Assert.Equal(fromCms.Entries.Select(e => (e.Name, e.Kind, e.Mask)), fromDb.Entries.Select(e => (e.Name, e.Kind, e.Mask)));
    }
}
