---
title: Hand a batch of changes to editors as a project
summary: Put the drafts a plan or your coding agent makes in a CMS project, so an editor reviews them together in the CMS and they go live in one go, when someone decides they should.
audience: developers
order: 6
written-for: 0.33.0
---

A feature often comes with content: a new section, a campaign across the start page and a few product pages, a
restructure. Your coding agent can write all of it with opticli, but someone should look at it before it goes live, and
it should go live together. A CMS project is made for that: it collects drafts of many items, an editor reviews them in
the edit UI, and publishing the project puts the ready ones live at once, now or at a set time. This guide puts a plan's
drafts in a project, hands the project over, and shows how it is published, by the editor or with opticli.

## Before you start

- opticli 0.33.0 or newer, run from inside the site's repository, with the site running (`opticli serve`).
- A site whose edit UI has projects: project mode (the project bar; CMS 12's default) or the Projects gadget (CMS 13's
  default). Where both are turned off, project writes are refused with `projectsDisabled`.
- Someone to review the drafts on a site that uses the same database: your local site (on a restored database, give
  them or yourself a login with `opticli users add`), or the deployed development site when you work against the
  shared development database.

## Steps

### 1. Write the plan with a project

`"project"` next to `"operations"` puts the draft of every saving step in that project; `"ready": true` on a step marks
its draft Ready to publish. A plan for Alloy's spring campaign:

```json
{
  "project": "Spring campaign",
  "operations": [
    {"op": "create", "id": "news", "parent": "/en/about-us/", "type": "ArticlePage",
     "name": "Spring news", "properties": {"TeaserText": "Alloy Plan, Track and Meet get a spring update."}, "ready": true},
    {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Spring teaser", "for": "/en/",
     "properties": {"Heading": "Spring is here", "Text": "See what's new this spring.",
                    "Image": "671c6657-6792-4280-b5a9-b88dffc2f6c7", "Link": "$news"}, "ready": true},
    {"op": "area", "ref": "/en/", "property": "MainContentArea", "action": "add", "item": "$teaser", "ready": true},
    {"op": "set", "ref": "/en/alloy-plan/", "properties": {"TeaserText": "New this spring: plan in half the time."}}
  ]
}
```

A teaser needs an image: the GUID is the Alloy Plan image in Alloy's global assets (`opticli get <ref>` gives an
item's `guid`). The last step has no `ready`: its draft goes into the project but stays behind when the project is
published, until someone marks it ready. A step's own `"project"` puts it in another project, and `"project": null` leaves it out of
the plan's.

Or let your coding agent do it. The bundled skill tells it how, and that it publishes nothing unless asked:

```prompt
Write the spring campaign content as an opticli plan, with every draft in a CMS project called "Spring campaign".
Mark the news page and the teaser ready, but not the Alloy Plan change. Don't publish anything.
```

### 2. Dry-run it

```bash
opticli apply spring.plan.json --dry-run
```

```json
{"dryRun":true,"operations":[...],"projects":[{"name":"Spring campaign","status":"toCreate"}]}
```

No unpublished project is called "Spring campaign", so the run will make it before its first save (`toCreate`). A
project that exists is `existing`, and the steps are then checked against it: content that is already in another
project is a `conflict` (`inOtherProject`), and `ready` under an approval sequence is refused (step 5). For a project
still to be made, the dry run warns that it can only check content in another project when the plan runs.

A plan never publishes a project: a step with `"publish"` or `"publishAt"` and a project is a plan problem.

### 3. Run it

```bash
opticli apply spring.plan.json
```

```json
{"dryRun":false,"operations":[{"index":0,"op":"create","status":"saved","result":{"ref":"212","status":"checkedIn",
"project":{"id":31,"name":"Spring campaign","item":"added","ready":true,"editorUi":"projectMode"}, ...}}, ...],
"projects":[{"id":31,"name":"Spring campaign","status":"created",
"undo":"opticli projects delete 31 (the drafts saved into it stay as they are)"}]}
```

Each step's `project` says what happened to its draft: `added`, or `replaced` when the project held an earlier version
of that content. A step marked ready whose own content a later step changes (a page created with `ready`, then an
`area` step on that page) is marked ready once the plan has run, so the later step saves into the same draft;
`projects[].markedReady` lists those.

### 4. Check the project and hand it over

