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
| `doctor` | Always exits 0. `data.healthy`, `data.warnings`; `connection.candidates`, `database`, `agent` (`scheduler`: `on`/`off` in the running site), `scheduler` (`serve`: what `serve` does, `running`, `overdueJobs`), `skills`, `sitesMapping` (each entry of the saved `sites primary` mapping: `key`, `saved`, `primary`, `status`: `matches`, `differs`, `noSite`, `invalid`). |
| `sites` | Hosts (`name`, `type`: undefined, primary, edit, redirectPermanent, redirectTemporary; `language`, `https`), URL (SiteUrl), start page, master language, assets root. |
| `languages [--all]` | Enabled branches (all with `--all`), item counts, which sites use each as master. |
| `types [--kind page\|block\|media\|folder\|other] [--unused] [--orphaned] [--sort name\|instances]` | `instances` = non-deleted items. Sorted by name unless `--sort instances`. `--orphaned`: types defined in code (the CMS has a class on record, `modelType`) whose class is gone; the CMS keeps such a type while content (or a block property) uses it. Through the running site when `serve` runs (every class it can't load, packages' too); otherwise a source scan of the site's own assemblies' types (by GUID, then name), with a warning. Only reads: opticli doesn't delete types or properties. |
| `type <name\|class\|guid>` | `properties[]` (name, type, blockType, list, cultureSpecific, required, tab, order, displayName, `source` file:line, `declaredIn`, `allowedTypes`/`restrictedTypes` from `[AllowedTypes]`, `uiHint`), `classes[]` (file, line, `baseTypes`), `views[]` (file, `matchedBy`: fileName, partialName, viewComponent, model), `sourceRoot`. `existsOnModel: false` = in the DB but not in code (removed from code, which the CMS keeps while it has values, or added in admin mode), with `values` (`content`: stored on content items, `versions`: in all versions) and a warning. Controllers are not listed. |
| `allowed-in <type> [--kind K] [--explicit]` | ContentArea/reference properties of every type that can hold `<type>`, from `[AllowedTypes]` in code: `allowed` (`explicit` + `matchedBy`, or `any` for a ContentArea/reference list without the attribute), `allowedTypes`, `uiHint`, `source`. Base classes and interfaces declared in the sources count. Editor descriptors (`uiHint`) and metadata extenders can change the rules at runtime; they are not evaluated. |
| `get <ref>` | `--lang`, `--version published\|latest\|<id>` (default: published, or the latest draft if the branch was never published), `--fields A,B` (whole values; identity fields like `name`, `saved` are always shown, so `--fields name` gives just those), `--full`, `--all-properties`, `--expand` (inline ContentArea items / referenced content one level). `saved`/`changedBy` belong to the version shown. With `--lang`, language settings apply: a replacement language, or (no branch) the first fallback language that has one; `languageRule` (`replacement`, `fallback`, `none`) and `notes` say so. `projects` lists the projects that hold a version of it. |
| `resolve <url>` | `--site`. Result has `site`, `host`, `languageSource`, `matchedBy`. |
| `url <ref>` | Per language: `path` (site-relative), `url` (absolute), `site`. |
| `tree <ref> [--depth N] [--limit N]` | Default depth 2, max 5000 nodes (`capped`). `--limit` is children per node; the rest are counted in `more`. |
| `children <ref>` / `ancestors <ref>` | One level down, in the parent's `childSortOrder` / the path from the root. Children show `sortIndex` when the parent sorts by `Index`. |
| `find --type T [--where ...] [--under <ref>] [--status published\|draft\|scheduled\|expired\|any] [--lang]` | `--where` is repeatable: `Prop=value` exact, `Prop~value` contains, `Block.Prop=...` inside a local block, `Name~...` on the name, `Area=<ref>` for ContentAreas/references containing that content. Deleted items excluded. `--status scheduled`: a version waits for the "Publish delayed content versions" job (`publishAt`, its first); `expired`: published, but the stop-publish date has passed, so visitors don't see it (`expiredAt`). |
| `search <text> [--in names\|strings\|all] [--lang]` | One row per item and language with `matches[]` (`property`, `snippet`). Capped at 2000 values per source (`meta.warnings`). |
| `where-used --type T` | Every instance of a type (outside the recycle bin): `ref`, `name`, `count`, `usages[]` (as below), the most used first, unused ones last; `meta.warnings` sums it up. |
| `where-used <ref> [--pages]` | Rows: owner identity + `saved`, `changedBy` + `property` (e.g. `MainArea`, `Hero.Link`, `MainArea[2].Text`), `kind` (`contentArea`, `contentReference`, `contentReferenceList`, `richTextLink`, `richTextBlock`, `link`, `linkCollection`, `url`, `text`, `softlink`), `sources` (`softlink` = CMS link index, `property` = value scan). Deleted owners last, `deleted: true`. The value scan reads each branch's primary values (published, else the latest draft); the link index covers saved versions. `--pages` follows block owners up to the pages (rows get `via`: the blocks in between), newest `saved` first. A reference only inside personalized rich text has `visitorGroups` (and `visitorGroupNames`): only those visitors see it. |
| `versions <ref> [--lang]` | Newest first: `ref` (`id_version`), `language`, `status`, `name`, `saved`, `changedBy`, `startPublish`, `primary`. |
| `drafts [--since <date>] [--by <user>] [--kind K] [--type T] [--lang]` | One row per item and language with unpublished changes: `status` and `version` of the newest draft, `saved`, `changedBy`, `drafts` (unpublished versions newer than the published one), `publishAt` for a scheduled one. `--since` is UTC. |
| `history <ref> [--since <date\|7d>] [--by <user>]` | The CMS's change log (`tblActivityLog`) for one item, newest first: `when`, `by`, `action` (`create`, `publish`, `delayedPublish`, `requestApproval`, `rejected`, `checkIn`, `move`, `delete` = to the recycle bin, `restore` = out of it, `deletePermanently`, `deleteLanguage`, `deleteVersion`, ...), `version`, `language`, `name` (as it was), `from`/`to` (`ref`, `name`) for moves, `previousStatus` for a publish. The only record of moves and deletes; drafts saved aren't in it (`versions`), and the Change Log Auto Truncate job removes old entries. Provider content (`63__provider`) works too. |
| `categories` | The category tree, depth first: `id`, `name` (what `set Category=...` takes; `get` shows it), `description` (edit mode's name, which `set` takes too), `parent`, `path`, `depth`, `selectable` (only those can be set), `visible`, `items` (content that has it), `guid`. |
| `visitor-groups` | `id` (what `visitorGroups` in `get` and `set` hold), `name`, `match` (`all`, `any`, `points` with `pointsThreshold`), `securityRole` (usable in access rights), `statistics`. Criteria and notes aren't read. |
| `projects [<id>]` | Projects (versions of several items published together): `id`, `name`, `status`, `created`, `createdBy`, `publishAt`, `items`. With an id, its items: `ref`, `version`, `type`, `name`, `language`, `status`. Read-only. |
| `trash [--since <date\|7d>] [--by <user>] [--type T]` | What is directly in the recycle bin (what was deleted), newest first: identity, `deletedBy`, `deleted` (UTC), `descendants` (below it, coming back with it), `originalParent` (`ref`, `name`, `type`, `path`, `url`; `deleted: true` when it is in the recycle bin too, `missing: true` when it is gone): the parent the CMS stored when it was deleted, where `restore` puts it. Null when the CMS has no record of it (`restore --to`). Read from the database, or from the site while `serve` runs (as `restore` reads it). |
| `blob <ref>` | Media only: blob URI, file path on disk, `exists`; same for the thumbnail. |
| `jobs [--all] [--enabled\|--disabled] [--failed]` | Scheduled jobs: `id`, `name`, `enabled`, `schedule` (`every 1 hour`, `manual`), `nextRun`, `overdue` (enabled, next run passed), `lastRun`, `lastStatus` (`succeeded`, `failed`, `cancelled`, `unableToStart`, `aborted`), `lastMessage` (one line), `running` (`true`, `false`, `"stale"`: its process stopped pinging), `stoppable`, `class`, `source` (file:line in the site's code; null for a job from a package), `registered: false` for a job in the code the database lacks (the site registers jobs when it starts). `--all` adds jobs the CMS hides. See [Scheduled jobs](#scheduled-jobs). |
| `jobs log [<job>] [--failed] [--since <date\|7d>] [--limit N]` | Runs, latest first (20 by default): `job`, `jobId`, `started`, `finished`, `duration`, `durationMs`, `status`, `trigger` (`scheduler`, `user`, `restart`), `server`, `message` (whole; it can be HTML). Without `<job>`: every job's runs. |
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
     "group":"g1","visitorGroups":["<visitor group id>"],"visitorGroupNames":["Returning visitors"]}]},
   "Hero":{"type":"Block","blockType":"HeroBlock","value":{"Heading":{"type":"String","value":"..."}}}}}
