# Changelog

Every release is on [nuget.org](https://www.nuget.org/packages/OptiCli). After updating, run `opticli skill install`
again to update the skill.

## Unreleased

### Fixed

- The MCP module hides a property on a tab that requires access the editor lacks on the content (a tab only
  administrators see, say) in inline blocks and block lists' items too, as the edit UI does: `get_content` and diffs
  leave it out, writing it is refused (by position, `areaOps`, in a new block or a nested area's), and a whole area or
  list written back keeps it. It was only applied to content's own properties, so an editor's assistant could read and
  change such a property in an inline block or a list item. A local block's properties stay shown, as in the edit UI.

## 0.16.0 (9 October 2026)

### New

- **Inline blocks in ContentAreas (CMS 12.20+ and CMS 13).** Blocks stored in a ContentArea itself, which opticli could
  only read so far, can now be made and changed:
  - **`opticli area <ref> <Prop> add --type T Prop=value...`** (with `--values`, `--name`, `--at`, `--display`) adds a
    new inline block, made as the CMS makes one (the type's default values) and validated by the CMS on save (required
    properties, the area's `[AllowedTypes]`). In a plan: `area` with `"type"`, `"values"` and `"name"`.
  - **`opticli set <ref> 'MainArea[2].Heading=New'`** (or `--values '{"MainArea[2]": {...}}'`) and **`opticli area <ref>
    <Prop> set <position> Prop=value --name N`** change one inline block's values (and name), the rest of it staying.
  - In a whole ContentArea (`--values`), an item with `type` is an inline block, `{"type": "TeaserBlock", "properties":
    {...}, "name": "Intro"}`. An exact copy of a block the area has (as `get` shows it) keeps that block; any other inline
    item is a new block with just the values it gives. It takes over the block it replaces (its render settings,
    personalization, and what the areas inside it pair with) only when which block that is is unambiguous; otherwise a
    warning names the positions whose settings were dropped. So the area `get` shows can be written back as is
    (unchanged, it changes nothing), also with blocks left out or moved.
  - For an editor (MCP), a whole area can't drop values they can't see or change, in the block, its local blocks, its
    block lists' items or the blocks inside its areas at any depth; the error names the value's path. Removing a block on purpose works by its
    area's path.
  - `area add`, `remove`, `move` and `set` take an area inside an inline or local block by its path
    (`'MainArea[0].Items'`), in plans and MCP `areaOps` too, and take an inline block by its position; `ref:` names shared
    blocks only. Area edits of an area that isn't culture-specific are refused outside the master language, as `set` is.
    In a plan, `MainArea[n]` after an `area add` is the new block, in the dry run too.
  - With `apply --update-existing`, an inline `area add` changes nothing when the area already has an inline block of
    its type with the step's name (whatever its values), or without a name, with the step's values; so a plan that adds
    a named block and changes it later runs again unchanged.
  - On a CMS before 12.20 these writes are refused with the version they need. Where the site's CMS 12 edit UI doesn't
    make inline blocks itself (`UIOptions.InlineBlocksInContentAreaEnabled`, off by default), a write that adds one says
    so in a warning: editors still see, edit, move and remove it.
  - The MCP module takes them too (`update_content`: `properties` and `areaOps` with `type` and `values`), with the edit
    UI's rules for each property and the type's access rights for a new block. `get_content` shows an inline block's
    `name`.

### Fixed

- The MCP module builds the edit UI's metadata for a block that isn't content (a block list's item, an inline block) the
  way the edit UI does when the block's own fails (a URL property's editor descriptor needs content to find), instead of
  refusing every change to it.

## 0.15.0 (7 October 2026)

### New

- **Optimizely CMS 13.** One opticli reads and writes CMS 12 and CMS 13 sites, fresh CMS 13 installs and databases
  upgraded from CMS 12 alike. Reads choose their SQL from the database's schema. The package carries two site agents,
  `agent/cms12/` (.NET 8, built against `EPiServer.CMS.Core` 12.0.3) and `agent/cms13/` (.NET 10, 13.0.0), and `serve`
  and `env` pick the one for the project's CMS major (else the database's); `doctor` shows `cmsMajor` for both. A CMS 13
  build against a CMS 12 database is refused (exit 3), since starting it would upgrade the database for good, and so
  is the reverse. Every existing command works on CMS 13, shared databases and drift included. See the README's
  "CMS 13" section.
  - **Sites are applications:** `sites` lists CMS 13's applications in the same shape, without `guid` and with
    `application` (the name that identifies it, which every site argument takes), `applicationType` and `isDefault`.
    `sites primary` and `sites host add|remove` change applications: `*` makes an application the default (moved from
    the previous one in one save), an application's URL follows its hosts (no SiteUrl, so no `--keep-site-url` or
    `--https unset`), a new host without a scheme gets http on loopback names and the site URL's scheme otherwise, and
    `--type preview|media` adds CMS 13's preview and media hosts (refused on CMS 12).
  - **Job names:** CMS 13 stores a job's class name. `jobs` shows the name admin mode shows (a built-in table for the
    CMS's own jobs, `[ScheduledJob(DisplayName = ...)]` in the site's code for its own), and both names work wherever a
    job is named. Remove Unused Content Variations (new in CMS 13) needs `--allow-destructive`.
  - **Content types of unknown origin:** CMS 13 records no class for a type whose class has a GUID. A type with neither
    a class nor a model-sync version on record, and no class in the build with its GUID (made in admin mode, or a code
    type a content import overwrote), is listed by `types --orphaned` with `originUnknown: true` once its class is gone,
    and `types remove`, `remove-property` and `prune` remove it only with **`--include-unknown-origin`**. In a shared
    database's drift such types are listed with `informational: true` and don't stop writes.
