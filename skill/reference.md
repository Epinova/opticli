# opticli reference

Companion to [SKILL.md](SKILL.md). `opticli <command> --help` is always the authority for the installed version.

## Global options (every command)

| Option | Use |
|---|---|
| `--project <path>` | The site's `.csproj` (or a directory/solution to search) when discovery finds none or several. |
| `--connection-name <name>` | Connection string name in the site's config (default `EPiServerDB`). |
| `--db <id\|name>` | Use another of the project's connection strings for this run (ids from `opticli db list`). |
| `--profile <name>` | Use the connection string of this `Properties/launchSettings.json` profile. |
| `--connection <string>` / `OPTICLI_DB` | Override the connection string for this run. |
| `--json` / `--text` | Force JSON / tables. List commands also take `--jsonl`. |

Which database: `--connection`/`OPTICLI_DB`, `--db` or `--profile` for one run; otherwise the development database:
the one saved with `opticli db use`, else `connection` in the user config (`~/.config/opticli/config.json`,
`%APPDATA%\opticli\config.json` on Windows), else the first of `launchSettings.json` profiles, an exported
`ConnectionStrings__EPiServerDB` (source `environment`), user secrets, `appsettings.Development.json`, `appsettings.json`
(ASP.NET Core's order) if it is local. A remote default, disagreeing launch profiles, or only other environments' `appsettings.{Env}.json` files give
`needs_selection` (exit 6) with `error.details.choices`. The user picks, then `opticli db use <id>`.
`opticli doctor` shows every candidate and why it was or wasn't used.

| Command | Notes |
|---|---|
| `db list` | `choices[]` (`n`, `id`, `server`, `database`, `local`, `from`, `development`), `development`, `mode` (`saved`, `configured`, `automatic`), `saved` (with `stillMatches`), `problem`. |
| `db use <id>` | Saves the development database (the id changes when that setting's server or database changes, and opticli asks again). Without an id on a terminal: pick from a list. Only with the user's choice. |
| `db forget` | Removes the saved choice. |

## Read commands

| Command | Notes |
|---|---|
| `doctor` | Always exits 0. `data.healthy`, `data.warnings`; `connection.candidates`, `database`, `agent`, `skills`. |
| `sites` | Hosts, start page, master language, assets root. |
| `languages [--all]` | Enabled branches (all with `--all`), item counts, which sites use each as master. |
| `types [--kind page\|block\|media\|folder\|other] [--unused] [--sort name\|instances]` | `instances` = non-deleted items. Sorted by name unless `--sort instances`. |
| `type <name\|class\|guid>` | `properties[]` (name, type, blockType, list, cultureSpecific, required, tab, order, displayName, `source` file:line, `declaredIn`, `allowedTypes`/`restrictedTypes` from `[AllowedTypes]`, `uiHint`), `classes[]` (file, line, `baseTypes`), `views[]` (file, `matchedBy`: fileName, partialName, viewComponent, model), `sourceRoot`. `existsOnModel: false` = in the DB but gone from code. Controllers are not listed. |
| `allowed-in <type> [--kind K] [--explicit]` | ContentArea/reference properties of every type that can hold `<type>`, from `[AllowedTypes]` in code: `allowed` (`explicit` + `matchedBy`, or `any` for a ContentArea/reference list without the attribute), `allowedTypes`, `uiHint`, `source`. Base classes and interfaces declared in the sources count. Editor descriptors (`uiHint`) and metadata extenders can change the rules at runtime; they are not evaluated. |
| `get <ref>` | `--lang`, `--version published\|latest\|<id>` (default: published, or the latest draft if the branch was never published), `--fields A,B` (whole values; identity fields like `name`, `saved` are always shown, so `--fields name` gives just those), `--full`, `--all-properties`, `--expand` (inline ContentArea items / referenced content one level). `saved`/`changedBy` belong to the version shown. |
| `resolve <url>` | `--site`. Result has `site`, `host`, `languageSource`, `matchedBy`. |
| `url <ref>` | Per language: `path` (site-relative), `url` (absolute), `site`. |
| `tree <ref> [--depth N] [--limit N]` | Default depth 2, max 5000 nodes (`capped`). `--limit` is children per node; the rest are counted in `more`. |
| `children <ref>` / `ancestors <ref>` | One level down, in the parent's `childSortOrder` / the path from the root. Children show `sortIndex` when the parent sorts by `Index`. |
| `find --type T [--where ...] [--under <ref>] [--status published\|draft\|any] [--lang]` | `--where` is repeatable: `Prop=value` exact, `Prop~value` contains, `Block.Prop=...` inside a local block, `Name~...` on the name, `Area=<ref>` for ContentAreas/references containing that content. Deleted items excluded. |
| `search <text> [--in names\|strings\|all] [--lang]` | One row per item and language with `matches[]` (`property`, `snippet`). Capped at 2000 values per source (`meta.warnings`). |
| `where-used <ref> [--pages]` | Rows: owner identity + `saved`, `changedBy` + `property` (e.g. `MainArea`, `Hero.Link`, `MainArea[2].Text`), `kind` (`contentArea`, `contentReference`, `contentReferenceList`, `richTextLink`, `richTextBlock`, `link`, `linkCollection`, `url`, `text`, `softlink`), `sources` (`softlink` = CMS link index, `property` = value scan). Deleted owners last, `deleted: true`. The value scan reads each branch's primary values (published, else the latest draft); the link index covers saved versions. `--pages` follows block owners up to the pages (rows get `via`: the blocks in between), newest `saved` first. |
| `versions <ref> [--lang]` | Newest first: `ref` (`id_version`), `language`, `status`, `name`, `saved`, `changedBy`, `startPublish`, `primary`. |
| `drafts [--since <date>] [--by <user>] [--kind K] [--type T] [--lang]` | One row per item and language with unpublished changes: `status` and `version` of the newest draft, `saved`, `changedBy`, `drafts` (unpublished versions newer than the published one). `--since` is UTC. |
| `blob <ref>` | Media only: blob URI, file path on disk, `exists`; same for the thumbnail. |
| `access <ref>` | `inherited`, `from` (the item the entries are stored on: itself, or the nearest ancestor with its own), `entries[]` (`name`, `kind`: role, user or visitorGroup, `levels`: `FullAccess` or e.g. `["Read","Edit"]`, `mask`). Read from the database; no `serve` needed. |
| `sql "<SELECT ...>" [--limit N] [--full] [--include-personal-data]` | One SELECT/WITH statement, run in a rolled-back transaction; returns 100 rows unless `--limit` (`truncated: true` when there were more); `--jsonl` prints rows. Forms submissions and user/membership tables need `--include-personal-data`. In `sys`, only the views that describe this database's schema (`sys.objects`, `tables`, `columns`, `indexes`, `index_columns`, `types`, `schemas`, `foreign_keys`, `sql_modules`, ...) are allowed, and `INFORMATION_SCHEMA` views; `sys.dm_*`, `fn_*`, `sys*` compatibility views and server-wide views are refused. Put a space between a number and a following word (`1 AS x`, not `1AS x`). |

### Shape of `get`

```json
{"ref":"123","guid":"...","type":"ArticlePage","name":"News","language":"en","status":"published",
 "url":"/en/news/","kind":"page","version":"123_456","masterLanguage":"en","languages":["en","de"],
 "parent":"45","saved":"2024-05-01T10:00:00Z","changedBy":"editor","startPublish":"...",
 "childSortOrder":"PublishedDescending","sortIndex":100,"simpleAddress":"/news","category":["Press"],
 "shortcut":{"type":"shortcut","to":{"ref":"456","type":"NewsPage","name":"News archive","url":"/en/archive/"}},
 "properties":{
   "Heading":{"type":"String","value":"Hello","culture":"en"},
   "MainArea":{"type":"ContentArea","value":[{"ref":"789","type":"TeaserBlock","name":"Teaser","displayOption":"wide",
     "group":"g1","visitorGroups":["<visitor group id>"]}]},
   "Hero":{"type":"Block","blockType":"HeroBlock","value":{"Heading":{"type":"String","value":"..."}}}}}
```

`simpleAddress`, `shortcut` (absent on a normal page; an external link also has `url`, and `to`/`anchor` when it is
a permanent link to a page) and `category` appear when set. `culture` on a top-level property is the branch its value came from (shared properties come from the master
language). Empty properties are omitted unless `--all-properties`. Rich text gives the (possibly truncated) HTML
plus its resolved links and embedded blocks.

## Write commands (need `opticli serve`)

All take `--dry-run`. `set`, `create`, `area`, `block create`, `translate` save a draft unless `--publish`.
`--lang <code>` picks the branch. Output: `ref`, `version` (the new version ref), `baseVersion`, `status`, `saved`,
`published`, `valid`, `changes[]` (`property`, `before`, `after`), `validation[]`. A saved draft becomes the
primary draft, the version edit mode opens. A write that publishes existing content also has `previouslyPublished`
(the version live until then; absent after a first publish) and, when it put other people's changes live,
`pendingDraft` (see [Other people's drafts](#other-peoples-drafts)).

| Command | Example |
|---|---|
| `set <ref> Prop=value... [--values json] [--name N]` | `opticli set 123 Heading="New title" --dry-run` |
| `create <parent-ref> --type T --name N [Prop=value...]` | `opticli create 45 --type ArticlePage --name "News" Heading=Hi` |
| `area <ref> <Prop> add <block-ref> [--at N] [--display opt]` | `opticli area 123 MainArea add 789 --at 0` |
| `area <ref> <Prop> remove <position\|ref:id>` | `opticli area 123 MainArea remove ref:789` |
| `area <ref> <Prop> move <position\|ref:id> <to>` | `opticli area 123 MainArea move 0 2` |
| `block create --type T --name N (--for <page-ref> \| --parent <folder-ref>)` | `opticli block create --type TeaserBlock --name Teaser --for 123` |
| `upload <file> (--for <ref> \| --parent <folder-ref>) [--name N] [--type T] [Prop=value...]` | `opticli upload report.pdf --parent 456 --name "Annual report" --dry-run` |
| `translate <ref> --lang <code> [--name N] [Prop=value...]` | `opticli translate 123 --lang de --name "Neuigkeiten"` |
| `publish <ref> [--version id] [--include-draft]` | `opticli publish 123_456` (only when the user asked) |
| `move <ref> --to <parent-ref>` | `opticli move 123 --to 45` |
| `delete <ref>` | `opticli delete 123 --dry-run` (recycle bin; only when the user asked) |
| `access <ref> [--grant Role=Levels] [--user Name=Levels] [--revoke Name] [--break-inheritance \| --inherit]` | `opticli access 123 --break-inheritance --revoke Everyone --grant Authenticated=Read --dry-run` (only when the user asked) |
| `apply <plan.json\|->` | `opticli apply plan.json --dry-run` |

`move` and `delete` refuse start pages, site and asset roots, the recycle bin and anything that contains them.
As in the CMS, `delete` leaves the "For this page" folders of the item and its descendants where they are (they go
when the recycle bin is emptied, and are there if the item is restored); a warning lists them.
`--dry-run` on `publish`, `move` and `delete` checks the arguments and the item without asking the site.

Positions in `area` are zero-based; a plain number is a position, `ref:789` (or a GUID) names the item by the
content it shows. `--at` and `--display` only apply to `add`; `--display` is checked like `displayOption` below.
`remove` and `move` leave the items' display options and personalization as they are.

### Property values

- `Prop=value`: a string, parsed by the CMS the way it parses imported values (numbers, booleans, dates, a content
  id for a reference, HTML for rich text).
- `Prop=` clears the property. `Prop=@file.html` reads the value from a file (`@@` for a literal `@`).
- `Block.Prop=value` sets a property of the local block property `Block`.
- `--values '<json object>'` is merged on top, for structured values:
  - ContentArea: `{"MainArea":[{"ref":"456"},{"ref":"789","displayOption":"wide"}]}` (replaces the whole area;
    use `area` to add/remove single items). An item may name its content by `"guid"` instead of `"ref"`.
    `displayOption` is the id of one of the site's display options (its name or tag works too); an unknown one is
    refused with the list. Personalization: `"group":"g1"` and `"visitorGroups":["<visitor group id>"]`, as `get`
    shows them. An item without them keeps those of the item for the same content it replaces, and that item's other
    render settings: the n-th item for some content keeps the n-th current one's, so inserting, removing or reordering
    items doesn't move personalization to another item. `"group":""` and `"visitorGroups":[]` remove it. The
    ContentArea value `get` shows can be sent back as is.
  - Local block: `{"Hero":{"Heading":"Hi","Link":"/en/about/"}}`.
  - Block list (`IList<SomeBlock>`, shown by `get` as `BlockList`): an array of such objects, replacing the whole list:
    `{"Persons":[{"Name":"Kari","Biography":"<p>...</p>","Image":"63__provider"},{"Name":"Per"}]}`.
  - Content provider content (e.g. DAM images, shown by `get` as `63__provider`) works wherever a content ref does:
    `HeroImage=63__provider`, a ContentArea item `{"ref":"63__provider"}`, or its GUID.
  - Link (`LinkItem`): `{"Button":{"href":"456","text":"Read more"}}`, optionally with `title` and `target`. `Button=456` or `Button=/en/about/` changes
    only the href and keeps the text. Link collections take an array of the same objects. A content ref as `href`
    is stored as a permanent link, like the editor stores it.
  - Category property: category names (`CategoryName` in admin mode, or the display name) or ids, as an array or
    comma-separated: `Topics=News,Events` or `{"Topics":["News","Events"]}`. `Topics=` clears it.
- Property names are checked against the content type before anything is sent; a typo fails with a suggestion.

### Built-in settings

Besides the type's properties, `set`, `create`, `translate` and plan steps take these by name (a type's own
property of the same name wins). All are versioned, show in `changes`, and are read back by `get`.

- `StartPublish` and `StopPublish` (also `PageStartPublish`/`PageStopPublish`): the publish dates, e.g.
  `StartPublish=2025-02-14` or `2025-02-14T08:00:00+01:00` (no offset: the site's local time); `StopPublish=` clears
  it. Lists and archives often sort and filter by `StartPublish`, which otherwise is the time of publishing.
- `Category` (also `PageCategory`), on pages, shared blocks and media: the built-in category, with the same values as a
  Category property. Lists and filters often select by it. Only categories marked selectable in admin mode are taken.
- Pages only:
  - `ChildSortOrder` (also `PageChildOrderRule`): how the page's children are sorted, edit mode's Settings > Sort
    order: `CreatedDescending` (the default), `CreatedAscending`, `Alphabetical`, `Index`, `ChangedDescending`,
    `PublishedAscending` or `PublishedDescending`. List pages that show children usually use this order.
  - `SortIndex` (also `PagePeerOrder`): the page's place among its siblings when the parent sorts by `Index`. It and
    `ChildSortOrder` can only be changed on the master language.
  - `SimpleAddress` (also `ExternalURL`/`PageExternalURL`): a short address such as `/campaign`, as `campaign`,
    `/campaign` or `~/campaign`; `SimpleAddress=` clears it. A warning names any page of the same site and language
    that already has it (the CMS saves the clash).
  - `Shortcut`: where the page's link goes instead of the page (Settings > Shortcut), which is how menus link to
    other pages or sites. `Shortcut=456` is a shortcut to page 456 (id or GUID; a plan's `$id` works), a URL
    (`https://...`, `/path/`, `mailto:`) is an external link, `Shortcut=inactive` shows the name without a link, and
    `Shortcut=` makes it a normal page again. As an object:
    `{"Shortcut":{"type":"external","to":"456","anchor":"reports","target":"_blank"}}`, where `type` is `shortcut`,
    `external`, `fetchData` (show another page's content) or `inactive`, `to` the page, `url` an external URL,
    `anchor` a fragment for an external link to a page, and `target` the window (`_blank`, `_top`). The object `get`
    shows can be sent back as is (its `url` of a link to a page is the stored permanent link, which `to` replaces).

### Uploads

- The media type is the one the site registers for the file's extension (`[MediaDescriptor(ExtensionString = ...)]`);
  `--type` picks another, which must accept the extension. Types that accept any file (often a content provider's)
  are only used with `--type`.
- At most 50 MB; only regular files (a symlink is followed to its file). `--dry-run` checks type, parent, name and
  properties without sending the file.
- Media folders: `opticli create <folder-ref> --type SysContentFolder --name Reports`. `--for <ref>` puts the file in
  that page's or block's "For this page" folder.
- Output: as `create`, plus `upload` (`file`, `bytes`, `blob`: where the site stored it, as `opticli blob` shows it).
  A draft medium is only visible to editors; `--publish` (only when asked) makes it public.
- If the site's own code fails after the save (e.g. a search indexer that can't parse the file), the error says the
  item was saved and gives its id.

### Access rights

- Levels: a comma list of `Read`, `Create`, `Edit`, `Delete`, `Publish`, `Administer`, or `FullAccess`.
  `--grant Role=Levels` (roles) and `--user Name=Levels` (users) set that entry to exactly those levels;
  `--revoke Name` removes the entry. Options repeat; revokes apply before grants. An item has one entry per name, so
  a `--user` grant for a name that has a role entry (or the reverse) is refused unless it is revoked in the same change.
- An item that inherits can only be changed with `--break-inheritance`, which first copies the inherited entries onto
  it. `--inherit` drops the item's own entries. Children that inherit follow; descendants are never rewritten.
- Role names are checked against the site's virtual roles (Everyone, Authenticated, ...), its role provider and the
  item's current entries; a typo fails with a suggestion. `--allow-unknown-role` accepts a role that only exists in
  an identity provider so far.
- Refused (exit 3): the root, the recycle bin, the global block folder, start pages, asset roots, and any change after
  which no role that had Administer (or Administrators, WebAdmins, CmsAdmins) keeps it.
- Output: `saved`, `dryRun`, `before` and `after` (both as `access <ref>` prints them). Access rights aren't
  versioned: `before` is the only record, and an `apply` step's `undo` is the inverse `access` command.

### Concurrency

`set` and `area` base the draft on the latest version, read just before saving. If someone saved a newer version
meanwhile the write fails with `conflict` (exit 5); look at it, then run again. `--base-version <id>` pins the base
explicitly; `--force` skips the check.

### Other people's drafts

A publish puts the whole version live, so with `set`/`area --publish` also every unpublished change in the version it
is based on. If a version saved after the published one (in that language) was saved by someone other than `opticli`,
these stop unless confirmed: `set`, `area` and plan steps with `publish`, `publish` without `--version`, and
`create`/`block`/`upload`/`translate` steps that publish existing content (`apply --update-existing`).
- Not on a terminal: `conflict` (exit 5), `error.details.reason: "pendingDraft"`, `error.details.draft`: `version`
  (the newest such version), `savedBy` (empty for saves without a user, e.g. a scheduled job), `saved`, `changes[]`
  (published version to the version that would go live). On a terminal opticli shows the same and asks.
- `--include-draft` confirms (`set`, `area`, `publish`); a plan step needs `"includeDraft": true`, or the plan fails
  validation. `publish --version <id>` (or `publish 123_456`) publishes that version as it is and needs no
  confirmation. Ask the user before confirming.
- `--dry-run` reports `pendingDraft` with a warning and doesn't fail. `publish --dry-run` reads it from the database:
  `changes` there are in `get`'s value shape.
- Versions saved by `opticli` itself never need it, so "save a draft, check it, publish" works as before.
- To go back after a publish: `opticli publish <ref> --version <previouslyPublished>`.

### Plans (`apply`)

```json
{"operations": [
  {"op": "create", "id": "page", "parent": "45", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
  {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
  {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
```

Ops: `set`, `create`, `area`, `block`, `upload`, `translate`, `publish`, `move`, `delete`, `access`, with the same
fields as the commands (`opticli apply --help` lists them). `"$id"` refers to what an earlier `create`, `block` or
`upload` with that `id` made.
- A string value `"@path"` anywhere in `properties` is that file's text, like `Prop=@file`:
  `"MainBody": "@texts/article.html"`. `"@@..."` is a literal `@`.
- `{"op": "upload", "id": "q1", "file": "files/q1.pdf", "parent": "$reports"}`.
- Plan files (`@path` values and upload files) are relative to the plan file (the working directory for `-`) and must
  stay inside its folder, also through symlinks, unless `apply --allow-outside`.
- `{"op": "access", "ref": "$page", "grant": {"Authenticated": "Read"}, "revoke": ["Everyone"], "breakInheritance": true}`
  (also `grantUsers`, `inherit`, `allowUnknownRole`).

Every operation is validated before anything is written; on a failure opticli stops and reports what was saved and
how to undo it. `--publish` on `apply` publishes every operation: only when the user asked.

How far the dry run gets (`meta.warnings` sums it up; step statuses):
- `valid`: the site dry-ran the operation as is.
- `simulated`: the operation is on content the plan creates, so the site dry-ran that content as it will be after the
  operation (every value set on it so far, area edits included; published if the operation publishes), under its
  nearest existing ancestor.
  Its warnings name the stand-ins and the values not checked yet: those that refer to other planned content. A
  required property or a site validator that fails at publish shows up here. Area placements and references to
  planned content are checked against `[AllowedTypes]` in the code.
- `deferred`: only names were checked (`access`, `translate`, `move`, `delete` on planned content); the site validates
  them when the plan runs.

Plans that run again (a section rebuilt after a database refresh, or repaired after edits):
- `"guidNamespace": "<GUID>"` next to `"operations"` gives every `create`, `block` and `upload` a GUID derived from its
  `id` (each such step then needs an `id`); `"guid"` on a step sets one explicitly. The content then has the same GUID,
  and permanent link `~/link/<guid without dashes>.aspx`, in every database. Each step result shows its `guid`.
- Without `--update-existing` a step whose GUID exists fails validation with `conflict`, before anything is written.
- `apply --update-existing`: such a step updates that content (name and properties, as a new draft) instead; content
  in the recycle bin is moved back under the plan's parent first (`restored: true`). A different type, or content that
  was moved elsewhere, is a `conflict`. `area add` of an item that is already there, `translate` to an existing branch
  (it becomes a `set` of its name and properties, or a `publish` without them), `publish` of a published version, a
  `publish: true` that changes nothing on published content, and `delete` of deleted content change nothing.
- Step statuses: `saved`, or `unchanged` when the content was already as the step wants it. `result.existing: true`
  marks updated content. `area remove`/`move` by index are not repeatable; set whole ContentAreas in `properties`
  instead. Content that is no longer in the plan is left alone. An existing upload keeps its file.

## serve and env

- `opticli serve [--build] [--port N] [--https] [--foreground] [--output <dll>] [--timeout s]`: runs the existing build
  output (`bin/Debug/<tfm>/<Site>.dll`) with the site agent injected through `DOTNET_STARTUP_HOOKS`, in Development, on
  `http://127.0.0.1:<port>` (default 5199, else the first free port up to 5299), waiting up to `--timeout`
  (default 180 s) for it to answer. A warning says when sources are newer than the
  build; `--build` runs `dotnet build` first. The site's code and files are not changed.
- `opticli serve --status | --logs [--tail N] | --stop`. `--logs` shows the latest run; `data.previous` lists the
  logs of the two runs before it. A `serve` started while another is starting the same site waits for that one.
- A site that redirects HTTP to HTTPS can't be browsed on the agent's address (`serve` warns). `--https` also binds
  `https://localhost:<port>` with the ASP.NET Core development certificate and prints it as `browseUrl`; opticli keeps
  talking to the agent over HTTP. Without a certificate the site fails to start: `dotnet dev-certs https --trust`.
- The agent refuses to start if the site would use a different database, or a remote one other than the development
  database (exit 3). It answers only loopback callers with the per-run token, and only in Development. `serve`
  refuses a remote database that isn't the development one.
- Against a remote development database the site runs with the scheduler, automatic schema updates and content type
  sync off (a warning says so): content types or properties that exist only in local code aren't in the database, so
  writes to them fail.
- `opticli env [--format shell|powershell|dotenv|json|launchSettings] [--include-connection]` prints the variables
  to start the site yourself (IDE, `dotnet run`). Set them only for the site process (a subshell or launch profile):
  `DOTNET_STARTUP_HOOKS` affects every .NET process started from a shell that exports it. The connection string is
  left out unless `--include-connection`.

## Troubleshooting

| Symptom | Do |
|---|---|
| `not_found`: no CMS project / several candidates | `cd` into the site's repository, or pass `--project path/to/Site.csproj`. |
| No connection string | `opticli doctor`; `--connection-name` if the site's connection string has another name. |
| `needs_selection` (exit 6) | Show the user `error.details.choices`, ask which is their development database, run `opticli db use <id>`. |
| `refused` (exit 3) | A safety rule: serving against a remote database that isn't the development one, SQL the `sql` guard won't run, or a request the site agent won't do (e.g. moving or deleting a start page or a folder that contains one). Don't work around it; tell the user. |
| `unreachable` (exit 4) on a remote database | Firewall (Azure SQL allows only listed client IPs), VPN, or login; for Entra ID auth, `az login`. Tell the user. |
| `unreachable` (exit 4) on a write | The site isn't running: `opticli serve`; if it was, `opticli serve --status` and `--logs --tail 80`. |
| `serve` times out or exits | `opticli serve --logs --tail 80`. Typical: the site needs a build (`--build`), a port is taken (`--port`), or the site's own startup fails. |
| `validation` (exit 5) | `error.details` lists each failing property; drafts may leave required properties empty, publishing may not. |
| `conflict` (exit 5) | A newer version exists: `opticli versions <ref> --limit 3`, then re-run. With `details.reason: "pendingDraft"`: someone else's unpublished changes would go live too; show `details.draft` and ask the user before `--include-draft`. |
| Values look cut off | `truncated: true`: use `get <ref> --fields Prop` or `--full`. |
