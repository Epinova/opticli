using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// CMS projects through the site agent, as the CLI writes them (<c>--in-project</c>, <c>projects ready|publish|...</c>):
/// on a scratch page below the edge-case site's root, with projects of their own, which each test deletes again.
/// </summary>
public sealed class ProjectTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [SiteFact]
    public async Task Saves_go_into_a_new_project_replace_their_item_there_and_can_mark_it_ready()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var name = scratch.Name("saves");
        var dry = await scratch.Projects.CreateAsync(name, dryRun: true, None);
        Assert.Equal((0, true), (dry.Project.Id, dry.DryRun));
        Assert.Contains(dry.EditorUi, new[] { ProjectEditorUi.ProjectMode, ProjectEditorUi.Gadget });
        var project = await scratch.CreateProjectAsync(name);
        // The name is taken now.
        await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.CreateAsync(name, dryRun: false, None));

        var page = await scratch.PageAsync("saves");
        var into = project.ToString(CultureInfo.InvariantCulture);
        // A dry run says what the save would do and changes nothing.
        var planned = await scratch.SetAsync(page, "first", project: name, dryRun: true);
        Assert.Equal((ProjectSaveItems.Added, false), (planned.Project!.Item, planned.Project.Ready));
        Assert.Empty(await ProjectReader.ItemsAsync(scratch.Site.Session, project, None));

        var first = await scratch.SetAsync(page, "first", project: name);
        Assert.Equal((project, ProjectSaveItems.Added, (string?)null, false), (first.Project!.Id, first.Project.Item, first.Project.Replaced, first.Project.Ready));
        Assert.Equal("checkedOut", await scratch.StatusAsync(first.Version!));

        // The next save's version replaces the older item in the same language, and ready marks it Ready to publish.
        var third = await scratch.SetAsync(page, "second", project: into, ready: true);
        Assert.Equal((ProjectSaveItems.Replaced, first.Version, true, "checkedIn"), (third.Project!.Item, third.Project.Replaced, third.Project.Ready, third.Status));
        Assert.Equal("checkedOut", await scratch.StatusAsync(first.Version!));
        var item = Assert.Single(await ProjectReader.ItemsAsync(scratch.Site.Session, project, None));
        Assert.Equal((third.Version, "checkedIn", "en", "default"), (item.Version, item.Status, item.Language, item.Category));
        var listed = Assert.Single(await ProjectReader.ListAsync(scratch.Site.Session.Db, None), p => p.Id == project);
        Assert.Equal((name, ProjectStatuses.Active, 1, 1), (listed.Name, listed.Status, listed.Items, listed.Ready));

        // A save that names no project saves as before, and warns that the project still holds the earlier version.
        var outside = await scratch.Writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = "outside" }), dryRun: false, None);
        var warning = Assert.Single(Assert.IsType<WriteOutput>(outside.Output).Validation ?? [], v => v.Message.Contains($"project {project}", StringComparison.Ordinal));
        Assert.Contains(third.Version!, warning.Message, StringComparison.Ordinal);

        // New content goes in too; a project that doesn't exist, or ready without a project, doesn't.
        var created = await scratch.Writes.RunAsync(new CreateOperation(scratch.Container, "StandardPage", scratch.Name("new page")) { Project = into, Ready = true }, dryRun: false, None);
        scratch.Track(created.CreatedId!.Value);
        Assert.Equal((ProjectSaveItems.Added, true), (Assert.IsType<WriteOutput>(created.Output).Project!.Item, Assert.IsType<WriteOutput>(created.Output).Project!.Ready));
        Assert.Equal(2, (await ProjectReader.ItemsAsync(scratch.Site.Session, project, None)).Count);
        await Assert.ThrowsAsync<NotFoundException>(() => scratch.SetAsync(page, "nowhere", project: $"opticli-it no such project {Guid.NewGuid():N}"));
        await Assert.ThrowsAsync<UsageException>(() => scratch.Writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = "x" }) { Ready = true }, dryRun: false, None));
        await Assert.ThrowsAsync<UsageException>(() => scratch.Writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = "x" }, Publish: true) { Project = into }, dryRun: false, None));
    }

    [SiteFact]
    public async Task The_edge_case_sites_campaign_project_has_a_ready_item_and_one_that_isnt()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null || (await ProjectReader.ListAsync(scratch.Site.Session.Db, None)).FirstOrDefault(p => p.Name == "Edge campaign") is not { } campaign)
        {
            return;
        }
        Assert.Equal((ProjectStatuses.Active, 2, 1), (campaign.Status, campaign.Items, campaign.Ready));
        var items = await ProjectReader.ItemsAsync(scratch.Site.Session, campaign.Id, None);
        // "Campaign page, draft", then "Campaign page, ready".
        Assert.Equal(["checkedOut", "checkedIn"], items.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Status));
        Assert.Equal(["Campaign page, draft", "Campaign page, ready"], items.Select(i => i.Name).Order(StringComparer.Ordinal));
        // A dry run of its publish puts only the ready one live (nothing is published).
        var preview = await scratch.Projects.PublishAsync("Edge campaign", null, includeDraft: false, dryRun: true, confirm: null, None);
        Assert.Equal("Campaign page, ready", Assert.Single(preview.Published!).Content.Name);
        Assert.Equal("Campaign page, draft", Assert.Single(preview.NotReady!).Content.Name);
    }

    [SiteFact]
    public async Task Ready_under_an_approval_sequence_is_refused_and_a_review_request_sends_it_with_its_comment()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("approval"))).ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("approval", parent: WriteOutput.Id(scratch.ApprovalRoot.Id), publish: false);
        var draft = await scratch.SetAsync(page, "for review", project: project);

        // On the save and afterwards: refused, pointing at --request-approval; a dry run reports it.
        var onSave = await Assert.ThrowsAsync<RefusedException>(() => scratch.SetAsync(page, "ready", project: project, ready: true));
        Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.IsType<AgentErrorDetails>(onSave.Details).Reason);
        var refused = await Assert.ThrowsAsync<RefusedException>(() => scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: false, comment: null, includeDraft: false, dryRun: false, None));
        Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.Equal(ProjectWrites.ReadyApprovalHint, refused.Hint);
        Assert.False((await scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: false, comment: null, includeDraft: false, dryRun: true, None)).Valid);
        Assert.Equal("checkedOut", await scratch.StatusAsync(draft.Version!));

        // Where the sequence asks for a comment, a review request without one is refused, and one with it starts the review.
        if (await scratch.Site.RequireApprovalCommentAsync(true, None))
        {
            try
            {
                var noComment = await Assert.ThrowsAsync<UsageException>(() => scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: true, comment: null, includeDraft: false, dryRun: false, None));
                Assert.Contains("comment", Assert.Single(Assert.IsType<AgentErrorDetails>(noComment.Details).Project!.Items!).Problem!.Message, StringComparison.Ordinal);
                Assert.Equal("checkedOut", await scratch.StatusAsync(draft.Version!));
            }
            finally
            {
                await scratch.Site.RequireApprovalCommentAsync(false, None);
            }
        }
        await Assert.ThrowsAsync<UsageException>(() => scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: false, comment: "a comment without a review", includeDraft: false, dryRun: false, None));
        var sent = await scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: true, comment: "Campaign texts, please review", includeDraft: false, dryRun: false, None);
        var item = Assert.Single(sent.Items!);
        Assert.Equal((ProjectItemOutcomes.ApprovalRequested, "awaitingApproval"), (item.Outcome, item.Content.Status));
        Assert.Equal("awaitingApproval", await scratch.StatusAsync(draft.Version!));
        Assert.Equal(["Campaign texts, please review"], await scratch.ReviewCommentsAsync(draft.Version!));
        // It stays in the project, in review; publishing the project finds nothing ready.
        Assert.Equal("awaitingApproval", Assert.Single(await ProjectReader.ItemsAsync(scratch.Site.Session, int.Parse(project, CultureInfo.InvariantCulture), None)).Status);
        var nothing = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None));
        Assert.Equal(AgentErrorReasons.NothingReady, Assert.IsType<AgentErrorDetails>(nothing.Details).Reason);
    }

    [SiteFact]
    public async Task A_version_marked_ready_and_then_moved_below_an_approval_sequence_isnt_published_with_the_project()
    {
        // A version checked in past its reviewers: CMS 12 lets the API check one in, and a project publish would put it live.
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("moved"))).ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("moved");
        var ready = await scratch.SetAsync(page, "ready elsewhere", project: project, ready: true);
        Assert.Equal("checkedIn", ready.Status);
        await scratch.Writes.RunAsync(new MoveOperation(page, WriteOutput.Id(scratch.ApprovalRoot.Id)), dryRun: false, None);
        try
        {
            var preview = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: true, confirm: null, None);
            Assert.False(preview.Valid);
            Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.Single(preview.Published!).Problem!.Reason);
            var refused = await Assert.ThrowsAsync<RefusedException>(() => scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None));
            Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
            Assert.Contains("--request-approval", refused.Hint, StringComparison.Ordinal);
            Assert.Equal("checkedIn", await scratch.StatusAsync(ready.Version!));
        }
        finally
        {
            await scratch.Writes.RunAsync(new MoveOperation(page, scratch.Container), dryRun: false, None);
        }
    }

    [SiteFact]
    public async Task Publishing_puts_only_the_ready_items_live_and_a_published_project_takes_nothing_more()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("publish"))).ToString(CultureInfo.InvariantCulture);
        var readyPage = await scratch.PageAsync("ready");
        var draftPage = await scratch.PageAsync("draft");
        var ready = await scratch.SetAsync(readyPage, "goes live", project: project, ready: true);
        var draft = await scratch.SetAsync(draftPage, "stays behind", project: project);

        var preview = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: true, confirm: null, None);
        Assert.Equal((true, true), (preview.DryRun, preview.Valid));
        Assert.Equal(ready.Version, Assert.Single(preview.Published!).Content.Ref);
        Assert.Equal(draft.Version, Assert.Single(preview.NotReady!).Content.Ref);
        Assert.Contains("Stays behind, not ready (1)", ProjectWrites.Summary(preview), StringComparison.Ordinal);
        Assert.Equal("checkedIn", await scratch.StatusAsync(ready.Version!));

        var published = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None);
        Assert.Equal(ProjectStatuses.Published, published.Project.Status);
        var live = Assert.Single(published.Published!);
        Assert.NotNull(live.PreviouslyPublished);
        // The version marked ready is the one that went live (the same version id); the draft is still a draft.
        Assert.Equal(VersionId(ready.Version!), await scratch.PublishedVersionAsync(readyPage, "en"));
        Assert.Equal("checkedOut", await scratch.StatusAsync(draft.Version!));
        Assert.Equal(draft.Version, Assert.Single(published.NotReady!).Content.Ref);

        // A published project takes no saves, ready marks or publishes.
        var save = await Assert.ThrowsAsync<ConflictException>(() => scratch.SetAsync(draftPage, "too late", project: project));
        Assert.Equal(AgentErrorReasons.ProjectNotActive, Assert.IsType<AgentErrorDetails>(save.Details).Reason);
        await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.ReadyAsync(project, null, null, all: true, requestApproval: false, comment: null, includeDraft: false, dryRun: false, None));
        await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None));

        // A project with nothing ready isn't published (the CMS would mark it published with nothing live).
        var empty = (await scratch.CreateProjectAsync(scratch.Name("nothing ready"))).ToString(CultureInfo.InvariantCulture);
        await scratch.SetAsync(draftPage, "still a draft", project: empty);
        var nothing = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.PublishAsync(empty, null, includeDraft: false, dryRun: false, confirm: null, None));
        Assert.Equal(AgentErrorReasons.NothingReady, Assert.IsType<AgentErrorDetails>(nothing.Details).Reason);
    }

    [SiteFact]
    public async Task A_scheduled_project_is_reactivated_into_drafts_that_have_to_be_marked_ready_again()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = await scratch.CreateProjectAsync(scratch.Name("schedule"));
        var id = project.ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("schedule");
        var other = await scratch.PageAsync("schedule, not ready");
        var ready = await scratch.SetAsync(page, "later", project: id, ready: true);
        await scratch.SetAsync(other, "not ready", project: id);
        var at = new DateTimeOffset(DateTime.UtcNow.AddDays(3).Date.AddHours(7), TimeSpan.Zero);

        var scheduled = await scratch.Projects.PublishAsync(id, at, includeDraft: false, dryRun: false, confirm: null, None);
        Assert.Equal((ProjectStatuses.DelayedPublished, at.UtcDateTime), (scheduled.Project.Status, scheduled.ScheduledFor));
        Assert.Equal(ready.Version, Assert.Single(scheduled.Scheduled!).Content.Ref);
        Assert.Null(scheduled.Published);
        Assert.Single(scheduled.NotReady!);
        Assert.Equal("delayedPublish", await scratch.StatusAsync(ready.Version!));

        // While it is scheduled: saves and ready marks are refused with the reactivate hint, and so is deleting it.
        var save = await Assert.ThrowsAsync<ConflictException>(() => scratch.SetAsync(other, "meanwhile", project: id));
        Assert.Contains("projects reactivate", save.Hint, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.DeleteAsync(id, dryRun: true, None));
        Assert.Equal(AgentErrorReasons.ProjectNotActive, Assert.IsType<AgentErrorDetails>(delete.Details).Reason);
        await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.DeleteAsync(id, dryRun: false, None));

        var reactivated = await scratch.Projects.ReactivateAsync(id, dryRun: false, None);
        Assert.Equal(ProjectStatuses.Active, reactivated.Project.Status);
        var item = Assert.Single(reactivated.Items!);
        Assert.Equal((ProjectItemOutcomes.Unscheduled, ready.Version), (item.Outcome, item.Content.Ref));
        // The same version, a draft again: not ready, so publishing finds nothing ready until it is marked again.
        Assert.Equal("checkedOut", await scratch.StatusAsync(ready.Version!));
        Assert.NotEqual(VersionId(ready.Version!), await scratch.PublishedVersionAsync(page, "en"));
        var nothing = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.PublishAsync(id, null, includeDraft: false, dryRun: false, confirm: null, None));
        Assert.Equal(AgentErrorReasons.NothingReady, Assert.IsType<AgentErrorDetails>(nothing.Details).Reason);
        await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.ReactivateAsync(id, dryRun: false, None));

        var again = await scratch.Projects.ReadyAsync(id, null, null, all: true, requestApproval: false, comment: null, includeDraft: false, dryRun: false, None);
        Assert.Equal(2, again.Items!.Count(i => i.Outcome == ProjectItemOutcomes.Ready));
        Assert.True((await scratch.Projects.PublishAsync(id, null, includeDraft: false, dryRun: true, confirm: null, None)).Valid);
    }

    [SiteFact]
    public async Task A_colleagues_changes_go_live_without_include_draft_only_where_they_marked_the_item_ready()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("colleagues"))).ToString(CultureInfo.InvariantCulture);
        const string Alice = "alice@example.com";
        const string Bob = "bob@example.com";

        // 1. opticli's draft, marked ready by Alice: her consent; the result still says whose changes go live.
        var hers = await scratch.PageAsync("marked ready by a colleague");
        var hersDraft = await scratch.SetAsync(hers, "opticli's draft", project: project);
        if (!await scratch.Site.HandOverAsync(hersDraft.Version!, None, Alice, ready: true))
        {
            return;
        }
        // 2. Bob's older draft, carried by opticli's newer version, which Alice marked ready: Bob didn't consent.
        var carried = await scratch.PageAsync("carries a colleague's draft");
        var bobs = await scratch.SetAsync(carried, "Bob's draft");
        Assert.True(await scratch.Site.HandOverAsync(bobs.Version!, None, Bob));
        var onTop = await scratch.Writes.RunAsync(new SetOperation(carried, new JsonObject { ["MetaKeywords"] = new JsonArray("on top") }) { Project = project }, dryRun: false, None);
        var onTopVersion = Assert.IsType<WriteOutput>(onTop.Output).Version!;
        Assert.NotEqual(bobs.Version, onTopVersion);
        Assert.True(await scratch.Site.HandOverAsync(onTopVersion, None, Alice, ready: true));

        var preview = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: true, confirm: null, None);
        Assert.False(preview.Valid);
        var consented = Assert.Single(preview.Published!, i => i.Content.Ref == hersDraft.Version);
        Assert.Null(consented.Problem);
        Assert.Equal((Alice, Alice, hersDraft.Version), (consented.ReadyBy, consented.PendingDraft?.SavedBy, consented.PendingDraft?.Version));
        var unconfirmed = Assert.Single(preview.Published!, i => i.Content.Ref == onTopVersion);
        Assert.Equal((AgentErrorReasons.PendingDraft, Alice, Bob, bobs.Version), (unconfirmed.Problem?.Reason, unconfirmed.ReadyBy, unconfirmed.PendingDraft?.SavedBy, unconfirmed.PendingDraft?.Version));
        Assert.Contains("marked ready by alice@example.com", ProjectWrites.Summary(preview), StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None));
        Assert.Equal(AgentErrorReasons.PendingDraft, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.Equal(ProjectWrites.PendingDraftHint, refused.Hint);
        Assert.Equal(Bob, Assert.IsType<AgentErrorDetails>(refused.Details).Project!.Published!.Single(i => i.Problem is not null).PendingDraft!.SavedBy);
        Assert.Equal("checkedIn", await scratch.StatusAsync(hersDraft.Version!));

        // Taken out, the item Alice marked ready publishes without --include-draft.
        await scratch.Projects.RemoveAsync(project, [carried], null, dryRun: false, None);
        var published = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None);
        var live = Assert.Single(published.Published!);
        Assert.Equal((Alice, Alice), (live.ReadyBy, live.PendingDraft?.SavedBy));
        Assert.Equal(VersionId(hersDraft.Version!), await scratch.PublishedVersionAsync(hers, "en"));

        // In a project of its own, Bob's draft carried by the version Alice marked ready needs --include-draft.
        var second = (await scratch.CreateProjectAsync(scratch.Name("colleagues, included"))).ToString(CultureInfo.InvariantCulture);
        await scratch.Writes.RunAsync(new SetOperation(carried, new JsonObject { ["MetaKeywords"] = new JsonArray("on top") }) { Project = second }, dryRun: false, None);
        var included = await scratch.Projects.PublishAsync(second, null, includeDraft: true, dryRun: false, confirm: null, None);
        Assert.Equal(onTopVersion, Assert.Single(included.Published!).Content.Ref);
        Assert.Equal(VersionId(onTopVersion), await scratch.PublishedVersionAsync(carried, "en"));
    }

    [SiteFact]
    public async Task Marking_a_colleagues_draft_ready_needs_include_draft()
    {
        // The CMS records whoever checks a version in as the one who saved it, so the publish couldn't ask about it later.
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("their draft"))).ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("their draft");
        var draft = await scratch.SetAsync(page, "opticli's draft", project: project);
        if (!await scratch.Site.HandOverAsync(draft.Version!, None))
        {
            return;
        }

        var dry = await scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: false, comment: null, includeDraft: false, dryRun: true, None);
        Assert.False(dry.Valid);
        Assert.Equal((AgentErrorReasons.PendingDraft, SiteUnderTest.SomeoneElse), (Assert.Single(dry.Items!).Problem?.Reason, dry.Items![0].PendingDraft?.SavedBy));
        var refused = await Assert.ThrowsAsync<ConflictException>(() => scratch.Projects.ReadyAsync(project, null, null, all: true, requestApproval: false, comment: null, includeDraft: false, dryRun: false, None));
        Assert.Equal(ProjectWrites.ReadyPendingDraftHint, refused.Hint);
        Assert.Equal("checkedOut", await scratch.StatusAsync(draft.Version!));

        // A save that changes nothing doesn't mark it ready either: it says why.
        var unchanged = await scratch.Writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = "opticli's draft" }) { Project = project, Ready = true }, dryRun: false, None);
        var output = Assert.IsType<WriteOutput>(unchanged.Output);
        Assert.Equal((false, false), (output.Saved, output.Project!.Ready));
        Assert.Contains(output.Validation ?? [], v => v.Message.Contains("--include-draft", StringComparison.Ordinal));
        Assert.Equal("checkedOut", await scratch.StatusAsync(draft.Version!));

        var marked = await scratch.Projects.ReadyAsync(project, [page], null, all: false, requestApproval: false, comment: null, includeDraft: true, dryRun: false, None);
        Assert.Equal((ProjectItemOutcomes.Ready, SiteUnderTest.SomeoneElse), (Assert.Single(marked.Items!).Outcome, marked.Items![0].PendingDraft?.SavedBy));
        Assert.Equal("checkedIn", await scratch.StatusAsync(draft.Version!));
    }

    [SiteFact]
    public async Task Content_in_another_project_is_refused_in_its_language()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var first = await scratch.CreateProjectAsync(scratch.Name("first"));
        var second = (await scratch.CreateProjectAsync(scratch.Name("second"))).ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("in another project");
        await scratch.SetAsync(page, "in the first", project: first.ToString(CultureInfo.InvariantCulture));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => scratch.SetAsync(page, "in the second", project: second));
        Assert.Equal(AgentErrorReasons.InOtherProject, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.Contains($"opticli projects remove {first}", refused.Hint, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ConflictException>(() => scratch.SetAsync(page, "in the second", project: second, dryRun: true));
        Assert.Empty(await ProjectReader.ItemsAsync(scratch.Site.Session, int.Parse(second, CultureInfo.InvariantCulture), None));

        // Another language of the same page may be in another project.
        var swedish = await scratch.Writes.RunAsync(new TranslateOperation(page, "sv", Name: "på svenska") { Project = second }, dryRun: false, None);
        Assert.Equal(ProjectSaveItems.Added, Assert.IsType<WriteOutput>(swedish.Output).Project!.Item);
        // Taken out of the first, the page goes in the second.
        await scratch.Projects.RemoveAsync(first.ToString(CultureInfo.InvariantCulture), [page], "en", dryRun: false, None);
        Assert.Equal(ProjectSaveItems.Added, (await scratch.SetAsync(page, "in the second", project: second)).Project!.Item);
    }

    [SiteFact]
    public async Task Two_languages_of_a_page_go_live_together_in_one_project()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var project = (await scratch.CreateProjectAsync(scratch.Name("languages"))).ToString(CultureInfo.InvariantCulture);
        var page = await scratch.PageAsync("two languages");
        var english = await scratch.SetAsync(page, "in English", project: project, ready: true);
        var translated = await scratch.Writes.RunAsync(new TranslateOperation(page, "sv", Name: "två språk", new JsonObject { ["MetaDescription"] = "på svenska" }) { Project = project }, dryRun: false, None);
        var swedish = Assert.IsType<WriteOutput>(translated.Output);
        Assert.Equal((ProjectSaveItems.Added, false), (swedish.Project!.Item, swedish.Project.Ready));
        var items = await ProjectReader.ItemsAsync(scratch.Site.Session, int.Parse(project, CultureInfo.InvariantCulture), None);
        Assert.Equal(["en", "sv"], items.Select(i => i.Language).Order(StringComparer.Ordinal));

        // --lang marks only that language ready (and takes only that language out).
        var marked = await scratch.Projects.ReadyAsync(project, [page], "sv", all: false, requestApproval: false, comment: null, includeDraft: false, dryRun: false, None);
        Assert.Equal((ProjectItemOutcomes.Ready, "sv"), (Assert.Single(marked.Items!).Outcome, marked.Items![0].Content.Language));

        var published = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None);
        Assert.Equal(["en", "sv"], published.Published!.Select(i => i.Content.Language).Order(StringComparer.Ordinal));
        Assert.Equal(VersionId(english.Version!), await scratch.PublishedVersionAsync(page, "en"));
        Assert.Equal(VersionId(swedish.Version!), await scratch.PublishedVersionAsync(page, "sv"));
    }

    [SiteFact]
    public async Task A_site_handler_that_runs_during_a_project_publish_is_reported_and_a_failure_is_per_item()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null || !scratch.Site.HasProjectsFixture)
        {
            return;
        }
        // ProjectsFixture.cs makes and publishes a page next to every published "opticli-pp-notify ..." page, on the CMS's
        // publish thread, and fails the first publish of an "opticli-pp-flaky ..." page as CMS 12's task executor does.
        var project = (await scratch.CreateProjectAsync(scratch.Name("site handler"))).ToString(CultureInfo.InvariantCulture);
        var notify = await scratch.PageAsync("notify", name: $"opticli-pp-notify {Guid.NewGuid():N}");
        await scratch.SetAsync(notify, "goes live", project: project, ready: true);
        var flaky = await scratch.PageAsync("flaky", name: $"opticli-pp-flaky {Guid.NewGuid():N}");
        var flakyReady = await scratch.SetAsync(flaky, "fails once", project: project, ready: true);

        var published = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None);
        var made = Assert.Single(published.SiteChanges ?? [], c => c.Content.Name?.StartsWith("opticli-pp-made ", StringComparison.Ordinal) == true);
        scratch.Track(made.Content.Id);
        // The flaky page failed while the other went live: the project failed, nothing was tried again, and the result says so.
        Assert.Equal(ProjectStatuses.PublishFailed, published.Project.Status);
        Assert.Equal(flakyReady.Version, Assert.Single(published.Failed!).Content.Ref);
        Assert.Contains("ObjectDisposedException", published.SiteError, StringComparison.Ordinal);
        Assert.Null(published.Validation);

        // A failed project publishes again: the flaky page now goes live.
        var again = await scratch.Projects.PublishAsync(project, null, includeDraft: false, dryRun: false, confirm: null, None);
        Assert.Equal(ProjectStatuses.Published, again.Project.Status);
        Assert.Equal(flakyReady.Version, Assert.Single(again.Published!).Content.Ref);
        Assert.Single(again.AlreadyPublished!);

        // Alone, the flaky page's first publish fails before anything went live, so it is tried again, with a warning.
        var alone = (await scratch.CreateProjectAsync(scratch.Name("retried"))).ToString(CultureInfo.InvariantCulture);
        var flakyAlone = await scratch.PageAsync("flaky alone", name: $"opticli-pp-flaky {Guid.NewGuid():N}");
        await scratch.SetAsync(flakyAlone, "fails once", project: alone, ready: true);
        var retried = await scratch.Projects.PublishAsync(alone, null, includeDraft: false, dryRun: false, confirm: null, None);
        Assert.Equal(ProjectStatuses.Published, retried.Project.Status);
        Assert.Contains(retried.Validation ?? [], v => v.Message.Contains("tried again", StringComparison.Ordinal));
    }

    [SiteFact]
    public async Task A_plan_makes_its_project_and_marks_its_steps_ready_once_the_last_one_ran()
    {
        await using var scratch = await Scratch.StartAsync();
        if (scratch is null)
        {
            return;
        }
        var name = scratch.Name("plan");
        var plan = WritePlan.Parse($$$"""
            {"project": "{{{name}}}", "operations": [
              {"op": "create", "id": "page", "parent": "{{{scratch.Container}}}", "type": "StandardPage", "name": "{{{scratch.Name("plan page")}}}", "ready": true},
              {"op": "set", "ref": "$page", "properties": {"MetaDescription": "set later"}},
              {"op": "create", "id": "outside", "parent": "{{{scratch.Container}}}", "type": "StandardPage", "name": "{{{scratch.Name("plan outside")}}}", "project": null}]}
            """);
        var runner = new PlanRunner(scratch.Site.Session, scratch.Writes, Path.GetTempPath());
        var dry = await runner.RunAsync(plan, dryRun: true, publishAll: false, None);
        Assert.Equal(("toCreate", name), (Assert.Single(dry.Projects!).Status, dry.Projects![0].Name));
        Assert.DoesNotContain((await ProjectReader.ListAsync(scratch.Site.Session.Db, None)), p => p.Name == name);

        var run = await runner.RunAsync(plan, dryRun: false, publishAll: false, None);
        foreach (var id in run.Created.Values)
        {
            scratch.Track(int.Parse(id, CultureInfo.InvariantCulture));
        }
        var made = Assert.Single(run.Projects!);
        scratch.TrackProject(made.Id!.Value);
        Assert.Equal("created", made.Status);
        Assert.Single(made.MarkedReady ?? []);
        var item = Assert.Single(await ProjectReader.ItemsAsync(scratch.Site.Session, made.Id.Value, None));
        // The page's last version, with the later step's change, is the ready one.
        Assert.Equal((run.Created["page"], "checkedIn"), (item.Ref, item.Status));
        Assert.Equal(Assert.IsType<WriteOutput>(run.Operations[1].Result).Version, item.Version);

        // Plans don't publish a project.
        var publishing = Assert.Throws<UsageException>(() => WritePlan.Parse($$$"""{"project": "{{{name}}}", "operations": [{"op": "set", "ref": "{{{run.Created["page"]}}}", "properties": {"MetaDescription": "x"}, "publish": true}]}"""));
        Assert.Contains("isn't published or scheduled by itself", publishing.Message, StringComparison.Ordinal);
    }

    [SiteFact]
    public async Task Projects_are_refused_only_where_editors_have_neither_project_mode_nor_the_gadget()
    {
        await using var scratch = await Scratch.StartAsync(container: false);
        if (scratch is null)
        {
            return;
        }
        OptiCliException? refusal = null;
        ProjectResult? dry = null;
        try
        {
            dry = await scratch.Projects.CreateAsync(scratch.Name("disabled?"), dryRun: true, None);
        }
        catch (RefusedException ex)
        {
            refusal = ex;
        }
        if (dry is not null)
        {
            // The site's defaults: project mode on CMS 12, the Projects gadget on CMS 13 (where project mode is off), and
            // projects work in either (a site started with project mode turned on says projectMode on CMS 13 too).
            Assert.Contains(dry.EditorUi, new[] { ProjectEditorUi.ProjectMode, ProjectEditorUi.Gadget });
            if (scratch.Site.Session.Model.Schema.Major < 13)
            {
                Assert.Equal(ProjectEditorUi.ProjectMode, dry.EditorUi);
            }
            return;
        }
        // A site started with both off (EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=false and ...ProjectGadgetEnabled=false).
        Assert.Equal(AgentErrorReasons.ProjectsDisabled, Assert.IsType<AgentErrorDetails>(refusal!.Details).Reason);
        var page = (await ContentHeaderReader.ByIdAsync(scratch.Site.Session.Db, scratch.ApprovalRoot.ParentId!.Value, None))!;
        var save = await Assert.ThrowsAsync<RefusedException>(() => scratch.Writes.RunAsync(
            new SetOperation(WriteOutput.Id(page.Id), new JsonObject { ["MetaDescription"] = "x" }) { Project = "Edge project" }, dryRun: true, None));
        Assert.Equal(AgentErrorReasons.ProjectsDisabled, Assert.IsType<AgentErrorDetails>(save.Details).Reason);
        await Assert.ThrowsAsync<RefusedException>(() => scratch.Projects.ReadyAsync("Edge project", null, null, all: true, requestApproval: false, comment: null, includeDraft: false, dryRun: true, None));
        await Assert.ThrowsAsync<RefusedException>(() => scratch.Projects.PublishAsync("Edge project", null, includeDraft: false, dryRun: true, confirm: null, None));
    }

    private static int VersionId(string versionRef) => int.Parse(versionRef.Split('_')[1], CultureInfo.InvariantCulture);

    /// <summary>A scratch page below the edge-case root, and the projects a test makes; all deleted at the end.</summary>
    private sealed class Scratch : IAsyncDisposable
    {
        private readonly List<int> _projects = [];

        private readonly List<int> _pages = [];

        private Scratch(SiteUnderTest site, ContentHeader approvalRoot)
        {
            Site = site;
            ApprovalRoot = approvalRoot;
            Writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
            Projects = new ProjectWrites(Writes);
            Stamp = Guid.NewGuid().ToString("N")[..8];
        }

        public SiteUnderTest Site { get; }

        public ContentHeader ApprovalRoot { get; }

        public WriteExecutor Writes { get; }

        public ProjectWrites Projects { get; }

        /// <summary>The scratch page the test's pages go below.</summary>
        public string Container { get; private set; } = "";

        private string Stamp { get; }

        /// <returns>Null on a site without the edge-case fixture.</returns>
        public static async Task<Scratch?> StartAsync(bool container = true)
        {
            var site = await SiteUnderTest.ConnectAsync(None);
            if (await EdgeFixture.FindAsync(site, EdgeFixture.ApprovalRoot, None) is not { ParentId: not null } root)
            {
                await site.DisposeAsync();
                return null;
            }
            var scratch = new Scratch(site, root);
            if (container)
            {
                var made = await scratch.Writes.RunAsync(new CreateOperation(WriteOutput.Id(root.ParentId.Value), "StandardPage", scratch.Name("scratch"), Publish: true), dryRun: false, None);
                scratch.Container = WriteOutput.Id(made.CreatedId!.Value);
            }
            return scratch;
        }

        public string Name(string what) => $"opticli-it project {what} {Stamp}";

        public void Track(int page) => _pages.Add(page);

        public void TrackProject(int project) => _projects.Add(project);

        public async Task<int> CreateProjectAsync(string name)
        {
            var made = await Projects.CreateAsync(name, dryRun: false, None);
            Assert.True(made.Created);
            _projects.Add(made.Project.Id);
            return made.Project.Id;
        }

        /// <summary>A published page below the container (or <paramref name="parent"/>).</summary>
        public async Task<string> PageAsync(string what, string? parent = null, bool publish = true, string? name = null)
        {
            var made = await Writes.RunAsync(new CreateOperation(parent ?? Container, "StandardPage", name ?? Name(what), Publish: publish), dryRun: false, None);
            _pages.Add(made.CreatedId!.Value);
            return WriteOutput.Id(made.CreatedId.Value);
        }

        /// <summary>Sets MetaDescription, as <c>opticli set &lt;page&gt; MetaDescription=... --in-project ... [--ready]</c>.</summary>
        public async Task<WriteOutput> SetAsync(string page, string text, string? project = null, bool ready = false, bool dryRun = false)
        {
            var outcome = await Writes.RunAsync(new SetOperation(page, new JsonObject { ["MetaDescription"] = text }) { Project = project, Ready = ready }, dryRun, None);
            return Assert.IsType<WriteOutput>(outcome.Output);
        }

        /// <summary>A version's status as the CLI names it (checkedOut, checkedIn, published, ...), from the database.</summary>
        public async Task<string?> StatusAsync(string versionRef) =>
            (await Site.Session.Db.QueryAsync("SELECT Status FROM tblWorkContent WHERE pkID = @version", r => r.GetInt32(0), None,
                new SqlParameter("@version", VersionId(versionRef)))).Select(VersionStatusName).SingleOrDefault();

        /// <summary>The published version of a page's language branch, from the database; null when there is none.</summary>
        public async Task<int?> PublishedVersionAsync(string page, string language) =>
            (await Site.Session.Db.QueryAsync("""
                SELECT w.pkID FROM tblWorkContent w JOIN tblLanguageBranch l ON l.pkID = w.fkLanguageBranchID
                WHERE w.fkContentID = @id AND w.Status = 4 AND l.LanguageID = @language
                """, r => (int?)r.GetInt32(0), None,
                new SqlParameter("@id", int.Parse(page, CultureInfo.InvariantCulture)), new SqlParameter("@language", language))).SingleOrDefault();

        /// <summary>The comments a version's review was started with (the step -1 decisions).</summary>
        public async Task<IReadOnlyList<string>> ReviewCommentsAsync(string versionRef) =>
            await Site.Session.Db.QueryAsync("""
                SELECT d.Comment FROM tblApprovalStepDecision d JOIN tblApproval a ON a.pkID = d.fkApprovalID
                WHERE a.ApprovalKey = @key AND d.StepIndex = -1 AND d.Comment IS NOT NULL
                """, r => r.GetString(0), None,
                new SqlParameter("@key", $"content:/{versionRef.Replace('_', '/')}/"));

        private static string VersionStatusName(int status) => status switch
        {
            1 => "rejected",
            2 => "checkedOut",
            3 => "checkedIn",
            4 => "published",
            5 => "previouslyPublished",
            6 => "delayedPublish",
            7 => "awaitingApproval",
            _ => status.ToString(CultureInfo.InvariantCulture),
        };

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var project in _projects)
                {
                    try
                    {
                        if ((await ProjectReader.ListAsync(Site.Session.Db, None)).FirstOrDefault(p => p.Id == project) is { Status: ProjectStatuses.DelayedPublished })
                        {
                            await Projects.ReactivateAsync(project.ToString(CultureInfo.InvariantCulture), dryRun: false, None);
                        }
                        await Projects.DeleteAsync(project.ToString(CultureInfo.InvariantCulture), dryRun: false, None);
                    }
                    catch (OptiCliException)
                    {
                        // Gone already, or busy: the scratch content goes regardless.
                    }
                }
                foreach (var page in _pages.Where(p => Container != WriteOutput.Id(p)).Reverse())
                {
                    await TryDeleteAsync(WriteOutput.Id(page));
                }
                if (Container.Length > 0)
                {
                    await TryDeleteAsync(Container);
                }
            }
            finally
            {
                await Site.DisposeAsync();
            }
        }

        private async Task TryDeleteAsync(string page)
        {
            try
            {
                await Writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, None);
            }
            catch (OptiCliException)
            {
                // Below the container, which goes to the recycle bin with it.
            }
        }
    }
}