- **Visual Builder (CMS 13).** Kinds `experience`, `section`, `element` and `contract` (`--kind page` includes
  experiences, `--kind block` sections and elements). **`get`** of an experience or section shows its **`composition`**
  (sections → rows → columns → elements, each with key, name, type, display template and settings, and an inline
  block's values or a shared block's identity) instead of the `Layout` and `UnstructuredData` properties it is stored in
  (`--fields Layout` shows those); `get --text` prints it as a tree. `search` and `where-used` name the section and
  element a match is in; `where-used --type` counts inline blocks; `types` and `type` show `compositionBehaviors`,
  `contracts`, `implementedBy`, `blueprints`, `inlineUses` and `displayTemplates`; `allowed-in` lists where a
  composition takes a type. **`opticli display-templates`** lists display templates and their settings.
  - **`opticli composition <ref> add|remove|move|set`** changes one node (a section, row, column or element, named by
    its key or its unique name) on a new version, a draft unless `--publish`, with `--dry-run`: new inline blocks with
    their values, shared blocks by reference, sections from a section blueprint, names, display templates and settings
    (checked against the site's), and the whole node as JSON (`--node`). The CMS's structure rules are checked first, in
    opticli's words.
  - **`composition`** in `set` and `create` values (`composition=@file.json`) writes a whole composition in `get`'s shape:
    structure, order, names and styles as given, nodes with a key keep their block and get only the values given, so
    `get`'s output can be edited and written back. Write results list the changed nodes. Plans have an op `composition`.
  - **Content variations:** `versions` marks a variation's versions (`variation`), **`get --variation <key>`** shows one,
    `drafts` and `find --status draft` count a variation's drafts (`variationDrafts`), and **`--variation <key>`** on `set`
    and `composition` writes one.
  - **Blueprints** are left out of `tree`, `children`, `find`, `search` and `drafts` unless **`--blueprints`**.
    **`create --blueprint <ref|name>`** makes content from one (its type, values and composition), and `composition add
    section --blueprint` adds a section from a section blueprint.
- **LocalDB:** a connection string with `AttachDbFilename=|DataDirectory|\...` (the CMS templates' default) is resolved
  to the project's `App_Data` on Windows, as the site does. On Linux and macOS, where LocalDB doesn't exist, it is
  refused (exit 3) with a hint instead of failing with `internal`.

### Changed

- `--values` (on `set`, `create`, `composition` and the other writes) also takes a value as `get` shows it,
  `{"type": ..., "value": ...}`, references, links and local blocks included; a value `get` cut short is refused.
- Validation issues can carry a `code`; CMS 13's unresolved references have `unresolvedReference`. On CMS 13, `apply`
  refuses up front a plan whose rich text links to content a later step creates (CMS 13 refuses such a save): reorder
  it, or add the link in a later `set`.
- `--connection` strings with `Network Library=...` or `np:` are `refused` with a hint instead of `internal`.

### Fixed

- A PageType property (a page list's type filter) takes the page type's name, as `get` shows it, as well as its id or
  GUID; another kind of content type is a `validation` error instead of an internal cast error.
- Two `serve`s started at the same moment (different sites, or separate state) could pick the same free port, and one
  site then failed to start. A starting `serve` now holds a lock on the port it picked until its site listens.
- `get` showed `startPublish: null` for content whose current version is scheduled; it shows the scheduled time.

### Development

- `tests/fixtures/cms13/setup.sh` builds a fresh and an upgraded CMS 13 Alloy site with `VisualBuilderFixture.cs` (Visual
  Builder types, display templates, experiences, blueprints and content variations). `tests/fixtures/edge-cases/setup.sh`
  also builds the edge-case site from the CMS 13 site, with `Cms13Fixture.cs` in place of `Cms12Fixture.cs`, and
  `drift.sh` runs on CMS 13 copies. `read-battery.sh`, `write-battery.sh` and `vb-write-battery.sh` run whole command
  sets against one site. The integration oracle compares compositions node by node with the CMS's own composition
  mapper.
- `OptiCli.Agent.Tests` runs on both builds (net8.0 for CMS 12, net10.0 for CMS 13). CI and the release workflow build
  and pack both agents, `check-package.sh` checks that each is there and built for its .NET, and the Windows CI job
  starts SQL Server Express LocalDB (installing Microsoft's MSI, hash-checked, only when the image lacks it) and requires
  `LocalDbTests` to pass (`OPTICLI_REQUIRE_LOCALDB`).

## 0.14.0 (6 October 2026)

### New

- **`opticli types remove <type>...`**, **`opticli types remove-property <type> <prop>...`** and **`opticli types prune`**
  remove content types and properties that removed code left in the database, through the site (`serve`) with the CMS's
  own `IContentTypeRepository.Delete` and `IPropertyDefinitionRepository.Delete`. Only orphans, as the running site
  judges them: a type whose class it can't load (never one made in admin mode, nor one of the CMS's own) and a property
  that isn't in its type's code (`existsOnModel: false`) on a type defined in code. A type stays (`conflict`, exit 5)
  while content of it exists, also in the recycle bin, while a property has it as its block type, and while page-type
  values name it (the CMS would clear them); opticli never deletes content. A property's stored values, which the CMS
  deletes with it in every version and language, need `--allow-destructive`. Named items are all checked first, and
  nothing is removed if one can't go. `prune` removes every orphaned type that can go (a block type after the
  properties and types that used it) and lists the rest under `kept` with the reason; `--properties` adds orphaned
  properties, which it leaves alone otherwise, since one added in admin mode looks the same. Every command takes
  `--dry-run`. The output records what was removed (names, GUIDs, classes, properties, value counts) to make it again
  by hand: content types aren't versioned. Without `serve`, `unreachable` (exit 4); refused against a shared database
  (exit 3); `apply` has no such steps and the MCP module no such tools.
  Before each removal the site appends the full record to `removals.jsonl` in opticli's state directory (never rotated,
  `recordFile` in the output) and to its own output, so the record survives a lost answer, a timeout or Ctrl+C; a run
  that stops halfway puts the records of what it removed in `error.details.removed`. A property a content provider uses
  (where opticli can't count or see the values) is a `conflict` with no override, and a type that page-type values name
  lists the versions holding them. Each item is checked again just before it goes, and one removal runs at a time per
  site. On a terminal every field of what was removed is shown in full.

### Changed

- **`type`**'s `values` for a property that isn't in the code count content items and versions holding a value (they
  counted rows), and include values stored inside a block property and category selections: what removing it deletes.

### Fixed

- `restore` of a published block, media file or folder no longer says it is live again "at its URL": only pages have one;
  the rest are published again.

### Development

- The edge-case site's `OrphansFixture.cs` adds `EdgeTrashedPage` (an orphaned page type whose only page is in the
  recycle bin), `EdgeRemovedEmptyPage` and its block type `EdgeRemovedBlock` (orphans nothing uses), the property
  `EdgeRemovedEmptyText` (no values) and `EdgeAdminPage` (made in admin mode), makes them again at every start, and
  answers `POST /opticli-fixture/orphans` on loopback so the removal tests can make them again and run twice.
- `OPTICLI_REMOVALS_FILE` names another file for the removal record. The edge-case site sets it to its own
  `App_Data/opticli-removals.jsonl`, so the integration tests' removals stay out of your `removals.jsonl`.

## 0.13.0 (6 October 2026)

### New

- **`opticli trash`** lists the recycle bin, newest first: what was deleted (what was below it is counted in
  `descendants`), who deleted it and when, and `originalParent`, where `restore` puts it back. That is the parent the
  CMS itself stores for every move, for the edit UI's Restore (not the change log, so truncating that changes nothing):
  read through the site while `serve` runs (`meta.source: agent`), otherwise from that store's rows in the database
  (only those), with a warning that says why the site wasn't asked. `--since`, `--by`, `--type`.
- **`opticli restore <ref>`** brings deleted content back out of the recycle bin through the site, as the edit UI's
  Restore does: below the parent it had, or below `--to <parent>` (needed when the CMS has no record of it). It checks
  first that the item is what was deleted (not something below it: the hint names what to restore), that the parent
  exists and isn't in the recycle bin too, and that its type is allowed there. Content keeps its versions, so what was
  published is live again; a warning says so. Give its id or GUID: content in the recycle bin has no URL. `--dry-run`;
  in a plan `{"op": "restore", "ref": ..., "to": ...}`, checked when the plan runs if an earlier step deletes the same
  item (under whatever ref). The MCP module has no such tool. `delete`'s undo hint is now `opticli restore <ref>`.
- **`opticli users add <name>`** makes a local login for a restored database through the site's ASP.NET Identity (the
  user class the CMS UI uses, found at runtime), as the CMS's first-admin registration does: approved, in `WebAdmins`
  unless `--role` says otherwise (a missing role is created), tagged as made by opticli. The password comes from
  `--password-stdin`, a hidden prompt on a terminal, or is generated and written to a file only you can read
  (`passwordFile`); it is never printed. When the site's answer gets lost, that file is kept (the error says where) and
  only `users remove` deletes it. **`opticli users remove <name>`** removes only users opticli made, and
  **`opticli users roles`** shows the roles with member counts and what the virtual roles map to, never names or
  addresses. Refused against a shared database and on sites without ASP.NET Identity (exit 3); the MCP module has no
  such tools.
