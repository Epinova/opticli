# CMS projects

A project in the CMS's edit mode collects drafts of many items, so editors review them together and they go live in one
go, now or at a set time: a campaign, a new section, thirty pages. opticli makes projects, puts its drafts in them,
marks items Ready to publish, and publishes or schedules a project, through the CMS's own project API in the running
site. Reading projects comes from the database and needs nothing running.

```sh
opticli projects create "Spring campaign"
opticli set 123 Heading="Spring sale" --in-project "Spring campaign"          # a draft, in the project
opticli create 45 --type ArticlePage --name "Spring news" --in-project "Spring campaign" --ready
opticli projects "Spring campaign"                                             # its items and their status
opticli projects ready "Spring campaign" 123                                   # mark the first draft ready too
opticli projects publish "Spring campaign" --dry-run                           # what goes live, what stays behind
opticli projects publish "Spring campaign" --at 2026-12-01T07:00Z              # or now, without --at
```

A `<project>` is its id or its exact name (whole, ignoring case). Names aren't unique in the CMS: a project that isn't
published wins over published ones of the same name, and a name two unpublished projects share is a `usage` error that
lists their ids. Only active projects (status `active`, or `publishFailed` after a failed publish) take items, have
items marked ready or taken out, and publish; a published project is a `conflict` (`details.reason:
"projectNotActive"`), and so is a scheduled one (`delayedPublished`), whose hint is `projects reactivate`.

| Command | Does |
|---|---|
| `projects [<project>]` | lists projects (`id`, `name`, `status`, `created`, `createdBy`, `publishAt`, `items`, `ready`: the items a publish puts live); with a project, its items (`ref`, `version`, `type`, `name`, `language`, `status`: `checkedIn` is ready, `checkedOut` a draft, `awaitingApproval` in review) |
| `projects create <name>` | an active, empty project. A name a project that isn't published has already is a `conflict`; at most 255 characters |
| `<write> --in-project <project> [--ready]` | `set`, `create`, `area`, `composition`, `block create`, `upload` and `translate` (with `--with-blocks`, the translated blocks too) put the draft they save in the project; `--ready` marks it Ready to publish |
| `projects ready <project> <ref>... \| --all [--lang] [--request-approval [--comment <text>]] [--include-draft]` | marks items Ready to publish, as the edit UI's Ready to Publish does. Refs are content (every item of it, in `--lang` if given) or versions; `--all` takes every item that is a draft |
| `projects remove <project> <ref>... [--lang]` | takes items out; their versions stay as they are |
| `projects publish <project> [--at <time>] [--include-draft] [--with-block <ref>]... [--without-block <ref>]...` | publishes (or schedules) the items marked Ready to publish, together; where the site makes shared area blocks, with the never-published blocks of each item's own folder (the block options choose as for [`publish`](content-areas.md#new-blocks-in-a-contentarea)) |
| `projects reactivate <project>` | cancels a scheduled project's publish |
| `projects delete <project>` | deletes the project, as the edit UI's Delete Project does; its drafts stay drafts. Not while it is scheduled, being published or reactivated |

Every write takes `--dry-run` and prints the project (`id`, `name`, `status`, `scheduledFor` when scheduled, `items`, `ready`), `editorUi`, `dryRun`
and `valid`. `projects delete --dry-run` checks the project in the database (`meta.source: db`).

