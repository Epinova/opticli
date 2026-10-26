# opticli reference

Companion to [SKILL.md](SKILL.md). `opticli <command> --help` is always the authority for the installed version.

## Global options (every command)

| Option | Use |
|---|---|
| `--project <path>` | The site's `.csproj` (or a directory/solution to search) when discovery finds none or several. |
| `--connection-name <name>` | Connection string name in the site's config (default `EPiServerDB`). |
| `--db <id\|name>` | Use another of the project's connection strings for this run: its id or name from `opticli db list`, or its database name. A `prod` database is only read. |
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
| `db list` | `choices[]` (`n`, `id`, `name`, `server`, `database`, `local`, `role`, `from`, `development`), `development`, `mode` (`saved`, `configured`, `automatic`), `saved` (with `stillMatches`), `problem`. |
| `db use <id>` | Saves the development database (the id changes when that setting's server or database changes, and opticli asks again). Without an id on a terminal: pick from a list. Only with the user's choice. |
| `db forget` | Removes the saved choice. |
| `db add <name> --role <local\|dev\|test\|prod> [--connection <string>]` | The user adds another environment's database by name (in the user config), with a role; without `--connection` at a terminal it asks (the password hidden). `data.database` is its choice, `note` says what `prod` means. Only when the user asks. |
| `db remove <name>` | Removes an entry; the role of its server and database stays (`roleKept`). |
| `db role <id\|name> <role>` | Gives a database (any `db list` entry) a role. Roles belong to the server and database, so every entry and setting naming them has it. A weaker role than it has needs the user's own terminal (`refused`, `details.reason: "roleLowered"`, elsewhere). Only when the user asks. |

Roles: `local` (the default for a database on this machine), `dev`, `test`, `prod` (a remote database without one
behaves as `dev`/`test` do: shared mode, drift gate). `prod` is only read, when a run picks it: `meta.warnings` says
so and `meta.database.role` is `prod`, and opticli connects with `ApplicationIntent=ReadOnly`. It is never the
development database (`db use` refuses it; a saved choice, configured connection or Development default that is
production is `refused` with a hint), and writes (`--dry-run` too), `apply`, `serve` and `env` against it are
`refused` (exit 3, `details.reason: "prodDatabase"`). `sql` works, with the warning. `doctor` warns when a
production entry's login can write. The one write to production is a sealed apply the user runs: see
[Sealed apply to production](#sealed-apply-to-production).

## Read commands

| Command | Notes |
|---|---|
| `doctor` | Always exits 0. `data.healthy`, `data.warnings`; `connection.candidates`, `database`, `agent` (`scheduler`: `on`/`off` in the running site), `scheduler` (`serve`: what `serve` does, `running`, `overdueJobs`), `skills`, `sitesMapping` (each entry of the saved `sites primary` mapping: `key`, `saved`, `primary`, `status`: `matches`, `differs`, `noSite`, `invalid`). |
| `sites` | Hosts (`name`, `type`: undefined, primary, edit, redirectPermanent, redirectTemporary, on CMS 13 also preview, media; `language`, `https`), URL (SiteUrl), start page, master language, assets root. CMS 13 lists applications: no `guid`, plus `application` (its name, the key), `applicationType` (`inProcessWebsite`, `website`), `isDefault` (answers unknown hosts, as `*` did). |
| `languages [--all]` | Enabled branches (all with `--all`), item counts, which sites use each as master. |
| `types [--kind page\|block\|media\|folder\|other] [--unused] [--orphaned] [--sort name\|instances]` | CMS 13 kinds too: `experience`, `section`, `element`, `contract` (`--kind page` includes experiences, `block` sections and elements); CMS 13 fields `compositionBehaviors`, `contracts`, `blueprints` (not in `instances`), `inlineUses` (inline blocks in current versions; `--unused` leaves out types used inline, and contracts). `instances` = non-deleted items. Sorted by name unless `--sort instances`. `--orphaned`: types defined in code (the CMS has a class on record, `modelType`) whose class is gone; the CMS keeps such a type while content (or a block property) uses it. Through the running site when `serve` runs (every class it can't load, packages' too; `meta.source: agent`); otherwise a source scan of the site's own assemblies' types (by GUID, then name; on CMS 13 also the GUIDs of the build output's classes), with a warning that says why the site wasn't asked (not running, an older agent, or its error). CMS 13: a type with no class or model-sync version on record and no class in the build with its GUID has `originUnknown: true` (made in admin mode, or a code type a content import overwrote). `types remove|remove-property|prune` remove them: see [Orphaned content types](#orphaned-content-types). |
| `type <name\|class\|guid>` | `properties[]` (name, type, blockType, list, cultureSpecific, required, tab, order, displayName, `source` file:line, `declaredIn`, `allowedTypes`/`restrictedTypes` from `[AllowedTypes]`, `uiHint`), `classes[]` (file, line, `baseTypes`), `views[]` (file, `matchedBy`: fileName, partialName, viewComponent, model), `sourceRoot`; CMS 13: `kind`, `compositionBehaviors`, `contracts`, `implementedBy` (a contract's), `blueprints`, `displayTemplates`. `existsOnModel: false` = in the DB but not in code (removed from code, which the CMS keeps while it has values, or added in admin mode), with `values` (`content`: content items holding a value, `versions`: versions holding one; values inside a block property and category selections count, as removing it deletes them) and a warning. Controllers are not listed. |
| `allowed-in <type> [--kind K] [--explicit]` | ContentArea/reference properties of every type that can hold `<type>`, from `[AllowedTypes]` in code: `allowed` (`explicit` + `matchedBy`, or `any` for a ContentArea/reference list without the attribute), `allowedTypes`, `uiHint`, `source`. Base classes and interfaces declared in the sources count. Editor descriptors (`uiHint`) and metadata extenders can change the rules at runtime; they are not evaluated. CMS 13: `allowed: "composition"` rows (`property: "composition"`) for an experience's outline (SectionEnabled types) and a section's columns (ElementEnabled types). |
| `get <ref>` | `--lang`, `--version published\|latest\|<id>` (default: published, or the latest draft if the branch was never published), `--fields A,B` (whole values; identity fields like `name`, `saved` are always shown, so `--fields name` gives just those), `--full`, `--all-properties`, `--expand` (inline ContentArea items / referenced content one level). `saved`/`changedBy` belong to the version shown. With `--lang`, language settings apply: a replacement language, or (no branch) the first fallback language that has one; `languageRule` (`replacement`, `fallback`, `none`) and `notes` say so. `projects` lists the projects that hold a version of it. CMS 13: `--variation <key>` shows a content variation (see [Visual Builder](#visual-builder-cms-13)); an experience or section shows `composition`. |
| `resolve <url>` | `--site`. Result has `site`, `host`, `languageSource`, `matchedBy`. |
| `url <ref>` | Per language: `path` (site-relative), `url` (absolute), `site`. |
| `tree <ref> [--depth N] [--limit N]` | Default depth 2, max 5000 nodes (`capped`). `--limit` is children per node; the rest are counted in `more`. CMS 13: Visual Builder content has `kind`; blueprints are left out (`blueprints: N` on the parent) unless `--blueprints`, as in `children`, `find`, `search` and `drafts`. |
| `children <ref>` / `ancestors <ref>` | One level down, in the parent's `childSortOrder` / the path from the root. Children show `sortIndex` when the parent sorts by `Index`. |
| `find --type T [--where ...] [--under <ref>] [--status published\|draft\|scheduled\|expired\|any] [--lang]` | `--where` is repeatable: `Prop=value` exact, `Prop~value` contains, `Block.Prop=...` inside a local block, `Name~...` on the name, `Area=<ref>` for ContentAreas/references containing that content. Deleted items excluded. `--status scheduled`: a version waits for the "Publish delayed content versions" job (`publishAt`, its first); `expired`: published, but the stop-publish date has passed, so visitors don't see it (`expiredAt`). Without `--lang` both look at each item's master language only, as the rest of `find`. CMS 13: a content variation's draft or scheduled version counts too (`variationDrafts` names the variations). |
| `search <text> [--in names\|strings\|all] [--lang]` | One row per item and language with `matches[]` (`property`, `snippet`; CMS 13: `section` and `element` `{key, name, type}` for text in a composition). Capped at 2000 values per source (`meta.warnings`). |
| `where-used --type T` | Every instance of a type (outside the recycle bin): `ref`, `name`, `count`, `usages[]` (as below), the most used first, unused ones last; `meta.warnings` sums it up. CMS 13: blueprints last with `blueprint: true`, not counted; a row with `inline: true` for the type's inline blocks (sections, elements). |
| `where-used <ref> [--pages]` | Rows: owner identity + `saved`, `changedBy` + `property` (e.g. `MainArea`, `Hero.Link`, `MainArea[2].Text`), `kind` (`contentArea`, `contentReference`, `contentReferenceList`, `richTextLink`, `richTextBlock`, `link`, `linkCollection`, `url`, `text`, `softlink`), `sources` (`softlink` = CMS link index, `property` = value scan). Deleted owners last, `deleted: true`. The value scan reads each branch's primary values (published, else the latest draft); the link index covers saved versions. `--pages` follows block owners up to the pages (rows get `via`: the blocks in between), newest `saved` first. A reference only inside personalized rich text has `visitorGroups` (and `visitorGroupNames`): only those visitors see it. CMS 13: a usage inside a composition names its `section` and `element`; a shared block placed in one has `property` and `kind` `composition`. |
| `versions <ref> [--lang]` | Newest first: `ref` (`id_version`), `language`, `status`, `name`, `saved`, `changedBy`, `startPublish`, `primary`. CMS 13: a content variation's versions have `variation` (its key) and never `primary`. |
| `drafts [--since <date>] [--by <user>] [--kind K] [--type T] [--lang]` | One row per item and language with unpublished changes: `status` and `version` of the newest draft, `saved`, `changedBy`, `drafts` (unpublished versions newer than the published one), `publishAt` for a scheduled one. `--since` is UTC. CMS 13: a content variation with a draft newer than its own published version has a row of its own with `variation`. |
| `history <ref> [--since <date\|7d>] [--by <user>]` | The CMS's change log (`tblActivityLog`) for one item, newest first: `when`, `by`, `action` (`create`, `publish`, `delayedPublish`, `requestApproval`, `rejected`, `checkIn`, `move`, `delete` = to the recycle bin, `restore` = out of it, `deletePermanently`, `deleteLanguage`, `deleteVersion`, ...), `version`, `language`, `name` (as it was), `from`/`to` (`ref`, `name`) for moves, `previousStatus` for a publish. The only record of moves and deletes; drafts saved aren't in it (`versions`), and the Change Log Auto Truncate job removes old entries. |
| `categories` | The category tree, depth first: `id`, `name` (what `set Category=...` takes; `get` shows it), `description` (edit mode's name, which `set` takes too), `parent`, `path`, `depth`, `selectable` (only those can be set), `visible`, `items` (content that has it), `guid`. |
| `visitor-groups` | `id` (what `visitorGroups` in `get` and `set` hold), `name`, `match` (`all`, `any`, `points` with `pointsThreshold`), `securityRole` (usable in access rights), `statistics`. Criteria and notes aren't read. |
| `projects [<id>]` | Projects (versions of several items published together): `id`, `name`, `status`, `created`, `createdBy`, `publishAt`, `items`. With an id, its items: `ref`, `version`, `type`, `name`, `language`, `status`. Read-only. |
| `trash [--since <date\|7d>] [--by <user>] [--type T]` | What is directly in the recycle bin (what was deleted), newest first: identity, `deletedBy`, `deleted` (UTC), `descendants` (below it, coming back with it), `originalParent` (`ref`, `name`, `type`, `path`, `url`; `deleted: true` when it is in the recycle bin too, `missing: true` when it is gone): the parent the CMS stored when it was deleted, where `restore` puts it. Null when the CMS has no record of it (`restore --to`). Read from the database, or from the site while `serve` runs (as `restore` reads it; `meta.source: agent`); a warning says why the site wasn't asked when it wasn't. |
| `display-templates [--type T]` | CMS 13: Visual Builder display templates: `key`, `name`, `nodeType`/`baseType`/`contentType` it is for (absent: any), `isDefault`, `settings[]` (`key`, `name`, `editor` select or checkbox, `choices`). `--type`: the ones content of that type can use. Empty on CMS 12. |
| `blob <ref>` | Media only: blob URI, file path on disk (the file blob provider's folder, from appsettings or `EPiServer__Cms__FileBlobProvider__Path` in the environment), `exists`; same for the thumbnail. |
| `export <ref>... [--descendants] \| --site <name> \| --all` | Content as a plan folder for `apply --update-existing` in another database; only reads, so production too. See [Export](#export). |
| `jobs [--all] [--enabled\|--disabled] [--failed]` | Scheduled jobs: `id`, `name` (on CMS 13, whose database has only the class name: the CMS's own jobs' readable names, the site's from `[ScheduledJob(DisplayName = ...)]`, else the class), `enabled`, `schedule` (`every 1 hour`, `manual`), `nextRun`, `overdue` (enabled, next run passed), `lastRun`, `lastStatus` (`succeeded`, `failed`, `cancelled`, `unableToStart`, `aborted`), `lastMessage` (one line), `running` (`true`, `false`, `"stale"`: its process stopped pinging), `stoppable`, `class`, `source` (file:line in the site's code; null for a job from a package), `registered: false` for a job in the code the database lacks (the site registers jobs when it starts). `--all` adds jobs the CMS hides. See [Scheduled jobs](#scheduled-jobs). |
| `jobs log [<job>] [--failed] [--since <date\|7d>] [--limit N]` | Runs, latest first (20 by default): `job`, `jobId`, `started`, `finished`, `duration`, `durationMs`, `status`, `trigger` (`scheduler`, `user`, `restart`), `server`, `message` (whole; it can be HTML). Without `<job>`: every job's runs. |
| `access <ref>` | `inherited`, `from` (the item the entries are stored on: itself, or the nearest ancestor with its own), `entries[]` (`name`, `kind`: role, user or visitorGroup, `levels`: `FullAccess` or e.g. `["Read","Edit"]`, `mask`). Read from the database; no `serve` needed. |
| `sql "<SELECT ...>" [--limit N] [--full] [--include-personal-data]` | One SELECT/WITH statement, run in a rolled-back transaction; returns 100 rows unless `--limit` (`truncated: true` when there were more); `--jsonl` prints rows. Forms submissions and user/membership tables need `--include-personal-data`. In `sys`, only the views that describe this database's schema (`sys.objects`, `tables`, `columns`, `indexes`, `index_columns`, `types`, `schemas`, `foreign_keys`, `sql_modules`, ...) are allowed, and `INFORMATION_SCHEMA` views; `sys.dm_*`, `fn_*`, `sys*` compatibility views and server-wide views are refused. Put a space between a number and a following word (`1 AS x`, not `1AS x`). |

### Shape of `get`

```json
{"ref":"123","guid":"...","type":"ArticlePage","name":"News","language":"en","status":"published",
 "url":"/en/news/","kind":"page","version":"123_456","masterLanguage":"en","languages":["en","de"],
 "parent":"45","saved":"2024-05-01T10:00:00Z","changedBy":"editor","startPublish":"...",
 "childSortOrder":"PublishedDescending","sortIndex":100,"urlSegment":"news","visibleInMenu":true,
 "simpleAddress":"/news","category":["Press"],
 "shortcut":{"type":"shortcut","to":{"ref":"456","type":"NewsPage","name":"News archive","url":"/en/archive/"}},
 "properties":{
   "Heading":{"type":"String","value":"Hello","culture":"en"},
   "MainArea":{"type":"ContentArea","value":[{"ref":"789","type":"TeaserBlock","name":"Teaser","displayOption":"wide",
     "group":"g1","visitorGroups":["<visitor group id>"],"visitorGroupNames":["Returning visitors"]}]},
   "Hero":{"type":"Block","blockType":"HeroBlock","value":{"Heading":{"type":"String","value":"..."}}}}}
```

Pages show `urlSegment` (the name in its URL) and `visibleInMenu`, which `set` writes as `PageURLSegment` and
`PageVisibleInMenu`. `simpleAddress`, `shortcut` (absent on a normal page; an external link also has `url`, and `to`/`anchor` when it is
a permanent link to a page), `category` and `approval` (see [Approval sequences](#approval-sequences)) appear when
set. A fetch-data page (`shortcut.type: "fetchData"`) shows its own values; `notes` says the site fills the empty ones
from the page in `shortcut.to`. `culture` on a top-level property is the branch its value came from (shared properties come from the master
language). Empty properties are omitted unless `--all-properties`. Rich text gives the (possibly truncated) HTML
plus its resolved links and embedded blocks, and `personalized[]` for the sections only some visitor groups see
(`visitorGroups`, `visitorGroupNames`, `group`, their `value` and `links`). `visitorGroupNames` sit beside the ids, which
are what `set` takes back; a visitor group id that doesn't exist is refused on write.

### Visual Builder (CMS 13)

An experience (`kind: experience`) is a page made of sections; a section holds rows, a row columns, a column elements.
`get` shows its `composition` instead of the properties it is stored in (`Layout`, `UnstructuredData`; `--fields
Layout` shows the stored value), and `--text` as a tree:

```json
"composition": {"layout": "outline", "culture": "en", "displayTemplate": "page", "displaySettings": {"tone": "dark"}, "sections": [
  {"key": "<guid>", "name": "Hero", "type": "HeroSection", "inline": true, "displayTemplate": "heroLook", "displaySettings": {"background": "dark"}, "rows": [
    {"key": "<guid>", "name": "Row", "columns": [
      {"key": "<guid>", "name": "Left", "elements": [
        {"key": "<guid>", "name": "Intro", "type": "TextElement", "inline": true, "properties": {"Heading": {"type": "LongString", "value": "Hi"}}},
        {"key": "<guid>", "name": "Shared", "type": "TextElement", "content": {"ref": "103", "name": "Shared text", "status": "published"}}]}]}]},
  {"key": "<guid>", "nodeType": "component", "name": "Banner", "type": "BannerBlock", "inline": true, "properties": {"Title": {"type": "LongString", "value": "Sale"}}}]}
```

`inline: true` + `properties` is a block stored in the experience; `content` is a shared block placed by reference
(its values are its own: change them with `set <its ref>`). A section-enabled block in the outline has `"nodeType":
"component"` and no rows. `missing: true` marks a node whose block isn't stored, and `unplaced` lists stored blocks no
node shows (the CMS ignores both). A section (`kind: section`) or a section blueprint has a `grid` composition with
`rows`.
- **Content variations:** versions of their own (a key, e.g. a campaign) that store only what they change; the rest
  is the published version's. `versions` marks them `variation`; `get <ref> --variation <key>` shows its published
  version (`--version latest`: its newest), or `get <id>_<version>`.
- **Blueprints** (`blueprint: true`): templates new content is made from, under the "Blueprints" folder. Listings
  leave them out unless `--blueprints`; `where-used` keeps them.

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
| `area <ref> <Prop> add --type T [Prop=value...] [--values json] [--name N] [--at N] [--display opt]` (inline block, CMS 12.20+) | `opticli area 123 MainArea add --type TeaserBlock Heading=Hi --dry-run` (see [Inline blocks](#inline-blocks-in-contentareas)) |
| `composition <ref> add\|remove\|move\|set ...` (CMS 13) | `opticli composition 123 add element --in Left --type TextElement Heading=Hi --dry-run` (see [Compositions](#compositions-cms-13)) |
| `create <parent-ref> --blueprint B --name N` (CMS 13) | `opticli create 45 --blueprint "Landing blueprint" --name "Spring" --dry-run` |
| `area <ref> <Prop> remove <position\|ref:id>` | `opticli area 123 MainArea remove ref:789` (an inline block only by its position) |
| `area <ref> <Prop> set <position> [Prop=value...] [--values json] [--name N]` (inline block) | `opticli area 123 MainArea set 2 Heading=New --dry-run` |
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
| `sync pull --from <name> (<ref>... \| --site <name> \| --all) [--delete] [--overwrite-local]` | `opticli sync pull --from prod --all --dry-run` (only when the user asked; see [Sync pull](#sync-pull)) |
| `sync push --to <name> (<ref>... \| --site <name> \| --all)` | `opticli sync push --to prod /en/campaign/`: a sealed plan for production; hand the user `data.userCommand` (see [Sealed apply to production](#sealed-apply-to-production)) |
| `apply <plan> --dry-run --seal-for <name>` | `opticli apply plan.json --dry-run --seal-for prod --update-existing`: seals a plan for production; the user runs `apply <plan> --db <name> --seal <id>` |

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
below `--to`. Name it by id or GUID: content in the recycle bin has no URL (a URL is `not_found`). Output: `ref`, `parent`, `previousParent` (the recycle bin), `from` (`originalParent` or `to`),
`originalParent` (what the CMS stored, also when `--to` overrode it), `restored`, `descendants` (they come back with it).
It keeps its versions: content that was published is live again at once (a warning says so). Errors: `conflict` when it
isn't in the recycle bin, or the parent is in the recycle bin too or gone (the hint names what to restore first);
`usage` for content below deleted content (the hint names what was deleted), and when the CMS has no parent for it and
no `--to` was given; `validation` (`details.reason: "restore"`) when its type isn't allowed below the parent, as for `move`. `--dry-run` asks the
site, which runs every check. Undo: `opticli delete <ref>`. In a plan: `{"op": "restore", "ref": "123", "to": "45"}`
(`to` optional); a restore of what an earlier step deletes (named by any ref) is checked when the plan runs (`deferred`), and with
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
- `MainArea[2].Prop=value` sets a property of the inline block at position 2 (zero-based) of the ContentArea `MainArea`
  in the version changed (see [Inline blocks](#inline-blocks-in-contentareas) for where positions come from);
  `--values '{"MainArea[2]": {"Prop": "value"}}'` too, also with an area of its own as `get` shows it.
- `--values '<json object>'` is merged on top, for structured values:
  - ContentArea: `{"MainArea":[{"ref":"456"},{"ref":"789","displayOption":"wide"}]}` (replaces the whole area;
    use `area` to add/remove single items). An item may name its content by `"guid"` instead of `"ref"`.
    `displayOption` is the id of one of the site's display options (its name or tag works too); an unknown one is
    refused with the list. Personalization: `"group":"g1"` and `"visitorGroups":["<visitor group id>"]`, as `get`
    shows them. An item without them keeps those of the item for the same content it replaces, and that item's other
    render settings: the n-th item for some content keeps the n-th current one's, so inserting, removing or reordering
    items doesn't move personalization to another item. `"group":""` and `"visitorGroups":[]` remove it. An inline
    block (CMS 12.20+) is `{"type":"TeaserBlock","properties":{"Heading":"Hi"},"name":"Intro"}` (see
    [Inline blocks](#inline-blocks-in-contentareas): an inline item keeps a block's values only as an exact copy of it).
    The ContentArea value `get` shows can be sent back as is, inline blocks included: written back unchanged it changes
    nothing.
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
- PageType property (e.g. a page list's type filter, which `get` shows as the type's name): the page type's name, id or
  GUID, `PageTypeFilter=ArticlePage`. Another kind of type is a `validation` error.
- A value may also be given as `get` shows it, `{"type": ..., "value": ...}` (references, links and local blocks as
  `get` shows them too): the CLI unwraps it. A value `get` cut short (`truncated`) is refused: read it with `--full`.
- Property names are checked against the content type before anything is sent; a typo fails with a suggestion.

### Compositions (CMS 13)

`opticli composition <ref> <action> ...` edits a Visual Builder experience (or section) on a new version, a draft unless
`--publish`; all of `set`'s write options apply (`--dry-run`, `--lang`, `--from`, `--base-version`, ...). A node is
its key (as `get` shows it), or its name when only one node has it; `root` (or nothing) is the composition itself.
- `composition <ref> add section|row|column|element [Prop=value...]`: `--in <node>` (default: the experience's sections), `--at N`,
  and `--type T` (a new inline block, with its values), `--ref <ref>` (a shared block), or for a section `--blueprint
  <section blueprint>` (its rows, with new keys); `--name`, `--template <key>`, `--setting k=v` (repeatable). Or the
  whole node with its children, as `get` shows one: `--node '<json>'`.
- `composition <ref> set <node> [Prop=value...] [--values json]`: an inline block's values, `--name`, `--template` (`""` clears it and its
  settings), `--setting k=v` (merged; `k=` removes one). A shared block's values are refused: `set <its ref>`.
- `composition <ref> move <node> --in <node> --at N`; `composition <ref> remove <node>` (with everything in it).
- Structure rules (outline: sections and SectionEnabled blocks; section: rows; row: columns; column: ElementEnabled
  blocks) and display templates and settings (`opticli display-templates`: node type, content type, keys, choices) are
  checked first, as `usage` errors naming the node. The CMS validates the rest on save (`validation`).
- The whole composition: `composition` in `set`/`create` values (`--values '{"composition": {...}}'`,
  `composition=@file.json`, or a plan's `properties`), in `get`'s shape. Structure, order, names and styles are set as
  given; a node with a key keeps its block (its type can't change) and only the properties given change; a node
  without a key is new; nodes left out are removed. `get`'s output written back unchanged changes nothing.
- An empty composition (no nodes) on a language branch other than the master branch that isn't published is `refused`
  (CMS 13.3 would delete the master branch's stored blocks): leave the composition out, keep a node, or publish the
  branch first.
- Output: `composition[]` per changed node: `change` (`added`, `removed`, `moved`, `changed`), `nodeType`, `key`, `name`,
  `type`, `in`, `at`, `changes[]` (`field`, `before`, `after`). New nodes get new keys, shown here.
- `--variation <key>` (also on `set`): writes that content variation; one without a version yet starts from the
  published version. `publish <id>_<version>` publishes a variation's version; the content's own published version
  stays.
- `create <parent> --blueprint <ref|GUID|name> --name N`: the blueprint's type (`--type` optional, must match), values
  and composition, in one language and one version, saved like any `create` (dry run, `--guid`, draft or publish).
- Not supported: making or changing blueprints and display templates; dry runs of composition edits on content a plan
  creates (checked when the plan runs, `deferred`).

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

### Inline blocks in ContentAreas

CMS 12.20 and later (and CMS 13) can store a block in a ContentArea itself instead of as content of its own. `get` shows
one as `{"inline": true, "type": "TeaserBlock", "name": "Intro", "properties": {...}}` (`name` only when it has one),
with no `ref`.
- Add one: `opticli area 123 MainArea add --type TeaserBlock Heading=Hi Image=456 --name Intro --at 0 --display wide`
  (`--values` for structured values, as `set`). The block is made as the CMS makes it, with the type's default values;
  the CMS validates it on save (required properties, the area's `[AllowedTypes]`), as it validates one the edit UI makes.
- Change one block: `opticli set 123 'MainArea[2].Heading=New'` (zero-based position; quote it for the shell), or
  `opticli area 123 MainArea set 2 Heading=New --name Intro` (`--name ""` removes the name). Only the values given change;
  the rest of the block, the item's display option and personalization stay. A position that holds a shared block is
  refused with its ref: change that block itself. Positions are those of the version you change, the latest by default:
  read them with `get <ref> --version latest --fields MainArea`. The positions `search` and `where-used` show
  (`MainArea[2].Text`) are of each branch's primary version (published, else the latest draft), which may differ.
- Move or remove one by its position: `area 123 MainArea move 2 0`, `area 123 MainArea remove 2`. `ref:` names only
  shared blocks.
- An area inside an inline block (or a local block) is named by its path in every `area` action:
  `opticli area 123 'MainArea[0].Items' remove 1`, `area 123 Hero.Area add 456` (MCP `areaOps` and plan steps take the
  same `property`). Each property on the way must be one you may change.
- Area edits, like `set`, change an area that isn't culture-specific only in the master language: in another branch
  they are refused (`usage`).
- A whole area (`--values '{"MainArea": [...]}'`) replaces the area: an item with `type` (or `inline: true`) is an inline
  block, one with `ref` or `guid` a shared one (`get`'s `type` and `name` beside a `ref` are about that content and are
  left out). An inline item that is an exact copy of a block the area has (every value, as `get` shows it) keeps that
  block; of identical blocks, the one with the item's name and display option first. Any other inline item is a new block
  with only the values it gives (the type's defaults for the rest): no value carries over from the block it replaces. It
  takes over that block only when which block it replaces is unambiguous (the only changed item of its type, and the only
  block of the type not copied): then it keeps the block's personalization and other render settings, and a ContentArea
  given in it (also in its local blocks) pairs with that block's area, so the blocks there keep theirs too. `name` and
  `displayOption` are always as given. When changed items can't be told apart, the blocks they replace lose their render
  settings (anchors like `data-id`) and personalization, and a `warning` in `validation` names their positions. So the
  area `get` shows, written back unchanged, changes nothing; with a block left out or moved, the others keep theirs; a
  changed block is what you give. To change part of one block, use `MainArea[2]` or `area set` instead.
- For an editor (MCP): a whole area can't drop a value they can't see or change in the edit UI, in the block itself, in
  its local blocks, the items of its block lists or the inline blocks of its areas at any depth: `usage` with `reason: "unseenValues"`, naming the
  value's full path (`MainArea[0].Area[1].Secret`). Only an exact copy keeps such a block. To change part of it, set
  values by position; to remove a block on purpose, use `areaOps` `remove` with the area's path (`MainArea[0].Area`),
  as the edit UI lets them.
- On a CMS before 12.20 adding or changing one is refused (`usage`) with the version it needs.
- The CMS 12 edit UI makes new blocks in a ContentArea inline only when the site turns on
  `UIOptions.InlineBlocksInContentAreaEnabled` (off by default; "Create a new block" then makes a shared block in the
  page's "For this page" folder instead). Either way it shows the inline blocks an area has, and editors can edit, move
  and remove them; when the setting is off, a write that adds one has one `warning` in `validation` saying so.
- In a plan: `{"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "type": "TeaserBlock",
  "values": {"Heading": "Hi"}, "name": "Intro"}`, and `"action": "set"` with `"index"`, `"values"` and `"name"`. Steps run
  in order, so `MainArea[0]` in a later `set` is the block an earlier `area add ... "at": 0` made (the dry run follows
  that order too). An inline block has no identity, so with `apply --update-existing` an `area add` of one changes
  nothing when the area already has one like it: with `"name"`, an inline block of its type with that name, whatever its
  values (later steps may have changed them); without, one of its type with the step's values. Give an inline add a name
  when later steps change it, so the plan runs again unchanged. `area set` changes nothing when the block already has its
  values. A plan's dry run of an `area add` after a whole-area `set` of the same area diffs against the stored area, not
  the one that `set` writes; the plan's result is as the steps say.
- `where-used --type` and `types` count inline blocks on CMS 13 only (`inlineUses`); on CMS 12 `where-used <ref>`,
  `search` and `get` show them.

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
- The site's own code may also change content when something is saved (file a new page into a year folder, create
  and publish that folder): the write then warns, and `siteChanges` in the output lists each item with what happened
  to it (`created`, `saved`, `published`, `moved`, `deleted`) and, for a move, `previousParent`. A dry run can't show
  this.

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
  site keeps the old hosts until it restarts (in IDE mode the IDE's site saved, and uses them at once).
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
- CMS 13 (applications): the site is named by its display name, id or application name. No SiteUrl: an application's
  URL is its first primary host by name, else its first undefined one (`changes` shows `URL: old → new`);
  `--keep-site-url` and `--https unset` are `usage`. A new host without a scheme or `--https` is http on `localhost`,
  `*.localhost` and loopback addresses, else the scheme of the site's URL. `host add <site> "*"` makes it the default
  application, taking that from the previous default in one save (listed too; the output has `isDefault`, never a `*`
  host). `--type preview|media`: one each per application (preview only on a headless one), refused on CMS 12. No site
  whose start page is below another's.

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
  Truncate (the activity log), Notification Message Truncate, Monitored Tasks Auto Truncate, (CMS 13) Remove Unused
  Content Variations, and Commerce's Remove
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
  and `passwordFile` (the path) in the output, never the password. An `unreachable` error keeps the generated password in
  a `.pending` file beside it and names it (the site may have made the user); only `users remove` deletes it. Errors: `conflict` (name exists), `validation`
  (`details.reason: "users"`, the site's password or name rules, one issue each; nothing is made), `refused` (shared
  database; a site whose CMS users don't come from ASP.NET Identity, naming its user provider). `--dry-run` checks it
  all and writes no file.
- `users remove <name>`: only a user `users add` made (claim `opticli:created` = `true` and an `opticli.localhost`
  address; `refused` otherwise, `not_found` for none); deletes its password
  files too (`passwordFileRemoved`). Roles stay.

### Orphaned content types

Through the site (`serve`; without it `unreachable`, exit 4), with the CMS's `IContentTypeRepository.Delete` and
`IPropertyDefinitionRepository.Delete` (CMS 13: the type saved without the property). Refused against a shared database
(exit 3). Not in `apply`. CMS 13: a type of unknown origin (`originUnknown` in `types --orphaned`: made in admin mode,
or a code type an import overwrote) is `refused` unless `--include-unknown-origin` (`prune` lists it under `kept`), and
its record has `originUnknown: true`; ask the user first.
- `types remove <type>...` (name or GUID): only a type whose class (`modelType`) the running site can't load. `refused`:
  made in admin mode, one of the CMS's own, a class the site loads. `conflict`: content of the type (also in the recycle
  bin), its inline blocks in ContentAreas, page-type values naming it (the CMS would clear them), a property that has it
  as its block type (fine when that property's type is removed in the same run: it goes first). `not_found` with "did
  you mean". All named types are checked first; if one can't go, nothing is removed: `error.details.reason: "orphans"`,
  `details.validation[]` (`property`: the type or `Type.Property`, `message`: why), in the order named; the code is
  `refused` if any is, else `conflict`, else `not_found`.
- `types remove-property <type> <prop>...`: only properties with `existsOnModel: false` on a type defined in code (a
  type made in admin mode is refused, and a property the site's model has). Stored values (any version, language,
  inside a block property, category selections) need `--allow-destructive` (`refused` otherwise, with the counts).
  `values.providers` names content providers (other than the CMS's database) that use it, or `unknown` when the CMS's
  usage check says it is used where the counts see nothing: `conflict`, no override.
- Records: just before each removal the site appends the record (`time`, `project`, `database`, `type` or `property`)
  to `removals.jsonl` in opticli's state directory and echoes it to its output; a failed removal adds a line with
  `failed`. A run that stops halfway (the CMS refuses, values changed since the check, the CLI stopped waiting) fails
  with `error.details.removed` holding the records of what it removed. Text output shows every field in full.
- `types prune [--properties] [--allow-destructive]`: every orphaned type that can go; `--properties` adds every orphaned
  property (without it they are listed under `kept`, since one added in admin mode looks the same).
  `--allow-destructive` without `--properties` is `usage`.
- Output: `types[]` (`id`, `guid`, `name`, `base`, `displayName`, `description`, `modelType`, `properties[]`,
  `allowedChildren`, `availableUnder`), `properties[]` (`type`, `property`: `name`, `dataType`, `typeName`, `blockType`,
  `cultureSpecific`, `required`, `searchable`, `displayEditUi`, `editCaption`, `helpText`, `tab`, `fieldOrder`; `values`:
  `content`, `versions`, `providers`), `kept[]` for prune (`type`, `property`, `code`, `reason`, `values` or `usage`:
  `content`, `inRecycleBin`, `inlineBlocks`, `pageTypeValues`, `pageTypeVersions` (at most 20 refs), `usedBy`,
  `otherUses`), `removed`, `dryRun`, `recordFile`. `meta.warnings`: no
  undo; admin-mode properties look the same; which values were (would be) deleted; another process running the site
  keeps its cached content types until it restarts (in IDE mode the IDE's site removed them).

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
  `database` ahead: the database has what this build lacks. Either that environment runs newer code than this
  checkout (pull and build), or a class or property was removed from code and the CMS kept it for the content that
  uses it (orphaned: `opticli types --orphaned` lists such types; pulling doesn't change those). When only EF Core
  migrations or the CMS schema version are on the database's side, it is newer code: pull and build.
- A content type or property item with `difference: "only in the database"` may carry `removedFromCode`
  (`commit`, `date`, `subject`): this checkout's git history removed its class (or the property) in that commit, and
  `HEAD` doesn't have it. Tell the user so, naming the commit: either the CMS kept the type or property for its
  content (orphaned there), or that environment doesn't run the removal yet; pulling won't help either way, and
  writing with this build doesn't remove it (the site doesn't sync content types into a shared database). Without
  `removedFromCode` (no git, no removal opticli could tie to the type, or more than 50 items or 5 seconds of
  lookups) say both causes are possible and suggest checking `git log` or what is deployed. Don't remove orphans in a
  shared database: `types remove` and `types prune` are refused there anyway. `removedFromCode` is the CLI's: it isn't part of the fingerprint.
- CMS 13: types of unknown origin are listed with `ahead: unknown`, `informational: true`; they count toward neither
  `differences` nor the fingerprint, so they don't stop writes. New job classes the site registers at startup aren't
  drift.

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
`create`, `block` or `upload` with that `id` made. A top-level `"source"` (what `export` writes) is accepted and not read.
- In rich text (and `@file` HTML), `href="$id"` (or `"$id#anchor"`, `"$id?epslanguage=sv"`) links to planned content,
  and `src="$id"` shows a planned image: it is stored as its permanent link, `~/link/<guid>.aspx`, as the CMS does. A block in the text takes `data-contentguid="$id"` (or
  `data-contentlink="$id"`, its id). A link to content a later step creates (or the same step) needs its GUID up front:
  `"guidNamespace"` on the plan or `"guid"` on that step; `data-contentlink` always needs the content to come first.
  The dry run checks the HTML with that GUID, or a stand-in.
- A string value `"@path"` anywhere in `properties` is that file's text, like `Prop=@file`:
  `"MainBody": "@texts/article.html"`. `"@@..."` is a literal `@`.
- `{"op": "upload", "id": "q1", "file": "files/q1.pdf", "parent": "$reports"}`.
- A folder in a page's "For this page" folder: `{"op": "create", "id": "files", "for": "$page", "type": "SysContentFolder",
  "name": "Files"}` (`for` instead of `parent`, as for `block` and `upload`); what goes in it takes `"parent": "$files"`.
- Plan files (`@path` values and upload files) are relative to the plan file (the working directory for `-`) and must
  stay inside its folder, also through symlinks, unless `apply --allow-outside`.
- `{"op": "access", "ref": "$page", "grant": {"Authenticated": "Read"}, "revoke": ["Everyone"], "breakInheritance": true}`
  (also `grantUsers`, `inherit`, `allowUnknownRole`).
- An inline block in a ContentArea: `{"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "type": "TeaserBlock", "values": {"Heading": "Hi"}}`
  (`name` too); see [Inline blocks](#inline-blocks-in-contentareas) for `--update-existing`.
- CMS 13: `"composition"` in a `create`'s or `set`'s `properties` is the whole composition (`"@file.json"` reads it);
  `create` takes `"blueprint"` instead of `"type"`; `"variation"` on `set` and `composition`.
  `{"op": "composition", "ref": "$page", "action": "add", "nodeType": "element", "in": "Left", "value": {"type": "TextElement", "properties": {"Heading": "Hi"}}}`
  (`action`: add with `nodeType`, `in`, `at`, `value` as `get` shows a node; remove and move with `node` (and `in`, `at`);
  set with `node` and `value`: `name`, `displayTemplate`, `displaySettings`, `properties`).
- CMS 13 refuses a save whose rich text links to content that doesn't exist yet, so a link to content a later step
  creates is refused up front: put the linked content first, or add the link in a later `set`.

Every operation is validated before anything is written; on a failure opticli stops and reports what was saved and
how to undo it, also when it is interrupted (Ctrl+C: `cancelled`, exit 130) or something unexpected fails:
`details.partial: true`, `details.operations` with each step's status and undo, `details.created`. The step that was
running when the plan was interrupted, or whose answer timed out (`unreachable`), may still have been saved: check with
`opticli versions <ref>`, and `opticli get <ref> --version latest` for a step that saves into the run's draft (no new
version shows that), before running the plan again. `--publish` on `apply` publishes every operation: only when the
user asked.

One draft per content and language: a `set`, `area` or `composition` step on content and a language that an earlier
step of the same run saved a draft of (set, area, composition, or a create, block, upload or translate that didn't
publish) saves into that draft, as the edit UI does, instead of a new version. The step's result has `intoVersion: true`
and the step `savedInto: {"version": "123_456", "operation": 2}` (the step that made the draft); its `undo` points to
that step's undo, since discarding the draft would drop every change saved into it: a later `discard` without a
version discards them all. A step that publishes, schedules, requests approval, unpublishes, discards or deletes ends
the draft: the next change makes a new one, and the earlier steps' undos point to the step that published (scheduled,
sent for review, discarded) it. `from`,
`baseVersion`, `force` or a ref naming a version always make a new version. When the draft is no longer opticli's
latest unpublished version (someone saved or published in between), the step makes a new version and its warning says
so. Each step still runs and reports on its own. `apply --version-per-step` saves a version per step instead. A site
whose own code asks for a new version on every save (a saving-event handler setting `RequireNewVersion`) makes a save
into the draft fail (`conflict`, nothing saved, the hint names `--version-per-step`): tell the user, and run the rest
with `--version-per-step` if they agree.

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
  was moved elsewhere, is a `conflict`. `area add` of an item that is already there (for an inline block: one of its
  type with the step's values), `translate` to an existing branch
  (it becomes a `set` of its name and properties, or a `publish` without them), `publish` of a published version, a
  `publish: true` that changes nothing on published content, and `delete` of deleted content change nothing.
- Step statuses: `saved`, or `unchanged` when the content was already as the step wants it. `result.existing: true`
  marks updated content. `area remove`/`move` by index are not repeatable; set whole ContentAreas in `properties`
  instead. Content that is no longer in the plan is left alone. An existing upload gets the step's file in a new
  version when the bytes differ (compared by SHA-256, also in the dry run; a warning says so) and is `unchanged` when
  they are the same.
- CMS 13: a whole composition (`"composition"` in `properties`) whose nodes have keys the content lacks makes those
  nodes with those keys (a composition copied from another database keeps its keys), so running it again finds them
  and is `unchanged`. A key must be a GUID.

## Export

`opticli export <ref>... [--descendants] [--languages en,sv | --all-languages] [--out <dir>] [--blobs <dir|https URL>]
[--no-media] [--db <name>]`, or `--site <name>` / `--all` instead of refs. Reads the database only (over SQL), so it
works on production, and writes a folder:
- `plan.json`: `{"source": {...}, "operations": [...]}`; `files/<guid>/<name>`: media files; `texts/`: rich text over
  4000 characters (`"MainBody": "@texts/<guid>.<lang>.MainBody.html"`). `--out` defaults to `export-<database>-<time>`; an
  earlier export's folder (its `plan.json` has export's `source`) is written over, any other non-empty one refused
  (usage), also a folder with a plan of its own.
- What: each ref (`--descendants`: and everything below it); `--site`: the start page and everything below it, the site's
  assets and the global assets (not those folders themselves); `--all`: every site. Blocks, media and folders in a "For
  this page" folder come with their owner, and what is in such a folder with it. Never the root, the recycle bin, asset
  roots, "For this page" folders themselves, content in the recycle bin, blueprints, or content of other kinds than
  pages, blocks, media and folders (`skipped`).
- Branches: all, or the master branch plus `--languages`. A published branch's published version; a branch never
  published: its newest draft, written as a draft; when it is scheduled for publishing at a later time, a `publish`
  step with `publishAt` follows. A newer draft than the published version is left out (`drafts`).

The plan, in order:
1. One step per item, parents first (and a "For this page" folder's owner before its content), then where references
   allow it content before what refers to it: `{"op": "create"|"block"|"upload", "id": "c<guid without dashes>",
   "guid": "<source GUID>", "parent": "$id"|"<GUID>"}` (`"for": "$id"|"<GUID>"` instead of `parent` directly in a "For
   this page" folder, folders too), `type`, `name`, `lang` (master language), `file` (upload), `properties` and `"publish": true` when the
   branch is published (folders never). `properties` has every property of the type (null clears one the target has)
   except values that refer to content created by this step or a later one, plus the built-in settings: `StartPublish`,
   `StopPublish`, pages' `PageURLSegment`, `PageVisibleInMenu`, `SimpleAddress`, `Shortcut`, `ChildSortOrder`,
   `SortIndex`, and `Category` (names). CMS 13: `composition` with every node's `key`, so a second run finds the nodes.
2. `{"op": "set", "ref": "$id", "lang": ..., "properties": {...}}` per item with the values held back (a cycle: A refers
   to B and B to A), publishing the branch when it is published.
3. `{"op": "translate", "ref": "$id", "lang": "sv", "name": ..., "properties": {...}}` per other branch, with its
   culture-specific values and `StartPublish`, `StopPublish`, `PageURLSegment`, `SimpleAddress`, `Shortcut`; on CMS 13 a
   `set` with the branch's `composition` after it (translate takes none). The last step of a published branch publishes it.
4. `{"op": "publish", "ref": "$id", "lang": ..., "publishAt": "<UTC time>"}` per branch scheduled for later in the source.

Values are plain (never `get`'s `{type, value}`): references to planned content are `"$id"`, to anything else the GUID
(never an id), also in local and inline blocks, block lists, ContentReferenceLists, a shortcut's `to` and a
composition's shared blocks (`ref`). ContentArea items: `{"ref": ..., "displayOption", "group", "visitorGroups"}` or an
inline block `{"type", "name", "properties"}`. Links and Url values keep their permanent links (`~/link/<guid>.aspx`).
Rich text: links and images to planned content become `href="$id"` / `src="$id"`, blocks are named by
`data-contentguid` only (`data-contentlink`, an id, is dropped or turned into the GUID). A string value that starts with
`@` is written `@@`.

`source`: `server`, `database`, `role`, `readAt`, `opticli` (version), `cms` (major), `versions` (`{"<guid>": {"<lang>|*":
<version id written>}}`). `apply` accepts it and doesn't read it.

Media: `--blobs <folder>` holds the blob containers (`<folder>/<container>/<file>`, a copy of `App_Data/blobs`);
`--blobs https://<account>.blob.core.windows.net/<container>?<SAS>` reads each file with a GET (the token is never
printed). Without `--blobs`, a local database's files come from the project's file blob folder (appsettings, or
`EPiServer__Cms__FileBlobProvider__Path`); a remote one's media is left out with a warning. Media without its file, or
over 50 MB, is `skipped`; `--no-media` leaves all media out. References to skipped content stay GUIDs.

Output (`data`): `folder`, `plan`, `source`, `items`, `steps`, `kinds`, `types`, `languages` (branches per language, `*`
for none), `files` (`count`, `bytes`, `from`), `texts`, `heldBack` (values a later `set` writes), `skipped[]` (`ref`,
`guid`, `type`, `name`, `reason`), `drafts[]` (`ref`, `guid`, `name`, `language`, `draft`), `externalRefs[]` (content the
plan refers to and doesn't create: `guid`, `ref`, `type`, `name`, `missing`/`deleted` in the source, `placement` when
something is created under it, `from[]`: the target needs it, or the dry run fails with `not_found`), `visitorGroups[]`
(`id`, `name`; null for a role), `dropped[]` (`guid`, `language`, `property`, `reason`: a property the source's code no
longer has, a reference without a GUID (in a ContentArea only that item, `MainArea[2]`), rich text with an inline block
(its values are stored apart from the text, and no write takes them), a composition node whose block isn't stored,
an empty composition on a branch other than the master branch),
`providerRefs[]` (a content provider's content, kept as `id__provider`), `notExported`. Warnings sum up what needs
attention.

## Sync pull

`opticli sync pull --from <name|id> (<ref>... | --site <name> | --all) [--languages en,sv] [--delete] [--overwrite-local]
[--strict] [--blobs <dir|https URL>] [--out <dir>] [--dry-run]`. Needs `serve` on the local database, which must have the
role `local` (else `refused`, `details.reason: "syncTarget"`); `--from` is picked as `--db` is (`--connection` and
`OPTICLI_DB` name the local database). A ref syncs it and everything below it (found in the source).

It exports the source as `export` does (read over SQL only), compares by GUID with the local database and the sync
state (`<user config folder>/sync/<hash>.json`, per project, source and local database: per item and language, the
source's version read and the local version it became, with its saved time), and applies a plan of only what changed, as `apply
--update-existing` (plus a `move` step before an item the source moved, and `delete` steps with `ignoreReferences` for
`--delete`). Per item:

| Source | Local | Last sync | Result |
|---|---|---|---|
| has it | lacks it | not synced (or the sync trashed it) | `created` |
| has it | in the recycle bin | not synced, or the sync trashed it | `restored` |
| has it | in the recycle bin, or deleted for good | synced | conflict ("moved to the recycle bin in the target"); `--overwrite-local`: restored |
| unchanged since | unchanged since | synced | unchanged (not read) |
| unchanged since | changed (a branch saved, moved) | synced | `kept` |
| a branch it didn't have at the last sync | the same branch, made locally | synced | compared: unchanged when the same, else a conflict; `--overwrite-local`: updated |
| changed (version, publish state, place) | unchanged since | synced | compared: `updated` and/or `moved`, or unchanged |
| changed | changed | synced | compared: unchanged when the same, else a conflict; `--overwrite-local`: updated |
| has it | has it | no state yet (the baseline) | compared: `updated`/`moved` (a published local change is written over: the baseline can't tell it from the source's), unchanged, or a conflict when the local branch has a newer draft than its published version |
| gone, or in its recycle bin | has it | synced, or (no state) the GUID is in the source's recycle bin | `--delete`: `trashed` (a conflict when changed locally); else `gone` |
| — | only local, never synced | — | left alone |

Compared means every value the plan would write (with references by GUID), the name and the publish state, per branch;
values only the local model has and the GUID of a "For this page" folder in URLs don't count; a media item's file by
SHA-256 when the local site's file can be read (else the upload goes to the site, which compares it).

What the local database can't take: content of a type its build lacks (`skipped`; types by GUID, then name), a property
it lacks or has as another type (left out; both listed in `model[]`: `type`, `property`, `difference`; `--strict`
fails instead with `conflict`, exit 5, `details.reason: "modelDiffers"`, `details.differences`), a value referring to
content it lacks and the pull doesn't create, or to a visitor group or category it lacks (`dropped`; in a ContentArea
only that item), content whose parent it lacks (`skipped`).

Output (`data`): `source`, `target` (`server`, `database`, `role`), `dryRun`, `baseline` (the first pull from this
source), `state` (the file), `counts` (`created`, `updated`, `moved`, `restored`, `trashed`, `unchanged`, `kept`,
`conflicts`, `gone`, `skipped`), the lists `created[]`, `updated[]`, `moved[]`, `restored[]`, `trashed[]`, `conflicts[]`,
`kept[]`, `gone[]`, `skipped[]` (`guid`, `ref` (the local one), `type`, `name`, `reason`), `dropped[]`, `drafts[]` (newer
drafts in the source, with the source's refs), `model[]`, `files` (`count`, `bytes`, `from`), `steps` (0: nothing to
apply, and nothing was), `plan` (the folder, with `--out`), `run` (`apply`'s report: each step's `status`, `result`,
`undo`), `notSynced`. A dry run validates the plan against the local site and changes neither the database nor the
state. On a failed step or Ctrl+C the error's `details` is the same object, `run` with `partial: true`; the state has
what was saved, so the next pull carries on.

## Sealed apply to production

Production (role `prod`) is written to only by a sealed plan that the user runs in their own terminal. You prepare it:

- `opticli apply <plan> --dry-run --seal-for <name> [--update-existing] [--publish] [--request-approval]` (needs `serve` on
  the local development database, which must have the role `local`, else `refused`, `details.reason: "sealValidation"`;
  `<name>` must have the role `prod`, else `refused`, `details.reason: "sealTarget"`). No plan from stdin,
  `--allow-outside` or `--accept-drift` (`usage`).
- `opticli sync push --to <name> (<ref>... | --site <name> | --all) [--languages en,sv] [--media] [--out <dir>]`: the plan from the local
  database (as `export` writes it, by GUID), with only what differs from production (read over SQL), then sealed as
  above (with `--update-existing`). Output `data.push`: `created[]`, `updated[]`, `moved[]`, `restored[]` (from
  production's recycle bin), `unchanged` (a count), `conflicts[]` (production changed it since the last pull, or it was
  never pulled from there: pull first), `skipped[]` (someone's unpublished changes in production, a parent production
  lacks, a move into a "For this page" folder), `media[]` (left out without `--media`), `dropped[]`,
  `drafts[]`, `steps`, `plan`. No steps: nothing is sealed (`seal` null). `--out` (default `push-<database>-<time>`) must
  stay until the user ran the command. `--media` pushes media production lacks or has otherwise too (an `upload` with its
  GUID, the file copied from the local site's blob folder into `--out`); the user's run then asks for production's blob
  storage. Only for a site whose build has the CMS's Azure blob provider (else `refused`, `sealedPlan`); use it when the
  user wants the media in production too.

What a sealed plan may do: `create`, `block`, `set`, `area`, `composition`, `translate`, `publish`, `move` and `restore`,
with every ref a GUID or `$id` (also `parent`, `for`, `to`, `item`, a blueprint, a ContentArea item's `ref` and the value
of a reference property), every `create`/`block`/`upload` with a fixed GUID (`guid`, or `guidNamespace` and `id`), and
`upload` (also `replace` by GUID) only for a site whose build has the CMS's Azure blob provider (`EPiServer.Azure`, or
`EPiServer.Blobs.Azure` on CMS 13): the run writes files only to production's blob storage, which the user types there.
Not `delete`, `unpublish`, `access`, `discard`, `translate` with `remove`, `includeDraft`, `force`,
`baseVersion`, a version in `publish` or `from` (`"from": "published"` is fine): `refused`, exit 3,
`details.reason: "sealedPlan"`, `details.problems[]` lists each.

Sealing dry-runs the plan against the local site, and fails (nothing sealed) when production's content model differs from
the local database's, or the local site's code differs from its own database (types or properties only in the database:
the run's site would refuse to write against production): `conflict`, exit 5, `details.reason: "modelDiffers"`,
`details.differences[]` (`side`, `type`, `property`, `difference`) or `details.drift`. It reads production's versions of
everything the plan touches. Output (`data`; `sync push` puts it in `data.seal`): `seal` (the id), `file`, `expires` (24
hours), `target`, `plan`, `planHash`, `planFiles`, `options`, `summary` (`steps`, `operations`, `types`, `publishes`,
`existing`, `new`, `touched`, `expiring`: steps that set an expiry that has passed, which takes that content offline,
`media`: upload steps),
`build` (`output`, `built`, `commit`, `uncommitted`), `model`, `validatedOn`,
**`userCommand`** (`opticli apply <plan> --db <name> --seal <id>`), `notChecked`.

Hand the user `userCommand` with what it will do and `notChecked`. Never run it (without a terminal: `refused`,
`details.reason: "sealNeedsTerminal"`, `details.userCommand`), never fake a terminal or feed it input, never touch the
`seals` folder or `seal.key`, and don't change code or rebuild before the user ran it. What the user's run can answer, so
you know what to do next:

| `error` | Means | Do |
|---|---|---|
| `conflict`, `details.reason: "productionChanged"` | Production's model or content the plan touches changed since the seal (`details.changes[]`) | Pull, check, seal again |
| `conflict`, `"planChanged"` / `"buildChanged"` | The plan, a file it reads, or the site's build changed since the seal | Seal again (and don't change them before the run) |
| `conflict`, `"sealExpired"` | Older than 24 hours | Seal again |
| `refused`, "loads opticli's agent from `opticli ide on`" (also when sealing) | The build is from IDE mode: its runtimeconfig.json names opticli's agent copy | Ask the user to run `opticli ide off` and rebuild (or do it when they agree), then seal again |
| `refused`, `"sealUsed"` | A seal runs once | Seal again |
| `refused`, `"sealTampered"`, `"sealTarget"`, `"sealProject"`, `"sealPlan"` | The seal was edited, or run against another database, project or plan file | Seal again; run exactly `userCommand` |
| `drift` | The site's code differs from production's database | Tell the user; nothing was written, and a sealed run accepts no drift |
| `login_failed` | The write login was refused; nothing written, the seal unused | The user runs it again |
| `refused`, `"eventBus"` / `"blobStorage"` | The event bus's topic is missing or has no subscriptions, the container is missing, or the run's site didn't use them (then the seal is used) | The user checks the topic or container production uses, or runs without the event bus (Enter); seal again if it was used |
| `refused`, `"sealedPlan"` at the run | The plan uploads and the user gave no blob storage (Enter), or the build has no Azure blob provider | Seal a plan without uploads; the files go through production's edit UI |
| `conflict`, `"blobMissing"` | Saved, but an uploaded file isn't in the container or couldn't be checked (`details.media[]`, `problem`) | Tell the user: production's database points at it; check it and upload it in the edit UI |

**Production's event bus and blob storage.** When the site's build has the CMS's Azure providers, the user's run asks
for them at the terminal (never from an option, variable or setting; never written anywhere): the event bus is optional
(production's servers then drop their caches at once), blob storage is needed when the plan uploads. You never ask the
user for these connection strings and never pass them anywhere: the run asks. The result's **`restartNeeded`** says
whether production's servers still need a restart: false only when the run's cache drops were sent and counted on the
topic (`events.sent`); otherwise (no event bus, or something uncertain: `events.problem`) tell the user to restart the
site (every instance). `media[]` lists each uploaded file as found in the container (`blob`, `bytes`, `stored`, or
`problem` when it couldn't be checked). A run that stopped part way has `events`, `media` and `restartNeeded` in its
error's `details`, beside `operations`.

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
  refuses a remote database that isn't the development one. A string from `--connection`/`OPTICLI_DB` can't be
  chosen: the hint says how to add it to a launch profile, user secrets or `appsettings.Development.json`, so that
  `opticli db use <id>` can choose it. Pass that on to the user; adding it and choosing it are theirs to do.
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
  left out unless `--include-connection`. The scheduler stays as the site sets it (`OPTICLI_SCHEDULER=on`). Refused
  (`usage`) while IDE mode is on.

## IDE mode

The site the user starts from their IDE (Rider's run or debug, Visual Studio, `dotnet run`, `dotnet watch`) loads the
agent from its own build, so writes go through that process and show there at once (no second site with its own
cache, no restart). Only turn it on or off when the user asks: it edits their `.csproj.user` (and `.git/info/exclude`).

- `opticli ide on [--configuration Debug]`: writes a labelled `ItemGroup` (`Label="opticli ide"`) to
  `<Project>.csproj.user` that names opticli's copy of the agent (`<state dir>/agent/<version>-<hash>/cms<major>/`) as the
  build's startup hook (`RuntimeHostConfigurationOption` `STARTUP_HOOKS`, plus `OptiCli.IdeFile`, and
  `OptiCli.RemoteDb` for a remote development database). Debug builds only, never when publishing, and only while the
  copy exists. A `.csproj.user` git tracks is `refused` (exit 3); one git doesn't ignore goes into `.git/info/exclude`.
  Output: `userFile`, `created`, `agent`, `agentVersion`, `configuration`, `ideFile`, `database` (`server`, `name`,
  `remote`), `git` (`ignored`, `excluded`, `notIgnored`, `noRepository`), `excludeFile`, `prunedCopies`,
  `rebuildNeeded`, `next`. Then the user rebuilds (Rebuild in Rider or Visual Studio: Run's up-to-date check doesn't
  notice the changed `.csproj.user`) and starts the site from the IDE.
- `opticli ide status`: `on`, `agent`, `agentExists`, `agentVersion`, `agentCurrent` (false: from another opticli
  version, `ide on` again), `configuration`, `remoteDatabase`, `build` (`output`, `hasHook`: false means rebuild),
  `git`, and `site` (what `serve --status` reports). `meta.warnings` says what to do.
- `opticli ide off`: removes the block (and the file when nothing else is in it) and the exclude line. `rebuildNeeded`:
  the current build still loads the agent until it is rebuilt.
- The running site writes `<project>.ide.json` (pid, token, addresses) to the state directory once it listens, and
  removes it when it stops; opticli ignores and removes one whose process is gone (the IDE's stop kills the site). The
  agent is reached on the site's own addresses: HTTP on this machine first, else HTTPS on `localhost` with the
  development certificate.
- `serve --status` (and `doctor`) reports it as `mode: "ide"`, and every write goes there while it runs. `serve` then
  starts nothing (a warning says so); `serve --stop` never stops it. `unresponsive` with "paused in the debugger":
  the user has it stopped at a breakpoint; ask them to continue it. Without the IDE's site running, `serve` works as
  usual (it runs the same build, with the copy that build names).
- Sealing for production, and the user's sealed run, refuse a build from IDE mode (exit 3): `ide off`, rebuild, seal again.
- It never changes how the site starts: no pin, and the scheduler, schema updates and content type sync stay as the
  site sets them. Against another database, or outside Development, the agent stays off for the run (`[opticli] IDE
  mode: the agent is off for this run. <why>` in the site's output) and the site starts anyway.
- After updating opticli, or after `db use` changes the development database: `ide on` again, then rebuild.

## Troubleshooting

| Symptom | Do |
|---|---|
| `not_found`: no CMS project / several candidates | `cd` into the site's repository, or pass `--project path/to/Site.csproj`. |
| No connection string | `opticli doctor`; `--connection-name` if the site's connection string has another name. |
| Changes don't show in the site the user runs from their IDE until they restart it | That site isn't the one opticli wrote through (`serve`'s is). Tell the user about IDE mode (`opticli ide on`, then rebuild and restart from the IDE); don't turn it on unasked. |
| IDE mode on, but `serve --status` shows no `mode: "ide"` site | `opticli ide status`: rebuild when `build.hasHook` is false, `ide on` again when `agentCurrent` is false; the site's output says why when the agent stays off (another database, not Development). |
| `needs_selection` (exit 6) | Show the user `error.details.choices`, ask which is their development database, run `opticli db use <id>`. |
| `refused` (exit 3), `details.reason: "prodDatabase"` | The database is production: opticli only reads it. Tell the user; don't try another way to the same database (another `--db`, `--connection`, spelling, `OPTICLI_DB`, config edit, another user config through `XDG_CONFIG_HOME`/`APPDATA`). To write there, prepare a sealed apply for the user to run ([Sealed apply to production](#sealed-apply-to-production)). From a command without `--db`: the development database (saved, configured or the site's default) is production; the user chooses another with `opticli db use <id>`. |
| `refused` (exit 3), `details.reason: "sealNeedsTerminal"` | A sealed apply runs only in the user's own terminal. Give the user `details.userCommand`; never run it yourself or fake a terminal. |
| `refused` (exit 3), `details.reason: "roleLowered"` | Giving a database a weaker role needs the user's own terminal. Tell the user; never fake a terminal. |
| `refused` (exit 3) | A safety rule: serving against a remote database that isn't the development one, SQL the `sql` guard won't run, or a request the site agent won't do (e.g. moving or deleting a start page or a folder that contains one). Don't work around it; tell the user. |
| `unreachable` (exit 4) on a remote database | Firewall (Azure SQL allows only listed client IPs), VPN, or no Entra ID token: `az login`. Tell the user. |
| `login_failed` (exit 4) | The server was reached but refused the login. Tell the user who was refused (`error.details.identity`: UPN or object id): for Entra ID, that identity has no user in the database, which a database admin adds, or the user signs in as another identity (another account for the same subscription in its own `AZURE_CONFIG_DIR`, see the README); otherwise the password or the database name is wrong. Don't retry. |
| `unreachable` (exit 4) on a write | The site isn't running: `opticli serve`; if it was, `opticli serve --status` and `--logs --tail 80`. If the write timed out ("no response within"), it may still have been saved: `opticli versions <ref>` before you retry. |
| `serve` against a Microsoft Entra ID database (`Authentication=Active Directory Default`) fails to sign in (the site's log shows a credential or token error) while opticli's reads work | opticli doesn't set this for you; decide per case. The site's own `DefaultAzureCredential` may try another credential before the Azure CLI (a managed identity, Visual Studio, environment variables). Try one run with `AZURE_TOKEN_CREDENTIALS=AzureCliCredential opticli serve` (the site inherits it). It needs Azure.Identity 1.15 or newer in the site's build output (`Azure.Identity.dll`); 1.14 accepts only `dev` (developer tools, the Azure CLI among them) and fails on any other value; older versions ignore it. If it helps, tell the user; leave it to them whether to export it in their shell. |
| `serve` times out or exits | A crash's message names the site's first exception (also `error.details.firstException`), usually the cause; otherwise `opticli serve --logs --tail 80`. Typical: the site needs a build (`--build`), a port is taken (`--port`), or the site's own startup fails. |
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
| `refused` (exit 3): LocalDB "only runs on Windows" | The site's connection string is the CMS template's LocalDB one, which Linux and macOS can't use (the site can't either). Tell the user: point it at a SQL Server database (e.g. in a container), or pass `--connection`. |
| `validation` with `code: "unresolvedReference"` (CMS 13) | Rich text or a reference points at content that doesn't exist. Link existing content, or create the target first. |
| `refused` from `serve`: CMS 13 build, CMS 12 database | Starting it would upgrade the database for good. Tell the user; only they decide to upgrade it (by starting the site themselves). |