```bash
opticli projects "Spring campaign"
```

It lists the items: the news page, the teaser and the start page `checkedIn` (ready), the Alloy Plan page `checkedOut`
(a draft). `opticli projects` shows every project with `items` and `ready` counts.

Tell the editor the project's name and id, and where they find it. `editorUi` in the results says which:

- `projectMode`: they choose the project in the project bar at the top of the edit view and see its overview.
- `gadget`: they add the Projects gadget to a pane (Add Gadgets) and pick the project. **The gadget's Publish button
  puts every unpublished item of the project live, ready or not**, unlike project mode and opticli, which publish only
  ready items. Tell them, or leave only ready items in the project.

The editor's own guide is [Collect changes in a project and publish them together](../editors/collect-changes-in-a-project.md).

### 5. Single changes, and marking ready later

A command joins the project with `--in-project` (not `--project`, which names the site's .csproj):

```bash
opticli set /en/alloy-track/ TeaserText="New this spring: track it all." --in-project "Spring campaign" --ready
opticli projects ready "Spring campaign" /en/alloy-plan/
opticli projects ready "Spring campaign" --all --dry-run
```

`set`, `create`, `area`, `composition`, `block create`, `upload` and `translate` take it. A write joins a project only
when it names it; a save without `--in-project` of content that is in the project warns that the new version isn't in
it. Things that are refused:

- **Under an approval sequence**, `--ready` and `projects ready` would skip the reviewers (exit 3, `approvalSequence`).
  `--request-approval` sends the item for review instead (with `--comment` on `projects ready`); once approved it counts
  as ready.
- **Someone else's draft.** `projects ready` of an item whose version a colleague saved is a `conflict` (`pendingDraft`)
  until `--include-draft`: the CMS would record opticli as the one who saved it, and the project's publish could no
  longer tell whose changes go live. Ask them first.

`projects remove "Spring campaign" <ref>` takes an item out (its draft stays), and `projects delete` deletes the project
and leaves its drafts.

### 6. Publish it, when someone asks for it

Usually the editor publishes the project in the CMS. To publish it with opticli, dry-run first:

```bash
opticli projects publish "Spring campaign" --dry-run
opticli projects publish "Spring campaign" --at 2027-03-01T06:00Z
```

Only the items marked ready go live (`published`, or `scheduled` with `--at`); the rest stay drafts (`notReady`). Every
ready item is checked first, as `publish` checks one, and one problem stops it with nothing published. On a terminal it
shows what goes live, whose changes go with it and what stays behind, and asks. Changes of the colleague who marked an
item ready (`readyBy`) need no confirming; anyone else's unpublished changes in a ready item need `--include-draft`.
`projects reactivate` cancels a scheduled publish: the items become drafts again, to mark ready again.

## Running the plan again

Run the same plan again after a change: the project is found by its name (`existing`) and the drafts are saved into it
again. With `"guidNamespace"` and `apply --update-existing`, steps that are already done are `unchanged`. A project only
takes changes while it is active: once it is published, a plan that names it makes a new project of that name; while it
is scheduled, saves into it are refused until it is reactivated.

Sealed production runs refuse `"project"`: projects are a review tool on the site you work on, not something to push to
production.

## If something goes wrong

| Message or symptom | What to do |
|---|---|
| `inOtherProject` | The content is in another unpublished project. Take it out there (`projects remove`), or save it into that project. |
| `projectNotActive`, "is scheduled to be published" | `opticli projects reactivate <project>`, then mark the items ready again after your change. |
| `projectsDisabled` | The site's edit UI has neither project mode nor the Projects gadget (`EPiServer:CmsUI:ProjectUI`). Turn one on, or save without a project. |
| `nothingReady` | No item is marked ready: `projects ready <project> --all`, or mark the ones that should go live. |
| `pendingDraft` on `projects ready` or `projects publish` | A colleague's unpublished changes. Ask them, then `--include-draft`. |
| A typo in the plan's project name made a second project | The dry run's `projects` shows `toCreate`; check it before the run. Delete the stray one with the `undo` command. |

## Related

- [Collect changes in a project and publish them together](../editors/collect-changes-in-a-project.md), for editors
- [Build a test database from scratch](build-a-test-database.md)
- README: [CMS projects](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/cms-projects.md#cms-projects),
  [Writing](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commands.md#writing-needs-opticli-serve)