- **`opticli types --orphaned`** lists content types whose class is gone from the code, which the CMS keeps while
  content uses them, with their class on record (`modelType`) and how many items use them. With `serve` running the site
  says which classes it can't load (packages' too); otherwise the site's sources are scanned for the types of its own
  assemblies, with a warning that says why the site wasn't asked. **`opticli type`** now gives a property that isn't in the code (`existsOnModel: false`) its
  stored `values` (on content and in versions), and warns about such properties. Reads only: opticli doesn't remove
  types or properties.
- **`opticli history <ref>`** reads the CMS's change log for one item: creates, publishes, scheduled publishes, review
  requests, moves (from, to), deletes to and restores from the recycle bin, permanent deletes and deleted versions, with
  who and when. It is the only record of moves and deletes. `--since`, `--by`.
- **`find --status scheduled|expired`**: content with a version waiting to be published (`publishAt`), and published
  content whose stop-publish date has passed (`expiredAt`); in the master language unless `--lang`, as the rest of
  `find`.
- **`opticli categories`** lists the category tree with the names `set` takes, whether each is selectable, and how many
  items have it; **`opticli visitor-groups`** lists the visitor groups (the ids ContentAreas store, names, how the
  criteria combine), reading only that store's names and settings, never its criteria or notes. `get` already shows
  category names and the visitor groups of ContentArea items, and `set` already takes category names. A `set` with an
  unknown category on a site with many of them now points to `opticli categories` instead of listing them all.

### Development

- The edge-case site gets `UsersFixture.cs`: a user opticli didn't make, which `users remove` must refuse. The
  integration tests add a user, sign in with it on the CMS's login page, and remove it again.
- And `OrphansFixture.cs`: a page type whose class doesn't exist, used by a page, and an EdgePage property its class
  doesn't declare, with a value.

## 0.12.0 (6 October 2026)

### Changed

