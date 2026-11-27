# Tools

The module's own tools, which the assistant works with as the editor. Every tool's name is taken: a site's own tools
need others (see [Custom tools](#custom-tools)).

| Tool | Scope | Does |
|---|---|---|
| `whoami` | `content:read` | who the editor is, the scopes they approved, and what the site allows (publishing, deleting, upload size, preview links) |
| `get_content`, `list_children`, `resolve_url`, `find_content`, `get_content_type`, `list_versions`, `view_media` | `content:read` | read content, content types, versions and images as the editor |
| `create_content`, `update_content`, `add_language`, `upload_media`, `move_content`, `discard_draft` | `content:write` | change content, as a draft unless told otherwise, with a dry run first |
| `publish_content`, `unpublish_content` | `content:publish` | offered on every site, refused without `AllowPublish` (`publishingOff`); a version older than one someone else published since is a `conflict` (`newerPublished`) until `includeDraft` confirms it ([Security](security.md)) |
| `delete_content` | `content:write` | to the recycle bin; offered only with `AllowDelete` |
| `list_projects`, `get_project`, `create_project`, `mark_ready_to_publish`, `remove_from_project`, `publish_project`, `reactivate_project` | see [Projects](#projects-for-editors-assistants) | CMS projects |

## Projects for editors' assistants

An editor's assistant can collect drafts in a CMS project, so the editor reviews them together in the CMS and they go
live together: it suggests one when the editor wants several related changes reviewed or published together, an
existing project or a new one, and asks first. The module follows the same rules as the CLI's
[CMS projects](../cli/cms-projects.md#cms-projects), as the editor, with their access rights.

| Tool | Scope | Does |
|---|---|---|
| `list_projects(all, limit)` | read | projects newest first, each with `items` and `ready` (how many are marked Ready to publish): every one that isn't published (active, scheduled, failed), and published ones too with `all`; 50 by default, at most 200, `more` when there are more |
| `get_project(project)` | read | the project and its items, each with its version, `state` (`draft`, `ready`, `inReview`, `scheduled`, `published`, `deleted`: in the recycle bin, `missing`: the version is gone) and `editUrl` |
| `create_project(name, dryRun)` | write | an active, empty project; a name an unpublished project has is refused |
| `mark_ready_to_publish(project, items \| all, lang, requestApproval, comment, includeDraft, dryRun)` | write | marks items Ready to publish; under an approval sequence `requestApproval` sends them for review instead |
| `remove_from_project(project, items, lang, dryRun)` | write | takes items out of an active project; their versions stay as they are |
| `publish_project(project, publishAt, includeDraft, dryRun, blocks, skipBlocks)` | publish | publishes or schedules the items marked ready (with the blocks of their own folders, as `publish_content`); `failed` lists items the CMS didn't publish, with `siteError` |
| `reactivate_project(project, dryRun)` | publish | cancels a scheduled project's publish; its items are drafts again |

A project is named by its id or its exact name. `create_content`, `update_content`, `add_language` and `upload_media`
take `project` and `ready`: the draft they save goes into the project (`ready` marks it Ready to publish), and their
result's `project` says how (`added`, `replaced`, `unchanged`). A save goes into a project only when it names it, so the
assistant names it on every save that belongs there. The other tools save no draft that could go into one. `get_content`
lists the projects that hold a version of the content (`projects`: `id`, `name`, `status`, `version`, `language`).

- **Ready only.** `publish_project` puts live only the items marked Ready to publish; the rest stay as they are, and
  the result lists them (`notReady`, `alreadyPublished`) beside what went live (`published`, or `scheduled`), each item
  with its `editUrl`, and what went live with its `url` too. Every ready item is checked first, as `publish_content` checks one: one problem stops
  the publish and nothing goes live. A ready item older than a version someone else published since would replace their
  published changes: its problem is `newerPublished`, naming who published it and when, and `includeDraft` confirms it,
  as it does a colleague's unpublished changes (`pendingDraft`); the item's warnings then say which version it replaces. The server's instructions tell the assistant to dry-run first, show the editor
  what goes live, whose changes go with it and what stays behind, and publish only when they ask. The result's
  `siteChanges` says what the site's own code did meanwhile, as far as it ran on the publish's own thread (see
  [CMS projects](../cli/cms-projects.md#cms-projects)).
- **Publishing needs `AllowPublish`**, as `publish_content` does: `publish_project` and `reactivate_project` are offered
  on every site, need the `content:publish` scope, and are refused elsewhere (`publishingOff`) with a hint to mark the
  items ready and have the editor publish the project in the CMS. Where the site doesn't let assistants publish, the
  instructions say so.
- **Access.** Marking an item ready and taking it out need Edit access (and access to its language) on it, publishing
  needs Publish on every ready item, and reactivating Publish on every scheduled one, all checked before anything
  changes. `mark_ready_to_publish` with `all` leaves out drafts the editor may not edit (`noAccess`) and lists them;
  named in `items`, such an item stops the call. Items of content the editor can't read aren't in the project for them:
  lists and counts leave them out and naming one is `not_found`. A ready (or, for a reactivate, scheduled) item they
  can't read refuses the publish, with only how many there are.
- **Dry runs report problems per item.** `mark_ready_to_publish`, `remove_from_project`, `publish_project` and
  `reactivate_project` with `dryRun` answer `valid: false` with each item's `problem`; the real call is refused with
  every item in `error.project`.
- **Links.** Where the CMS has project mode, project results (and a save's `project`) have `projectUrl`, the project's
  overview in the edit UI (`/EPiServer/CMS/#context=epi.cms.project:///<id>`, under `/Optimizely/CMS/` on CMS 13). Opening
  it makes it the editor's current project, so their own later saves in the edit UI go into it too; the instructions
  tell the assistant to say so. An open overview can show an item's old status until the editor refreshes it. With only
  the Projects gadget (CMS 13's default) there is no such link: `projectNote` tells the editor to open the gadget, and
  that its Publish puts every unpublished item of the project live, ready or not.
- **Where editors have no projects** (project mode and the gadget both off), the reads answer with `editorUi: "none"`
  and every project write is refused (`projectsDisabled`).
- **Audit.** Each line of a call that names a project ends with `, project <id> (<name as given>)`, and items are logged
  as content references.

## Custom tools

A site can offer editors' assistants tools of its own beside the module's: a search in the site's own index, a report,
a change across many pages. They run on the module's endpoint only, as the signed-in editor, with the same sign-in,
scopes, audit log and errors as the module's tools. A tool that changes content doesn't save anything itself: it returns
a plan, and the module checks it with a dry run the assistant shows the user, then runs it through its own content
operations, as drafts.

`AddOptiCliMcp` returns a builder (`IOptiCliMcpBuilder`) to add them with:

```csharp
services.AddOptiCliMcp()
    .WithTools<ArticleSearchTools>()              // read tools: every tool method of the class
    .WithWriteTool<ReplaceInDescriptionsTool>()   // a write tool, one class each
    .WithInstructions("Search article pages with search_articles, which searches their text.");
```

`WithInstructions` adds a line to the instructions the server gives assistants, after the module's own, under a heading
that says it comes from the site's developers. Before 0.30.0, `AddOptiCliMcp` returned the `IServiceCollection`: a site
that ignores what it returns compiles as before, and one that chained other registrations on it goes on through
`.Services` (`services.AddOptiCliMcp().Services.AddSingleton<...>()`).

**Read tools** are MCP SDK tool classes, written as the SDK documents them (parameters with `[Description]`, async,
`CancellationToken`, image results). Every tool method needs `[McpServerTool]` with a `Name` and `ReadOnly = true`. The
class needs no `[McpServerToolType]`, and is better off without it: the SDK's `WithToolsFromAssembly()` picks up classes
that have it for the site's other MCP servers (the module removes them there, but there is no point). The class is made
for every call from the request's services, so it takes the site's services, and `IOptiCliEditor`, in its constructor.
A read or write tool class or method with `[Authorize]` (or another ASP.NET Core authorization attribute) stops the site
at startup: the module doesn't apply them, so the tool would be open to every editor connected to it. Check what the
tool needs in the tool itself; it always runs as the signed-in editor, with their access rights.



```csharp
using System.ComponentModel;
using EPiServer.Core;
using ModelContextProtocol.Server;
using OptiCli.Mcp;

public sealed class ArticleSearchTools(IOptiCliEditor editor, IArticleIndex index)
{
    [McpServerTool(Name = "search_articles", Title = "Search articles", ReadOnly = true, OpenWorld = false)]
    [Description("Finds article pages whose text contains the words, best match first.")]
    public async Task<IReadOnlyList<ContentDescription>> SearchArticles(
        [Description("The words to look for.")] string text,
        CancellationToken cancellationToken)
    {
        // The site's own search index (IArticleIndex here), which knows nothing of the editor's access rights.
        IReadOnlyList<ContentReference> hits = await index.SearchAsync(text, cancellationToken);
        // Readable keeps what the editor may read, in the same order; Describe gives each as the module's tools do.
        return editor.Readable(hits).Select(editor.Describe).ToList();
    }
}
```

`IOptiCliEditor` is the editor the call runs for: `Name`, `Roles`, `Scopes` and `HasScope`, and the content as they
may read it: `Load<T>` (as `get_content` loads it: the published version, or the newest with `latest: true`; `not_found`
alike for content that doesn't exist and content they can't read), `CanRead`, `Readable`, and `Describe` (a
`ContentDescription`: the fields the module's tools give for content, with `url` and `editUrl`). A tool fails on
purpose with `OptiCliToolException(code, message, hint)`, which the assistant gets as the module's own JSON error
(`usage`, `not_found`, `refused`, `conflict`). The CMS's own not-found and access-denied exceptions become `not_found`
and `refused`, as for the module's tools. Anything else it throws is logged with the tool's name and reaches the
assistant as `internal`, without its message. An argument that is missing or of the wrong type is a `usage` error. The
packages' XML docs have the details of every member.

**Write tools** implement `IOptiCliWriteTool<TArgs>`, whose one method, `PlanAsync`, returns a `ContentPlan`: what to
change, in order. The class has `[OptiCliWriteTool]`, with its name, title and the kinds of steps its plans may have,
and a `[Description]`:

```csharp
using System.ComponentModel;
using EPiServer;
using EPiServer.Core;
using OptiCli.Mcp;

public sealed record ReplaceInDescriptionsArgs(
    [property: Description("The id of the page below which to change pages.")] int Below,
    [property: Description("The text to replace, exactly as written.")] string Find,
    [property: Description("The text to put in its place.")] string Replace);

[OptiCliWriteTool("replace_in_descriptions", Title = "Replace in meta descriptions", Kinds = ContentPlanKinds.Update)]
[Description("Replaces a text in the meta description of every page below a page.")]
public sealed class ReplaceInDescriptionsTool(IOptiCliEditor editor, IContentLoader loader)
    : IOptiCliWriteTool<ReplaceInDescriptionsArgs>
{
    public Task<ContentPlan> PlanAsync(ReplaceInDescriptionsArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(args.Find))
        {
            throw new OptiCliToolException("usage", "find is empty.", "Give the text to replace.");
        }
        var below = editor.Load<PageData>(new ContentReference(args.Below));
        var plan = new ContentPlan();
        // In a fixed order, and only what the editor may read (SitePageData: Alloy's base class for its pages).
        var links = loader.GetDescendents(below.ContentLink.ToReferenceWithoutVersion()).OrderBy(link => link.ID);
        foreach (var found in editor.Readable(links).OfType<SitePageData>())
        {
            // The newest version, a draft included: what a change is planned from.
            var page = editor.Load<SitePageData>(found.ContentLink.ToReferenceWithoutVersion(), latest: true);
            if (page.MetaDescription?.Contains(args.Find, StringComparison.Ordinal) == true)
            {
                var text = page.MetaDescription.Replace(args.Find, args.Replace, StringComparison.Ordinal);
                plan.Update(page, p => p.Set("MetaDescription", text));
            }
        }
        return Task.FromResult(plan);
    }
}
```

The assistant sees a tool with `TArgs`' properties as its arguments, camelCase, plus two the module adds, `dryRun` and
`planToken` (so `TArgs` may not have properties of those names, in any case). An argument the type doesn't have, a missing required one, or `null` for one that can't be null is refused
before the tool runs (in a project without nullable reference types, as Alloy's, a `string` may be `null`, so check).
With `dryRun: true` the module calls `PlanAsync`, checks every step as the editor with its own dry runs (access rights,
the edit UI's rules, validation, `baseVersion`, the delete gate), and answers with each step's changes, a `planToken`
and when it expires (`planTokenExpires`); a plan with a step that would be refused gets no token. A plan with a step on
content the editor can't read (or that doesn't exist) is refused whole, `not_found`, naming no step or id. With `dryRun: false`
and the token, the module calls `PlanAsync` again, refuses the run (`conflict`) if the plan isn't the one the dry run
showed, checks every step again, and saves them in order. The tool's description tells the assistant to do the dry run
first and show the user the changes. After your `[Description]`, the module adds what the tool's steps do to the site,
from its `Kinds`: "saves drafts only, never publishing" for `Update` and `Create`; with `Move`, that moving published
content changes its live URL and those of the content below it; with `Trash`, that content put in the recycle bin goes
off the site until an editor restores it.

| Step | What it does |
|---|---|
| `Update(content, p => ...)` | a new draft of the content in its language, as `update_content` saves one: `p.Set(property, value)` (in the forms `update_content` takes; a ContentArea item `new { type, properties }` is a new block, inline or a shared block in the content's "For this page" folder as [the site makes them](../cli/content-areas.md#new-blocks-in-a-contentarea), listed in the step's `sharedBlocks`), `SetName`, `AddToArea`, `RemoveFromArea` |
| `Create(parent, type, name, v => ..., lang)` | new content, as a draft, as `create_content` makes it; `CreateFor(content, ...)` makes it in the content's "For this page" folder |
| `Move(content, destination)` | moves the content, and everything below it, as `move_content` does |
| `Trash(content)` | moves the content, and everything below it, to the recycle bin, as `delete_content` does |

`Create` returns a `PlannedContent`, which later steps of the same plan take as a parent, a destination or a reference
in a value. When the plan runs, the module puts the new content's reference in its place:

```csharp
// Kinds = ContentPlanKinds.Create | ContentPlanKinds.Update
var folder = plan.CreateFor(page, "SysContentFolder", "Spring campaign");
var teaser = plan.Create(folder, "TeaserBlock", "Spring sale", v => v.Set("Heading", "Spring sale"));
plan.Update(page, p => p.AddToArea("MainContentArea", teaser));
```

`plan.InProject(project, ready: false)` (the project's exact name, or its id) puts the draft of every update and create
in the plan in that [CMS project](#projects-for-editors-assistants), as the save tools' `project` does, and with `ready`
marks each Ready to publish. Moves and trashes save no draft, so they aren't in it, and a folder a create makes is left
out. A plan goes into one project, once (another project, or another `ready`, throws). The project must exist and be
active, and the dry run checks every step against it (content in another project, an approval sequence where `ready`
is asked for: a plan sends nothing for review); the plan token covers it. Name a project only when the editor asked for
it, typically from an argument of the tool:

```csharp
if (!string.IsNullOrWhiteSpace(args.Project))
{
    plan.InProject(args.Project, args.Ready);
}
```

What a site's tools must allow for:

- **Read as the editor.** The CMS's `IContentLoader` and `IContentRepository` don't check access rights. Load content
  through `IOptiCliEditor`, and pass ids that a search index or the site's own code found through `Readable` (not
  `Load`, whose `not_found` names the id), so content the editor can't read doesn't exist for the assistant: not in a
  result, a count or a message. The module checks every step as the editor all the same, and refuses a plan that names
  hidden content as a whole, without saying which. Use the editor during the
  call only: kept in a singleton, a cache or a background task, it throws.
- **Plan from the latest version.** An update is based on the version of the content passed, and is a `conflict` when
  there is a newer one. Content loaded without a version (`IContentLoader.Get`, `Load` without `latest`) counts as its
  published version, so any newer draft, the editor's own included, makes the step a `conflict` until that draft is
  published or discarded. Load what a tool changes with `Load<T>(link, latest: true)`.
- **Plans are deterministic.** The real run plans again and compares, so the same content and arguments must give the
  same plan: no timestamps, new GUIDs or random order (sort what a search returns).
- **Drafts only, recycle bin only.** Nothing in a plan publishes, and nothing deletes for good. Content without versions
  (other than folders) is refused, as a save would put it live at once. A move of content that may be live goes
  through the publishing gate, as `move_content` does: it needs `AllowPublish` and the `content:publish` scope.
- **No transaction.** The real run checks every step before saving the first, but a step can still fail as it runs (a
  value that refers to content an earlier step creates is only checked then). The run stops there, the steps before it
  stay saved, and the result says, step by step, what was saved, what failed and why, and what didn't run.
- **A run can't be resumed.** Plan from the content's current state (content that already has the change gets no step),
  so that after a run that stopped, a new dry run shows what is left.
- **A token runs once, within 30 minutes.** After a run the same token is a `conflict`, and so is one older than 30
  minutes: the assistant does a new dry run. A retry after a timeout needs a new dry run too. On a site whose own code
  files new content elsewhere when it is saved (in a year folder, say), a retry of a create with the same token within
  those 30 minutes can create it twice. Tokens are protected with the site's Data Protection keys, as access tokens are.
  The module takes the time from the site's `TimeProvider`, if it registers one before `AddOptiCliMcp()`.
- **References from drafts.** A trash step's dry run lists how much goes with it (`descendants`) and what refers to it
  (`referencedBy`), but not references in unpublished drafts; the report says so.
- **`MaxPlanSteps`** (100 by default, 1 to 1000) caps a plan's steps; a larger plan is refused at the dry run with a
  hint to narrow the arguments. A run saves its steps within the assistant's call, which its client gives up on after a
  while (often a minute): keep it at what the site saves in well under that.
- **Scopes and options.** A write tool needs `content:write`, as the module's own changes do; read tools run with
  `content:read` (`HasScope` tells a tool what else was approved). A tool whose `Kinds` has `Trash` is offered only with
  `AllowDelete`. A step of a kind the tool didn't declare, or a mistake in the plan (the same content updated twice, a
  step on content an earlier step trashes), fails as a fault of the site: `internal`, with the details in its log.
- **Names.** Every tool needs a name of its own: none of the module's tools' (`delete_content` included, whether it is
  offered or not), none of the site's others'. Names, attributes and argument types are checked at startup:
  `WithTools`, `WithWriteTool` or `MapOptiCliMcp()` throws an `InvalidOperationException` that names the class and
  method.
- **Audit.** Every call is in the audit log, as the module's own are, and a write tool's real run adds a line per step
  (its kind, the content and the outcome).
