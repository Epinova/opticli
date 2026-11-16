using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OptiCli.Cms.Operations;
using OptiCli.Mcp.OAuth;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// CMS projects (edit mode's Projects): drafts of many items, reviewed together and published together. Reading them,
/// making one, marking its items ready and taking items out, as the editor and with their access rights: Edit on every
/// item they mark ready or take out. Saves go into a project through the save tools' <c>project</c>
/// (<see cref="WriteTools"/>); publishing one is <see cref="ProjectPublishTools"/>.
/// </summary>
/// <remarks>
/// A project has no access rights of its own in the CMS: every editor sees every project, as in the edit UI. Of its
/// items, an editor sees only content they can read.
/// </remarks>
[McpServerToolType]
internal sealed class ProjectTools(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
    : ContentToolBase(http, options, loggers)
{
    internal const string Project = "The project: its id, or its exact name.";

    internal const string Items = "The items: content ids (every item of that content in the project, in lang if given) or versions (123_456), as get_project lists them.";

    [McpServerTool(Name = "list_projects", Title = "List projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the CMS's projects (drafts of many items that are reviewed and published together), newest first: id, name, status (active; delayedPublished: scheduled; publishFailed; published), items, and ready (how many are marked Ready to publish, which is what publishing the project puts live). Published projects only with all. Each has projectUrl where the CMS has project mode; elsewhere projectNote says where the user finds projects. editorUi none means editors have no projects on this site.")]
    public string ListProjects(
        [Description("Published projects too; left out, only the others (active, scheduled, failed).")] bool all = false,
        [Description("Most projects, default 50, at most 200.")] int? limit = null) =>
        Run(Scopes.Read, call => ProjectReadOperation.List(call, all, limit), ResultUrls.SerializeProjects);

    [McpServerTool(Name = "get_project", Title = "Get project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads one project: its status, and its items, each with its version, state (draft, ready, inReview, scheduled, published, deleted, missing) and editUrl for the user to open it in the CMS. Publishing the project puts live only the items whose state is ready. Returns projectUrl (or projectNote) for the user to open the project.")]
    public string GetProject([Description(Project)] string project) =>
        Run(Scopes.Read, call => ProjectReadOperation.Get(call, project), ResultUrls.SerializeProject);

    [McpServerTool(Name = "create_project", Title = "Create project", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a CMS project, to collect the drafts of several related changes so the user reviews them together and they go live together. Only when the user wants one. A name an unpublished project has already is refused: save into that one. Then save drafts into it with the save tools' project. Returns the project's id and projectUrl (or projectNote) for the user.")]
    public string CreateProject(
        [Description("The project's name, at most 255 characters.")] string name,
        [Description("Only check that it can be made.")] bool dryRun = false) =>
        Run(Scopes.Write, call => ProjectCreateOperation.Run(call, new ProjectCreateRequest { Name = name, DryRun = dryRun }), ResultUrls.SerializeProject);

    [McpServerTool(Name = "mark_ready_to_publish", Title = "Mark ready to publish", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Marks items of a project Ready to publish (the CMS's Ready to Publish), so publishing the project puts them live. Only when the user wants them ready. Give items, or all for every item that is a draft. Where an approval sequence applies it is refused: requestApproval sends those items for review instead, and marks the others ready. Needs edit access to each item. Every item is checked first: one that can't be marked stops the call, with each item's problem in error.project. Run it with dryRun first.")]
    public string MarkReadyToPublish(
        [Description(Project)] string project,
        [Description(Items + " Give this or all.")] string[]? items = null,
        [Description("Every item that is a draft; the others are left as they are (skipped).")] bool all = false,
        [Description("Only items in this language, e.g. en.")] string? lang = null,
        [Description("Send items that have an approval sequence for review instead (the CMS's Ready for Review).")] bool requestApproval = false,
        [Description("With requestApproval: a comment for the reviewers, required where the sequence asks for one.")] string? comment = null,
        [Description("Only check, and report what each item would get; valid: false, with each item's problem, when one can't be marked.")] bool dryRun = false) =>
        Run(Scopes.Write, call => ProjectReadyOperation.Run(call, project, new ProjectReadyRequest
        {
            Items = items,
            All = all,
            Lang = lang,
            RequestApproval = requestApproval,
            Comment = comment,
            DryRun = dryRun,
        }), ResultUrls.SerializeProject);

    [McpServerTool(Name = "remove_from_project", Title = "Remove from project", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Takes items out of a project: their versions stay as they are (a draft stays a draft), they just aren't published with it. Only from an active project. Needs edit access to each item. Confirm with the user first; dryRun shows what goes.")]
    public string RemoveFromProject(
        [Description(Project)] string project,
        [Description(Items)] string[] items,
        [Description("Only items in this language, e.g. en.")] string? lang = null,
        [Description("Only check, and report what would be taken out; valid: false, with each item's problem, when one can't be.")] bool dryRun = false) =>
        Run(Scopes.Write, call => ProjectRemoveOperation.Run(call, project, new ProjectRemoveRequest { Items = items, Lang = lang, DryRun = dryRun }), ResultUrls.SerializeProject);
}

/// <summary>
/// Publishing a project, scheduling it and cancelling the schedule: what a site allows only with
/// <see cref="OptiCliMcpOptions.AllowPublish"/>, so only offered there (<see cref="ModuleTools"/>), and then only to a
/// connection granted <see cref="Scopes.Publish"/>, as far as the editor's access rights allow (Publish on every item).
/// </summary>
/// <remarks>
/// Unlike <see cref="PublishTools"/>, not offered where the site doesn't allow publishing: a site that hasn't opted in
/// keeps the tool list it had, apart from the project tools that make drafts (<see cref="ProjectTools"/>). The gate is
/// checked all the same, should either be called regardless.
/// </remarks>
[McpServerToolType]
internal sealed class ProjectPublishTools(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
    : ContentToolBase(http, options, loggers)
{
    [McpServerTool(Name = "publish_project", Title = "Publish project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Publishes a project's items marked Ready to publish, together, now or at publishAt; items that aren't ready stay as they are (notReady), as do items live already (alreadyPublished). Only when the user asked to publish the project: run it with dryRun first, show the user what goes live (published, or scheduled), whose unpublished changes go with it (an item's pendingDraft) and what stays behind, and publish once they confirm. Every ready item is checked first, as publish_content checks one: a problem stops the publish, with each item's in error.project, and nothing goes live. Returns each item's url and editUrl, failed (items the CMS didn't publish, with siteError) and siteChanges (what the site's own code did meanwhile).")]
    public Task<string> PublishProject(
        [Description(ProjectTools.Project)] string project,
        [Description("Schedule the publish for this time instead (ISO 8601 with offset, e.g. 2026-10-05T08:00:00+02:00): only when the user asked for it.")] DateTime? publishAt = null,
        [Description("Also put live the unpublished changes someone else saved that ready items carry (their pendingDraft): only after asking the user.")] bool includeDraft = false,
        [Description("Only check, and report what would go live and what would stay behind; valid: false, with each ready item's problem, when the publish would be refused.")] bool dryRun = false) =>
        RunAsync(Scopes.Write, call => ProjectPublishOperation.RunAsync(call, project, new ProjectPublishRequest
        {
            PublishAt = publishAt,
            IncludeDraft = includeDraft,
            DryRun = dryRun,
        }), ResultUrls.SerializeProject, PublishingIf(true));

    [McpServerTool(Name = "reactivate_project", Title = "Reactivate project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Cancels a scheduled project's publish: the project is active again, and its scheduled items are drafts again, not ready (mark them ready again before the project is published). Needs publish access to each scheduled item. Only when the user asked for it, after confirming; dryRun checks it.")]
    public Task<string> ReactivateProject(
        [Description(ProjectTools.Project)] string project,
        [Description("Only check that it can be reactivated, and which items it would unschedule; valid: false, with each item's problem, when one can't be.")] bool dryRun = false) =>
        RunAsync(Scopes.Write, call => ProjectReactivateOperation.RunAsync(call, project, new ProjectReactivateRequest { DryRun = dryRun }), ResultUrls.SerializeProject, PublishingIf(true));
}