**A write joins a project only when it names it**, every time: there is no current project that later saves go into.
(`--in-project`, not `--project`: that global option names the site's .csproj.) The result's `project` says what
happened: `id`, `name`, `item` (`added`; `replaced`, with `replaced` the version the project held before; `unchanged`
when the project holds that version already; `none` when nothing was saved and the version it was based on isn't a
draft), `ready`, `editorUi`, and `error` when the save stood but putting it in
the project failed (also a warning).
- A project holds one version per content and language, as the edit UI's save in project mode does: a new draft takes
  the place of the content's earlier version in that project and language. Two languages of a page are two items.
- Content that is in another project that isn't published, in the same language, is a `conflict` (`details.reason:
  "inOtherProject"`, naming the project and the version): take it out of that project first. Folders and content
  without versions (which would go live at once) are a `usage` error.
- `--in-project` with `--publish` or `--publish-at`, and `--ready` without `--in-project`, are `usage` errors before
  the database is opened: publish or schedule the project instead.
- A write with nothing to change puts the version it is based on in the project when that is a draft (a plan run
  again), and warns when it is published.
- A save without `--in-project` of content that is in a project saves as before, and warns that the new version isn't
  in the project: which version the project holds, and to save again with `--in-project` (for a scheduled project,
  reactivate it first).

**Marking ready** checks the version in; it keeps its version id, and the project keeps pointing at it.
- Every item is checked before any is changed: an item that can't be marked (in review, published, scheduled, a
  validation error) stops the call, with each item's `problem` in `details.project`. With `--all`, items that aren't
  drafts are left as they are (`skipped`); an item ready already is `alreadyReady`. Each item's `outcome` is in
  `items[]`.
- Under an approval sequence, marking ready would skip the reviewers, so it is refused (exit 3, `details.reason:
  "approvalSequence"`), on a save with `--ready` too. `--request-approval` sends those items for review instead, as the
  edit UI's Ready for Review does (`approvalRequested`; they stay in the project); items without a sequence are marked
  ready as usual. `--comment` goes to the reviewers on `projects ready`, and is required where the sequence says so.
  Once a reviewer approves an item, it counts as ready without another step.
- An item whose version someone else saved is a `conflict` (`details.reason: "pendingDraft"`) without
  `--include-draft`: the CMS records whoever marks a version ready as the one who saved it, so the project's publish
  could no longer tell whose changes it carries. Confirm with the person whose draft it is. A save with `--ready` never
  checks in someone else's draft: a changed save makes a version of its own, and an unchanged one puts their draft in the
  project without marking it, with a warning.

**Publishing a project puts only the items marked Ready to publish live**, as the edit UI does in project mode. Items
that aren't ready (drafts, in review, rejected) stay as they are, and the result lists them. That holds wherever
editors use projects, also on a site with only the Projects gadget (CMS 13's default, `editorUi: "gadget"`), whose own
Publish button puts every unpublished item of the project live, ready or not: the same project goes live differently
from the gadget than from opticli.
- Before anything is published, every ready item goes through `publish`'s checks: an approval sequence (the version must
  have been approved), validation, and other people's unpublished changes. One problem stops the whole publish with
  nothing published, each item's `problem` in `details.project` (`details.reason` is the one the problems share, such as
  `pendingDraft`, else `projectItems`). No item ready is a `conflict` (`details.reason: "nothingReady"`). A passed
  stop-publish date is a warning.
- **Marking an item ready is consent to publish it.** The changes of whoever marked an item ready (`readyBy`) go live
  without `--include-draft`, also their own older drafts in it; after an approval that is the reviewer, whom the CMS
  records as the one who checked the version in. Changes of someone else that the ready version carries still need
  `--include-draft` (`pendingDraft` names them). Each item's `pendingDraft` and `readyBy` say whose changes go live,
  either way.
- On a terminal (stdin, stdout and stderr), `projects publish` first does a dry run, shows what goes live and whose
  changes go with it, what stays behind and what is published already, and asks; no is a `conflict` (exit 5, "Not
  published, as answered"). Elsewhere it publishes as asked, and other people's changes need `--include-draft`.
- It publishes through the site's `ProjectPublisher` (a site's own one too) and waits until every item has been tried.
  The result has `published[]` (or `scheduled[]` and `scheduledFor`), `notReady[]`, `alreadyPublished[]`, `failed[]`
  (each with the CMS's message in `error`; the publish's own in `siteError`), and per item `previouslyPublished`. When an item fails, the others still go
  live and the project is `publishFailed`; publishing it again tries the rest.
- `siteChanges` lists what the site's own code did while the project was published (content its handlers created,
  published, moved or deleted). The CMS publishes a project on a background thread of its own, so opticli records the
  content events raised on that thread while the publish runs; work that a site's handler hands to yet another thread
  isn't seen.
- On CMS 12 the CMS's background task sometimes fails to start before it publishes anything ("Cannot access a disposed
  object"). opticli then tries again, up to twice, only while every ready item is still unpublished, and says so in a
  warning.
- `--at <time>` (an ISO 8601 time, UTC unless it has an offset) schedules it instead: the ready items wait to be
  published and the project is `delayedPublished`. `projects reactivate` cancels that: the project is active again, and
  its scheduled items are drafts again (the same versions), not ready, so mark them ready again before publishing.

**Plans** take `"project"` next to `"operations"` (a name, or an id as a number): every `set`, `create`, `area`,
`composition`, `block`, `upload` and `translate` step puts its draft in that project. A step's own `"project"` overrides
it, and `"project": null` leaves the step out; a folder a `create` step makes is left out. `"ready": true` on a step
marks its draft ready. A project the plan names (on the plan or on a step) that no unpublished project has is created by
the run, after the whole plan passed its check and before the first step saves; a number that is no project's id is
`not_found`, never a new project's name. A dry run checks the steps of a project still to be made without it, with a
warning (content in another project is then found only when the plan runs). A step with `ready` whose content a later
step changes is marked ready after the last step, so the later step saves into the same draft. `data.projects[]` has
each project's `id`, `name`, `status` (`existing`, `created`, `toCreate` in a dry run), `markedReady` and, for a project
the run made, `undo`. Plans don't publish projects: `"publish"` or `"publishAt"` on a step with a project is a plan
problem, and `apply --publish` with a project a `usage` error. Sealed production runs refuse `"project"`.

Editors only have projects where the CMS's edit UI offers them: project mode (the project bar; on by default on CMS 12,
off on CMS 13: `EPiServer:CmsUI:ProjectUI:ProjectModeEnabled`) or the Projects gadget (`ProjectGadgetEnabled`, on by
default). Where both are off, every project write is `refused` (exit 3, `details.reason: "projectsDisabled"`).
`editorUi` says which one the site has (`projectMode` or `gadget`), so you can tell the user where to find the project
in the CMS.
