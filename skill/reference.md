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
`%APPDATA%\opticli\config.json` on Windows), else the first of `launchSettings.json` profiles, user secrets,
`appsettings.Development.json`, `appsettings.json` (ASP.NET Core's order) if it is local. A
remote default, disagreeing launch profiles, or only other environments' `appsettings.{Env}.json` files give
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
| `children <ref>` / `ancestors <ref>` | One level down / the path from the root. |
| `find --type T [--where ...] [--under <ref>] [--status published\|draft\|any] [--lang]` | `--where` is repeatable: `Prop=value` exact, `Prop~value` contains, `Block.Prop=...` inside a local block, `Name~...` on the name, `Area=<ref>` for ContentAreas/references containing that content. Deleted items excluded. |
| `search <text> [--in names\|strings\|all] [--lang]` | One row per item and language with `matches[]` (`property`, `snippet`). Capped at 2000 values per source (`meta.warnings`). |
| `where-used <ref> [--pages]` | Rows: owner identity + `saved`, `changedBy` + `property` (e.g. `MainArea`, `Hero.Link`, `MainArea[2].Text`), `kind` (`contentArea`, `contentReference`, `contentReferenceList`, `richTextLink`, `richTextBlock`, `link`, `linkCollection`, `url`, `text`, `softlink`), `sources` (`softlink` = CMS link index, `property` = value scan). Deleted owners last, `deleted: true`. The value scan reads each branch's primary values (published, else the latest draft); the link index covers saved versions. `--pages` follows block owners up to the pages (rows get `via`: the blocks in between), newest `saved` first. |
| `versions <ref> [--lang]` | Newest first: `ref` (`id_version`), `language`, `status`, `name`, `saved`, `changedBy`, `startPublish`, `primary`. |
| `drafts [--since <date>] [--by <user>] [--kind K] [--type T] [--lang]` | One row per item and language with unpublished changes: `status` and `version` of the newest draft, `saved`, `changedBy`, `drafts` (unpublished versions newer than the published one). `--since` is UTC. |
| `blob <ref>` | Media only: blob URI, file path on disk, `exists`; same for the thumbnail. |
| `access <ref>` | `inherited`, `from` (the item the entries are stored on: itself, or the nearest ancestor with its own), `entries[]` (`name`, `kind`: role, user or visitorGroup, `levels`: `FullAccess` or e.g. `["Read","Edit"]`, `mask`). Read from the database; no `serve` needed. |
| `sql "<SELECT ...>" [--limit N] [--full] [--include-personal-data]` | One SELECT/WITH statement, run in a rolled-back transaction; returns 100 rows unless `--limit` (`truncated: true` when there were more); `--jsonl` prints rows. Forms submissions and user/membership tables need `--include-personal-data`. Put a space between a number and a following word (`1 AS x`, not `1AS x`). |

### Shape of `get`

```json
{"ref":"123","guid":"...","type":"ArticlePage","name":"News","language":"en","status":"published",
 "url":"/en/news/","kind":"page","version":"123_456","masterLanguage":"en","languages":["en","de"],
 "parent":"45","saved":"2024-05-01T10:00:00Z","changedBy":"editor","startPublish":"...",
 "properties":{
   "Heading":{"type":"String","value":"Hello","culture":"en"},
   "MainArea":{"type":"ContentArea","value":[{"ref":"789","type":"TeaserBlock","name":"Teaser","displayOption":"wide"}]},
   "Hero":{"type":"Block","blockType":"HeroBlock","value":{"Heading":{"type":"String","value":"..."}}}}}
```

`culture` on a top-level property is the branch its value came from (shared properties come from the master
language). Empty properties are omitted unless `--all-properties`. Rich text gives the (possibly truncated) HTML
plus its resolved links and embedded blocks.

## Write commands (need `opticli serve`)

All take `--dry-run`. `set`, `create`, `area`, `block create`, `translate` save a draft unless `--publish`.
`--lang <code>` picks the branch. Output: `ref`, `version` (the new version ref), `baseVersion`, `status`, `saved`,
`published`, `valid`, `changes[]` (`property`, `before`, `after`), `validation[]`. A saved draft becomes the
primary draft, the version edit mode opens.

| Command | Example |
|---|---|
| `set <ref> Prop=value... [--values json] [--name N]` | `opticli set 123 Heading="New title" --dry-run` |
| `create <parent-ref> --type T --name N [Prop=value...]` | `opticli create 45 --type ArticlePage --name "News" Heading=Hi` |
| `area <ref> <Prop> add <block-ref> [--at N] [--display opt]` | `opticli area 123 MainArea add 789 --at 0` |
| `area <ref> <Prop> remove <position\|ref:id>` | `opticli area 123 MainArea remove ref:789` |
| `area <ref> <Prop> move <position\|ref:id> <to>` | `opticli area 123 MainArea move 0 2` |
| `block create --type T --name N (--for <page-ref> \| --parent <folder-ref>)` | `opticli block create --type TeaserBlock --name Teaser --for 123` |
| `translate <ref> --lang <code> [--name N] [Prop=value...]` | `opticli translate 123 --lang de --name "Neuigkeiten"` |
| `publish <ref> [--version id]` | `opticli publish 123_456` (only when the user asked) |
| `move <ref> --to <parent-ref>` | `opticli move 123 --to 45` |
| `delete <ref>` | `opticli delete 123 --dry-run` (recycle bin; only when the user asked) |
| `access <ref> [--grant Role=Levels] [--user Name=Levels] [--revoke Name] [--break-inheritance \| --inherit]` | `opticli access 123 --break-inheritance --revoke Everyone --grant Authenticated=Read --dry-run` (only when the user asked) |
| `apply <plan.json\|->` | `opticli apply plan.json --dry-run` |

`move` and `delete` refuse start pages, site and asset roots, the recycle bin and anything that contains them.
`--dry-run` on `publish`, `move` and `delete` checks the arguments and the item without asking the site.

Positions in `area` are zero-based; a plain number is a position, `ref:789` (or a GUID) names the item by the
content it shows. `--at` and `--display` only apply to `add`.

### Property values

- `Prop=value`: a string, parsed by the CMS the way it parses imported values (numbers, booleans, dates, a content
  id for a reference, HTML for rich text).
- `Prop=` clears the property. `Prop=@file.html` reads the value from a file (`@@` for a literal `@`).
- `Block.Prop=value` sets a property of the local block property `Block`.
- `--values '<json object>'` is merged on top, for structured values:
  - ContentArea: `{"MainArea":[{"ref":"456"},{"ref":"789","displayOption":"wide"}]}` (replaces the whole area;
    use `area` to add/remove single items). An item may name its content by `"guid"` instead of `"ref"`.
  - Local block: `{"Hero":{"Heading":"Hi","Link":"/en/about/"}}`.
  - Content provider content (e.g. DAM images, shown by `get` as `63__provider`) works wherever a content ref does:
    `HeroImage=63__provider`, a ContentArea item `{"ref":"63__provider"}`, or its GUID.
  - Link (`LinkItem`): `{"Button":{"href":"456","text":"Read more"}}`, optionally with `title` and `target`. `Button=456` or `Button=/en/about/` changes
    only the href and keeps the text. Link collections take an array of the same objects. A content ref as `href`
    is stored as a permanent link, like the editor stores it.
- Property names are checked against the content type before anything is sent; a typo fails with a suggestion.

### Access rights

- Levels: a comma list of `Read`, `Create`, `Edit`, `Delete`, `Publish`, `Administer`, or `FullAccess`.
  `--grant Role=Levels` (roles) and `--user Name=Levels` (users) set that entry to exactly those levels;
  `--revoke Name` removes the entry. Options repeat; revokes apply before grants.
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

### Plans (`apply`)

```json
{"operations": [
  {"op": "create", "id": "page", "parent": "45", "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
  {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
  {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}]}
```

Ops: `set`, `create`, `area`, `block`, `translate`, `publish`, `move`, `delete`, `access`, with the same fields as
the commands (`opticli apply --help` lists them). An `access` step:
`{"op": "access", "ref": "$page", "grant": {"Authenticated": "Read"}, "revoke": ["Everyone"], "breakInheritance": true}`
(`grantUsers` for users, `inherit`, `allowUnknownRole`). `"$id"` refers to what an earlier `create`/`block` with that `id` made.
Every operation is validated before anything is written; on a failure opticli stops and reports what was saved and
how to undo it. `--publish` on `apply` publishes every operation: only when the user asked.

## serve and env

- `opticli serve [--build] [--port N] [--foreground] [--output <dll>] [--timeout s]`: runs the existing build
  output (`bin/Debug/<tfm>/<Site>.dll`) with the site agent injected through `DOTNET_STARTUP_HOOKS`, in Development, on
  `http://127.0.0.1:<port>` (default 5199, else the first free port up to 5299), waiting up to `--timeout`
  (default 180 s) for it to answer. A warning says when sources are newer than the
  build; `--build` runs `dotnet build` first. The site's code and files are not changed.
- `opticli serve --status | --logs [--tail N] | --stop`.
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
| `conflict` (exit 5) | A newer version exists: `opticli versions <ref> --limit 3`, then re-run. |
| Values look cut off | `truncated: true`: use `get <ref> --fields Prop` or `--full`. |