- **`opticli serve` turns the site's scheduler off.** A restored production database has its jobs enabled with next
  runs in the past, and a site started against it started every overdue one at once: imports and syncs reached
  external systems, emails went out, and Automatic Emptying of Trash deleted the recycle bin for good. Now the site
  agent turns `SchedulerOptions.Enabled` off for every `serve` run (the site's output says so), as it already did
  against a shared database. The site still registers its jobs, and `opticli jobs run` still runs one.
  `serve --scheduler` (or `"scheduler": true` in the user config, which `--scheduler false` overrides) leaves the
  scheduler as the site sets it and warns about the overdue jobs; against a shared database it is a `usage` error.
  `serve --status`, `serve` and `doctor` report `scheduler: on|off` as the running site has it, and `doctor` counts the
  overdue jobs. `opticli env` leaves the scheduler as the site sets it (`OPTICLI_SCHEDULER=on`): that run is yours.

### New

- **`opticli jobs`** lists the site's scheduled jobs from the database: schedule, next run, `overdue`, last run and
  how it ended (`succeeded`, `failed`, `cancelled`, `unableToStart`, `aborted`), whether one runs now (`"stale"` when
  the process running it stopped pinging), its class, and its file and line in the site's code. A job in the code that
  the database doesn't have yet is listed with `registered: false`. `--failed`, `--enabled`, `--disabled`, `--all`.
- **`opticli jobs log [<job>]`** lists runs, latest first, with status, trigger (scheduler, user, restart), server,
  duration and the job's message in full; without a job, every job's runs (`--failed --since 1d`: what failed since
  yesterday). A `<job>` is its id, name, class or a part of its name only it has.
- **`opticli jobs run <job>`** runs a job now through the site, as the admin UI's "Start manually" does, also while the
  scheduler is off, as the user `opticli`. It waits by reading the job tables, shows the job's status messages on a
  terminal, and ends with the run's status, duration and message: exit 0 when it succeeded, the new exit code 7
  (`job_failed`) when it didn't. `--no-wait` returns once it has started; `--timeout <s>` (the new error code
  `timeout`, exit 4) and Ctrl+C stop the waiting, not the job. Jobs that delete for good (the CMS's emptying of the
  recycle bin, removal of unused files, trimming of versions, truncating of the change log, ...; Commerce's removal of
  expired carts, archived items and lowest-price history) and Archive Function, which moves content across the site,
  need `--allow-destructive`. Jobs other than the CMS's own (the site's, add-ons') run with a warning, in
  `meta.warnings` or, when the run fails, `error.details.warnings`: opticli doesn't know what they change or contact.
- **`opticli jobs stop <job>`** stops a job the site runs, as the admin UI's Stop does, and waits up to 30 s for it to
  end; **`opticli jobs set <job>`** changes `--enabled`, the interval (`--every 1h`, `manual`) and the next run
  (`--next now|<time>`) through the CMS's job repository, showing the job before and after; a change that lets the
  scheduler run a job `jobs run` needs `--allow-destructive` for (enabled with a next run, sooner, more often) needs it
  too. `--every 1M` is refused as ambiguous (`m` is minutes, `mo` months). Both take `--dry-run`.
- Against a shared database `jobs run`, `stop` and `set` are refused (exit 3): the deployed site's scheduler uses the
  same jobs. The MCP module has no such tools.
- `doctor` and `serve --status` warn when the running site's agent is older than this release: it doesn't turn the
  scheduler off, so overdue jobs may run there.

### Development

- The edge-case site gets `JobsFixture.cs` (copied by `setup.sh`): "opticli test job", a manual, stoppable job with
  status messages that fails on request, and, with `OPTICLI_FIXTURE_SCHEDULER=on`, a scheduler the site turns on itself.
  The integration tests run, fail, stop and reschedule it.

## 0.11.0 (6 October 2026)

### MCP server for editors (preview): security fixes

Fixes from a security review of `OptiCli.Mcp` 0.9.0-preview (the same module shipped in 0.10.0-preview and
0.10.1-preview). Update before using it on a real site.

- **Return addresses are limited to Claude's and the editor's own machine.** A new option, `AllowedRedirectHosts`
  (`claude.ai` and `claude.com` by default), lists the hosts an app's OAuth redirect URI may be on, over https and
  matched exactly; a loopback address is always allowed. Checked when an app registers and again at every sign-in, also
  for apps with a metadata document and apps registered before. Before, any app could register any https return
  address under any name, and an editor's Allow would send the code there. `["*"]` allows any https host (with a
  warning at startup). The consent page now says plainly, in a warning, that an app which registered itself chose its
  own name.
- **Registration can't fill the database.** `RateLimits.RegisterPerMinute` is 10 by default (was 60), an IPv6 address
  counts by its /64, and registration stops (429) at 5,000 registered apps with no connection and no sign-in under way.
  Such an app is deleted a day after it registered (was 30 days). The cleanup runs in the background, never holding up
  a token response, without the request's context, and deletes at most 500 rows a run. Clients are only queried with a
  filter; grants and codes, which exist only by an editor's consent, are still loaded whole, and `register` counts the
  unused clients at most once a minute.
- **No script in rich text or links for editors.** Rich text with `<script>`, event handler attributes, `javascript:`,
  `vbscript:` or non-image `data:` URLs, `<iframe>`, `<object>`, `<form>` and the like is refused, however it is
  written; links (link properties and collections, URL properties, URLs in list properties' items, a shortcut's
  external link) must be http, https, mailto, tel or relative, have no attributes but href, title and target, and are
  given as objects, not as the CMS's stored markup, without control characters. What a property already has, live, may
  be written back as it is, but nothing new: script inside a `<textarea>`, `<style>`, `<template>`, `<svg>` and the like
  doesn't count as already there. The README recommends the CMS's own `ScriptParser` `SavingMode=Remove` too.
