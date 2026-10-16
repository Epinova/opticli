using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <see cref="DraftRequest.IntoVersion"/>: a change saved into opticli's latest draft instead of a new version, and a new
/// version wherever that draft isn't one to save into, on a scratch page.
/// </summary>
public sealed class IntoVersionTests
{
    [SiteFact]
    public async Task A_change_goes_into_opticlis_latest_draft_and_into_nothing_else()
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
        var id = created.CreatedId!.Value;
        var page = WriteOutput.Id(id);
        var live = VersionId(Assert.IsType<WriteOutput>(created.Output).Version!);
        try
        {
            var draft = VersionId(Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaTitle"] = "Draft title" }), dryRun: false, cancellationToken)).Output).Version!);
            var savedBefore = (await VersionReader.ByIdAsync(site.Session.Db, site.Session.Model, draft, cancellationToken))!.Saved;

            // A dry run says it would go into the draft, and saves nothing.
            var dry = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Into the draft"), IntoVersion = draft, DryRun = true }, cancellationToken);
            Assert.True((dry.DryRun, dry.IntoVersion, dry.Content?.Version) == (true, true, draft), $"{dry.DryRun} {dry.IntoVersion} {dry.Content?.Version}");
            Assert.Equal([live, draft], await VersionIdsAsync(site, id, cancellationToken));
            Assert.Null(await ValueAsync(site, page, draft, "MetaDescription", cancellationToken));

            // Saved into it: the same version id, with both changes, still opticli's draft and the primary one.
            await Task.Delay(1100, cancellationToken);
            var into = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Into the draft"), IntoVersion = draft, BaseVersion = draft }, cancellationToken);
            Assert.True((into.Saved, into.IntoVersion, into.Content?.Version, into.Content?.Status) == (true, true, draft, "checkedOut"), $"{into.Saved} {into.IntoVersion} {into.Content?.Version} {into.Content?.Status}");
            Assert.Equal(draft, into.BaseVersion);
            Assert.Equal(["MetaDescription"], into.Changes.Select(c => c.Property));
            Assert.Null(into.LeftOut);
            Assert.Equal([live, draft], await VersionIdsAsync(site, id, cancellationToken));
            Assert.Equal(("Draft title", "Into the draft"), (await ValueAsync(site, page, draft, "MetaTitle", cancellationToken), await ValueAsync(site, page, draft, "MetaDescription", cancellationToken)));
            var stamped = (await VersionReader.ByIdAsync(site.Session.Db, site.Session.Model, draft, cancellationToken))!;
            Assert.Equal(AgentProtocol.PrincipalName, stamped.ChangedBy);
            Assert.True(stamped.Saved > savedBefore, $"saved {stamped.Saved:O}, before {savedBefore:O}");
            Assert.Equal(draft, await CommonDraftAsync(site, id, cancellationToken));

            // Through the CLI's operations too; an unchanged change saves nothing.
            var viaSet = await writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaKeywords"] = new JsonArray("into") }) { IntoVersion = draft }, dryRun: false, cancellationToken);
            Assert.Equal(WriteOutput.VersionRef(id, draft), Assert.IsType<WriteOutput>(viaSet.Output).Version);
            var unchanged = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Into the draft"), IntoVersion = draft }, cancellationToken);
            Assert.True((unchanged.Saved, unchanged.IntoVersion) == (false, false));
            Assert.False((await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Into the draft"), IntoVersion = draft, DryRun = true }, cancellationToken)).IntoVersion);
            Assert.Equal([live, draft], await VersionIdsAsync(site, id, cancellationToken));

            // With from (or a ref naming the version), the change is based on what it names: a new version.
            var fromLatest = await DraftAsync(site, WriteOutput.VersionRef(id, draft), new DraftRequest { Properties = Values("MetaDescription", "From the ref"), IntoVersion = draft }, cancellationToken);
            Assert.True((fromLatest.Saved, fromLatest.IntoVersion) == (true, false));
            var newer = fromLatest.Content!.Version!.Value;
            Assert.True(newer > draft);

            // Not into one that isn't the latest: a new version, and the old draft stays as it was.
            var notLatest = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Not the latest"), IntoVersion = draft }, cancellationToken);
            Assert.True((notLatest.Saved, notLatest.IntoVersion) == (true, false));
            var theirs = notLatest.Content!.Version!.Value;
            Assert.True(theirs > newer);
            Assert.Equal("Into the draft", await ValueAsync(site, page, draft, "MetaDescription", cancellationToken));

            // Not into one someone else saved (the agent saves only as opticli; the edge-case fixture saves it again as them).
            Assert.True(await site.HandOverAsync(WriteOutput.VersionRef(id, theirs), cancellationToken), "The edge-case site's EdgeCasesFixture.cs has no /opticli-fixture/hand-over (run setup.sh again).");
            var notTheirs = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Not theirs"), IntoVersion = theirs }, cancellationToken);
            Assert.True((notTheirs.Saved, notTheirs.IntoVersion) == (true, false));
            Assert.True(notTheirs.Content!.Version > theirs);
            Assert.Equal("Not the latest", await ValueAsync(site, page, theirs, "MetaDescription", cancellationToken));

            // A publish publishes the draft itself, which becomes the primary draft, as a new version's publish does.
            var ours = (await VersionIdsAsync(site, id, cancellationToken)).Max();
            var published = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaTitle", "Published title"), Publish = true, IncludeDraft = true, IntoVersion = ours }, cancellationToken);
            Assert.True((published.Saved, published.Published, published.IntoVersion, published.Content?.Version) == (true, true, true, ours), $"{published.Saved} {published.Published} {published.IntoVersion} {published.Content?.Version}");
            Assert.Equal(live, published.PreviouslyPublished);
            Assert.Equal(ours, await CommonDraftAsync(site, id, cancellationToken));
            var versions = await VersionReader.ListAsync(site.Session.Db, site.Session.Model, id, null, 0, 50, cancellationToken);
            Assert.Equal(("published", "previouslyPublished"), (versions.Single(v => v.Id == ours).Status, versions.Single(v => v.Id == live).Status));

            // Not into a published version.
            var afterPublish = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaTitle", "After the publish"), IntoVersion = ours }, cancellationToken);
            Assert.True((afterPublish.Saved, afterPublish.IntoVersion, afterPublish.Content?.Status) == (true, false, "checkedOut"));
            Assert.True(afterPublish.Content!.Version > ours);
            Assert.Equal("Published title", await ValueAsync(site, page, ours, "MetaTitle", cancellationToken));

            // A scheduled publish schedules the draft itself; a scheduled version isn't saved into.
            var next = afterPublish.Content.Version!.Value;
            var scheduled = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "Scheduled"), PublishAt = DateTime.UtcNow.AddDays(1), IntoVersion = next }, cancellationToken);
            Assert.True((scheduled.Saved, scheduled.IntoVersion, scheduled.Content?.Version, scheduled.Content?.Status) == (true, true, next, "delayedPublish"), $"{scheduled.Saved} {scheduled.IntoVersion} {scheduled.Content?.Version} {scheduled.Content?.Status}");
            Assert.NotNull(scheduled.ScheduledFor);
            var afterSchedule = await DraftAsync(site, page, new DraftRequest { Properties = Values("MetaDescription", "After the schedule"), IntoVersion = next }, cancellationToken);
            Assert.True((afterSchedule.IntoVersion, afterSchedule.Content?.Status) == (false, "checkedOut"));
            Assert.True(afterSchedule.Content!.Version > next);
            Assert.Equal(("After the publish", "Scheduled"), (await ValueAsync(site, page, next, "MetaTitle", cancellationToken), await ValueAsync(site, page, next, "MetaDescription", cancellationToken)));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    private static Task<WriteResult> DraftAsync(SiteUnderTest site, string reference, DraftRequest request, CancellationToken cancellationToken) =>
        site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(reference), request, cancellationToken);

