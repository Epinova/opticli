# New blocks in a ContentArea

opticli makes only what an editor could make in the same site's edit UI. There, "Create a new block" in a ContentArea
makes an inline block, stored in the area, where the CMS UI's `UIOptions.InlineBlocksInContentAreaEnabled` is on, and a
shared block in the page's "For this page" folder where it is off. Optimizely's default is off on CMS 12 and on on CMS
13 (from 13.0.2; 13.0.0 defaults to off). Every write that makes a new block in an area follows the site: a
`{"type", "properties"}` item in a whole area (`set`, `create`), `area add --type`, a plan's `area add` with `type`
(`apply` of an export and `sync pull` included), and the MCP module's `create_content`, `update_content` and custom
tools. `serve --status` and `doctor` say which (`inlineBlocks`: `inline` or `shared`). Where the setting can't be read
(no CMS UI loaded, or a CMS UI without it) writes make inline blocks. There is no option to make inline blocks where the
edit UI makes none: such a site may not render them. Where the site makes inline blocks, writes are as before (CMS
12.20+; see [skill/reference.md](../../../skill/reference.md#inline-blocks-in-contentareas)).

```sh
opticli area 123 MainContentArea add --type EditorialBlock MainBody='<p>Hi</p>' --name Intro
```

```json
"sharedBlocks": [{"path": "MainContentArea[1]", "ref": "2691_3623", "name": "Intro", "type": "EditorialBlock",
                  "folder": "2675", "status": "checkedOut", "action": "created"}]
```

Where the site makes shared blocks:

- **The block.** A new block is a shared block of the item's type in the "For this page" folder of the content written,
  also when its area is inside an inline or local block, or inside another new block. The folder is made when the
  content has none (for new content, before the content is saved). The block gets the item's values (the type's
  defaults for the rest), and the item keeps its display option and personalization. Its name is the one given
  (`name`, `--name`), else the CMS's pattern for a block the edit UI makes without one: `<content name> <type name>
  <n>`. It is made in the language written, which becomes its master language.
- **Checked first.** Every block is validated as content of its own before anything is saved, in a dry run too: its
  type in a folder, required values, the site's validators and the area's `[AllowedTypes]`. Problems name the item
  (`MainArea[1].Heading`), and one invalid block saves nothing.
- **Drafts.** The blocks are saved as drafts, into the write's project when it has one, before the content, whose items
  then point at them. The edit UI publishes a new block at once when the editor may publish; opticli publishes only when
  asked (see Publishing below).
- **`sharedBlocks`** in the result lists the blocks of the content's own folder that the write made, changed, found or
  published along. `validation` has an `info` line naming the items that became shared blocks. A dry run's `changes`
  show a planned block as `{"type", "name", "properties", "shared": "planned"}` (with its display option and
  personalization); after the save the item is `{"ref"}`.

  | Field | |
  |---|---|
  | `path` | the area item: `MainArea[2]`, or `MainArea[0].Area[1]` deeper |
  | `ref` | the block's saved version; none in a dry run for a block it would make |
  | `name`, `type` | the block's name and content type |
  | `folder` | the "For this page" folder's id; none in a dry run when the folder would be made |
  | `status` | the version's status (`checkedOut` is a draft, `checkedIn` ready, `awaitingApproval`, `delayedPublish` scheduled, `published`); in a dry run the one it would get |
  | `action` | `created`; `updated`, a new version of a block of the folder that a whole area written again takes over (below); `published`, a draft published along with the content (see Publishing below); `existing`, found there by an add run again (`apply --update-existing`, or `ifMissing` in the MCP module's `areaOps`) |

- **A failed save.** When the content's own save fails after its blocks were saved, the blocks (and a folder made for
  them) stay, and the error's `details.madeBlocks` lists them as `sharedBlocks` does, so a retry can give their refs as
  `{"ref"}` items. For new content they are then in a folder no content owns, so later writes don't treat them as the
  new content's own.
- **Inline blocks an area has stay inline.** Another tool may have put inline blocks on the site. They are edited
  (`MainArea[2].Heading=...`, `area set`), moved and removed by position as before. A whole area written again keeps an
  inline block for the item it pairs with, and only an item that pairs with no current block becomes a new shared block.
  Items pair in this order, each pairing only when it is unique on both sides:
  1. as where the site makes inline blocks: an exact copy of an inline block, then the only changed item of a type with
     the only inline block of that type left;
  2. an exact copy of a block of the content's folder: the item stays `{"ref"}`, nothing is saved, and it isn't listed;
  3. by name: an inline block of the item's type with the item's `name`, or a block of the folder of that type with
     that name and display option;
  4. the only block of its type left, inline or of the folder, for the only item of that type left.

  A block of the folder that an item pairs with gets a new draft version with the item's values (`updated`), built as a
  changed inline block is: only what the item gives, and its name. Shared blocks from anywhere else are never taken
  over; give them as `{"ref"}` items. Writing back the `{"ref"}` items `get` shows and changing a block by its ref
  (`opticli set 2691 MainBody='<p>New</p>'`) stays the plain way.

  **Someone else's draft of such a block.** When the block's latest version (in the language written) is a draft
  someone else saved, the item would save the block's new version over their changes, which it doesn't carry. So the
  write is refused, as a publish over someone else's draft of the content is: a `conflict` (exit 5) with
  `details.reason: "pendingDraft"` and `details.draft` naming the block's draft (`version`, `savedBy`, `saved`, its
  `changes` since the block's published version) and the item (`path`), in a dry run too. On a terminal opticli asks
  first. `--include-draft` (a plan step's `includeDraft`, the MCP tools' `includeDraft`) confirms it: the block gets its
  new version over their draft, and a `warning` says whose draft it replaced. That version is then the write's own, so
  a write that publishes takes the block along as its own change. Without it, give that item back as `{"ref"}` to leave
  the block as it is, or change the block by its ref, which builds on their draft. A draft that is the caller's own needs
  no confirming. `--include-draft` confirms only this and someone else's draft of the content itself: it never takes
  someone else's drafts of other blocks along with a publish (below).
- **Positions name inline blocks only.** `MainArea[2].Heading=...` and `area set 2` on an item that shows a shared
  block are refused, with the block's ref in the hint.
- **Adds run again** (`apply --update-existing`, or `ifMissing` in the MCP module's `areaOps`): the add is there
  already when the area shows a block of its type from the content's own folder, or an inline one, with the add's name
  (with a name) or its values (without). A block of the folder found so is listed as `existing`.
- A new block in a ContentArea inside a CMS 13 Visual Builder element is refused (`usage`): make it with `block create
  --for` and add it by its ref.

**Publishing.** A publish of content through opticli (`publish`, a write with `--publish` or `--publish-at`,
`projects publish`, the MCP module's `publish_content`) also publishes the latest drafts, in its language, of the
blocks of its own "For this page" folder that the published version's areas show, at any depth, **that were never
published in that language** (a block published only in another language counts as never published), and lists them in
`sharedBlocks` (`published`). They can't show until they are published. A block that is live already keeps its newer
draft, with a `warning` that names it (place, ref, name) and how to take it along: the content goes live showing the
block's published version, so an editor's unfinished change to a live block doesn't go live with the page.

```sh
opticli publish 123 --dry-run                     # the site checks it: sharedBlocks goes along, warnings stay behind
opticli publish 123 --with-block 456              # also the newer draft of live block 456
opticli publish 123 --without-block 789           # keep never-published block 789 a draft
```

- **`--with-block <ref>`** (repeatable; a plan's `withBlocks`, `publish_content`'s `blocks`): a live block whose
  newer draft goes along too, or a block whose draft someone else saved. Naming a block is the only way another
  person's draft of it goes live with the content: `--include-draft` doesn't.
- **`--without-block <ref>`** (repeatable; `withoutBlocks`, `skipBlocks`): a never-published block that stays a draft;
  the warning says the content goes live without it showing.
- A named ref must be a block of the content's own folder that its areas show; any other ref, or the same ref in both,
  is `usage`, and nothing is published. On a site that makes inline blocks (nothing is published along there) they are
  `usage` too, and so they are against an agent of opticli 0.34 or older, which would ignore them. So are both with a
  publish that goes for review instead (`--request-approval` where an approval sequence applies; MCP `requestApproval`),
  dry run too: the approval publishes the content alone. `projects publish` takes both, for the blocks of any ready item.
- **Content published already.** When the version to publish is the published one already (the content has nothing
  new), the publish puts live only the block drafts it names (`--with-block`, a never-published block's too) and, in a
  plan, those the run's earlier steps saved (below). A never-published block it doesn't name stays a draft, with a
  warning: it isn't new with this publish, so someone probably kept it back. The content gets no new version;
  `published` is false, `sharedBlocks` lists the blocks, and an `info` line says so. With no such block it is a
  `conflict`, as before, whose hint lists the blocks of the folder it shows that have drafts (never published, or live
  with a newer draft, and who saved the draft when it is someone else's) and the command that names them (`opticli publish 123 --with-block 456 --with-block 789`; for
  the MCP module `publish_content` with `blocks`). A plan run again with `--update-existing` reports such a publish
  `unchanged`.
- **A write that publishes** (`--publish`, `--publish-at`) takes along the blocks it made or changed itself (`created`,
  `updated`) without naming them, and, when it saves a new version of the content, the never-published blocks the
  content shows; another live block's newer draft stays, with a warning to publish it on its own. A write whose only
  change is to blocks of the folder publishes no new version of the content, so as for content published already, only
  its own blocks go along. Writes have no `--with-block`: save a draft, then `publish` with it.

A scheduled publish schedules the blocks for the same time. Blocks from anywhere else are never published along. A
block whose latest draft someone else saved stays a draft unless the publish names it, also when it was never
published, with a `warning` that says who saved it and how to name it (`--with-block`, `withBlocks`, `blocks`):
`--include-draft` confirms someone else's draft of the content, not of its blocks. So an editor who agrees to replace
one colleague's draft of a block (a takeover, above) doesn't put another colleague's draft of another block live with
the same `--include-draft`. A block the write itself takes over is its own change by then and goes along. A sealed
apply to production never takes someone else's block draft along, named or not ([Writing to
production](databases.md#writing-to-production)). Also left as drafts, each with a `warning`, named or not: a block under
an approval sequence, one that fails validation, and for an editor one they may not publish. A block whose latest
version is waiting for review, was rejected, or is scheduled on its own isn't published along either; where it would
have gone (never published, named, or saved by the write or the plan run), a `warning` says so. `unpublish` publishes
no blocks along: taking content offline puts nothing live. A block's draft whose stop-publish date has passed (saved
after the block was taken offline) goes along with the date cleared and a `warning`, as the content's own does. The edit UI's own Publish
publishes the page alone. A block left a draft once the content is published goes live by its own ref (`publish
<block>`), or named in a publish of the content (`publish <content> --with-block <block>`, which then publishes just
the block). Until a changed block is published, the CMS's preview of the content shows the block's published version;
an `info` line says so, with both commands.

Where the site makes shared blocks, `publish --dry-run` asks the site (as the MCP module's `publish_content` with
`dryRun` does): it validates the version and lists the blocks it would publish along (`sharedBlocks`) and leave behind
(`validation` warnings), saving nothing. Elsewhere, and against an older agent, it is answered from the database as
before. The dry run of a write with `--publish` lists what it would publish along too.

**Review requests.** An approval publishes the content alone. So where the site makes shared blocks, sending content
for review (`--request-approval`) is refused (`usage`) while its areas show blocks of its own folder that were never
published (drafts), or blocks the write makes or changes. A live block with a newer draft doesn't stop it: the content
goes live showing that block's published version, as a publish leaves it. Dry runs are refused too. The hint gives two
ways: save into a CMS project (below), or save a draft, publish the blocks, then send the content for review. Blocks in
the folder of content under an approval sequence inherit it, so they go for review on their own (`publish <block>
--request-approval`); a block awaiting approval no longer stops the content's request.

**Projects.** A write with `--in-project` puts its new and changed blocks in the project too, as drafts that then
follow the content: with `--ready` they are marked Ready to publish, and with `--request-approval` the blocks under an
approval sequence go for review with the content (the others are marked ready). `--ready` without
`--request-approval` where a block has an approval sequence is `refused` (`approvalSequence`), in a dry run too. A
write into a project isn't refused for its blocks. `projects publish` publishes along the never-published block drafts of
each page's folder that aren't in the project (and the live ones `--with-block` names); a block draft that is an item of
the project follows the project's rules, and when it isn't marked ready, the page's item in the result warns that it
stays a draft.

**Plans.** An `area add` with `type` may have an `id`: later steps reach the block it makes as `$id` (a `set` with
`"ref": "$intro"`, a link in rich text), and `created` lists it. Run again with `--update-existing`, the add finds the
block (`existing`) and `$intro` names it again. The dry run refuses (`invalid`):
- a later step that names a planned shared block by its position (`"MainArea[1].Heading"`, `area set`, an area path
  through it), with a hint to use an `id`;
- on a site that makes inline blocks, an `id` that a later step uses (an inline block has no ref; an unused one is a
  warning);
- with `--update-existing`, an add whose area already has an inline block like it, so it makes no shared block, when a
  later step uses its `id`.

A run that meets a `$id` naming no block stops there (`usage`).

A `publish` step takes `withBlocks` and `withoutBlocks` (arrays of refs or `$id`s), as `--with-block` and
`--without-block`. Besides those, it takes along the block drafts that **earlier steps of the same run** saved (their
`sharedBlocks`, `created` or `updated`), as one write with `"publish": true` takes along the blocks it changes: a live
block an area step rewrote goes live with the page without naming it. `withoutBlocks` still keeps one back. Blocks an
add only found there (`existing`) weren't changed by the run, so they follow the default rule. A block a later step
took out of the area again isn't published (its draft stays), and neither is one whose newer draft someone saved after
the run's. When those blocks are all that changed (an area step whose only change is to a block of the folder: it
reports the block draft it saved, `saved` with `sharedBlocks`, though the content got no new version), the `publish`
step publishes just them instead of failing with `conflict`; a never-published block the page shows goes along then
only when `withBlocks` names it, as for content published already. Run again with `--update-existing`, such a `publish`
has nothing named or written left, so it is `unchanged`. A `publish` with `requestApproval` counts them as blocks the
review would leave unpublished, as a write that changes them does.

`includeDraft` on the step that writes the area confirms someone else's block draft its items save over (above); the
`publish` step's own `includeDraft` confirms only the content's drafts it puts live. Neither takes someone else's draft
of another block along: only the `publish` step's `withBlocks` does. The dry run, checking such steps together, keeps
the two apart (the block drafts count as confirmed only when every step that writes the content has `includeDraft`).

The dry run checks a `publish` after other steps on the same content together with them, as one write that publishes
(nothing is saved yet to publish), with the step's `withBlocks`, `withoutBlocks`, `requestApproval` and `publishAt`:
it lists the same blocks going along (`sharedBlocks`) and the same warnings as the run, also when they change only
blocks (only those and the named ones go along then). Its note says the content would be scheduled, sent for review
(where an approval sequence applies), or published, as the step does. When those steps change nothing (no value of
the content, no block of its folder), the `publish` meets the content as it is, so it is dry-run on its own: a
`conflict` where the version is published already and no block draft goes along, as the run says.

**Editors' assistants** ([MCP server for editors](../mcp/install.md#mcp-server-for-editors)) follow the same rules, as the editor: a new
block needs Edit and Create on the content (Create on its parent for new content) and a block type they may create in
its folder (on existing content, one the edit UI offers for a new block there), and changing a block of the folder by
writing the area whole needs Edit on that block. The tools' descriptions say what a `{type, properties}` item becomes,
and on a site that makes shared blocks the server's instructions add that `publish_content` of a new version publishes
the never-published ones along while the CMS's own Publish doesn't, that a live block keeps its newer draft unless named
in `blocks`, as does a block whose draft a colleague saved (naming it puts their changes live, `includeDraft` doesn't, so
the assistant asks first), that content with nothing new to publish puts live only the blocks named in `blocks`, and
to show the editor a `dryRun` of the publish first: which blocks go live with the content and which stay behind (where
assistants may not publish: that the editor publishes the blocks too). A whole-area write over a colleague's draft of a
block of the folder is refused with a `pendingDraft` that has a `path`; the instructions tell the assistant to ask the
editor before retrying with `includeDraft` (`update_content`), as for a publish over a colleague's draft. A custom
tool's `ContentPlan` step makes them the same way, and its step result lists them in `sharedBlocks` (it has no
`includeDraft`: such a step is refused, and the hint says how else to go on).

**Trying either mode locally.** Set `EPiServer:CmsUI:UI:InlineBlocksInContentAreaEnabled` (`true` or `false`) in the
site's configuration (not `EPiServer:CmsUI:InlineBlocksInContentAreaEnabled`, which neither CMS 12 nor 13 reads), or for
one run in `serve`'s environment:

```sh
opticli serve --stop
EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled=false opticli serve    # shared blocks; true: inline
opticli serve --status                                                        # inlineBlocks: shared
```