- **No files that run script for editors:** HTML, XML, XSLT, JavaScript and compressed SVG uploads are refused, an SVG
  file with script, `foreignObject`, event handlers or script links is refused (an ordinary drawing uploads), and so is
  markup named as a raster image. The README recommends `X-Content-Type-Options: nosniff` for the site's media.
- **Changes that put content live need the publish gate.** Moving content that has a published version or anything
  below it, discarding a version scheduled for publishing, and saving content without versions are refused
  (`publishingOff`) where the site doesn't allow publishing, also in a dry run. Content that may be live isn't moved
  below another approval sequence (`approvalSequence`).
- **Discarding someone else's draft needs `AllowDelete`** (`deletingOff`), as well as `includeDraft`. An editor's own
  drafts can be discarded as before.
- **What the edit UI checks is checked for editors:** the access rights per language (no draft, publish, unpublish,
  discard or new branch in a language the editor may not edit); properties the edit UI hides or locks for the editor
  (not shown in edit mode, `[Editable(false)]`, a tab whose required access they lack, or an editor descriptor or
  metadata extender, through the CMS UI's own metadata, also inside local blocks), which `get_content`,
  `get_content_type` and diffs leave out and writes refuse (and refused altogether for an item whose metadata can't be
  built), and which a rewritten block list keeps; and the access a group of content types requires on the parent of
  new content.
- **Content references given as text get the read check.** A ContentArea or reference list given as text (or a
  number) is refused, with a hint to give an array of refs; a content reference given as a number, and an embedded
  block in rich text, is looked up as the editor, so content they can't read is the same `not_found` as missing content.
- **Uploads follow the CMS UI's own upload rules:** its size limit (the lower of it and `MaxUploadBytes` applies) and,
  from CMS UI 12.33, its allowed file extensions.
- **The consent and connections pages refuse to be framed** whatever headers the site sends (`Sec-Fetch-Dest`), and
  their posts are refused when the browser says they came from another site (`Sec-Fetch-Site`).
- **The metadata documents are sent with `Cache-Control: no-store`.** The README recommends `RequireHost` or the
  site's `AllowedHosts`, and forwarded headers from known proxies only.
- `find_content` doesn't count content the editor can't read, so `truncated` no longer tells of it.

**Breaking** for a site that implements `IOAuthStore` itself: `DeleteExpiredAsync` takes a limit,
`CountUnusedClientsAsync` is new, and the store is called from a background thread after the request (without its
HttpContext or services, with the site's `ApplicationStopping` as the token), so it must be thread-safe and not scoped.
The developer's site agent (`opticli serve`) behaves as before.

### Fixed

- A value in `get_content`'s `{type, value}` shape, or one JSON can't make into the property's type, is a usage error
  rather than an internal one, for the CLI as well. New content whose save failed inside the CMS (a value the database
  can't take) is no longer reported as saved because it could still be found by its GUID for a moment.

### Development

- The MCP test site has a block type with properties the edit UI hides or locks, a language only administrators may
  edit, and a search fixture; `serve.sh --no-publish` now also turns deleting off (the module's defaults).

## 0.10.1 (5 October 2026)

### Fixed

- `sites primary "Site A=<host>"` with a host that is already the site's primary host for one language, beside its
  primary host for every language, kept the host's language but still moved SiteUrl onto it. Such a pair now works
  as `Site A@<lang>=<host>` would: SiteUrl stays unless it was on the primary host the pair replaces, and a warning
  says how the pair was read. On a site whose hosts are all for one language, a pair without `@lang` still moves
  SiteUrl. As before, every primary pair makes the Edit host undefined (unless `--keep-edit`).

## 0.10.0 (5 October 2026)

### New

- **`opticli sites primary` points a restored database's sites at localhost.** A copy of a production database has
  the production host names, so locally every site but the one with `*` is unreachable and absolute URLs point at
  production. `sites primary "Site A=localhost:5001" "Site B=localhost:5002"` adds each host if the site lacks it and
  makes it the primary host; the previous primary host and the Edit host become undefined, and SiteUrl follows,
  keeping its path (`--keep-edit`, `--keep-site-url` to leave them). `Site A@nb=...` sets a language's primary host,
  `https://localhost:5001/` or `--https true|false|unset` its https setting; a default port goes (`localhost:443` is
  `localhost` with https). The production hosts stay. It saves through the site (needs `serve`), which clears the site
  definition cache: no restart for the site `serve` runs; a warning says to restart any other process running it.
- A pair without `@lang` replaces the site's primary host: on a site whose only primary host is bound to a language
  (all its hosts for one language), the new host gets that language, so one pair is enough, in any order with the
  other pairs; a host that is already primary keeps its language, so running the pairs again changes nothing. A
  `@lang` pair moves SiteUrl when SiteUrl was on the primary host it replaces. `doctor` and `--from-config` read saved entries the same
  way. Once SiteUrl has moved, a SiteUrl that was on a host that isn't primary can't be put back with the commands.
- All pairs are one batch, checked before any site is saved, including the host the CMS adds for SiteUrl
  (`validation`, exit 5, naming the pair): a host another site has, two primary hosts for a site and language, `*`
  twice. Should the CMS still refuse a site while saving, the error says which sites were already saved. A site already
  as asked is `unchanged`, so it is safe to run again; `--dry-run` shows each site's end state. `meta.warnings` names
  languages whose URLs still use a production host.
- `--save` keeps the pairs, with `--keep-edit` and `--keep-site-url`, in the opticli user config for the project
  (`sites.primary`), so after the next restore `opticli sites primary --from-config` is all it takes; it skips a saved
  site the database doesn't have, and `--forget <site>` drops one. `doctor` reports sites whose primary host differs
  from the saved mapping.
- `opticli sites host add <site> <host>` (`--type`, `--lang`, `--https`) and `sites host remove <site> <host>` change
  one host. The site's last host and SiteUrl's host can't be removed.
- Against a shared database `sites primary` and `sites host remove` are refused (exit 3), and `sites host add` only
  adds hosts of type undefined: the deployed site uses the same site definitions. The MCP module has no such tool.

### Fixed

- URLs in every output (`url`, `get`, `resolve`, ...) used a language's primary host for the site's other languages
  when that host was listed before the site's own primary host, as it is once `sites primary` has added a local one.
  The host for every language comes first now, as in the CMS.
- A command with its own text rendering (`drift`, the new `sites primary`) left its warnings out in text mode; they
  are on stderr now, as for tables.

### Development

- The edge-case site has three more sites (`Edge hosts A`, `B`, and `C`, whose hosts are all for one language) for
  the site host integration tests, which put their hosts back after each test. Run
  `tests/fixtures/edge-cases/setup.sh` again to add them.

## 0.9.0 (5 October 2026)

### MCP server for editors (preview)

`OptiCli.Mcp` is published as a prerelease (`0.9.0-preview`): the options and tools may still change, and it supports
CMS 12 only, not CMS 13.


- **New package `OptiCli.Mcp`** for the site itself: editors connect Claude (claude.ai, Claude Desktop, Claude Code) to
  an Optimizely CMS 12 site, sign in with the site's own login, and Claude works as them, with their access rights.
  `services.AddOptiCliMcp()` and `endpoints.MapOptiCliMcp()` (after `MapContent()` and the site's own endpoints); the endpoint is
  `/episerver/opticli/mcp`. Needs CMS 12.12.1 and CMS UI 12.16.1 or newer. See the README's "MCP server for editors".
- A small OAuth 2.1 authorization server in the module: client ID metadata documents and dynamic client registration,
  PKCE, refresh tokens that rotate (a rotated-out one used again revokes the connection, after a grace period for a
  client's own retries, `RefreshTokenReuseGrace`), a consent page where the editor unticks what the app shouldn't do,
  and a gate checked at consent and at every refresh: the editor's current roles (`AllowedRoles`) and, for an
  account the site manages (ASP.NET Identity), that it is still active; a user synchronized from an external login
  goes by their roles. `ConnectionLifetime` (30 days) bounds a connection from when it was allowed, however often it is
  refreshed. Rate limits are options (`RateLimits`); the token
  endpoint counts per client, as claude.ai's editors share its addresses. The site's default authentication scheme
  is left as it is.
- Tools: `whoami`, `get_content`, `list_children`, `resolve_url`, `find_content`, `get_content_type`, `list_versions`,
  `create_content`, `update_content`, `add_language`, `upload_media`, `discard_draft`, `move_content`,
  `publish_content` and `unpublish_content`, which refuse (`publishingOff`) unless the site sets `AllowPublish`, and
  `delete_content`, offered only when it sets `AllowDelete`. Both options are off by default. `requestApproval` only
  ever sends content for review, so it works without publishing. Content an editor can't read is the same
  `not_found` as missing content. Every write result has `editUrl`, the version in the CMS edit UI.
- The module's errors (the MCP endpoint's 401 above all) have a body and switch the site's status code pages off, so
  custom error pages don't replace them.
- `/episerver/opticli/connections` lists an editor's connections with Revoke (administrators see everyone's), and the
  `OptiCli.Mcp.Audit` log category records sign-ins, tokens, revocations and tool calls.

### Development

- The content operations moved from the site agent to `src/OptiCli.Cms`, which the site agent and the MCP module both
  run. Nothing changes for the CLI.
- `tests/fixtures/mcp/setup.sh` builds an MCP test site from the edge-case site, and `tests/OptiCli.Mcp.Integration`
  tests the module end to end with the official MCP SDK's client.

## 0.8.0 (2 October 2026)

### Behaviour changes

- **The pending-draft check looks at content, not version numbers.** A publish asked for confirmation whenever
  someone else had saved a version between the published one and the one going live. It now asks only when the
  version going live still has that draft's value for at least one property the draft changed. A change based on the
  published version (see `--from`) therefore doesn't ask about drafts it leaves out, and nor do later publishes
  built on it. A draft by someone else that changed nothing no longer asks either.

### New

- `set` and `area` take **`--from <version|published>`** (`"from"` on plan steps): the change is based on that
  version instead of the latest, while the concurrency check still compares against the latest. With
  `--from published --publish` a change goes live without someone else's older draft. Newer versions the change leaves
  out are listed in `leftOut` with a warning: they stay as they are, and edit mode opens the new version instead.
  The conflict hint suggests `--from` when a newer version is in the way.

### Fixed

- **Publishing a language branch before its master language.** The CMS publishes a branch other than the master
  only once the master branch has been published, and refuses at save time, so every dry run missed it and a plan
  stopped halfway. `set`, `area`, `translate` and `publish` with `--lang` now refuse before saving (`validation`,
  exit 5, `details.reason: "masterNotPublished"`), also as a dry run. A plan's dry run checks the order of its
  publishes, and the hint names the step to move. A scheduled publish or a review request only warns, since the CMS
  saves those and fails when they come due. When the CMS refuses on its own, the hint now says to publish the master
  first instead of suggesting `--dry-run`.
- A plan's `publish` or `set` of another language of content the plan creates was dry-run as a publish of the master
  branch, so it always passed; it is now `deferred`.

### Skill

- Agents name content next to its ref ("About us" (page 10, draft 10_1473)) and say who saved a version and when,
  instead of giving bare ids.

## 0.7.1 (2 October 2026)

- Fixed: classes declared without a body (`public class AboutBlock : SiteBlockData;`, C# 12) were left out of the
  source scan. A plan's `area add` was refused as not allowed by `[AllowedTypes]` when the area named a base class
  of such a block, although the CMS allowed it; `allowed-in` missed the same areas, and `type` found no C# class.
  A body-less base class broke this for every type derived from it.

## 0.7.0 (2 October 2026)

### Shared databases

- **Writes stop when the build and a shared database differ** (drift). Against a remote development database the
  site doesn't sync its content types, so the database keeps what the deployed code made. A write would run this
  build's code against content the deployed site serves, so it now asks on a terminal, and elsewhere fails with
  `drift` (exit 5) with the differences in `error.details`. `--accept-drift <fingerprint>` confirms (once for a plan:
  `apply --accept-drift`); the fingerprint stops counting when the differences change. Dry runs don't stop.
- `opticli drift` lists what differs: content types and properties in the code but not in the database and the
  reverse, changed property types and culture-specific settings, renames a migration step hasn't applied, EF Core
  migrations, Dynamic Data Store types and the CMS schema version. Each says which side is ahead: `local`,
  `database`, or `unknown` when the database doesn't say; the report as a whole can also be `both`. `serve` prints a
  summary, `doctor` has a `drift` section, and once `serve` has reported drift, commands that use the database and
  `serve --status` carry a warning.
- `serve` refuses (exit 3) a build with EF Core migrations the database's `__EFMigrationsHistory` lacks, since a site
  that migrates at startup would apply them for everyone. `--allow-pending-migrations` starts it anyway.
- `serve` says when the database's CMS schema version doesn't fit the build's packages, before it starts the site,
  instead of leaving a stack trace in the log.
- Dynamic Data Store remapping is off in shared mode too: a store whose type changed fails when used, and drift
  reports it.
- `serve` predicts whether the CMS starts against a schema one version newer from the build's EPiServer.Framework
  version (12.17 and later accept it).
- README: a "Shared databases" section on what opticli can't turn off (the site's own startup code), and why a login
  without DDL rights helps.

## 0.6.0 (1 October 2026)

### Behaviour changes

These can break scripts and plans that relied on the old behaviour:
- **Approval sequences are respected.** A publish of content with an approval sequence (its own or inherited) is
  refused (exit 3, `details.reason: "approvalSequence"`), also as a dry run and as a plan step. It skipped the
  reviewers before. `--request-approval` sends it for review instead. Content in review can't be changed until a
  reviewer decides (`conflict`, `details.reason: "inReview"`).
- **`delete` stops when other content references what it deletes** (`conflict`, `details.reason: "referenced"`,
  `details.references`), and asks on a terminal. `--ignore-references` (a plan's `"ignoreReferences": true`) deletes all
  the same. In a plan, references from content that earlier steps change only warn.
- `get --lang` follows the language settings: a replacement language, or the first fallback language that has a
  branch, before the master language. `languageRule` says when they decided.
- A diff's `before` gives ContentArea items an explicit `"group": ""` and `"visitorGroups": []`, so sending it back
  takes away personalization added since.
- Any publish clears a stop-publish date that has passed (with a warning), unless the change sets one.

### Writes

- `opticli unpublish <ref>` takes a published branch offline, as the edit UI's expiry does. Undo with
  `opticli publish <ref> --version <previouslyPublished>`.
- `opticli discard <ref> [--version <id>]` deletes a version that was never published. It can't be undone; a version
  someone else saved needs `--include-draft`.
- `--publish-at <time>` on `set`, `area`, `create` and `publish` schedules the publish. `drafts` and `versions` show the
  time.
- `--request-approval` on every write that can publish, and `apply --request-approval`.
- `translate --with-blocks` also translates the blocks in the content's "For this page" folder.
  `translate --remove --confirm` deletes a language branch.
- `upload <file> --replace <media-ref>` gives existing media a new file, as a new version of the same type.
- Plans: `href="$id"` (and `data-contentguid`/`data-contentlink`) in rich text and `@file` HTML link to content the
  plan creates, as permanent links. Links to later steps need a `guidNamespace`. New ops `unpublish` and `discard`.
- Visitor group ids in ContentAreas are checked: an unknown id is refused.
- Saving a fetch-data page no longer always counts as a change.

### Reads

- `get` and `access` show the approval sequence that applies (`approval`).
- `get` shows `visitorGroupNames` beside visitor group ids, `personalized[]` for rich text only some visitor groups
  see, and `projects`. A fetch-data page notes where its empty properties come from.
- `where-used --type <T>` lists every instance of a type with its usages. A reference only inside personalized rich
  text says which `visitorGroups` see it.
- `opticli projects [<id>]` lists projects and their items.
- URLs: a site whose start page has language settings only takes its active languages as a prefix.
- `get` notes when published content is offline because its stop-publish date has passed.

### serve, env and configuration

- The site agent refuses requests that came through a proxy or tunnel (`X-Forwarded-For`, `Forwarded`, ...), and stops
  reading a body at the size limit.
- A body the site's web server refuses as too large (IIS 404.13, 413) is reported as such, and an agent that rejects a
  field this CLI sends says to restart `serve`.
- Ctrl+C while the site starts stops it within 5 s, so the `cancelled` envelope is printed.
- `env` warns about another spelling of the connection string exported in the shell.
- `Directory.Build.targets` is read too (it can set `UserSecretsId`). JSON configuration values come out as ASP.NET Core
  reads them (`True`/`False`, `""` for null, empty sections).

### Project

- `tests/fixtures/edge-cases/` builds an edge-case site from an Alloy site for the integration tests: fetch-data pages,
  a nested site, approvals, language settings, personalization, a project and PDF media. The integration tests run
  one at a time.

## 0.5.0 (1 October 2026)

### Output changes

These can break scripts that read opticli's JSON:
- `get`: ContentArea items carry `group` and `visitorGroups` as their own fields, the same names `set` takes. They
  were nested under `personalization`.
- `doctor` and `db list`: a connection string from `OPTICLI_DB` shows as source `optiCliDb`. `environment` now means
  an exported `ConnectionStrings__<Name>`.
- An interrupted command exits 130 with error code `cancelled`. It exited 1 with no output.

### Safety

- `sql`:
  - Closed a way around the read-only check: SQL Server ends a `--` comment at a lone carriage return, which opticli
    didn't see.
  - Look-alike (fullwidth) letters in names are refused.
  - So are server-wide views, functions, logs and traces. In `sys`, only the views that describe the database's own
    schema are allowed.
  - A query that ends its transaction fails instead of returning rows.
- **Publishing someone else's changes needs confirmation.** A publish whose version holds changes someone else saved
  since the published version stops. It shows who saved what, and asks on a terminal; elsewhere it fails with
  `conflict` (exit 5).
  - `--include-draft` (a plan step's `"includeDraft": true`) confirms.
  - `publish --version <id>` publishes that version as it is.
  - Drafts opticli saved itself don't need confirmation.
- Undo hints after a publish name the version that was live before, not the version the change was based on.
- Writing a whole ContentArea keeps each item's group and visitor groups. `group` and `visitorGroups` can be set, and
  unknown display options are refused with a suggestion.
- `UserSecretsId`, the target framework and the assembly name are read through `Directory.Build.props` and the files
  it imports, so user secrets set there are no longer skipped.
- An exported `ConnectionStrings__<Name>` counts as the site's connection string, in ASP.NET Core's order. `serve`
  removes other spellings of it, so they can't win over the pinned database.
- `create`, `block`, `upload` and `move` check where content may go: the CMS's allowed types, and where pages,
  blocks, media and folders may live. `create` refuses media types; use `upload`.

### Plans

- A `publish` after `set`, `area` or `translate` steps on the same content validates. So does a `set` on a language
  branch an earlier `translate` creates.
- `delete` and `move` of content that contains a start page or asset root are refused during validation, not
  halfway through the plan.
- A failing or interrupted `apply` still prints what it saved, with undo hints and `details.partial`.
- A restore from the recycle bin happens last, and is undone if the save fails.
- A save that the site's own code fails after is reported as saved, with `siteError`.

### serve and env

- `serve`:
  - A lock stops two `serve` runs from starting the same site twice.
  - `--stop` asks the site to shut down gracefully on every OS, including Windows, before signalling or killing it.
  - Sites with `Kestrel:Endpoints` in their configuration start: opticli adds its own endpoint. A timeout names the
    addresses the site listened on.
  - `--foreground` sends the site's output to stderr when stdout is redirected, so stdout stays one JSON line.
  - Each run starts a new log, and the last 3 are kept. `--logs --tail` reads from the end.
  - A corrupt state file counts as stale. Ctrl+C while the site starts stops it.
  - Build output is found in runtime-specific folders, `OutputPath`, `BaseOutputPath` and artifacts output. The
    "sources are newer" check follows project references.
  - macOS starts the site in its own session.
- `env` honours `--json` and blanks stale `OPTICLI_*` exports.
- `db use` keeps the config file's permissions.
- `skill install` ignores CRLF line endings when it checks for local edits.

### Reads

- `allowed-in`: media types match `ImageData`/`VideoData` only when the CMS records them as images or videos.
- `search --lang` and `find --where Block.Prop --lang` find shared values, as `get` shows them.
- URLs:
  - Fetch-data pages keep their own URL.
  - The nearest start page decides which site a page belongs to.
  - Simple addresses resolve only on their own site, and also below existing pages.
  - A site whose assets root is the global one gets `/globalassets/` media URLs.
- `PageType` properties were always empty; they show the type name.
- Faster on large sites: instance counts load only for `types` and `type`, `children` reads full details only for
  the page it returns, and wide `where-used --pages` traversals index references once.

### Project

- CI runs the tests on Linux, Windows and macOS. A release checks that the tag is on `main` and that the package
  holds the site agent and starts.
- New tests for the CLI itself and for the site agent's startup code.

## 0.4.1 (30 September 2026)

- Fixed:
  - `serve --status`, `--logs` and `--stop`, which 0.4.0 refused;
  - `"https": true` in the user config, which 0.4.0 ignored.

## 0.4.0 (30 September 2026)

- `opticli upload` creates media from files up to 50 MB, also as a plan step.
- `opticli access` shows and changes an item's access rights:
  - grants for roles and users;
  - revokes;
  - breaking and restoring inheritance, with a lock-out check.
- Plans:
  - `"@file"` values;
  - fixed GUIDs with `"guidNamespace"`;
  - `apply --update-existing`;
  - dry runs that check steps on planned content against stand-ins.
- Page settings in `get` and as values to set: Shortcut, SimpleAddress, Category, ChildSortOrder, SortIndex,
  StartPublish and StopPublish. Block list properties can be written.
- Content provider refs (`63__provider`) work as `get` prints them.
- `serve --https` also binds `https://localhost` for sites that redirect to HTTPS.
- `skill install` replaces a copy it installed, when the files are unedited, without `--force`.
- `delete` lists the "For this page" folders that stay behind, as in the CMS.

## 0.3.0 (30 September 2026)

- First release on nuget.org.
- Reads straight from the database: `doctor`, `get`, `tree`, `find`, `search`, `where-used`, `resolve`, `type`,
  `allowed-in`, `versions`, `drafts`, `sql` and more.
- Writes through the running site with `serve`: `set`, `create`, `block`, `area`, `publish`, `translate`, `move`,
  `delete` and `apply`.
- A Claude Code skill, installed with `opticli skill install`.