```

`simpleAddress`, `shortcut` (absent on a normal page; an external link also has `url`, and `to`/`anchor` when it is
a permanent link to a page), `category` and `approval` (see [Approval sequences](#approval-sequences)) appear when
set. A fetch-data page (`shortcut.type: "fetchData"`) shows its own values; `notes` says the site fills the empty ones
from the page in `shortcut.to`. `culture` on a top-level property is the branch its value came from (shared properties come from the master
language). Empty properties are omitted unless `--all-properties`. Rich text gives the (possibly truncated) HTML
plus its resolved links and embedded blocks, and `personalized[]` for the sections only some visitor groups see
(`visitorGroups`, `visitorGroupNames`, `group`, their `value` and `links`). `visitorGroupNames` sit beside the ids, which
are what `set` takes back; a visitor group id that doesn't exist is refused on write.

## Write commands (need `opticli serve`)

All take `--dry-run`. `set`, `create`, `area`, `block create`, `translate` save a draft unless `--publish`.
`--lang <code>` picks the branch. Output: `ref`, `version` (the new version ref), `baseVersion`, `status`, `saved`,
`published`, `valid`, `changes[]` (`property`, `before`, `after`), `validation[]`. A saved draft becomes the
primary draft, the version edit mode opens. A write that publishes existing content also has `previouslyPublished`
(the version live until then; absent after a first publish) and, when it put other people's changes live,
`pendingDraft` (see [Other people's drafts](#other-peoples-drafts)). Content with an approval sequence isn't published
directly: see [Approval sequences](#approval-sequences).

| Command | Example |
|---|---|
| `set <ref> Prop=value... [--values json] [--name N] [--from published\|<version>]` | `opticli set 123 Heading="New title" --dry-run` |
| `create <parent-ref> --type T --name N [Prop=value...]` | `opticli create 45 --type ArticlePage --name "News" Heading=Hi` |
| `area <ref> <Prop> add <block-ref> [--at N] [--display opt]` | `opticli area 123 MainArea add 789 --at 0` |
| `area <ref> <Prop> remove <position\|ref:id>` | `opticli area 123 MainArea remove ref:789` |
| `area <ref> <Prop> move <position\|ref:id> <to>` | `opticli area 123 MainArea move 0 2` |
| `block create --type T --name N (--for <page-ref> \| --parent <folder-ref>)` | `opticli block create --type TeaserBlock --name Teaser --for 123` |
| `upload <file> (--for <ref> \| --parent <folder-ref>) [--name N] [--type T] [Prop=value...]` | `opticli upload report.pdf --parent 456 --name "Annual report" --dry-run` |
| `upload <file> --replace <media-ref> [Prop=value...]` | `opticli upload report-v2.pdf --replace 789 --dry-run` |
| `translate <ref> --lang <code> [--name N] [--with-blocks] [Prop=value...]` | `opticli translate 123 --lang de --name "Neuigkeiten" --with-blocks` |
| `translate <ref> --lang <code> --remove [--confirm]` | `opticli translate 123 --lang de --remove --dry-run` (only when the user asked) |
| `publish <ref> [--version id] [--include-draft] [--request-approval] [--publish-at time]` | `opticli publish 123_456` (only when the user asked) |
| `unpublish <ref> [--lang]` | `opticli unpublish 123 --dry-run` (only when the user asked) |
| `discard <ref> [--version id] [--lang] [--include-draft]` | `opticli discard 123_457 --dry-run` (only when the user asked) |
| `move <ref> --to <parent-ref>` | `opticli move 123 --to 45` |
| `delete <ref> [--ignore-references]` | `opticli delete 123 --dry-run` (recycle bin; only when the user asked) |
| `restore <ref> [--to <parent-ref>]` | `opticli restore 123 --dry-run` (out of the recycle bin; only when the user asked) |
| `access <ref> [--grant Role=Levels] [--user Name=Levels] [--revoke Name] [--break-inheritance \| --inherit]` | `opticli access 123 --break-inheritance --revoke Everyone --grant Authenticated=Read --dry-run` (only when the user asked) |
| `sites primary <site>[@<lang>]=<host>... [--https true\|false\|unset] [--keep-edit] [--keep-site-url] [--save]` | `opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" --dry-run` (see [Site hosts](#site-hosts)) |
| `sites primary --from-config` / `sites primary --forget <site>` | `opticli sites primary --from-config --dry-run` |
| `sites host add <site> <host> [--type T] [--lang] [--https]` / `sites host remove <site> <host>` | `opticli sites host add "Site A" localhost:5001 --dry-run` |
| `jobs run <job> [--no-wait] [--timeout s] [--allow-destructive]` | `opticli jobs run "Publish Delayed Content Versions"` (only when the user asked; see [Scheduled jobs](#scheduled-jobs)) |
| `jobs stop <job>` | `opticli jobs stop "Content import"` |
| `jobs set <job> [--enabled true\|false] [--every 30m\|1h\|1d\|1w\|1mo\|1y\|manual] [--next now\|<time>] [--allow-destructive]` | `opticli jobs set "Content import" --every 1h --next now --dry-run` (only when the user asked) |
| `users add <name> [--role R]... [--password-stdin]` | `opticli users add dev --dry-run` (only when the user asked; see [Local users](#local-users)) |
| `users remove <name>` | `opticli users remove dev` |
| `apply <plan.json\|->` | `opticli apply plan.json --dry-run` |

`unpublish` takes a published branch offline as the edit UI's expiry does: a copy of the published version with
`StopPublish` set to now is published (`unpublished: true`), drafts stay as they are, and `get` notes that it is offline.
`previouslyPublished` is the version that was live; `opticli publish <ref> --version <it>` puts it back. Any publish
clears a stop-publish date that has passed (with a warning), unless the change sets one. Start pages, site and asset
roots are refused, and so is content with an approval sequence (save a draft with `StopPublish=<now>` and send it with
`--request-approval`).
`discard` deletes one version that was never published (default: the newest in the language) and can't be undone; its
`changes` show what the version holds compared with the published version (or the one before). The published version,
versions published before, the only version and a version in review are refused (exit 3 or 5). A version someone else
saved needs confirming as for a publish ([Other people's drafts](#other-peoples-drafts)): `--include-draft`.
`--publish-at <time>` (`set`, `area`, `create`, `publish`; in a plan `"publishAt"`) schedules the publish instead: ISO
8601, UTC unless it has an offset (`2025-03-01T08:00Z`), in the future. The version is saved as `delayedPublish` with
`scheduledFor`, and the CMS's scheduled job ("Publish delayed content versions") publishes it then; `drafts` shows it
with `publishAt`, `versions` with `delayPublishUntil`. The rules for a publish apply (other people's drafts, approval
sequences). Not with `--publish` or `--request-approval`. To cancel: `opticli discard <ref> --version <id>`.
`translate --with-blocks` also gives every block in the content's "For this page" folder the new branch (a copy of the
block's master language, published with `--publish`), so the new branch doesn't show blocks in another language;
`blocks[]` says per block `translated`, `exists` or `notLocalizable`. Blocks elsewhere (shared folders) are left alone.
`translate --remove` deletes a branch with all its versions; it can't be undone. Not the master language, nor a site's
start page. The dry run shows `versions` and whether it was `published`; the real run needs `--confirm` (a prompt on a
terminal; elsewhere `conflict`, `details.reason: "removesBranch"`).
The CMS publishes a branch other than the master language only once the master branch has been published (one that
was published and is offline now counts). Before that, a publish of another branch (`publish --lang`, a version of it,
`set`/`area`/`translate --publish`, also each block of `translate --with-blocks --publish`) is refused before anything
is saved, dry run included: `validation` (exit 5), `details.reason: "masterNotPublished"`. Publish the master branch
first (`opticli publish <ref>`, only when the user wants it live), or save the branch as a draft. A scheduled publish
(`--publish-at`) or a review request under an approval sequence only warns: the CMS saves it, and fails when it comes
due or is approved.
`move` and `delete` refuse start pages, site and asset roots, the recycle bin and anything that contains them.
`delete` (and its dry run) lists references from other content to the item or its descendants, which would point into
the recycle bin: `references[]` (`from`, `name`, `type`, `language`, `to`, `property`, `kind`; the first 50) and
`referenceCount`. A real delete with references stops: on a terminal it asks; elsewhere `conflict` (exit 5),
`details.reason: "referenced"`, `details.references`. `--ignore-references` (in a plan `"ignoreReferences": true`)
deletes all the same. In a plan, references from content that earlier steps change (an `area remove`, say) only warn;
the delete checks again when it runs. A `move` keeps references working (they follow the content by id).
`restore` brings deleted content back as the edit UI's Restore does: below the parent the CMS stored when it was deleted
(`trash`'s `originalParent`; it keeps one for every move, not the change log, so truncating that changes nothing), or
below `--to`. Output: `ref`, `parent`, `previousParent` (the recycle bin), `from` (`originalParent` or `to`),
`originalParent` (what the CMS stored, also when `--to` overrode it), `restored`, `descendants` (they come back with it).
It keeps its versions: content that was published is live again at once (a warning says so). Errors: `conflict` when it
isn't in the recycle bin, or the parent is in the recycle bin too or gone (the hint names what to restore first);
`usage` for content below deleted content (the hint names what was deleted), and when the CMS has no parent for it and
no `--to` was given; `validation` when its type isn't allowed below the parent, as for `move`. `--dry-run` asks the
site, which runs every check. Undo: `opticli delete <ref>`. In a plan: `{"op": "restore", "ref": "123", "to": "45"}`
(`to` optional); a restore of what an earlier step deletes is checked when the plan runs (`deferred`), and with
`--update-existing` content that isn't in the recycle bin is left as it is.
`create`, `block create`, `upload` and `move` put content only where it can go, or fail with `validation` (exit 5):
pages below pages (not in asset folders), blocks, media and folders in asset folders (not below pages: a page's own
go in its "For this page" folder, `--for <page>`), nothing below blocks or media, and only types the parent's type
allows (`[AvailableContentTypes]`, admin mode's settings, as the CMS's availability service answers). `create` refuses
media types: `upload` makes media.
As in the CMS, `delete` leaves the "For this page" folders of the item and its descendants where they are (they go
when the recycle bin is emptied, and are there if the item is restored); a warning lists them.
`--dry-run` on `publish` and `delete` checks the arguments and the item without asking the site; `move --dry-run`
asks the site, which checks the type below the new parent.

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
- `--replace <media-ref>` gives existing media a new file: a new version (a draft unless `--publish`) of the same media
  type, which must accept the file's extension; the published version keeps the old file until the new one is
  published. In a plan: `{"op": "upload", "file": "q1-v2.pdf", "replace": "$q1"}`.
- Media folders: `opticli create <folder-ref> --type SysContentFolder --name Reports`. `--for <ref>` puts the file in
  that page's or block's "For this page" folder.
- Output: as `create`, plus `upload` (`file`, `bytes`, `blob`: where the site stored it, as `opticli blob` shows it).
  A draft medium is only visible to editors; `--publish` (only when asked) makes it public.
- If the site's own code fails after the save (e.g. a search indexer that can't parse the file), the save stands: the
  write succeeds with a warning that names the error (`siteError` in the output), so a plan keeps the new `ref`. The
  same goes for every write.

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

### Site hosts

For a restored copy of a production database, whose sites still have the production host names. Through the site
(`serve`); `sites` itself is a database read.
- `sites primary "Site A=localhost:5001"`: the site is a name (case-insensitive), id or GUID as `sites` lists them;
  the pair splits on its last `=`. The host is `name[:port]`, or a URL with nothing after the host
  (`https://localhost:5001/`, whose scheme sets the host's `https`; `--https` overrides it, and `unset` uses SiteUrl's
  scheme). Default: an existing host keeps its setting. The default port of the host's scheme (the URL's, or
  `--https`) is dropped: `localhost:443` is `localhost` with https, `localhost:80` with http; `http://localhost:443`
  keeps its port. A host the site has as `name:443` (from admin mode) is found by that name, e.g. to remove it. No
  IPv6 addresses (the CMS can't store them).
- Per pair: the host is added if the site lacks it, made primary, the previous primary for that language and the
  site's Edit host become `undefined` (`--keep-edit` keeps the Edit host), and SiteUrl becomes `https://<host>/`
  with SiteUrl's path kept (`http://` with `--https false`; `--keep-site-url` keeps it). `Site A@nb=...` makes the
  host primary for that language only (an enabled language), and moves SiteUrl only when it was on the primary host
  it replaces.
- A pair without `@lang` replaces the site's primary host: the one for every language, or on a site whose only primary
  host is bound to a language (all its hosts for `nb`, say) that one, and the new host gets that language (a `changes`
  line says so). With primary hosts for several languages only (or one whose language isn't enabled), it is for
  every language beside them, and `meta.warnings` gives the pairs that work for the rest. A host that is a primary host
  already keeps its language, and the language comes from the site as it was before the command: the pairs' order
  doesn't matter, and running them again changes nothing. `Site A=h` and `Site A@nb=h` together for such an `nb` site:
  `validation` (the first already covers `nb`). `doctor` and `--from-config` read a saved entry the same way.
- SiteUrl follows a pair without `@lang`, except one whose host is already a language's primary host beside a primary
  host for every language: that is the language's pair, so SiteUrl stays (a warning says so) unless it was on the
  primary host the pair replaces. Every primary pair makes the Edit host `undefined` (`--keep-edit` keeps it): the
  CMS's Edit host is for every language.
- The commands only put SiteUrl on a primary host: once moved, a SiteUrl that was on a host that isn't primary (as on a
  site the CMS created on its first request) can't be put back with them alone.
- All pairs are one batch, checked before any site is saved, including the host the CMS adds for SiteUrl. Errors:
  `validation` (exit 5, `details.reason: "siteHosts"`, `details.validation[]` naming each failing pair) for a host
  another site has in any spelling (a host is never moved), two primary hosts for a site and language, `*` on two
  sites or as primary, and `host add --lang` with a language that isn't enabled; `usage` (exit 1) for a host with a
  path, query, other scheme or bad port; `not_found` (exit 2) for an unknown site, also `Site A@xx` when `xx` isn't an
  enabled language (`@xx` is then part of the name; the hint says so). Should the CMS still refuse a site while
  saving, the sites saved before it stay saved: the message ends `Already saved: ...; not saved: ...`, and running
  the same command again finishes the rest (the saved sites are `unchanged`).
- Output, per site: `site`, `id`, `guid`, `status` (`changed`, or `unchanged` when it already was so: nothing saved),
  `changes[]` (added, `a: primary → undefined`, `SiteUrl: old → new`, removed), `url`, `hosts[]` as `sites` prints
  them; `dryRun: true` for a dry run. `meta.warnings`: a language whose URLs still use a production host ("Site A's nb
  URLs still use site-a.no; add `Site A@nb=localhost:<port>`"), and after a save that another process running the
  site keeps the old hosts until it restarts.
- `--save` (after a real run) stores the pairs, with `--keep-edit` and `--keep-site-url`, in the user config's
  `sites.primary` for the project, replacing the entries for those sites (keys are `Site A` and `Site A@nb`: saving
  for a site `Shop` also replaces a site `Shop@en`'s entry); `--from-config` runs them, skipping (with a
  warning) a saved site the database doesn't have; `keepEdit`/`keepSiteUrl` saved on one of a site's entries apply to
  all of that site's entries. `--forget <site>` drops a site's entries, or one entry
  (`"Site A@nb"`). `doctor` warns when a site's primary host differs from the saved mapping.
- `sites host add <site> <host>`: `--type undefined|primary|edit|redirect-permanent|redirect-temporary` (default
  undefined; primary and edit demote the previous one), `--lang`, `--https`. A host the site has: `conflict`.
  `sites host remove <site> <host>`: not the site's last host (exit 3), nor SiteUrl's host, which the CMS adds back
  (`validation`; make another host primary first). Removing `*` warns.
- Against a shared database `sites primary` and `sites host remove` are refused (exit 3), and `host add` takes only
  `--type undefined`, which makes a site reachable on a local host without changing its URLs for anyone else.

### Scheduled jobs

`jobs` and `jobs log` read the database; `jobs run|stop|set` go through the site (`serve`), and are refused against a
shared database (exit 3). A `<job>` is its id, its name, its class (`BlobCleanupJob` or the full name), or a part of
its name only it has (several matches: `usage`, listing them).
- `serve` turns the site's scheduler off (a restored database's overdue jobs would all start at once): no job runs on
  its schedule. `serve --status` and `doctor` say `scheduler: off`; `serve --scheduler` (only when the user asks for
  it) leaves it as the site sets it, and warns about overdue jobs. `opticli env` doesn't change it.
- `jobs run <job>` starts it as the admin UI's "Start manually" does (also with the scheduler off), as the user
  `opticli`, and waits by reading the job tables (status messages go to stderr on a terminal). Output: `job`, `id`,
  `started`, `since`, `status`, `duration`, `durationMs`, `finished`, `message`. Exit 0 when it succeeded; 7
  (`job_failed`, `error.details` the same fields, plus `warnings`) when it failed, couldn't start, or was stopped or
  aborted.
  `--no-wait` returns after the start; `--timeout <s>` ends the waiting with `timeout` (exit 4) and Ctrl+C with
  `cancelled`: the job goes on running either way (`jobs stop`, `jobs log`). Already running: `conflict` (exit 5). Jobs
  other than the CMS's own (the site's, add-ons' such as Commerce or Search & Navigation) run without asking, with
  `meta.warnings` "runs code opticli doesn't know: it may change content or contact external systems" (imports, syncs,
  emails), so run one only when the user asked for it. A run
  of an overdue job moves its next run on, as the admin UI does.
- Jobs that delete for good are refused (exit 3) unless `--allow-destructive`: Automatic Emptying of Trash (the
  recycle bin), Remove Abandoned BLOBs, Trim Content Versions, Remove Unrelated Content Assets, Change Log Auto
  Truncate (the activity log), Notification Message Truncate, Monitored Tasks Auto Truncate, and Commerce's Remove
  Expired Carts, Permanently Delete Archived and Remove Expired Lowest Price; so is Archive Function, which moves every
  expired page to its archive page. Pass it only after the user confirmed that job by name: on a restored database
  these are often the only copy. `jobs set` needs it too for a change that lets the scheduler run one of them (enabled
  with a next run, an earlier next run, a shorter interval); disabling one or making it manual doesn't.
- `jobs stop <job>`: as the admin UI's Stop; the job's code decides when it stops. Waits up to 30 s: `stopped`,
  `status` (`cancelled`), `message`; `stopped: false` with a warning when it hasn't ended yet. Not stoppable: `refused`;
  not running (in the site `serve` runs): `conflict`.
- `jobs set <job>`: `--enabled true|false`, `--every` (`30m`, `1h`, `6h`, `1d`, `1w`, `1mo`, `1y`, or `manual`, which
  clears the next run; under a minute is refused, and so is `M`: `m` is minutes, `mo` months), `--next now|<time>` (UTC unless it has an offset; a time that has
  passed makes it overdue). `--every` on a job without a next run needs `--next`. Output: `before`, `after` (`enabled`,
  `schedule`, `every`, `nextRun`, ...), `changes[]`, `saved`; nothing changed: `saved: false`. Schedules aren't
  versioned: report `before`.

### Local users

A login for a restored database, through the site's ASP.NET Identity (`serve`). `users roles` (a read, through the site):
`roles[]` (`name`, `members`: a count, `virtualRoles` it gives), `virtualRoles[]` (`name`, `kind`: `mapped` or the
provider's class, `roles` and `matchAll` for a mapped one), `userType`, `optiCliUsers` (how many opticli made). Never
names or addresses.
- `users add <name>`: approved, address `<name>@opticli.localhost`, claim `opticli:created`, in `WebAdmins` unless
  `--role` (repeatable); a missing role is created (`createdRoles`, with a warning). A warning names the roles
  `CmsAdmins` maps to when the user gets none of them. Password: `--password-stdin` (one line), a hidden prompt on a
  terminal, else generated and written to a file only the user can read: `password` (`stdin`, `prompt`, `generated`)
  and `passwordFile` (the path) in the output, never the password. Errors: `conflict` (name exists), `validation`
  (`details.reason: "users"`, the site's password or name rules, one issue each; nothing is made), `refused` (shared
  database; a site whose CMS users don't come from ASP.NET Identity, naming its user provider). `--dry-run` checks it
  all and writes no file.
- `users remove <name>`: only a user `users add` made (`refused` otherwise, `not_found` for none); deletes its password
  file too (`passwordFileRemoved`). Roles stay.

### Concurrency

`set` and `area` base the draft on the latest version, read just before saving. If someone saved a newer version
meanwhile the write fails with `conflict` (exit 5); look at it, then run again. `--base-version <id>` (or a ref with
a version, `123_456`) pins the base explicitly, and it must still be the latest; `--force` skips the check.

`--from published` (or `--from <id>` / `--from 123_456`, a version of the same content and language) bases the change
on that version instead, e.g. to change the live page while someone's draft is newer. The check stays: the change
still fails with `conflict` when someone saves a newer version meanwhile (`--base-version` names the version expected
to be the latest; `--force` skips it). Not with a ref that names a version (usage error); `--from published` on a
branch that was never published is `not_found`.
- `baseVersion` in the output is the version it was based on; `leftOut[]` the newer versions it doesn't include
  (`version`, `status`, `savedBy`, `saved`, `primary`: what edit mode opened until then), with a warning in
  `meta.warnings` (dry runs too). They stay as they are, without the change, and the change has none of theirs. The new
  draft becomes the primary draft, which edit mode opens; once published, edit mode opens the published version.
  Publishing one of those drafts later puts its changes live without this change.
- In a plan, `"from"` on a `set` or `area` step does the same (`"published"`, `456` or `"123_456"`; only on existing
  content). Later steps on that content and language build on its result, and a later `publish` publishes that, so
  the plan is dry-run that way.

### Other people's drafts

A publish puts the whole version live, so with `set`/`area --publish` also every unpublished change in the version it
is based on. If a version saved after the published one (in that language) was saved by someone other than `opticli`,
and the version that would go live has its changes, these stop unless confirmed: `set`, `area` and plan steps with
`publish`, `publish` without `--version`, and `create`/`block`/`upload`/`translate` steps that publish existing content
(`apply --update-existing`).
- To publish a change without someone's draft, base it on the published version: `set <ref> ... --from published
  --publish` (see [Concurrency](#concurrency)). Their draft stays a draft and needs no `--include-draft`; neither does a
  later `publish` of what was based on it.
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

### Drift (shared databases)

Against a remote development database (shared mode) the site doesn't sync its content types into the database, so
the database keeps what the deployed code made while the site runs this build. `opticli drift` (needs `serve`) lists
what differs: `fingerprint`, `ahead` (`local`, `database`, `both`, `unknown`), `differences`, and per kind
`contentTypes[]`, `properties[]`, `migrations[]` (EF Core), `stores[]` (Dynamic Data Store types), `schema[]` (the CMS
schema version), each with `name`, `ahead` and `difference`. `notes[]` say what wasn't compared (`[AllowedTypes]`,
required, display names and order live only in code, so they never differ). Against a local database nothing is
compared (`checked: false`).
- While anything differs, every write stops, whatever it touches: on a terminal opticli shows the list and asks;
  elsewhere `drift` (exit 5) with the report in `error.details`. Dry runs don't stop. Once `serve` has reported drift,
  commands that use the database and `serve --status` carry a `drift:` warning in `meta.warnings` (not when the site
  was started with `opticli env`: use `opticli drift`).
- `--accept-drift <fingerprint>` confirms it (every write command; `apply --accept-drift` for a whole plan). The
  fingerprint is a hash of the differences: it stops counting when they change, e.g. after a deploy or a pull (restart
  `serve` to compare again). Ask the user before confirming.
- `local` ahead: this branch has changes that aren't deployed there (check out what is deployed, or deploy first).
  `database` ahead: that environment runs newer code than this checkout (pull and build).

### Approval sequences

Content can have an approval sequence (its own, or inherited from an ancestor): publishing it goes through reviewers,
step by step. `get` and `access` show it as `approval`: `definedOn`, `inherited`, `steps[]` (`name`, `reviewers[]` with
`name`, `kind` `role`/`user`, `languages`).
- A write that would publish such content is refused (exit 3, `error.details.reason: "approvalSequence"`), also as a
  dry run and as a plan step: publishing directly would skip its reviewers. Ask the user whether to send it for
  review instead.
- `--request-approval` (`set`, `area`, `create`, `block create`, `upload`, `translate`, `publish`; in a plan
  `"requestApproval": true`, or `apply --request-approval` for every step that publishes) saves the version and starts
  the sequence, as the edit UI's Ready for Review: status `awaitingApproval`, `approvalRequested: true`, nothing live
  changes. With `--publish` too, content without a sequence is published as usual; alone, it fails there (exit 1,
  `details.reason: "noApprovalSequence"`).
- opticli doesn't approve or reject: that is a reviewer's decision, in the CMS edit UI. `drafts` lists what awaits it.
- Content in review can't be changed until a reviewer decides: writes fail with `conflict` (exit 5,
  `details.reason: "inReview"`).

### Plans (`apply`)

```json
{"operations": [
  {"op": "create", "id": "page", "parent": "45", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
  {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
  {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
```

Ops: `set`, `create`, `area`, `block`, `upload`, `translate`, `publish`, `unpublish`, `discard`, `move`, `delete`,
`restore`, `access`, with the same fields as the commands (`opticli apply --help` lists them). `"$id"` refers to what an earlier
`create`, `block` or `upload` with that `id` made.
- In rich text (and `@file` HTML), `href="$id"` (or `"$id#anchor"`) links to planned content: it is stored as its
  permanent link, `~/link/<guid>.aspx`, as the CMS does. A block in the text takes `data-contentguid="$id"` (or
  `data-contentlink="$id"`, its id). A link to content a later step creates (or the same step) needs its GUID up front:
  `"guidNamespace"` on the plan or `"guid"` on that step; `data-contentlink` always needs the content to come first.
  The dry run checks the HTML with that GUID, or a stand-in.
- A string value `"@path"` anywhere in `properties` is that file's text, like `Prop=@file`:
  `"MainBody": "@texts/article.html"`. `"@@..."` is a literal `@`.
- `{"op": "upload", "id": "q1", "file": "files/q1.pdf", "parent": "$reports"}`.
- Plan files (`@path` values and upload files) are relative to the plan file (the working directory for `-`) and must
  stay inside its folder, also through symlinks, unless `apply --allow-outside`.
- `{"op": "access", "ref": "$page", "grant": {"Authenticated": "Read"}, "revoke": ["Everyone"], "breakInheritance": true}`
  (also `grantUsers`, `inherit`, `allowUnknownRole`).

Every operation is validated before anything is written; on a failure opticli stops and reports what was saved and
how to undo it, also when it is interrupted (Ctrl+C: `cancelled`, exit 130) or something unexpected fails:
`details.partial: true`, `details.operations` with each step's status and undo, `details.created`. The step that was
running when the plan was interrupted, or whose answer timed out (`unreachable`), may still have been saved: check with
`opticli versions <ref>` before running the plan again. `--publish` on `apply` publishes every operation: only when the
user asked.

How far the dry run gets (`meta.warnings` sums it up; step statuses):
- `valid`: the site dry-ran the operation as is.
- `simulated`: the operation is on content the plan creates, so the site dry-ran that content as it will be after the
  operation (every value set on it so far, area edits included; published if the operation publishes), under its
  nearest existing ancestor.
  Its warnings name the stand-ins and the values not checked yet: those that refer to other planned content. A
  required property or a site validator that fails at publish shows up here. Area placements and references to
  planned content are checked against `[AllowedTypes]` in the code.
  Also simulated, on existing content: a `publish` after `set`, `area`, `translate` (or `create`/`block`/`upload`
  updating existing content) on the same content and language is dry-run as those changes, published, so its
  pending-draft check (`includeDraft`) sees what will go live. A `publish` right after a step that already publishes
  it fails validation (`conflict`): there is nothing left to publish. After a step with `"from"`, a `set`, `area` or
  `publish` on that content and language is dry-run as that step and the ones since, on the version it names (steps
  before it are left out, as in the real run). A `set` on a language branch that an earlier
  `translate` creates is dry-run as the new branch with every value set on it so far; an `area` edit there is dry-run
  on the master branch.
  A step on existing content whose rich text links to planned content is dry-run with those links pointing at the
  content's GUID (or a stand-in).
- `deferred`: only names were checked (`access`, `translate`, `move`, `delete` on planned content, and a `set` or
  `publish` of another branch than the one it is created in); the site validates them when the plan runs.

Whatever the status, the order of publishes is checked: a step that publishes a branch other than the master before
the master branch is published (by an earlier step, or in the database) fails validation with `validation`,
`details.reason: "masterNotPublished"`, and the hint names the later step that publishes the master, to move before it.
A scheduled publish or a review request of the master doesn't count as publishing it.

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

- `opticli serve [--build] [--port N] [--https] [--scheduler] [--foreground] [--output <dll>] [--timeout s] [--allow-pending-migrations]`: runs the existing build
  output (`bin/Debug/<tfm>/<Site>.dll`) with the site agent injected through `DOTNET_STARTUP_HOOKS`, in Development, on
  `http://127.0.0.1:<port>` (default 5199, else the first free port up to 5299), waiting up to `--timeout`
  (default 180 s) for it to answer. A warning says when sources are newer than the
  build; `--build` runs `dotnet build` first. The site's code and files are not changed. The site's scheduler is off for
  the run (`scheduler: off` in the output) unless `--scheduler` (see [Scheduled jobs](#scheduled-jobs)).
- `opticli serve --status | --logs [--tail N] | --stop`. `--logs` shows the latest run; `data.previous` lists the
  logs of the two runs before it. A `serve` started while another is starting the same site waits for that one.
- A site that redirects HTTP to HTTPS can't be browsed on the agent's address (`serve` warns). `--https` also binds
  `https://localhost:<port>` with the ASP.NET Core development certificate and prints it as `browseUrl`; opticli keeps
  talking to the agent over HTTP. Without a certificate the site fails to start: `dotnet dev-certs https --trust`.
- The agent refuses to start if the site would use a different database, or a remote one other than the development
  database (exit 3). It answers only loopback callers with the per-run token, and only in Development. `serve`
  refuses a remote database that isn't the development one.
- Against a remote development database the site runs with the scheduler (`--scheduler` is a `usage` error there),
  automatic schema updates, content type sync and Dynamic Data Store remapping off (a warning says so): content types
  or properties that exist only in local code aren't in the database, so writes to them fail. Before it starts the site, `serve` refuses (exit 3) a build with
  EF Core migrations the database's `__EFMigrationsHistory` lacks (`details.reason: "pendingMigrations"`; a site that
  migrates at startup would apply them; `--allow-pending-migrations` starts it anyway), and a CMS schema version the
  CMS won't start with (`"schemaVersion"`). Once the site answers it reports drift (see
  [Drift](#drift-shared-databases)).
- `opticli env [--format shell|powershell|dotenv|json|launchSettings] [--include-connection]` prints the variables
  to start the site yourself (IDE, `dotnet run`). Set them only for the site process (a subshell or launch profile):
  `DOTNET_STARTUP_HOOKS` affects every .NET process started from a shell that exports it. The connection string is
  left out unless `--include-connection`. The scheduler stays as the site sets it (`OPTICLI_SCHEDULER=on`).

## Troubleshooting

| Symptom | Do |
|---|---|
| `not_found`: no CMS project / several candidates | `cd` into the site's repository, or pass `--project path/to/Site.csproj`. |
| No connection string | `opticli doctor`; `--connection-name` if the site's connection string has another name. |
| `needs_selection` (exit 6) | Show the user `error.details.choices`, ask which is their development database, run `opticli db use <id>`. |
| `refused` (exit 3) | A safety rule: serving against a remote database that isn't the development one, SQL the `sql` guard won't run, or a request the site agent won't do (e.g. moving or deleting a start page or a folder that contains one). Don't work around it; tell the user. |
| `unreachable` (exit 4) on a remote database | Firewall (Azure SQL allows only listed client IPs), VPN, or login; for Entra ID auth, `az login`. Tell the user. |
| `unreachable` (exit 4) on a write | The site isn't running: `opticli serve`; if it was, `opticli serve --status` and `--logs --tail 80`. If the write timed out ("no response within"), it may still have been saved: `opticli versions <ref>` before you retry. |
| `serve` times out or exits | `opticli serve --logs --tail 80`. Typical: the site needs a build (`--build`), a port is taken (`--port`), or the site's own startup fails. |
| `validation` (exit 5) | `error.details` lists each failing property; drafts may leave required properties empty, publishing may not. |
| `validation` (exit 5), `details.reason: "masterNotPublished"` | The master language branch was never published, and the CMS publishes no other branch before it. Ask the user whether the master should go live first (`opticli publish <ref>`; in a plan, an earlier step), or save the branch as a draft. |
| `refused` (exit 3), `details.reason: "approvalSequence"` | The content has an approval sequence. Ask the user whether to send it for review; if so, run again with `--request-approval`. |
| `conflict` (exit 5), `details.reason: "inReview"` | The content awaits a reviewer's decision; tell the user, who decides in the CMS edit UI. |
| `conflict` (exit 5), `details.reason: "referenced"` | Other content references what `delete` would remove (`details.references`). Show them; remove the references, or ask the user before `--ignore-references`. |
| `drift` (exit 5) | This build and the shared database differ (`error.details`, `opticli drift`). Show the user the differences and which side is ahead; pass `--accept-drift <details.fingerprint>` only after they said to write anyway. |
| `refused` (exit 3) from `serve`, `details.reason: "pendingMigrations"` or `"schemaVersion"` | The build doesn't fit the shared database: migrations it lacks, or a CMS schema the packages can't run. Tell the user (check out what is deployed, or pull); `--allow-pending-migrations` only when they say the site doesn't migrate at startup. |
| `job_failed` (exit 7) | The job `jobs run` ran didn't succeed: `error.details.status` and `message` say how it ended; `opticli jobs log <job>` has its earlier runs, `opticli serve --logs` the site's log. Tell the user; don't run it again unasked. |
| `timeout` (exit 4) from `jobs run` | `--timeout` ran out and the job still runs: `opticli jobs log <job>` later shows how it ended; `opticli jobs stop <job>` only if the user wants it stopped. |
| `refused` (exit 3) from `jobs run` or `jobs set`, naming what the job deletes | A job that deletes for good (or moves content across the site). Ask the user, naming the job and what it does; only then `--allow-destructive`. |
| `conflict` (exit 5) | A newer version exists: `opticli versions <ref> --limit 3`, then re-run. With `details.reason: "pendingDraft"`: someone else's unpublished changes would go live too; show `details.draft` and ask the user before `--include-draft`. |
| Values look cut off | `truncated: true`: use `get <ref> --fields Prop` or `--full`. |
