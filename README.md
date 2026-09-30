# opticli

A command-line tool for reading and changing the content of an **Optimizely CMS 12** site you develop on your own
machine. It is built for AI coding agents (Claude Code and similar) as much as for people. It answers the questions
you would otherwise answer with hand-written SQL or by clicking through the edit UI: what content exists, what a page
contains, which C# class and view render it, where a block is used, what is unpublished. It can also make changes
(set properties, add blocks to a ContentArea, create, translate, publish), as drafts by default.

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

To update, run `dotnet tool update -g OptiCli`.

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
 "properties":{"Heading":{"type":"String","value":"Hello"}}},"meta":{"source":"db","version":"0.3.0"}}
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
when an installed skill is older than the opticli you run.

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
   Development configuration, in ASP.NET Core's order. The first source that has it wins: launch profiles, user
   secrets, `appsettings.Development.json`, `appsettings.json`. If that database is **local**, opticli uses it
   without asking.
2. Otherwise opticli asks once. That happens when:
   - the connection string points at a remote server such as Azure SQL;
   - several launch profiles disagree;
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

`serve` runs the site only against a local database or the chosen development database. Against a remote one it
turns off, for that run:
- the site's scheduler;
- automatic schema updates;
- content type sync.

So a local build can't change a shared database just by starting.

## Safety model

| Rule | Enforced by |
|---|---|
| A remote database is used only when the user chose it as the development database, or for one run with `--db` or `--connection`, with a warning on every response. Only literal loopback names (`localhost`, `127.0.0.1`, `::1`, `.`, `(local)`) and LocalDB count as local. A host name that resolves to 127.0.0.1 counts as remote. | CLI, before connecting |
| The site started by `serve` is pinned to the database the CLI reads. It refuses to start against any remote database except the approved development one. There it runs without scheduler, schema updates or content type sync. If the site turns hosting startups off in its code, so the pin can't run, it refuses to start. | CLI + site agent at startup |
| The site agent answers only loopback callers that send the per-run token, and only in `Development`. The token is kept in a state file only your user can read. | Site agent, per request |
| Writes create drafts; publishing needs `--publish` (or `publish`). Saves are attributed to the user `opticli`. | CLI + site agent |
| `delete` moves content to the recycle bin; nothing empties it. Site roots, start pages, asset roots and anything above them can't be moved or deleted. | Site agent |
| Reads use fixed queries. `sql` accepts a single SELECT, refuses anything that writes, runs code or reaches another database, and always runs in a rolled-back transaction. Personal-data tables (form submissions, users) need `--include-personal-data`. | CLI |
| Passwords are never printed; `doctor` redacts connection strings. The exception is `opticli env`: it prints the per-run token, and with `--include-connection` the connection string too. | CLI |

Things these rules can't see:
- A port-forward or tunnel on `localhost,<port>` to a remote server looks local to opticli.
- So does a hosts-file entry or SQL client alias that maps a loopback name elsewhere.

Don't point opticli at such a connection unless you mean to write to what is behind it.

## Commands

`opticli <command> --help` lists every option with an example, and a bare `opticli` shows an overview.

### Reading

