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

            // One entry per name: a user grant for a role's name is refused rather than replacing the role.
            var clash = await Assert.ThrowsAnyAsync<Core.Errors.OptiCliException>(() => writes.RunAsync(new AccessOperation(folder,
                GrantUsers: new Dictionary<string, string> { ["Everyone"] = "Read" }), dryRun: true, cancellationToken));
            Assert.Contains("has a role entry", clash.Message);

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
    public async Task Inherit_saves_below_a_parent_with_its_own_access_rights()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));

        var parent = await ScratchFolderAsync(site, writes, cancellationToken);
        try
        {
            await writes.RunAsync(new AccessOperation(parent, BreakInheritance: true), dryRun: false, cancellationToken);
            var child = WriteOutput.Id((await writes.RunAsync(new CreateOperation(parent, FolderType, "child"), dryRun: false, cancellationToken)).CreatedId!.Value);
            await writes.RunAsync(new AccessOperation(child, BreakInheritance: true), dryRun: false, cancellationToken);
            await AssertSameAsync(site, child, inherited: false, cancellationToken);

            // Same entries as the parent's, so only the inherited flag changes.
            var dry = Assert.IsType<AccessOutput>((await writes.RunAsync(new AccessOperation(child, Inherit: true), dryRun: true, cancellationToken)).Output);
            Assert.Equal((true, parent), (dry.After.Inherited, dry.After.From));

            var inherited = Assert.IsType<AccessOutput>((await writes.RunAsync(new AccessOperation(child, Inherit: true), dryRun: false, cancellationToken)).Output);
            Assert.True(inherited.Saved);
            await AssertSameAsync(site, child, inherited: true, cancellationToken);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(parent), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_draft_shows_its_own_stop_publish_and_an_unchanged_publish_saves_nothing()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), projectDirectory: site.ProjectDirectory);

        var folder = await ScratchFolderAsync(site, writes, cancellationToken);
        var file = Path.Combine(Path.GetTempPath(), $"opticli-it-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(file, MinimalPdf(), cancellationToken);
        try
        {
            var media = (await writes.RunAsync(new UploadOperation(file, Parent: folder, Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value;
            var stop = new System.Text.Json.Nodes.JsonObject { ["StopPublish"] = "2099-01-01T00:00:00Z" };
            await writes.RunAsync(new SetOperation(WriteOutput.Id(media), stop), dryRun: false, cancellationToken);

            Assert.Null((await GetAsync(site, media, VersionSelector.Published, cancellationToken)).StopPublish);
            Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), (await GetAsync(site, media, new VersionSelector(VersionKind.Latest), cancellationToken)).StopPublish?.ToUniversalTime());

            var published = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(WriteOutput.Id(media), stop, Publish: true), dryRun: false, cancellationToken)).Output);
            Assert.True(published.Published);
            var again = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(WriteOutput.Id(media), stop, Publish: true), dryRun: false, cancellationToken)).Output);
            Assert.Equal((false, published.Version), (again.Saved, again.Version));
        }
        finally
        {
            File.Delete(file);
            await writes.RunAsync(new DeleteOperation(folder), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Publishing_someone_elses_draft_needs_confirmation_and_the_undo_names_the_previously_published_version()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));

        var folder = await ScratchFolderAsync(site, writes, cancellationToken);
        // An image: media is versioned, and every site has an image type.
        var file = Path.Combine(Path.GetTempPath(), $"opticli-it-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, Convert.FromBase64String(OnePixelPng), cancellationToken);
        try
        {
            var uploaded = Assert.IsType<WriteOutput>((await writes.RunAsync(new UploadOperation(file, Parent: folder, Publish: true), dryRun: false, cancellationToken)).Output);
            var media = uploaded.Ref!;
            var stop = new System.Text.Json.Nodes.JsonObject { ["StopPublish"] = "2099-01-01T00:00:00Z" };
            var draft = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(media, stop), dryRun: false, cancellationToken)).Output);
            var rename = new SetOperation(media, Name: "renamed", Publish: true);

            // opticli's own draft goes live without asking (checked from the database, which leaves the CMS's version
            // list uncached for the change below).
            Assert.Null(Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(media), dryRun: true, cancellationToken)).Output).PendingDraft);

            // The agent saves only as opticli, so the draft becomes another user's in the database. The CMS caches
            // version lists until the next save, so this has to come before the agent lists them again.
            await site.Session.Db.QueryAsync("UPDATE tblWorkContent SET ChangedByName = 'someone-else@example.com' WHERE pkID = @version; SELECT @@ROWCOUNT",
                r => r.GetInt32(0), cancellationToken, new Microsoft.Data.SqlClient.SqlParameter("@version", int.Parse(draft.Version!.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture)));

            var dry = await writes.RunAsync(rename, dryRun: true, cancellationToken);
            var pending = Assert.IsType<WriteOutput>(dry.Output).PendingDraft;
            Assert.Equal((draft.Version, "someone-else@example.com"), (pending?.Version, pending?.SavedBy));
            Assert.Contains(pending!.Changes, c => c.Property == "StopPublish");
            Assert.Contains(dry.Warnings, w => w.StartsWith("pendingDraft:", StringComparison.Ordinal));
            var dryPublish = Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(media), dryRun: true, cancellationToken)).Output).PendingDraft;
            Assert.Equal(draft.Version, dryPublish?.Version);
            Assert.Contains(dryPublish!.Changes, c => c.Property == "StopPublish");

            var refused = await Assert.ThrowsAsync<Core.Errors.ConflictException>(() => writes.RunAsync(rename, dryRun: false, cancellationToken));
            Assert.Equal(PendingDraft.Reason, Assert.IsType<Core.Serve.AgentErrorDetails>(refused.Details).Reason);
            Assert.Contains("--include-draft", refused.Hint);
            await Assert.ThrowsAsync<Core.Errors.ConflictException>(() => writes.RunAsync(new PublishOperation(media), dryRun: false, cancellationToken));

            var plan = WritePlan.Parse($$"""{"operations": [{"op": "set", "ref": "{{media}}", "name": "renamed", "publish": true}]}""");
            var invalid = await Assert.ThrowsAnyAsync<Core.Errors.OptiCliException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
            Assert.Equal(Core.Errors.ErrorCode.Conflict, invalid.Code);
            var confirmedPlan = WritePlan.Parse($$"""{"operations": [{"op": "set", "ref": "{{media}}", "name": "renamed", "publish": true, "includeDraft": true}]}""");
            Assert.Equal(PlanStepStatus.Valid, (await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(confirmedPlan, dryRun: true, publishAll: false, cancellationToken)).Operations[0].Status);

            var published = Assert.IsType<WriteOutput>((await writes.RunAsync(rename with { IncludeDraft = true }, dryRun: false, cancellationToken)).Output);
            Assert.True(published.Published);
            Assert.Equal(draft.Version, published.PendingDraft?.Version);
            Assert.Equal(uploaded.Version, published.PreviouslyPublished);
            Assert.Equal($"{published.Version} is now published; to go back, publish the previously published version: opticli publish {media} --version {uploaded.Version!.Split('_')[1]}",
                UndoHints.For(rename, published));
            Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), (await GetAsync(site, int.Parse(media, System.Globalization.CultureInfo.InvariantCulture), VersionSelector.Published, cancellationToken)).StopPublish?.ToUniversalTime());

            // Publishing a named version counts as confirmation, and the undo goes back to the version before.
            var older = Assert.IsType<WriteOutput>((await writes.RunAsync(new PublishOperation(media, Version: int.Parse(uploaded.Version.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture)), dryRun: false, cancellationToken)).Output);
            Assert.Equal(published.Version, older.PreviouslyPublished);
        }
        finally
        {
            File.Delete(file);
            await writes.RunAsync(new DeleteOperation(folder), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task An_uploaded_pdf_is_media_whose_blob_is_on_disk()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), projectDirectory: site.ProjectDirectory);

        var folder = await ScratchFolderAsync(site, writes, cancellationToken);
        var file = Path.Combine(Path.GetTempPath(), $"opticli-it-{Guid.NewGuid():N}.pdf");
        // A real (tiny) PDF: sites index uploaded documents on save, and reject what they can't parse.
        await File.WriteAllBytesAsync(file, MinimalPdf(), cancellationToken);
        try
        {
            var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(new UploadOperation(file, Parent: folder), dryRun: true, cancellationToken)).Output);
            Assert.False(dry.Saved);
            Assert.Null(dry.Upload!.Blob);

            var draft = await writes.RunAsync(new UploadOperation(file, Parent: folder, Name: "draft pdf"), dryRun: false, cancellationToken);
            var output = Assert.IsType<WriteOutput>(draft.Output);
            Assert.Equal(dry.Type, output.Type);
            Assert.Equal(Core.Cms.ContentKind.Media, site.Session.Model.Kind(site.Session.Model.RequireType(output.Type!).Id));
            Assert.Equal("checkedOut", output.Status);
            Assert.True(output.Upload!.Blob!.Exists);
            Assert.Equal(new FileInfo(file).Length, output.Upload.Blob.Size);

            var published = Assert.IsType<WriteOutput>((await writes.RunAsync(new UploadOperation(file, Parent: folder, Publish: true), dryRun: false, cancellationToken)).Output);
            Assert.Equal("published", published.Status);
        }
        finally
        {
            File.Delete(file);
            await writes.RunAsync(new DeleteOperation(folder), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_create_with_a_fixed_guid_can_run_again_and_restores_from_the_recycle_bin()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var rerun = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true);

        var parent = await ScratchFolderAsync(site, writes, cancellationToken);
        var create = new CreateOperation(parent, FolderType, "fixed guid") { ContentGuid = Guid.NewGuid() };
        try
        {
            var first = Assert.IsType<WriteOutput>((await writes.RunAsync(create, dryRun: false, cancellationToken)).Output);
            Assert.Equal(create.ContentGuid, first.Guid);

            await Assert.ThrowsAsync<Core.Errors.ConflictException>(() => writes.RunAsync(create, dryRun: false, cancellationToken));

            var again = await rerun.RunAsync(create, dryRun: false, cancellationToken);
            Assert.Equal(int.Parse(first.Ref!, System.Globalization.CultureInfo.InvariantCulture), again.CreatedId);
            Assert.Equal((true, false), (((WriteOutput)again.Output).Existing == true, ((WriteOutput)again.Output).Saved));

            await writes.RunAsync(new DeleteOperation(first.Ref!), dryRun: false, cancellationToken);
            var restored = Assert.IsType<WriteOutput>((await rerun.RunAsync(create with { Name = "renamed" }, dryRun: false, cancellationToken)).Output);
            Assert.True(restored.Restored);
            Assert.Equal(parent, restored.Parent);
            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, again.CreatedId!.Value, cancellationToken);
            Assert.False(header!.Deleted);
            Assert.Equal("renamed", header.LanguageRow(null)?.Name);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(parent), dryRun: false, cancellationToken);
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

    /// <summary>A 1x1 transparent PNG.</summary>
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    /// <summary>A one-page PDF with a line of text and a valid cross-reference table.</summary>
    private static byte[] MinimalPdf()
    {
        const string text = "BT /F1 12 Tf 20 50 Td (opticli integration test) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {text.Length} >>\nstream\n{text}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static async Task<string> ScratchFolderAsync(SiteUnderTest site, WriteExecutor writes, CancellationToken cancellationToken)
    {
        var parent = site.Session.Model.Sites.GlobalAssetsRoot ?? throw new InvalidOperationException("The site has no global assets folder.");
        var created = await writes.RunAsync(new CreateOperation(WriteOutput.Id(parent), FolderType, $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken);
        return WriteOutput.Id(created.CreatedId ?? throw new InvalidOperationException("The folder was not created."));
    }

    /// <summary>The item as <c>get</c> shows it, read fresh from the database.</summary>
    private static Task<ContentDocument> GetAsync(SiteUnderTest site, int id, VersionSelector version, CancellationToken cancellationToken) =>
        new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model)).GetAsync(id, version, null, new Core.Properties.DecodeOptions(), cancellationToken);

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