    private static Dictionary<string, JsonElement> Values(string property, string value) => new() { [property] = JsonSerializer.SerializeToElement(value) };

    private static int VersionId(string versionRef) => int.Parse(versionRef.Split('_')[1], CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<int>> VersionIdsAsync(SiteUnderTest site, int id, CancellationToken cancellationToken) =>
        (await VersionReader.ListAsync(site.Session.Db, site.Session.Model, id, null, 0, 50, cancellationToken)).Select(v => v.Id).Order().ToList();

    private static async Task<string?> ValueAsync(SiteUnderTest site, string id, int version, string property, CancellationToken cancellationToken)
    {
        var document = await new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model))
            .GetAsync(int.Parse(id, CultureInfo.InvariantCulture), new VersionSelector(VersionKind.Specific, version), null, new Core.Properties.DecodeOptions(), cancellationToken);
        return document.Properties[property]?["value"]?.GetValue<string>();
    }

    private static async Task<int?> CommonDraftAsync(SiteUnderTest site, int id, CancellationToken cancellationToken)
    {
        var rows = await site.Session.Db.QueryAsync("SELECT pkID FROM tblWorkContent WHERE fkContentID = @id AND CommonDraft = 1", r => r.GetInt32(0), cancellationToken, new SqlParameter("@id", id));
        return rows.Count == 1 ? rows[0] : null;
    }
}