| Command | Answers |
|---|---|
| `doctor` | project, connection string candidates, database, schema version, site agent, installed skill |
| `db list`, `db use`, `db forget` | the development database (see [Which database](#which-database)) |
| `sites`, `languages` | site definitions and hosts; language branches |
| `types [--kind] [--unused] [--sort]` | content types with instance counts |
| `type <name>` | properties (type, culture-specific, required, tab, order, source line, `[AllowedTypes]`), C# class file, views |
| `allowed-in <type>` | which ContentArea/reference properties accept a type, from `[AllowedTypes]` in code |
| `get <ref> [--lang] [--version] [--fields] [--expand]` | one item, typed and decoded (ContentAreas, local blocks, rich-text links) |
| `tree`, `children`, `ancestors` | the content tree |
| `find --type T [--where Prop=value] [--under] [--status]` | items of a type, filtered |
| `search <text> [--in names\|strings\|all]` | names and text properties containing a string |
| `where-used <ref> [--pages]` | ContentAreas, references, links and rich text pointing at an item (`--pages`: through nested blocks up to pages) |
| `resolve <url>`, `url <ref>` | URL to content, and content to URL per language |
| `versions <ref>`, `drafts [--since] [--by] [--kind] [--type]` | version history; unpublished changes |
| `blob <ref>` | where a media file lives on disk |
| `sql "<SELECT …>"` | anything else, read-only |

A `<ref>` is a content id (`123`), a version (`123_456`), a content GUID, or a URL or path (`/en/about/`,
`https://host/en/about/`). Content from a content provider, such as images from a DAM, shows up as `63__provider`. You
can pass that form back in property values, ContentArea items and links. The id before `__` is local to one database;
the GUID is the same in every environment. `--lang <code>` picks the language branch. The default is the master
language, or the language the URL selects.

### Writing (needs `opticli serve`)

These commands write:
- `set`, `create`, `area` (add, remove or move ContentArea items), `block create` and `translate` save a draft
  unless `--publish` is given.
- `publish`, `move` and `delete` (to the recycle bin).
- `apply plan.json` runs several operations validated together. Later operations can refer to an item an earlier
  one created as `$id`.

Every write command takes `--dry-run`. Structured values use `--values`, e.g.
`--values '{"MainArea":[{"ref":"456"}]}'`. `set` and `area` check that nobody saved a newer version in the meantime
(exit 5 on a conflict). `--base-version <id>` pins the version the change is based on, and `--force` skips the check.
[skill/reference.md](skill/reference.md) documents value syntax and the plan format.

## serve and env

`opticli serve` runs the site's existing build output in `Development` on `http://127.0.0.1:<port>`, with the site
agent injected, and returns once the site agent answers.

| Option | Default |
|---|---|
| `--output <dll>` | the newest `bin/{Debug,Release}/<tfm>/<AssemblyName>.dll`, or `output` from the user config |
| `--port <n>` | `port` from the user config, else 5199, else the first free port up to 5299 |
| `--build` | off: opticli warns when sources are newer than the build, and `--build` runs `dotnet build` first |
| `--timeout <s>` | 180 seconds to wait for the site to answer |
| `--foreground` | off: the site runs in the background; with it, the site's output streams until Ctrl+C |

`serve --status`, `serve --logs [--tail N]` and `serve --stop` manage the running site.

To run the site yourself (IDE, `dotnet run`, hot reload), `opticli env` prints the variables `serve` would set:
- `--format shell|powershell|dotenv|json|launchSettings` picks the format.
- `--port` picks the port.

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
| `OPTICLI_REMOTE_DB` | against a remote development database | the one remote database the site agent accepts; turns on shared-database mode |

### How the injection works

`serve` starts `dotnet <Site>.dll` with `DOTNET_STARTUP_HOOKS` pointing at `OptiCli.Agent.dll`, which ships in the
tool package under `agent/`. The startup hook makes the site agent's assembly resolvable and appends it to
`ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`. The hosting startup then:
1. pins the connection string, added as the last configuration source and as a `PostConfigure` of the CMS's data
   access options;
2. fails the start if the effective connection string is neither local nor the approved development database;
3. maps `/_opticli/v1/*` ahead of the site's own middleware.

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
| 4 | `unreachable` | database or site agent not reachable, or a query failed on the server |
| 5 | `conflict` / `validation` | a newer version exists, or the CMS rejected the values |
| 6 | `needs_selection` | the user must choose the development database first (`error.details.choices`) |

## Files

| What | Linux / macOS | Windows |
|---|---|---|
| User config | `$XDG_CONFIG_HOME/opticli/config.json` (default `~/.config/opticli/config.json`) | `%APPDATA%\opticli\config.json` |
| `serve` state and logs | `$XDG_STATE_HOME/opticli/` (default `~/.local/state/opticli/`) | `%LOCALAPPDATA%\opticli\` |

You can set per-project defaults in the user config. `opticli db use` adds the chosen `database` there.

```json
{"projects": {"/abs/path/to/Site": {"connection": "...", "output": "bin/Debug/net8.0/Site.dll", "port": 5199}}}
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
| `src/OptiCli.Protocol` | request and response types shared by the CLI and the site agent (source-linked into both) |
| `skill/` | the coding-agent skill, embedded in `opticli.dll` |
| `tests/` | unit tests for Core and the site agent; `OptiCli.Integration`, a test against a real site |

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

Some differences the database can't reproduce by design, such as URL segments a site drops in code. Those are listed
with the reason in `tests/OptiCli.Integration/Comparison/KnownDifferences.cs`; any other mismatch fails the run.

Issues and pull requests are welcome. Please run the unit tests before sending a change. When a change touches
reads, also run the integration test against a site you have.

### Releasing

Set the new version as `<Version>` in [Directory.Build.props](Directory.Build.props) and as `opticli-version` in
[skill/SKILL.md](skill/SKILL.md). Commit, then push a matching tag:

```sh
git tag v0.4.0 && git push origin v0.4.0
```

The [release workflow](.github/workflows/release.yml) checks that the tag matches both versions, runs the unit
tests and publishes the package to nuget.org.

## Licence

[Mozilla Public License 2.0](LICENSE).
