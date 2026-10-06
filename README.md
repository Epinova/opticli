# opticli

A command-line tool for reading and changing the content of an **Optimizely CMS 12** site you develop on your own
machine. It is built for AI coding agents (Claude Code and similar) as much as for people. It answers the questions
you would otherwise answer with hand-written SQL or by clicking through the edit UI: what content exists, what a page
contains, which C# class and view render it, where a block is used, what is unpublished. It can also make changes
(set properties, add blocks to a ContentArea, create, translate, publish), as drafts by default.

For editors, the separate `OptiCli.Mcp` package adds an MCP server to the site itself, so they can work on its content
with Claude: see [MCP server for editors](#mcp-server-for-editors-preview).

opticli is an independent open-source project. It is not affiliated with or endorsed by Optimizely.

## How it works

```
reads:   opticli ──fixed read-only SQL──▶ the site's CMS database
writes:  opticli ──HTTP on 127.0.0.1 + per-run token──▶ site agent inside your running site ──IContentRepository──▶ database
```

- **Reads** query the site's SQL Server database directly with fixed queries. Nothing needs to be running.
  Content comes back with every property decoded (ContentAreas, local blocks, links, rich text). Content types come
  with their C# class files and Razor views, found by scanning the site's source.
- **Writes** go through the CMS's own API, so versioning, validation, caches and events behave as they do in the
  edit UI. `opticli serve` starts the site's existing build output with the **site agent** injected at startup: a
  small assembly that adds a few HTTP endpoints under `/_opticli/`. The site's code and files are not changed.
- Output is compact JSON when stdout is redirected (agents, pipes) and tables on a terminal. Every item has a
  `ref` you can pass back to another command.

In this README, "site agent" means that injected assembly. "Coding agent" means an AI assistant that runs opticli.

## Requirements

- The **.NET 8 SDK** or newer to install opticli. It runs on the newest .NET runtime installed.
- An **Optimizely CMS 12** site (`EPiServer.CMS.AspNetCore` 12.x) whose repository you have checked out.
- Its database on **SQL Server**: a local instance, LocalDB, a container, or a remote development database such as
  Azure SQL. For Microsoft Entra ID authentication (`Authentication=Active Directory Default`), sign in with
  `az login` first.
- For writes only:
  - The site must build and start locally in the `Development` environment, with whatever it needs to start (its
    own secrets and services).
  - The site must run on **.NET 8 or newer**, since the site agent is built for .NET 8. Reads work for any CMS 12 site.

opticli is developed on Linux. Paths and process handling for Windows and macOS are covered, but get less testing.

## Install

Install opticli from NuGet as a global .NET tool:

```sh
dotnet tool install -g OptiCli
opticli --version
```

To update, run `dotnet tool update -g OptiCli`. [CHANGELOG.md](CHANGELOG.md) lists what changed in each release.

To install from source instead, build the package and install it from the output folder:

```sh
git clone https://github.com/epinova/opticli.git && cd opticli
dotnet pack src/OptiCli -c Release -o artifacts
dotnet tool install -g OptiCli --add-source ./artifacts
```

To update, run `dotnet tool update -g OptiCli --add-source ./artifacts` after a new `pack`.
To run from source without installing: `dotnet run --project src/OptiCli -- <command>`.

## Quick start

Run opticli from anywhere inside the site's repository. It walks up to the solution and picks the web project that
references `EPiServer.CMS.AspNetCore`. It then finds that project's development database, as described under
[Which database](#which-database).

```sh
opticli doctor                                      # what was found and where; does the database answer?
opticli types --kind page                           # page types and how many pages use each
opticli type ArticlePage                            # properties, C# class file, views
opticli resolve https://www.example.com/en/news/    # which content a URL shows
opticli get 123 --fields Heading,MainArea           # one item, decoded
opticli where-used 456                              # every page and block that references content 456
```

A change, end to end:

```sh
opticli serve                                       # start the site with the site agent (background, 30-60 s)
opticli set 123 Heading="New title" --dry-run       # validate; show before and after
opticli set 123 Heading="New title"                 # save a new draft version
opticli get 123 --version latest --fields Heading   # check the draft
opticli serve --stop
```

When stdout is redirected, every command prints a single JSON line:

```json
{"ok":true,"data":{"ref":"123","type":"ArticlePage","name":"News","status":"published","url":"/en/news/",
 "properties":{"Heading":{"type":"String","value":"Hello"}}},"meta":{"source":"db","version":"0.12.0"}}
```

## Using opticli with coding agents

opticli ships a [Claude Code skill](skill/SKILL.md) that tells an agent when to use opticli and how to use it
safely. [skill/reference.md](skill/reference.md) has the details it needs for writing content.

```sh
opticli skill install          # into ~/.claude/skills/opticli/
opticli skill install --repo   # into <repository>/.claude/skills/opticli/, to share it through git
```

For other agents, add the skill text to their instruction file, e.g.
`opticli skill print >> AGENTS.md` (and `opticli skill print reference.md` for the details). `opticli doctor` warns
when an installed skill is older than the opticli you run. Run `skill install` again after updating opticli. It
replaces a copy it installed itself, and asks for `--force` only when the files were edited since (or were installed
by an opticli older than 0.4, which kept no record of them).

The skill sets these rules for agents:
- Never publish or delete unless the user asked for it.
- Dry-run first.
- Start `serve` only when writing.
- Never choose the development database for the user.

opticli enforces its own safety rules either way (see [Safety model](#safety-model)).

## Which database

opticli works with the project's **development database**: the one the site uses when you run it locally in
`Development`.

1. opticli reads the connection string (`EPiServerDB` unless `--connection-name` says otherwise) from the site's
   Development configuration, in ASP.NET Core's order. The first source that has it wins:
   - launch profiles;
   - `ConnectionStrings__EPiServerDB` (or `ConnectionStrings:EPiServerDB`) exported in the shell, shown as source
     `environment`. A launch profile that sets the same variable replaces it when the site runs with that profile,
     so profiles come first;
   - user secrets;
   - `appsettings.Development.json`;
   - `appsettings.json`.

   If that database is **local**, opticli uses it without asking.

   User secrets are found by the project's `UserSecretsId`. opticli reads it, like `TargetFramework` and
   `AssemblyName`, from the project file and the nearest `Directory.Build.props` (and the files that one imports).
   The project file wins. `$(Name)` references to properties set earlier are expanded. Conditions are evaluated
   when they are simple comparisons or `Exists(...)`, with `Configuration` taken as `Debug`. A value opticli can't
   expand is left as written (such a `UserSecretsId` reads no user secrets), a condition it can't evaluate is
   skipped, and `doctor` warns about both.
2. Otherwise opticli asks once. That happens when:
   - the connection string points at a remote server such as Azure SQL;
   - several launch profiles (or exported variables) disagree;
   - only other environments' `appsettings.{Env}.json` files have one.

   On a terminal it shows a numbered list. Anywhere else, commands fail with `needs_selection` (exit 6), and
   `error.details.choices` lists the options, so a coding agent can ask the user and run `opticli db use <id>`.
3. The choice is saved in the user config file (see [Files](#files)) and holds until that setting changes. If the
   server or database at that source changes, opticli asks again.

```sh
opticli db list               # every connection string: id, server, database, local?, where it was found
opticli db use a71c3f         # make it the development database (no id on a terminal: pick from a list)
opticli db forget             # back to the default
opticli get 123 --db e02d9b   # another database, for one run (a remote one is flagged in meta.warnings)
```

When the database in use is remote, every response carries `meta.database` (`server`, `name`, `local`,
`development`). A remote database that isn't the development one also adds a warning.

`serve` runs the site only against a local database or the chosen development database. It sets
`ConnectionStrings__<Name>` to that database and removes other spellings of it inherited from the shell, so an
exported one can't win over the pin. It turns the site's scheduler off for every run (see
[Scheduled jobs](#scheduled-jobs)), and against a remote one it also turns off, for that run:
- automatic schema updates;
- content type sync;
- the remapping of Dynamic Data Store types whose properties changed.

So the CMS doesn't change a shared database just because a local build starts. `serve` also checks, before it
starts the site:
- EF Core migrations. A migration in the build that the database's `__EFMigrationsHistory` lacks would be applied
  by a site that migrates at startup, so `serve` refuses (exit 3) unless `--allow-pending-migrations`.
- The CMS schema version. When the database's doesn't fit the build's EPiServer packages, the CMS won't start, and
  `serve` says which side to update. EPiServer.Framework 12.17 and later accept a schema one version newer; earlier
  ones (and a version opticli can't read) are taken not to.

Once the site answers, `serve` reports drift: what differs between the build and the database (see
[Shared databases](#shared-databases)).

## Safety model

| Rule | Enforced by |
|---|---|
| A remote database is used only when the user chose it as the development database, or for one run with `--db` or `--connection`, with a warning on every response. Only literal loopback names (`localhost`, `127.0.0.1`, `::1`, `.`, `(local)`) and LocalDB count as local. A host name that resolves to 127.0.0.1 counts as remote. | CLI, before connecting |
| The site started by `serve` is pinned to the database the CLI reads. It refuses to start against any remote database except the approved development one. There it runs without scheduler, schema updates, content type sync or store remapping, and `serve` refuses a build with EF Core migrations the database lacks. If the site turns hosting startups off in its code, so the pin can't run, it refuses to start. | CLI + site agent at startup |
| The site agent answers only loopback callers that send the per-run token, and only in `Development`. The token is kept in a state file only your user can read. | Site agent, per request |
| Writes create drafts; publishing needs `--publish` (or `publish`). Saves are attributed to the user `opticli`. | CLI + site agent |
| A publish that would also put live changes someone else saved after the published version stops: on a terminal it shows who saved what and asks, elsewhere it fails with `conflict` (exit 5) listing them. `--include-draft` (a plan step's `"includeDraft": true`) confirms; `publish --version <id>` publishes that version as it is; `--from published` bases the change on the published version, leaving them out. | Site agent |
| Against a shared database, writes stop while the build and the database differ (drift): on a terminal it shows the differences and asks, elsewhere it fails with `drift` (exit 5). `--accept-drift <fingerprint>` confirms; the fingerprint stops counting when the differences change. | Site agent, CLI first |
| Against a shared database, `sites primary` and `sites host remove` are refused (exit 3), and `sites host add` only adds hosts of type undefined: the deployed site uses the same site definitions. There is no override; change those sites in their own admin UI. Site definitions are never reachable from the MCP module. | Site agent, CLI first |
| `delete` moves content to the recycle bin; nothing empties it, and `restore` brings it back. Site roots, start pages, asset roots and anything above them can't be moved or deleted. The MCP module doesn't restore. | Site agent |
| `serve` turns the site's scheduler off, so a restored database's overdue jobs don't all start (`--scheduler` leaves it on). `jobs run` refuses jobs that delete for good (emptying the recycle bin, trimming versions, truncating the change log, Commerce's expired carts and archived items, ...) or move content across the site, and `jobs set` refuses to let the scheduler run one (sooner), without `--allow-destructive`. Against a shared database jobs aren't run, stopped or changed, and the scheduler stays off. | Site agent, CLI first |
| `users add` only adds users to the site's ASP.NET Identity, tagged as made by opticli, and `users remove` only removes those: an existing account's password, address and roles are never touched. A generated password is written to a file only you can read and never printed. No command lists users; `users roles` counts them per role. Refused against a shared database, and on a site whose CMS users come from elsewhere (OpenID Connect). | Site agent, CLI first |
| Reads use fixed queries. `sql` accepts a single SELECT, refuses anything that writes, runs code, reaches another database or reads server-wide views, logs and traces (in `sys`, only the views that describe the database's own schema), and always runs in a rolled-back transaction. Personal-data tables (form submissions, users) need `--include-personal-data`. Where another command's fixed query reads the Dynamic Data Store's tables, it reads only one store's rows and only what it shows (the parents the CMS stored for `trash`, visitor group names), nothing that names a person. | CLI |
| Passwords are never printed; `doctor` redacts connection strings. The exception is `opticli env`: it prints the per-run token, and with `--include-connection` the connection string too. | CLI |

Things these rules can't see:
- A port-forward or tunnel on `localhost,<port>` to a remote server looks local to opticli.
- So does a hosts-file entry or SQL client alias that maps a loopback name elsewhere.

Don't point opticli at such a connection unless you mean to write to what is behind it.

### Shared databases

A remote development database is often shared: with other developers, and with the environment it belongs to. The
database doesn't say which build or commit is deployed against it. So opticli checks whether the database matches
what the local build expects, which is what matters before a write:
- content types and properties in the code but not in the database, and the reverse (properties added in admin
  mode don't count);
- a property's type or culture-specific setting, and renames a migration step hasn't applied yet;
- EF Core migrations, Dynamic Data Store types and the CMS schema version.

Each difference says which side is ahead. `local`: your branch has changes that aren't deployed there (check out what
is deployed, or deploy first). `database`: the environment runs newer code than your checkout (pull and build).
`unknown`: they differ, and the database doesn't say which side changed. Required, display names, sort order, tabs
and `[AllowedTypes]` aren't stored for a model: the running code decides them, so they never show as drift.

`opticli drift` lists the differences and `doctor` shows them. Once `serve` has reported drift, a short warning comes
with every command that uses the database, and with `serve --status`. A site you start yourself with `opticli env`
reports drift only through `opticli drift` and the write errors. While there are any differences, every write stops, not only those to a type that differs: the risk is the build as a whole (its event
handlers and validators), not only its models. Reads keep working, since they don't run the site's code. Restart
`serve` to compare again after a deploy or a pull.

What opticli can't turn off is the site's own startup code. Against a shared database, a local build still:
- runs `Database.Migrate()` if the site calls it at startup (the reason for the migration check);
- registers its scheduled jobs (new jobs get rows, changed ones are updated), even with the scheduler off;
- creates the content root folders that add-ons register at startup (`IContentRootService`), and Dynamic Data Store
  stores when they are first used, if they don't exist yet;
- runs initialization modules and other startup code that writes, such as a site that creates its own content.

So use a SQL login without DDL rights (no `db_ddladmin` or `db_owner`) for a shared database where you can. Schema
changes then fail instead of reaching everyone. Some add-ons create or alter their own tables, views or stored
procedures every time the site starts, and a site with those may not start with such a login. Check what your
add-ons need first.

## Commands

`opticli <command> --help` lists every option with an example, and a bare `opticli` shows an overview.

### Reading

| Command | Answers |
|---|---|
| `doctor` | project, connection string candidates, database, schema version, site agent, drift (against a shared database), sites whose primary host differs from the saved `sites primary` mapping, installed skill |
| `db list`, `db use`, `db forget` | the development database (see [Which database](#which-database)) |
| `sites`, `languages` | site definitions and hosts; language branches |
| `types [--kind] [--unused] [--orphaned] [--sort]` | content types with instance counts; `--orphaned`: types whose class is gone from the code, which the CMS keeps while content uses them (the running site checks every class; without `serve`, the site's sources are scanned for its own types) |
| `type <name>` | properties (type, culture-specific, required, tab, order, source line, `[AllowedTypes]`; one that isn't in the code with how many values it has stored), C# class file, views |
| `allowed-in <type>` | which ContentArea/reference properties accept a type, from `[AllowedTypes]` in code |
| `get <ref> [--lang] [--version] [--fields] [--expand]` | one item, typed and decoded (ContentAreas, local blocks, rich-text links) |
| `tree`, `children`, `ancestors` | the content tree |
| `find --type T [--where Prop=value] [--under] [--status]` | items of a type, filtered; `--status scheduled` (a version waits to be published: `publishAt`) and `expired` (published, stop-publish date passed: `expiredAt`) |
| `search <text> [--in names\|strings\|all]` | names and text properties containing a string |
| `where-used <ref> [--pages]`, `where-used --type T` | ContentAreas, references, links and rich text pointing at an item (`--pages`: through nested blocks up to pages); `--type`: for every instance of a type |
| `resolve <url>`, `url <ref>` | URL to content, and content to URL per language |
| `versions <ref>`, `drafts [--since] [--by] [--kind] [--type]` | version history; unpublished changes |
| `history <ref> [--since] [--by]` | the CMS's change log for one item: creates, publishes, moves (from, to), deletes and restores, with who and when; the only record of moves and deletes (drafts saved aren't in it) |
| `categories`, `visitor-groups` | the category tree (names `set` takes, selectable, how many items use each); visitor groups (ids ContentAreas store, names, how criteria combine), without their criteria or notes |
| `projects [<id>]` | projects, and the versions in one |
| `trash [--since] [--by] [--type]` | what is in the recycle bin: who deleted it and when, what came along (`descendants`), and `originalParent`, where `restore` puts it back |
| `jobs [--failed]`, `jobs log [<job>] [--failed] [--since]` | scheduled jobs: schedule, next and last run, how it ended, overdue, running; their runs with status and message (see [Scheduled jobs](#scheduled-jobs)) |
| `blob <ref>` | where a media file lives on disk |
| `drift` | what differs between the build and a shared database (needs `serve`; see [Shared databases](#shared-databases)) |
| `access <ref>` | who may read and edit an item: its access rights, and the ancestor they are inherited from |
| `sql "<SELECT …>"` | anything else, read-only |

A `<ref>` is a content id (`123`), a version (`123_456`), a content GUID, or a URL or path (`/en/about/`,
`https://host/en/about/`). Content from a content provider, such as images from a DAM, shows up as `63__provider`. You
can pass that form back in property values, ContentArea items and links. The id before `__` is local to one database;
the GUID is the same in every environment. `--lang <code>` picks the language branch. The default is the master
language, or the language the URL selects.

### Writing (needs `opticli serve`)

These commands write:
- `set`, `create`, `area` (add, remove or move ContentArea items), `block create`, `upload` and `translate` save a
  draft unless `--publish` is given. `upload <file>` adds a PDF, image or other file (up to 50 MB) as media, as the
  type the site maps its extension to, and prints where the file was stored. `create` doesn't take media types (that
  would be media without a file).
- `publish`, `unpublish` (takes a published branch offline, as the edit UI's expiry does), `discard` (deletes one
  unpublished version; it can't be undone), `move` and `delete` (to the recycle bin). `delete` stops when other content
  references what it deletes, unless `--ignore-references`.
- `restore <ref>` brings deleted content back out of the recycle bin, as the edit UI's Restore does: below the parent
  it had before it was deleted, which the CMS stores for every move (`trash` shows it as `originalParent`), or below
  `--to <parent>`. It keeps its versions, so what was published is live again. Something below deleted content can't be
  restored on its own (restore what was deleted), and a parent that is in the recycle bin too is a `conflict`.
- `create`, `block create`, `upload` and `move` put content only where it can go: pages below pages, blocks, media and
  folders in asset folders, and only where the parent type allows the type (`[AvailableContentTypes]` and admin mode's
  settings, as the CMS answers it).
- `access <ref>` with `--grant Role=Levels`, `--user Name=Levels`, `--revoke Name`, `--break-inheritance` or
  `--inherit` changes one item's access rights. Children that inherit follow; nothing is applied to descendants.
  Access rights aren't versioned, so the output shows them before and after. The root, the recycle bin, start pages
  and asset roots are refused, and so is a change that leaves no role with Administer.
- `sites primary` and `sites host add|remove` change site definitions' host names; see
  [Site hosts after a database restore](#site-hosts-after-a-database-restore).
- `jobs run`, `jobs stop` and `jobs set` run, stop and reschedule scheduled jobs; see [Scheduled jobs](#scheduled-jobs).
- `users add` and `users remove` make and remove a local login for a restored database; see
  [Local users](#local-users-after-a-database-restore).
- `apply plan.json` runs several operations validated together. Later operations can refer to an item an earlier
  one created as `$id`. A property value `"@texts/body.html"` is that file's text, as `Prop=@file` is on the command
  line. Files in a plan are relative to the plan file and must stay inside its folder. With `"guidNamespace"`, what a
  plan creates gets the same GUIDs in every database, and `apply --update-existing` runs it again: existing content is
  updated (and moved back out of the recycle bin), and steps that are already done change nothing. A plan that stops
  halfway (a failure, Ctrl+C) still reports what it saved, with undo hints and `details.partial: true`.

Every write command takes `--dry-run`. Structured values use `--values`, e.g.
`--values '{"MainArea":[{"ref":"456"}]}'`. `set` and `area` check that nobody saved a newer version in the meantime
(exit 5 on a conflict). `--base-version <id>` pins the version the change is based on, and `--force` skips the check.
`--from published` (or `--from <version>`) bases the change on that version instead of the latest, and the check
stays: newer drafts are left out and stay as they are, and the output lists them (`leftOut`).
A publish puts the whole version live. When someone else saved unpublished changes in it, opticli shows them and asks on
a terminal; elsewhere it fails with `conflict` and `details.reason: "pendingDraft"`, and `--include-draft` confirms.
`set ... --from published --publish` publishes a change without them.
Output after a publish names `previouslyPublished`, the version to publish again to go back. Content with an approval
sequence isn't published directly (exit 3); `--request-approval` sends it for review instead, as the edit UI does.
Against a shared database that differs from the build, writes stop with `drift` (exit 5) until `--accept-drift
<fingerprint>` (`apply --accept-drift` for a whole plan); dry runs don't stop.
[skill/reference.md](skill/reference.md) documents value syntax and the plan format.

### Site hosts after a database restore

A restored copy of a production database has the production host names. Locally, every site but the one with the `*`
host is unreachable, and absolute URLs (canonical links, sitemaps, structured data, emails) point at production.
`sites primary` points each site at the port the site listens on, through the running site (`opticli serve`):

```sh
opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" "Site C=localhost:5003" --dry-run
opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" "Site C=localhost:5003" --save
```

For each pair the site gets the host if it doesn't have it, as its primary host. The previous primary host and the
site's Edit host become undefined: an Edit host on a production name sends the edit UI and preview there, and
without one the CMS edits on any host. The site's URL (SiteUrl) follows the new primary host, `https://` unless
`--https false`, keeping SiteUrl's path. The CMS builds a host's links on SiteUrl's scheme when the host has no https
setting of its own, and adds SiteUrl's host back to the site whenever it is saved. The production hosts stay, so
`opticli resolve https://site-b.example/...` still finds content.

A pair without `@lang` replaces the site's primary host: the one for every language, or, on a site whose only primary
host is bound to a language (a site whose only hosts are for `nb`, say), that one, and the new host is for `nb` too;
the `changes` line says so. With primary hosts for several languages only (or one in a language that isn't enabled),
the new host is for every language beside them, and `meta.warnings` gives the pairs for the rest. A host that is a
primary host already keeps its language, and the language is worked out on the site as it was before the command, so
the order of the pairs doesn't matter and running them again changes nothing. SiteUrl follows a pair without
`@lang`, and a `@lang` pair whose primary host SiteUrl was on. A pair without `@lang` whose host is already one
language's primary host, beside a primary host for every language, is that language's pair (`Site A@sv=...`): SiteUrl
stays, and `meta.warnings` says so. Every primary pair, with or without `@lang`, makes the Edit host undefined (unless
`--keep-edit`): the CMS has one Edit host per site, for every language. `doctor` and `--from-config` read a saved
entry without a language the same way.

Putting a site back: the commands only ever put SiteUrl on a primary host. Once it has moved, a SiteUrl that was on
a host that isn't primary (as on a site the CMS created on its first request) can't be put back with them alone.

A host is `name[:port]`. The default port of the host's scheme is left out, as browsers leave it out of the requests'
host: `localhost:443` is the host `localhost` with https, `localhost:80` with http, while `http://localhost:443` (or
`localhost:443 --https false`) keeps its port. A host a site already has under another spelling
(`www.site.example:443`, from admin mode) is found by the name `opticli sites` shows. IPv6 addresses can't be site
hosts in the CMS; use `localhost`.

| Option | Does |
|---|---|
| `"Site A@nb=localhost:5004"` | the primary host for one language (an enabled one); SiteUrl stays. Without it, the host is for every language, as most are. `meta.warnings` names the languages whose URLs still use a production host. |
| `https://localhost:5001/` | the same host; the scheme sets its https setting. `--https true\|false\|unset` overrides it. |
| `--keep-edit`, `--keep-site-url` | leave the Edit host, or SiteUrl, as they are |
| `--dry-run` | validates through the site and shows each site's end state, saving nothing |
| `--save` | after a real run, saves the pairs (with `--keep-edit` and `--keep-site-url`) in the opticli user config, replacing that project's entries for those sites. Entries are keyed `Site A` and `Site A@nb`, so with sites named both `Shop` and `Shop@en`, saving for `Shop` replaces the `Shop@en` entry too. |
| `--from-config` | runs the saved pairs: after the next restore, `opticli sites primary --from-config` is all it takes. A saved site the database doesn't have is skipped with a warning. |
| `--forget <site>` | drops a site's entries (or one entry, `"Site A@nb"`) from the saved mapping, e.g. for a site that was deleted or renamed |

All pairs are one batch: every pair is checked, and the end state of every site as the CMS would save it, before
any site is saved. Then nothing is saved if one fails, and the error names each failing pair. Should the CMS still
refuse a site while saving (or the database fail), the sites saved before it stay saved, and the message says
`Already saved: ...; not saved: ...`. A site that is already as asked is `unchanged` and isn't saved, so the command
is safe to run again, also after such a failure. `doctor` compares the saved mapping with the database and says when
to run `--from-config`.

| Problem | Error |
|---|---|
| a host another site has (in any spelling: `localhost:443` is `localhost`), two primary hosts for a site and language, `*` on two sites, `host add --lang` with a language that isn't enabled | `validation`, exit 5 |
| a host with a path, query, other scheme or a bad port, a pair without `=` | `usage`, exit 1 (read by the CLI) |
| a site that doesn't exist, also `"Site A@xx=..."` when `xx` isn't an enabled language (`@xx` is then read as part of the name; the hint says so) | `not_found`, exit 2 |
| adding a host the site has | `conflict`, exit 5 |
| removing the site's last host; anything but `host add --type undefined` against a shared database | `refused`, exit 3 |

`sites host add <site> <host>` adds one host (`--type undefined|primary|edit|redirect-permanent|redirect-temporary`,
`--lang`, `--https`); a host the site has is a `conflict`. `sites host remove <site> <host>` removes one, but never the
site's last host or SiteUrl's host. Both take `--dry-run`.

Saving through the site clears its site definition cache, so the site `serve` runs uses the new hosts at once. Another
process running the site against the same database (your IDE's) keeps the old ones until it restarts, unless remote
events are set up. Site definitions aren't versioned: the output lists every change and the hosts after it.

### Scheduled jobs

A restored copy of a production database has its jobs enabled, with next runs in the past. A site started against it
would start every overdue job at once: imports and syncs reach external systems, emails go out, and the built-in
Automatic Emptying of Trash deletes the recycle bin for good. So `serve` turns the site's scheduler off for its run
(`[opticli] Scheduler is off for this run` in the site's output, `scheduler: off` in `serve --status` and `doctor`).
The site still registers its jobs at startup, and `jobs run` still runs one. `serve --scheduler` (or `"scheduler": true`
in the user config) leaves the scheduler as the site sets it, and then warns about the jobs that are overdue;
`doctor` counts them too. `opticli env` leaves it as the site sets it: a site you start yourself is yours.

```sh
opticli jobs                                   # every job: schedule, next run, overdue, last run and how it ended
opticli jobs log --failed --since 1d           # what failed since yesterday, with the jobs' messages
opticli jobs run "Publish Delayed Content Versions"   # run it now (needs serve) and wait for it
opticli jobs set "Content import" --every 1h --next now --dry-run
```

Reading (`jobs`, `jobs log`) comes from the database (`tblScheduledItem`, `tblScheduledItemLog`) and needs nothing
running. A job in the site's code (a `[ScheduledPlugIn]` class) has its file and line as `source`, and one the database
doesn't have yet is listed with `registered: false`: the site registers jobs when it starts. `running: "stale"` means
the job is marked as running but the process that ran it stopped pinging. A run's `finished` is when the CMS logged
it, and `started` that less its duration.

A `<job>` is its id (GUID), its name, its class (`EPiServer.Util.BlobCleanupJob` or `BlobCleanupJob`), or a part of its
name only it has. Several matches are a `usage` error that lists them.

| Command | Does |
|---|---|
| `jobs run <job>` | runs it now through the site, as the admin UI's "Start manually" does, also while the scheduler is off, as the user `opticli`; waits for it by reading the job tables, shows its status messages on a terminal, and ends with its status, duration and message. Exit 0 when it succeeded, 7 (`job_failed`) when it failed, couldn't start, or was stopped or aborted. `--no-wait` returns once it has started, `--timeout <s>` stops waiting (exit 4, `timeout`) and Ctrl+C too: the job goes on running. Already running: `conflict`. Jobs other than the CMS's own (the site's, and add-ons' such as Commerce or Search & Navigation) run without asking, with a warning in `meta.warnings` (in `error.details.warnings` when the run fails): opticli doesn't know what they change or contact. A run of an overdue job moves its next run on, as in the admin UI. |
| `jobs run <job> --allow-destructive` | needed for the jobs that delete for good: the CMS's Automatic Emptying of Trash, Remove Abandoned BLOBs, Trim Content Versions, Remove Unrelated Content Assets, Change Log Auto Truncate, Notification Message Truncate and Monitored Tasks Auto Truncate, and Commerce's Remove Expired Carts, Permanently Delete Archived and Remove Expired Lowest Price; and for Archive Function, which moves every expired page to its archive page (each move can be undone by hand). Matched by class name. Without it they are `refused` (exit 3), naming what they do; nothing implies it. |
| `jobs stop <job>` | asks the job the site runs to stop, as the admin UI's Stop does (the job's own code decides when), and waits up to 30 s for its run to end: normally `cancelled`. A job whose class can't be stopped is `refused`; one that isn't running is a `conflict`. |
| `jobs set <job> --enabled true\|false --every 30m\|1h\|1d\|1w\|1mo\|1y\|manual --next now\|<time>` | changes its schedule through the CMS's job repository, as the admin UI's form does; the output has it before and after (schedules aren't versioned). `--every` on a job without a next run needs `--next`; `manual` clears the next run. Units: `m` minutes, `h`, `d`, `w`, `mo` months, `y`, in any case except `M`, which is refused as ambiguous; intervals under a minute are refused. A next run in the past makes it overdue; with the scheduler off nothing starts it there. A change that lets the scheduler run one of the `--allow-destructive` jobs (enabled with a next run, an earlier next run or a shorter interval) needs `--allow-destructive` too; disabling one or making it manual never does. |

`run`, `stop` and `set` take `--dry-run`. Against a shared database they are refused (exit 3), with no override: the
deployed site's scheduler uses the same jobs, and a job writes to the content everyone shares. `jobs` and `jobs log`
work there.

### Local users after a database restore

A restored copy of a production database has production's users: often accounts from another identity provider, or
local ones whose passwords nobody knows here. `users add` makes a login to sign in with locally, through the site's own
ASP.NET Identity (`services.AddCmsAspNetIdentity<...>()`, the user class the CMS UI uses, found at runtime), as the
CMS's first-admin registration does:

```sh
opticli users roles                    # the roles, how many users each has, and what CmsAdmins, CmsEditors, ... map to
opticli users add dev --dry-run        # the name, the password against the site's rules, the roles
opticli users add dev                  # in WebAdmins (--role, repeatable, for others); a missing role is created
opticli users remove dev               # only a user opticli made
```

| Command | Does |
|---|---|
| `users add <name> [--role R]... [--password-stdin]` | makes the user, approved, with the address `<name>@opticli.localhost` (it reaches nobody) and the claim `opticli:created`, in `WebAdmins` (the role the first-admin registration uses) unless `--role` says otherwise; a role that doesn't exist is created (`createdRoles`). A warning says when the roles don't give `CmsAdmins` (admin mode) on this site. The password: `--password-stdin` reads one line; on a terminal opticli asks without showing it; otherwise it generates one and writes it to a file only you can read (`passwordFile`, see [Files](#files)), never to the output. The site's password rules apply (`validation`, exit 5). A name that exists is a `conflict`. |
| `users remove <name>` | removes a user `users add` made (it has the claim; in a user store without claims, the address), and its password file. Any other user is `refused` (exit 3); roles stay. |
| `users roles` | role names with their member counts, the virtual roles each gives, the virtual roles (mapped ones with the roles that give them), and how many users opticli made. No user names or addresses. |

All three go through the site (`serve`) and take no part of an existing account. Against a shared database they are
refused (exit 3): its users are real. On a site whose CMS users come from OpenID Connect, Opti ID or another identity
provider (no ASP.NET Identity), they are refused (exit 3) with the user provider the site has: sign in through that
provider. `doctor` doesn't hint at `users add`: whether anyone can sign in locally depends on password hashes and
external logins in the user tables, which are personal data.

## serve and env

`opticli serve` runs the site's existing build output in `Development` on `http://127.0.0.1:<port>`, with the site
agent injected, and returns once the site agent answers.

| Option | Default |
|---|---|
| `--output <dll>` | the newest `bin/{Debug,Release}/<tfm>/<AssemblyName>.dll` (also below a `<rid>/` folder, under the project's `OutputPath`/`BaseOutputPath`, or under `artifacts/` with `UseArtifactsOutput`), or `output` from the user config |
| `--port <n>` | `port` from the user config, else 5199, else the first free port up to 5299 |
| `--build` | off: opticli warns when sources (the site's or a referenced project's) are newer than the build, and `--build` runs `dotnet build` first |
| `--timeout <s>` | 180 seconds to wait for the site to answer |
| `--foreground` | off: the site runs in the background; with it, the site's output streams until Ctrl+C (to stderr when stdout is redirected, so stdout stays one JSON envelope) |
| `--https` | off (or `"https": true` in the user config, which `--https false` overrides): also listen on `https://localhost:<next free port>` with the development certificate, printed as `browseUrl`, for sites that redirect to HTTPS. `serve` warns when a site does |
| `--scheduler` | off (or `"scheduler": true` in the user config, which `--scheduler false` overrides): the site's scheduler stays as the site sets it, so jobs run on their schedule and overdue ones start at once. Without it the scheduler is off for the run. A `usage` error against a shared database, where it stays off |
| `--allow-pending-migrations` | off: against a shared database, `serve` refuses a build with EF Core migrations the database lacks |

`serve --status`, `serve --logs [--tail N]` and `serve --stop` manage the running site:
- `--stop` asks the site to shut down through the site agent, on every OS, and falls back to SIGTERM on Linux and
  macOS. A site that hasn't exited after 20 s is killed. On Windows that kill is all there is when the agent doesn't
  answer.
- Each start writes a new log. `--logs` reads the latest from its end, and `data.previous` lists the two before it.
- A state file opticli can't read is treated as stale: `--status` and `--stop` remove it, `doctor` reports it.
- Two `serve` runs for the same project don't start two sites: the second waits for the first and reports its site.
- Ctrl+C while `serve` waits for the site stops the site again.
- A site that configures `Kestrel:Endpoints` ignores `ASPNETCORE_URLS`. `serve` and `env` then add opticli's address
  as one more endpoint (`Kestrel__Endpoints__OptiCli__Url`) and warn. If the site sets its addresses in code, a
  timeout names the addresses it listens on instead.

To run the site yourself (IDE, `dotnet run`, hot reload), `opticli env` prints the variables `serve` would set:
- `--format shell|powershell|dotenv|json|launchSettings` picks the format. `--json` is the same as `--format json`.
- `--port` picks the port.
- The site's scheduler stays as the site sets it (`OPTICLI_SCHEDULER=on`): that run is yours.
- `OPTICLI_*` variables that this run leaves out but the shell still exports (from an earlier `opticli env`) are set
  to empty, so they can't pin or approve another database.

The connection string is left out unless `--include-connection` is given. Without it the site uses its own
configuration, which is still checked to be local (or the approved development database) and the same database
the CLI reads.

Set the variables for the site process only, e.g. in a subshell:

```sh
(eval "$(opticli env)" && dotnet run --no-launch-profile)
```

`DOTNET_STARTUP_HOOKS` makes every .NET process started from a shell that exports it load the hook. Launch profiles
are often committed, so keep the token out of git.

| Variable | Set by `serve`/`env` for the site | Meaning |
|---|---|---|
| `DOTNET_STARTUP_HOOKS` | always | path to `OptiCli.Agent.dll`, which loads the site agent |
| `OPTICLI_TOKEN` | always | the per-run token the site agent requires |
| `OPTICLI_DB` | `serve`, `env --include-connection` | the connection string to pin the site to (read by the CLI, it is the same as `--connection`) |
| `OPTICLI_CONNECTION_NAME` | when not `EPiServerDB` | which connection string to pin |
| `ConnectionStrings__<Name>` | `serve`, `env --include-connection` | the same connection string, for code that reads it before the agent's pin; `serve` also removes other spellings of it (`ConnectionStrings:<Name>`, another case) |
| `OPTICLI_REMOTE_DB` | against a remote development database | the one remote database the site agent accepts; turns on shared-database mode |
| `OPTICLI_DRIFT_FILE` | `serve`, against a remote development database | what `serve` compared before the start (EF Core migrations, CMS schema version), for the site agent's drift report |
| `OPTICLI_SCHEDULER` | `serve --scheduler`, `env` (not against a remote database) | `on` leaves the site's scheduler as the site sets it; without it the site agent turns the scheduler off |

### How the injection works

`serve` starts `dotnet <Site>.dll` with `DOTNET_STARTUP_HOOKS` pointing at `OptiCli.Agent.dll`, which ships in the
tool package under `agent/`. The startup hook makes the site agent's assembly resolvable and appends it to
`ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`. The hosting startup then:
1. pins the connection string, added as the last configuration source and as a `PostConfigure` of the CMS's data
   access options;
2. fails the start if the effective connection string is neither local nor the approved development database;
3. turns the scheduler off (`SchedulerOptions.Enabled`, as a `PostConfigure`) unless `OPTICLI_SCHEDULER=on`; a site
   that turns it on in a `PostConfigure` of its own wins, and `serve --status` then says `scheduler: on`;
4. maps `/_opticli/v1/*` ahead of the site's own middleware.

The site agent is compiled against `EPiServer.CMS.Core` 12.0 and binds to the site's own, newer CMS assemblies at
runtime. It ships no copies of them.

## Output and exit codes

- Redirected stdout gets compact JSON:
  `{"ok": true, "data": …, "meta": {"source", "version", "next", "warnings", "database"}}`.
  - `source` is `db` for reads, `agent` for writes and `cli` for local commands.
  - `database` is present only when the database is remote.
- A terminal gets tables. `--json` and `--text` force either.
- `--jsonl` on list commands prints one item per line. A final `{"meta": {…}}` line follows only when there is a
  next page or warnings.
- Lists return 50 items by default (`sql`: 100 rows). Pass `meta.next` as `--cursor` for the next page, or raise
  `--limit`. There is no total count.
- Strings over 300 characters are cut (`truncated: true, length: N`); `--full` or `--fields` gives them whole.
  Dates are UTC (`…Z`).
- Errors look like `{"ok": false, "error": {"code", "message", "hint", "details"}}`. The hint says what to do next.

| Exit | Code | Meaning |
|---|---|---|
| 0 | | ok |
| 1 | `usage` / `internal` | bad arguments (or a bug in opticli) |
| 2 | `not_found` | project, connection string, content, type or version not found |
| 3 | `refused` | a safety rule blocked it |
| 4 | `unreachable` | database or site agent not reachable, or a query failed on the server (a write that timed out may still have been saved) |
| 4 | `timeout` | `jobs run --timeout` ran out: the job goes on running |
| 5 | `conflict` / `validation` / `drift` | a newer version exists, a publish would include someone else's unpublished changes (`details.reason: "pendingDraft"`), the CMS rejected the values, or a write against a shared database that differs from the build wasn't confirmed (`details` is the drift report) |
| 6 | `needs_selection` | the user must choose the development database first (`error.details.choices`) |
| 7 | `job_failed` | a job `jobs run` ran didn't succeed: it failed, couldn't start, or was stopped or aborted (`error.details` has its status and message) |
| 130 | `cancelled` | interrupted (Ctrl+C); an `apply` reports what it saved until then |

## Files

| What | Linux / macOS | Windows |
|---|---|---|
| User config | `$XDG_CONFIG_HOME/opticli/config.json` (default `~/.config/opticli/config.json`) | `%APPDATA%\opticli\config.json` |
| `serve` state, start lock and logs (the last 3 runs) | `$XDG_STATE_HOME/opticli/` (default `~/.local/state/opticli/`) | `%LOCALAPPDATA%\opticli\` |
| Generated passwords of `users add` (readable by you only) | `$XDG_STATE_HOME/opticli/users/<project>/<name>.txt` | `%LOCALAPPDATA%\opticli\users\<project>\<name>.txt` |

You can set per-project defaults in the user config (`output`, `port`, `https` and `scheduler` for `serve`).
`opticli db use` adds the chosen `database` there, and `opticli sites primary --save` the `sites.primary` mapping.
opticli keeps the file's permissions when it rewrites it, and creates it readable by you only on Linux and macOS.

```json
{"projects": {"/abs/path/to/Site": {"connection": "...", "output": "bin/Debug/net8.0/Site.dll", "port": 5199, "https": true,
  "sites": {"primary": {"Site A": "localhost:5001", "Site A@nb": "https://localhost:5004",
    "Site B": {"host": "localhost:5002", "keepEdit": true}}}}}}
```

## Supported versions

- Optimizely CMS 12 (`EPiServer.CMS.AspNetCore` 12.x): reads on any runtime; writes on a site running .NET 8 or newer.
- SQL Server on the local machine (including LocalDB and containers), or a remote SQL Server / Azure SQL
  development database.
- Not supported:
  - CMS 11, Commerce, Forms data and search indexes;
  - writes to environments other than development (test, staging, production).

## Known limitations

- Rules that live only in code are read from the C# sources, not evaluated:
  - `[AllowedTypes]` is parsed. Editor descriptors and metadata extenders that change allowed types at runtime are
    not; a `uiHint` flags the property.
  - Validation attributes are only pointed at (file and line).
  - URLs a site rewrites in code (custom segments, partial routing) show as the plain content path.
  - `type` lists views, but not controllers.
- Content types are matched to C# classes by GUID or name with a lightweight source scan, not a compiler. Unusual
  declarations may be missed.
- `where-used` works per item: there is no type-wide usage in one call.
- `serve` runs the existing build output; it doesn't build unless `--build` is given.
- Output field names may still change before 1.0.

## MCP server for editors (preview)

`OptiCli.Mcp` is a separate NuGet package for the site itself, not for developers' machines. Installed in a site, it
lets **editors** connect Claude (claude.ai, Claude Desktop, Claude Code) to that site, in any environment, production
included. The editor signs in with the site's own login, and Claude then works as that editor, with their access
rights in the CMS. The tools run the same content operations as the site agent: drafts by default, dry runs with a
list of changes, validation, approval sequences, `baseVersion` conflicts, and errors with a hint.

It is a **preview**, not yet recommended for production: the options and tools may still change, and the package is
published as a prerelease (a `-preview` version). It supports CMS 12 only (12.12.1 / CMS UI 12.16.1 or newer), not CMS 13.

### Install

```sh
dotnet add package OptiCli.Mcp --prerelease
```

In `Startup.cs`:

```csharp
using OptiCli.Mcp;

public void ConfigureServices(IServiceCollection services)
{
    // ... AddCms() and the rest
    services.AddOptiCliMcp(o =>
    {
        o.AllowPublish = true;   // optional: off by default
    });
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ... UseRouting(), UseAuthentication(), UseAuthorization()
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapContent();
        endpoints.MapControllers();   // the site's own endpoints, if it has them
        endpoints.MapOptiCliMcp();    // after everything else
    });
}
```

Map the module **after** `MapContent()` and the site's own endpoints. Its paths are literal, so they win over content
routing's catch-all wherever they are mapped, and mapped last it changes nothing for the rest. Mapped before
`MapContent()` it does: the CMS's documentation asks for other endpoints before or after `MapContent()`, not both,
because with anything mapped before it, the CMS freezes the routes add-ons register, and a `MapControllers()` after it
then maps the site's controllers a second time. A site with a named route then fails every request with "Duplicate
endpoint name". The module logs a warning when it is the site's first endpoint mapping.

The module adds a bearer scheme of its own for the MCP endpoint, and leaves the site's default authentication scheme
as it is: editors sign in with whatever the site uses (ASP.NET Identity, Microsoft Entra ID, Opti ID). Connections are
kept in the Dynamic Data Store, and access tokens are protected with the site's Data Protection keys, which every
instance of a load-balanced site already shares for its login cookies.

Requirements: .NET 8 or newer, `EPiServer.CMS.Core` 12.12.1 or newer and `EPiServer.CMS.UI.Core` 12.16.1 or newer
(the first CMS 12 versions whose dependencies allow the MCP SDK's `Microsoft.Extensions` 10.x packages), and not CMS
13. The package brings the official MCP C# SDK (`ModelContextProtocol.AspNetCore` 2.2.0).

### Options

The options are read from the `OptiCli:Mcp` configuration section, then from the delegate passed to `AddOptiCliMcp`,
which wins. In configuration, an `AllowedRoles` or `AllowedRedirectHosts` list replaces the default list.

| Option | Default | Meaning |
|---|---|---|
| `BasePath` | `/episerver/opticli` | where the module lives: the MCP endpoint is `{BasePath}/mcp`, the OAuth issuer `{origin}{BasePath}`; `""` for a host of its own (with `RequireHost`) |
| `RequireHost` | none | only answer on this host name (`host` or `host:port`), e.g. an editors' host |
| `AllowedRoles` | WebEditors, WebAdmins, CmsEditors, CmsAdmins, Administrators | who may connect an assistant at all; the CMS's access rights then decide per item |
| `AllowedRedirectHosts` | claude.ai, claude.com | the hosts an app's return address (OAuth redirect URI) may be on, https only, matched exactly (no subdomains or wildcards); a loopback address on the editor's own machine is always allowed. `["*"]` allows any https host, and logs a warning at startup (see below) |
| `AccessTokenLifetime` | 1 hour | how long an access token works; also how long a removed role can keep working |
| `RefreshTokenLifetime` | 30 days | how long a connection survives unused; each refresh extends it |
| `ConnectionLifetime` | 30 days | the longest a connection lasts, however often it is used; then the editor connects again, through the site's login (see below) |
| `RefreshTokenReuseGrace` | 1 minute | how long after a refresh the refresh token it replaced, sent again by the same client, is only refused; later (or from another client) it revokes the connection |
| `AllowPublish` | `false` | let assistants publish, unpublish and schedule publishing (the `content:publish` scope) |
| `AllowDelete` | `false` | let assistants delete content, always to the recycle bin |
| `MaxUploadBytes` | 10 MB | the largest media file an assistant may upload (at most 50 MB); the CMS UI's own upload limit applies too, if it is lower (see below) |
| `RateLimits` | see below | requests a minute the OAuth endpoints take |

```json
{"OptiCli": {"Mcp": {"AllowDelete": true, "AllowedRoles": ["WebEditors", "WebAdmins", "ProductEditors"]}}}
```

`AllowedRedirectHosts` is what stops a stranger from connecting an app to an editor's account. Registration is open to
anyone and an app names itself, so without it someone could register an app called "Claude" with a return address of
their own, send a signed-in editor the link to connect it, and receive the code that the editor's Allow sends there.
The module checks the return address when an app registers and again at every sign-in, also for apps with a metadata
document and for apps registered before the list changed. Add a host only for a web app the site knowingly accepts.

`RateLimits` are fixed windows of a minute, counted per instance; over a limit, a request gets 429 with `Retry-After`.
claude.ai calls `register` and `token` from Anthropic's addresses (below), which every editor of the site who uses
claude.ai shares, so the token endpoint counts per client as well as per address. An IPv6 address counts by its /64,
which one machine usually has whole. Behind a proxy or load balancer, the site needs ASP.NET Core's forwarded headers
set up (as its login usually already does), limited to its known proxies: otherwise every request seems to come from
the proxy, and everyone shares one window.

| `RateLimits.` | Default | Counts |
|---|---|---|
| `TokenPerMinute` | 60 | token requests (code exchanges, refreshes) per client and address |
| `TokenPerAddressPerMinute` | 600 | token requests per address, whatever the client |
| `RegisterPerMinute` | 10 | client registrations per address (clients with a metadata document, as Claude has, don't register) |
| `AuthorizePerMinute` | 60 | the consent page and its post, per address (the editor's browser) |

Besides the rate limit, registration stops at 5,000 registered apps that have no connection and no sign-in under way:
`register` then answers 429 until the cleanup has deleted some. An app that registers and doesn't sign in within a day
is deleted.

```json
{"OptiCli": {"Mcp": {"RateLimits": {"TokenPerAddressPerMinute": 1200}}}}
```

### Endpoints

With the default `BasePath`, on the site's own host:

| URL | What it is |
|---|---|
| `https://www.example.com/episerver/opticli/mcp` | the MCP endpoint (Streamable HTTP): the URL to give Claude |
| `/.well-known/oauth-protected-resource/episerver/opticli/mcp` | protected resource metadata (RFC 9728) |
| `/.well-known/oauth-authorization-server/episerver/opticli` | authorization server metadata (RFC 8414), also as `/.well-known/openid-configuration/episerver/opticli` and `/episerver/opticli/.well-known/openid-configuration` |
| `/episerver/opticli/oauth/register`, `/oauth/authorize`, `/oauth/token` | client registration, sign-in and consent, tokens |
| `/episerver/opticli/connections` | the editor's connections, with Revoke |

The site's own `/.well-known` documents at the root are left alone. A request to the MCP endpoint without a token
gets a 401 that points to the metadata, never a redirect to the login page.

### Connecting Claude

- **claude.ai, Claude Desktop and mobile:** add a custom connector with the MCP endpoint's URL (on Team and Enterprise
  plans an owner may have to add it for the organization). Claude opens the site's login page, then the module's consent page.
- **Claude Code:** `claude mcp add --transport http cms https://www.example.com/episerver/opticli/mcp`, then `/mcp` in
  Claude Code to sign in.

**Trying it locally with Claude Code:** plain http on a loopback address (`http://127.0.0.1:5000/...`) works where
the site allows it. Over https, Claude Code (a native executable) honours `NODE_EXTRA_CA_CERTS` but doesn't accept the
ASP.NET Core development certificate as a trust root, since that certificate is a self-signed leaf (`CA:FALSE`): it
fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE`, though curl and Node accept it. A localhost certificate signed by a test
CA of your own works:

```sh
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=Local MCP test CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -keyout ca.key -out ca.pem
printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' > localhost.ext
openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" -keyout localhost.key -out localhost.csr
openssl x509 -req -in localhost.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 30 -extfile localhost.ext -out localhost.pem

# the site, with that certificate
ASPNETCORE_URLS=https://localhost:5443 \
ASPNETCORE_Kestrel__Certificates__Default__Path=$PWD/localhost.pem \
ASPNETCORE_Kestrel__Certificates__Default__KeyPath=$PWD/localhost.key \
dotnet run

# Claude Code, trusting the test CA
NODE_EXTRA_CA_CERTS=$PWD/ca.pem claude
```

Keep `ca.key` to yourself and delete it when you are done: whoever has it can make certificates your Claude Code trusts.

Nothing needs registering on the site first. Claude identifies itself with a client ID metadata document (CIMD): the
site fetches the document from the client's own HTTPS URL, which needs outbound HTTPS from the site. Clients without
CIMD register themselves (dynamic client registration, DCR).

**The site must be publicly reachable** for claude.ai, Claude Desktop and mobile: their connectors call the site from
Anthropic's cloud, not from the editor's machine, so a site that is only on a VPN, a private network or split DNS
can't be used. A site that restricts `/episerver` by IP address must allow Anthropic's outbound range,
`160.79.104.0/21`, for the MCP endpoint, the metadata documents and the `register` and `token` endpoints. The login,
`authorize` and the connections page open in the editor's own browser, so they need what the CMS login needs. Claude
Code connects from the editor's machine.

**Dedicated host mode:** `BasePath = ""` with `RequireHost = "mcp.example.com"` puts the module at the root of a host
name of its own (which must reach the same site). The issuer then has no path, which suits clients that don't handle
an issuer with one, and the root `/.well-known` documents on that host are the module's. `RequireHost` with the default
`BasePath` only keeps the module off the site's other host names.

### Security model

- **Claude is the editor.** Every read is checked against the editor's Read access; content they can't read gives
  exactly the same `not_found` as content that doesn't exist, and lists leave it out. Every save, publish, move and
  delete goes through the CMS's own access checks for the editor. The CMS records the editor as the one who saved.
- **What the CMS leaves to the edit UI is checked too**, since the CMS's repository doesn't check it on save:
  - a content type's own access rights (who may create it), and the access its group of types requires on the parent;
  - the access rights per language (admin mode, Languages): an editor who may not edit a language gets no draft,
    publish, unpublish or discard in it, and can't add it to content;
  - properties the edit UI hides or locks for the editor: not displayed in edit mode (`[ScaffoldColumn(false)]`),
    read-only (`[Editable(false)]`), on a tab whose required access they lack, or hidden or locked for them by an editor
    descriptor or metadata extender. The module asks the CMS UI's own metadata, as the edit UI's form builds it (local
    blocks nested in their content's), and when that can't be built for an item, the model's settings decide what is
    shown and nothing of the item may be changed. `get_content` leaves these properties out, and so do diffs, also
    inside local blocks and block lists; writing them is refused, and a block list rewritten keeps what its items had of
    them. `get_content_type` leaves out what the type's settings hide or lock; what depends on the content and the
    editor only `get_content` knows;
  - content references: every one is looked up as the editor, also in rich text (embedded blocks), so an assistant
    can't point to content the edit UI's pickers wouldn't offer, or learn from a dry run that it exists. The CMS's own
    text forms of a ContentArea or a reference list, which skip that, are refused: give an array of refs;
  - restoring from the recycle bin, which the module doesn't do at all (a move out of the recycle bin is refused, with
    a hint to restore it in the CMS, where the editor sees what comes back live).
- **No script in rich text or links.** The CMS saves rich text as it is given, and the edit UI's TinyMCE only cleans it
  in the browser. A draft with script would run it in the edit UI of whoever opens it, with their session, and could
  publish through the CMS's own API, past `AllowPublish`. So rich text with `<script>`, event handler attributes
  (`onerror=...`), `javascript:`, `vbscript:` or non-image `data:` URLs, `<iframe>`, `<object>`, `<embed>`, `<form>`,
  `<base>`, `<meta>`, `<link>`, `<svg>` and the like is refused, however it is written (character references, tabs or
  odd case in a scheme, attributes without quotes), and so are links (link properties, link collections, URL
  properties, URLs in a list property's items, a shortcut's external link) other than http, https, mailto, tel and
  relative ones, and links with attributes other than href, title and target (the CMS renders every attribute a link
  has), or with a control character in them. Links are given as `{href, text, title, target}`, never as the CMS's
  stored markup. Ordinary rich text passes: tables, images, links, embedded blocks, `style` and `class`. What a property
  already has may be written back as it is, so an editor's assistant can still fix a typo in rich text with a video's
  `<iframe>` or a link collection with an sms: link; anything new is refused, also such a construct copied once more or
  into another property. Only what a browser surely reads as live counts as already there: script inside an element it
  reads as text or keeps inert (`<textarea>`, `<style>`, `<template>`, `<svg>`, ...) is never written back live, nor a
  live construct moved into one. Set the CMS's own script parser to remove script as well, for every save, the edit
  UI's included (CMS 12.15 or newer): `"EPiServer": {"Cms": {"ScriptParser": {"SavingMode": "Remove"}}}`.
- **No files that run script.** A file uploaded to the CMS is served from the site's own origin, so one a browser runs
  script in would run it with the session of whoever opens it. An editor's assistant can't upload HTML, XML, XSLT or
  JavaScript files (`.html`, `.htm`, `.xhtml`, `.xht`, `.shtml`, `.mht`, `.mhtml`, `.xml`, `.xsl`, `.xslt`, `.js`, `.mjs`)
  or compressed SVG (`.svgz`); the hint says to upload such a file in the CMS. An SVG file is read: one with
  `<script>`, `<foreignObject>`, event handler attributes, `javascript:` or non-image `data:` links (also set by an
  animation), entity or attribute list declarations or an `xml-stylesheet` instruction is refused, and an ordinary
  drawing uploads. A file named as a raster image (`.jpg`, `.png`, ...) that starts like markup is refused too. Have the
  site send `X-Content-Type-Options: nosniff` with its media, so a browser never takes a file for anything but what its
  content type says.
- **The role gate** (`AllowedRoles`) is checked against the editor's roles as they are now (the CMS UI's role
  provider), not the ones in their login cookie: at consent and at every token refresh. So is their account, where the
  site manages it: an ASP.NET Identity user who is disabled, locked out or deleted gets no consent and no new tokens.
  An editor who fails either at a refresh gets no new tokens, and the connection is deleted; the access token they
  have works until it expires (`AccessTokenLifetime`).
- **External logins** (Microsoft Entra ID, Opti ID, any OpenID Connect provider): the account is managed by the
  identity provider, not the CMS, so for a user the CMS synchronized from one the module doesn't judge the account at
  all, whatever a user provider for such users reports, and the roles decide. Those roles are only as current as the
  CMS's copy, which it updates when the editor signs in; and the CMS never hears that an account was disabled in the
  identity provider. So such an editor, disabled there, could keep refreshing for as long as the connection is used.
  `ConnectionLifetime` bounds that: counted from when the editor allowed the connection, checked at every refresh and
  on every access token, after which they have to connect again through the site's login, where the identity provider
  has its say. The default is 30 days; for a site with external logins, 1 to 7 days is a better choice. The site's
  login cookie counts as signed in while it lasts, so its lifetime matters too.
- **Approval sequences are enforced.** The CMS doesn't apply them to API saves, so the module refuses a publish of
  content under one and offers `requestApproval`, which sends it for review as the edit UI's Ready for Review does.
  `requestApproval` never publishes, so it needs neither `AllowPublish` nor `content:publish`: where no sequence
  applies, it is refused (`noApprovalSequence`) and nothing is saved.
- **Publishing and deleting are opt-in** (`AllowPublish`, `AllowDelete`). Without `AllowPublish` the consent page
  doesn't offer the `content:publish` scope, and every publish is refused with a hint to save a draft instead; with it,
  the editor still chooses on the consent page, and their Publish rights still apply. Besides each tool's own check,
  every save that would publish or schedule is checked against the same gate, and so is every other change that puts
  something live at once: moving content that has a published version or anything below it (its URLs change, it takes
  the access rights of its new parent; the CMS itself only asks for Publish on the destination when the moved item is
  published), discarding a version scheduled for publishing, and saving content without versions. Content that may be
  live isn't moved from one approval sequence to another at all (move it in the CMS). Changing access rights and
  removing language branches aren't offered at all.
- **Deleting** only moves content to the recycle bin, but note that this takes published content offline, with
  `AllowDelete` alone (and the editor's Delete rights): `delete_content` needs no publishing rights. Discarding a draft
  someone else saved deletes their work for good, so it needs `AllowDelete` too; an editor's own drafts can always be
  discarded.
- **`includeDraft` is a confirmation, not a boundary.** It stops the assistant from putting live or deleting someone
  else's changes without asking the user, but the assistant sets it itself; what the site allows is decided by
  `AllowPublish`, `AllowDelete` and the editor's access rights.
- **The consent page** lists what the app may do, with a checkbox each: reading is required, writing and (where the
  site allows it) publishing can be unticked, and the connection gets only what was left ticked. For an app that
  registered itself, a warning says plainly that it chose its own name and the site can't confirm who made it; an app
  with a metadata document is named with the domain that publishes it. The connections page shows each connection's
  scopes.
- **OAuth:** PKCE (S256) is required; codes work once, for 5 minutes. Refresh tokens rotate, as a compare-and-swap, so
  of two refreshes with the same token only one succeeds and a connection revoked meanwhile stays revoked. The refresh
  token before the current one, used again, revokes the connection (RFC 9700), since the site can't tell a leaked
  token from a confused client; but not from the same client within `RefreshTokenReuseGrace` of the refresh that
  replaced it, which is a client refreshing twice at once or retrying after a lost answer: that is only refused, and
  the connection keeps the tokens the other refresh got. Older refresh tokens are just refused. A refresh may ask for fewer scopes: the access token
  gets those, and the connection keeps what the editor approved. Tokens are bound to the MCP endpoint and checked
  against their connection on every use, so revoking one cuts the client off at once (another instance of a
  load-balanced site notices within 30 seconds). Codes, secrets and refresh tokens are stored as hashes only. Redirect
  URIs must match exactly (any port on loopback), and be on a loopback address or one of `AllowedRedirectHosts`. The
  consent page can't be framed and checks an antiforgery token. Client metadata documents are only fetched from public
  addresses, checked on the connected socket, without redirects. Registration, authorization and token requests are
  rate-limited (`RateLimits`). Registered clients without a connection are deleted a day after they registered, and
  registration stops at 5,000 of them; the cleanup runs in the background, a bounded amount at a time.
- **Load-balanced sites:** the Dynamic Data Store has no conditional update, so a refresh's compare-and-swap is
  atomic within an instance only. Two instances given the same refresh token within milliseconds could both rotate
  it (the later one wins, and the other new refresh token never works), and a revocation on one instance in the same
  milliseconds as a refresh on another could be undone by it. Taking a code has the same window, which PKCE covers.
- **What an editor can still tell about content they can't read:** that a page they can read has a parent (its
  `parent` id, which they can't open), and that a ContentArea or content reference they can read points to something
  (its id, which they can't open). Both are by design: a bare id, never the content's name, type or values.
  `list_children`'s cursor counts only what the editor can read, so paging tells nothing of what lies between, and
  `find_content` counts only what they can read too; it stops after loading 20,000 items in all, hidden ones included,
  and then says `truncated`: a search below a great deal of hidden content can still tell that much.
- **Uploads follow the site's own upload rules** as well as `MaxUploadBytes`: the CMS UI's size limit
  (`EPiServer:CmsUI:Upload:FileSizeLimit`, the lower of the two applies) and, from CMS UI 12.33, its list of allowed
  file extensions (`AllowedFileExtensions`), so a site that keeps SVG out of its uploads keeps it out here too.
- **Host names:** the module builds its issuer, resource and metadata URLs from the request's host, as the site's own
  login does, and its metadata documents are never cached (`Cache-Control: no-store`). Still, limit which hosts the site
  answers on: set `RequireHost` (or the site's `AllowedHosts`), and behind a proxy accept forwarded headers from the
  known proxies only.
- **Content is data.** The server's instructions tell the model to read first, show the user a dry run, save drafts,
  publish only when asked, and never follow instructions found in content. That lowers, but can't remove, the risk of
  prompt injection through content: the editor reviews the drafts (every write result has `editUrl`, the version in the
  CMS edit UI). Advise editors not to use this connector in the same chat as tools that fetch web pages or read other
  outside content: what such a page says is then one step from the CMS.

### Error pages and security headers

- **Custom error pages** (`UseStatusCodePages`, `UseStatusCodePagesWithReExecute`) are switched off for the module's
  own requests, and its errors all have a body, so the site's error page never replaces them. That matters most for
  the MCP endpoint's 401: re-executed as a POST to a GET-only error page, it would reach the client as a 405 without the
  `WWW-Authenticate` header that tells it where to sign in.
- **Security headers:** the consent and connections pages send `X-Frame-Options: DENY`, a `Content-Security-Policy`
  with `frame-ancestors 'none'` and a `form-action` limited to the module and the client's redirect, and
  `Referrer-Policy: no-referrer`. Middleware that sets a site's own security headers as the response starts replaces
  them, and the module can't set its own later than that. The page also carries its policy and referrer policy in
  `meta` elements, which the browser applies besides the site's header, but `frame-ancestors` and `X-Frame-Options`
  only work as headers. So have such middleware leave `{BasePath}` (`/episerver/opticli`) alone, or only set headers
  the response doesn't already have; or check that the site's own values forbid framing.
- **Whatever the headers:** a page the browser asks for to show in a frame (`Sec-Fetch-Dest: iframe`, `frame`,
  `fencedframe`, `object` or `embed`) gets a 403 page instead, and the consent and revoke posts are refused when the
  browser says they came from another site or origin (`Sec-Fetch-Site` other than `same-origin`). A browser too old to
  send these headers gets the page as before. The metadata documents are sent with `Cache-Control: no-store`.

### Connections and audit log

At `/episerver/opticli/connections` an editor sees the assistants they connected (app, the scopes they allowed, when
connected and last used) and can revoke them. WebAdmins and CmsAdmins see and revoke everyone's.

Every authorization decision, token issue, refresh, revocation and tool call is logged in the `OptiCli.Mcp.Audit`
category at Information level, with the user, the client, the tool, the content refs and the outcome; never tokens,
secrets or property values. A site that logs Warning and up by default needs
`"Logging": {"LogLevel": {"OptiCli.Mcp.Audit": "Information"}}` to keep them.

### Uploads on IIS

The MCP endpoint takes requests up to `MaxUploadBytes` as base64 plus 1 MB, and refuses larger ones with a 413; a file
larger than the CMS UI's own upload limit (`EPiServer:CmsUI:Upload:FileSizeLimit`: 4 MB by default up to CMS UI 12.31,
30,000,000 bytes from 12.33) is refused too, as the edit UI refuses it, so raise that as well for larger uploads. IIS has
a limit of its own, `maxAllowedContentLength`, 30,000,000 bytes by default, so a site that raises `MaxUploadBytes` past
about 20 MB must raise that too, in `web.config`:

```xml
<system.webServer>
  <security>
    <requestFiltering>
      <requestLimits maxAllowedContentLength="73400320" />
    </requestFiltering>
  </security>
</system.webServer>
```

## Development

```sh
dotnet build opticli.slnx       # warnings are errors
dotnet test opticli.slnx
```

The EPiServer packages come from Optimizely's public NuGet feed, which [nuget.config](nuget.config) already lists.

| Project | What it is |
|---|---|
| `src/OptiCli` | the `opticli` command line: commands, options, help text |
| `src/OptiCli.Core` | everything the CLI does: discovery, connection resolution and safety, SQL readers, decoders, output, `serve` |
| `src/OptiCli.Agent` | the site agent, loaded into the site process; referenced by nothing, copied into the tool package |
| `src/OptiCli.Cms` | the content operations (read, create, draft, publish, ...) the site agent and the MCP module run, for a developer or a signed-in editor; source-linked into both |
| `src/OptiCli.Mcp` | the MCP server for editors, the `OptiCli.Mcp` package: OAuth authorization server, tools, connections page |
| `src/OptiCli.Protocol` | request and response types shared by the CLI, the site agent and the MCP module (source-linked) |
| `skill/` | the coding-agent skill, embedded in `opticli.dll` |
| `tests/` | unit tests for Core, the site agent and the MCP module; `OptiCli.Integration` and `OptiCli.Mcp.Integration`, tests against a real site |

The unit tests use generic fixtures and need no database. The integration test is an oracle. It samples content
across types, kinds and languages from a real site, reads each item both from the database and through the CMS
(via the site agent), and compares them property by property. It is skipped unless a site is configured:

```sh
cd path/to/Site && opticli serve        # the site agent must be running
OPTICLI_IT_PROJECT=path/to/Site \
OPTICLI_IT_SAMPLE=200 \
OPTICLI_IT_REPORT=/tmp/oracle-report.md \
dotnet test tests/OptiCli.Integration
```

| Variable | Meaning |
|---|---|
| `OPTICLI_IT_PROJECT` | site project directory (required; the test is skipped without it) |
| `OPTICLI_IT_SAMPLE` | content branches to compare (default 200) |
| `OPTICLI_IT_DRAFTS` | most recent drafts to compare as well (default 10) |
| `OPTICLI_IT_SEED` | sampling seed (fixed by default, so runs repeat) |
| `OPTICLI_IT_REPORT` | write the mismatch report here, with every mismatch as `.jsonl` next to it |
| `OPTICLI_IT_PLAN` | a plan whose content is always compared, on top of the sample (the edge-case plan below) |

Some differences the database can't reproduce by design, such as URL segments a site drops in code. Those are listed
with the reason in `tests/OptiCli.Integration/Comparison/KnownDifferences.cs`; any other mismatch fails the run.

#### The edge-case site

A sample site lacks much of what real sites have: fetch-data pages, a site whose start page is under another site's,
simple addresses on several sites, culture-specific properties in shared and local blocks, personalized ContentAreas,
an approval sequence, language fallback settings, a media type for PDF files, three sites whose hosts the site host
tests change (and put back), and a scheduled job of its own. `tests/fixtures/edge-cases/` builds them from an Alloy
site (`dotnet new epi-alloy-mvc`) without changing it. `setup.sh` copies the site and its database, adds
`EdgeCasesFixture.cs` (the extra content types, plus a startup module for what a plan can't create) and
`JobsFixture.cs`, `UsersFixture.cs` and `OrphansFixture.cs`, and applies `edge-cases.plan.json`. `JobsFixture.cs` has "opticli test job", a manual, stoppable job
that writes a status message a second for 3 steps (the number in `App_Data/opticli-job-steps`, if it exists) and fails
when `App_Data/opticli-job-fail` exists; the `jobs` tests run it. `OrphansFixture.cs` leaves what removed code leaves behind: the page type `EdgeRemovedPage`, whose class doesn't exist,
used by one page, and a property `EdgeRemovedText` of EdgePage that its class doesn't declare, with one value (made at
the second start, once the plan's content exists). `UsersFixture.cs` makes the user `edge-fixture-user` at startup (WebEditors, no password, not
made by opticli), which the `users` tests check `users remove` refuses; they also sign in on `/util/login` as a user
they add. With `OPTICLI_FIXTURE_SCHEDULER=on` in the environment
of `serve`, it also turns the scheduler on in the site's own configuration, as a real site has it (Alloy turns it off in
Development), to see that `serve` turns it off again and `serve --scheduler` doesn't. Run `setup.sh` again to update
the content: the plan is applied with `--update-existing`, and the database copy is kept unless `FRESH=1`.

```sh
SQLCMDPASSWORD=... tests/fixtures/edge-cases/setup.sh path/to/Alloy path/to/AlloyEdge alloy alloy-edge
OPTICLI_IT_PROJECT=path/to/AlloyEdge \
OPTICLI_IT_PLAN=tests/fixtures/edge-cases/edge-cases.plan.json \
dotnet test tests/OptiCli.Integration
```

The integration tests run one at a time, since the write tests make and remove scratch content on the same site.

`drift.sh` in the same folder runs the shared-database scenario on copies of the edge-case site and its database. It
reaches the copy through a host name instead of a loopback name (`<hostname>.localhost` resolves to the loopback
address, but counts as remote), so `serve` runs in shared mode. It then changes the copy's code and checks each step:
nothing differs, local ahead, the database ahead, and an EF Core migration that `serve` refuses. Against that copy,
`DriftTests` checks that writes stop on drift until its fingerprint confirms it.

```sh
SQLCMDPASSWORD=... tests/fixtures/edge-cases/drift.sh path/to/AlloyEdge path/to/AlloyDrift
```

#### The MCP test site

`tests/fixtures/mcp/setup.sh` builds a site for the MCP module's end-to-end tests from a copy of the edge-case site
and its database (`alloy-edge` to `alloy-mcp` by default; `FRESH=1` copies the database again). It adds a project
reference to this checkout's `src/OptiCli.Mcp`, calls `AddOptiCliMcp` (publishing and deleting on, `ProductEditors`
allowed) and, as many real sites have it, `UseStatusCodePagesWithReExecute("/error/{0}")` and `MapControllers()` after
`MapContent()`, then `MapOptiCliMcp()` after those, in `Startup.cs`. It adds `McpFixture.cs`: a GET-only error page, an
API controller with a named route, a route an add-on registers with the CMS (together, what fails with "Duplicate
endpoint name" when the module is mapped before `MapContent()`), and four test
users (`mcp-admin`, `mcp-editor`, `mcp-product`, `mcp-visitor`) with generated passwords in
`App_Data/mcp-test-users.json`, editing rights for WebEditors from the root, and the "Alloy Meet" page hidden from
everyone but administrators and product editors, who may edit it but not publish it. It also adds a client ID metadata
document to `wwwroot` (fetched over http on loopback, which the module allows only in Development), then builds the
site and starts it with `serve.sh` on `http://127.0.0.1:5180` (`MCP_PORT`).

`tests/OptiCli.Mcp.Integration` connects to it as Claude does, with the official MCP SDK's client: discovery from the
401, registration, the CMS login form and the consent page (both scripted), then tool calls. It covers sign-in, the
role gate and CIMD; the module's 401 untouched by the site's error pages, and the site's named route working beside
the module; that hidden content is the same `not_found` as missing content, also when it is named in a reference or
rich text, and that a search doesn't count it; return addresses only on Claude's hosts or loopback, and metadata that
isn't cached; drafts in the editor's name, publish refused without Publish rights, approval sequences, `baseVersion`
conflicts, uploads, `editUrl` and `resolve_url`; review requests without publishing (none where no sequence applies),
content type access rights, restores refused, a branch not published before its master, `list_children` paging; script
in rich text and links refused, properties the edit UI hides or locks (also by an editor descriptor, or a tab only
administrators see) left out and refused, a language only administrators may edit refused to an editor, live content
not moved below another approval sequence; links as markup refused, what a property already has written back while
anything new is refused, SVG files with script and HTML files not uploaded, locks inside local blocks and block lists;
parallel refreshes, refresh token rotation and reuse within and after the grace period, fewer scopes on a refresh,
revocation on the connections page, and a removed role or a disabled account caught at the next refresh (which changes
the test site's own database and changes it back). `McpFixture.cs` also adds `RestrictedBlock`, a block type only
WebAdmins may create; `McpFieldsBlock`, with a property not shown in edit mode, a read-only one, one an editor
descriptor locks for all but WebAdmins and one on a tab that needs Administer, plus rich text, link, URL, ContentArea
and reference properties, a local block and a block list (`McpInnerBlock`, with a property locked for all but
WebAdmins); an "opticli write-back fixture" block with a video's `<iframe>` and an sms: link; the language `de`,
enabled and editable by administrators only; and a folder "opticli find fixture" in the global assets with one folder
editors see and one only administrators see. `setup.sh` gives the site high `RateLimits`, as the tests sign in many
times a minute, and a `RefreshTokenReuseGrace` of 3 seconds, so the reuse test needn't wait a minute. The tests are
skipped unless the site is configured, and delete the content they create:

```sh
SQLCMDPASSWORD=... FRESH=1 tests/fixtures/mcp/setup.sh path/to/AlloyEdge path/to/AlloyMcp
OPTICLI_MCP_IT_URL=http://127.0.0.1:5180 \
OPTICLI_MCP_IT_USERS=path/to/AlloyMcp/App_Data/mcp-test-users.json \
dotnet test tests/OptiCli.Mcp.Integration
```

The tests for a site with the module's defaults, which allows neither publishing nor deleting (publishing refused,
moves of live content and discards of scheduled versions refused, discarding someone else's draft refused), need a
second instance of the same site, started with `OptiCli__Mcp__AllowPublish=false` and `OptiCli__Mcp__AllowDelete=false`
(`serve.sh --no-publish`), and `OPTICLI_MCP_IT_NO_PUBLISH_URL`:

```sh
tests/fixtures/mcp/serve.sh path/to/AlloyMcp --port 5181 --no-publish
OPTICLI_MCP_IT_NO_PUBLISH_URL=http://127.0.0.1:5181 OPTICLI_MCP_IT_URL=... OPTICLI_MCP_IT_USERS=... dotnet test tests/OptiCli.Mcp.Integration
tests/fixtures/mcp/serve.sh path/to/AlloyMcp --port 5181 --stop
```

[CI](.github/workflows/ci.yml) runs the unit tests on Linux, Windows and macOS for every push and pull request.
Issues and pull requests are welcome. Please run the unit tests before sending a change. When a change touches
reads, also run the integration test against a site you have.

### Releasing

Set the new version as `<Version>` in [Directory.Build.props](Directory.Build.props) and as `opticli-version` in
[skill/SKILL.md](skill/SKILL.md), and add the release to [CHANGELOG.md](CHANGELOG.md). Commit, then push a matching tag:

```sh
git tag v0.12.0 && git push origin v0.12.0
```

The [release workflow](.github/workflows/release.yml) checks that the tag matches both versions and is on `main`,
runs the unit tests, checks that the CLI package holds the site agent and starts, and publishes it to nuget.org together
with `OptiCli.Mcp` at the same version plus a `-preview` suffix (set in its project file while it is a preview).

## Licence

[Mozilla Public License 2.0](LICENSE).
