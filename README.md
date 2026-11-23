# opticli

A command-line tool for reading and changing the content of an **Optimizely CMS 12 or 13** site you develop on your
own machine. It is built for AI coding agents (Claude Code and similar) as much as for people. It answers the questions
you would otherwise answer with hand-written SQL or by clicking through the edit UI: what content exists, what a page
contains, which C# class and view render it, where a block is used, what is unpublished. It can also make changes
(set properties, add blocks to a ContentArea, build a Visual Builder page, create, translate, publish), as drafts by
default. On an **Optimizely Commerce** 14 or 15 site it also writes and reads the catalog, prices, stock and markets,
through the running site (see [Optimizely Commerce](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md)).

For editors, a separate package adds an MCP server to the site itself, so they can work on its content with their AI
assistant: `OptiCli.Mcp` for CMS 12, `OptiCli.Mcp.Cms13` for CMS 13. See
[MCP server for editors](#mcp-server-for-editors).

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
  small assembly that adds a few HTTP endpoints under `/_opticli/`. The site's code and files are not changed. With
  `opticli ide on`, the site you start from your IDE loads the agent instead, so changes show there without a restart
  (see [IDE mode](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/serve.md#running-the-site-from-your-ide-ide-mode)).
- Output is compact JSON when stdout is redirected (agents, pipes) and tables on a terminal. Every item has a
  `ref` you can pass back to another command.

In this README, "site agent" means that injected assembly. "Coding agent" means an AI assistant that runs opticli.

## Requirements

- The **.NET 8 SDK** or newer to install opticli. It runs on .NET 8, or on the next newer .NET runtime when 8 isn't
  installed.
- An **Optimizely CMS 12 or 13** site (`EPiServer.CMS.AspNetCore` or `EPiServer.CMS` 12.x or 13.x) whose repository
  you have checked out. What differs on CMS 13 is under [CMS 13](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/cms13.md#cms-13).
- Its database on **SQL Server**: a local instance, LocalDB (Windows), a container, or a remote development database
  such as Azure SQL. For Microsoft Entra ID authentication (`Authentication=Active Directory Default`), sign in with
  `az login` first. The Azure CLI keeps one account per subscription, so signing in with a second account for the
  same subscription replaces the first. To keep both, give the other one its own Azure CLI folder: `export
  AZURE_CONFIG_DIR=~/.azure-other` (in that shell; opticli, the site `serve` starts and `az` all follow it), then
  `az login`. If the site `serve` starts can't sign in while opticli's reads work, its `DefaultAzureCredential` may be
  trying another credential first: `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` makes it use the Azure CLI's sign-in
  (Azure.Identity 1.15 or newer in the site; 1.14 takes only `dev`, older versions ignore it). opticli doesn't set it.
- For writes only:
  - The site must build and start locally in the `Development` environment, with whatever it needs to start (its
    own secrets and services).
  - A CMS 12 site must run on **.NET 8 or newer**, since its site agent is built for .NET 8 (CMS 13 needs .NET 10, and
    has a site agent built for it). Reads work for any CMS 12 or 13 site.

opticli is developed on Linux. Paths and process handling for Windows and macOS are covered, but get less testing.

## Install

Install opticli from NuGet as a global .NET tool:

```sh
dotnet tool install -g OptiCli
opticli --version
```

To update, run `dotnet tool update -g OptiCli`. [CHANGELOG.md](https://github.com/Epinova/opticli/blob/main/CHANGELOG.md) lists what changed in each release.

To install from source instead, build the package and install it from the output folder. That needs the .NET 10 SDK
(the site agent is built for .NET 8 and .NET 10):

```sh
git clone https://github.com/epinova/opticli.git && cd opticli
dotnet pack src/OptiCli -c Release -o artifacts
dotnet tool install -g OptiCli --add-source ./artifacts
```

To update, run `dotnet tool update -g OptiCli --add-source ./artifacts` after a new `pack`.
To run from source without installing: `dotnet run --project src/OptiCli -- <command>`.

## Quick start

Run opticli from anywhere inside the site's repository. It walks up to the solution and picks the web project that
references `EPiServer.CMS.AspNetCore`, `EPiServer.CMS` or `EPiServer.CMS.UI` (or, for a site that lists the CMS's
packages one by one, any `EPiServer.CMS.*` package). It then finds that project's development database, as described
under [Which database](#which-database).

```sh
opticli doctor                                      # what was found and where; does the database answer?
opticli types --kind page                           # page types and how many pages use each
opticli type ArticlePage                            # properties, C# class file, views
opticli resolve https://alloy.example.com/en/news/  # which content a URL shows
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
 "properties":{"Heading":{"type":"String","value":"Hello"}}},"meta":{"source":"db","version":"0.35.0"}}
```

## Using opticli with coding agents

opticli ships a [Claude Code skill](https://github.com/Epinova/opticli/blob/main/skill/SKILL.md) that tells an agent when to use opticli and how to use it
safely. [skill/reference.md](https://github.com/Epinova/opticli/blob/main/skill/reference.md) has the details it needs for writing content.

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
- Never try to get around a refusal, such as a production database's: tell the user.
- Prepare writes to production as a sealed plan and hand the user the command; never run it, fake a terminal for it,
  change a role or edit a seal.

opticli enforces its own safety rules either way (see [Safety model](#safety-model)).

## Tests with Playwright and Node.js

The npm package [`@epinova/opticli`](https://github.com/Epinova/opticli/blob/main/npm/README.md) runs opticli from Node.js with types for its output, and has
Playwright fixtures that start the site with `serve`, apply a seed plan before the tests, give each test content of its
own and remove it after:

```sh
npm install -D @epinova/opticli
```

Each command a test or script needs is a typed method, generated from the CLI (`cli.languages()`,
`cli.whereUsed('123', { pages: true })`), and `OptiCliSync` has the same methods, blocking, for a `playwright.config`
that can't await. It runs the opticli CLI, which you install as usual, at the package's version or newer (a local tool
manifest pins it for CI). See [npm/README.md](https://github.com/Epinova/opticli/blob/main/npm/README.md).

## Which database

opticli works with the project's **development database**: the one the site uses when you run it locally in
`Development`. It reads the connection string from the site's Development configuration and uses a local database
without asking. When that points at a remote server, or the sources disagree, it asks once (`opticli db use`).
Other environments' databases can be added by name and given a role, so production is only ever read.
See [Databases and environments](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/databases.md).

## Safety model

- Writes go through the running site's own CMS API, as drafts unless `--publish`, and every write can be dry-run
  first.
- A remote database is used only when you choose it, as the development database or for one run. A database with the
  role `prod` is only read: content gets to production only as a plan sealed against it, which you run in your own
  terminal.
- Against a shared database, writes stop when the build and the database differ, until you confirm.

See [Safety model](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/safety.md).

## MCP server for editors

The MCP module is a separate NuGet package for the site itself, not for developers' machines. Installed in a site, it
lets **editors** connect an AI assistant that supports MCP servers (Claude, or another MCP client with an OAuth
sign-in) to that site, in any environment, production included. The editor signs in with the site's own login, and the
assistant then works as that editor, with their access rights in the CMS.

| The site runs | Package |
|---|---|
| CMS 12 (`EPiServer.CMS.Core` 12.12.1 and `EPiServer.CMS.UI.Core` 12.16.1 or newer), on .NET 8 or newer | `OptiCli.Mcp` |
| CMS 13 (13.0.0 or newer) | `OptiCli.Mcp.Cms13` |

See [Installing the MCP module](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/install.md).

## Documentation

The reference, one page per area:

| Page | What it covers |
|---|---|
| [Databases and environments](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/databases.md) | which database opticli uses, other environments and roles, `export`, `sync pull`, writing to production |
| [Safety model](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/safety.md) | what opticli refuses and why, shared databases and drift |
| [Commands](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commands.md) | every read and write command, plans, scheduled jobs |
| [New blocks in a ContentArea](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/content-areas.md) | inline and shared blocks, publishing them with their content |
| [CMS projects](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/cms-projects.md) | collecting drafts in a project and publishing it |
| [Database maintenance](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/database-maintenance.md) | site hosts after a restore, a database built from scratch, local users, orphaned content types |
| [CMS 13](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/cms13.md) | what differs on CMS 13, Visual Builder |
| [Optimizely Commerce](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md) | catalogs and catalog content, relations, prices, warehouses, stock and markets, through the site; catalog refs |
| [serve and env](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/serve.md) | running the site with the site agent, IDE mode |
| [Output and files](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/output.md) | the JSON envelope, exit codes, where opticli keeps its files |
| [MCP server for editors](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/install.md) | installing the MCP module |
| [Configuring the MCP module](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/configuration.md) | options, endpoints, another MCP server beside it, connections and audit log |
| [MCP tools](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/tools.md) | the module's tools, projects, a site's own tools |
| [Connecting an assistant](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/connecting.md) | Claude and other MCP clients |
| [MCP security model](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/security.md) | sign-in, tokens, what an editor's assistant can and can't do, headers |

[The guides](https://epinova.github.io/opticli/guides/) walk through one task each, for editors, CLI developers and
MCP integrators. [CHANGELOG.md](https://github.com/Epinova/opticli/blob/main/CHANGELOG.md) lists what changed in each release.

## Supported versions

- Optimizely CMS 12 (`EPiServer.CMS.AspNetCore` 12.x): reads on any runtime; writes on a site running .NET 8 or newer.
- Optimizely CMS 13 (`EPiServer.CMS` 13.x, .NET 10), fresh installs and databases upgraded from CMS 12: reads and writes,
  Visual Builder included (see [CMS 13](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/cms13.md#cms-13)).
- Optimizely Commerce 14 (on CMS 12) and 15 (on CMS 13): catalog content, relations, prices, warehouses, stock and
  markets, written and read through the running site (see [Optimizely Commerce](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md));
  `sql` reads Commerce's tables only as SQL. The MCP module for editors has no Commerce tools.
- The MCP server for editors: `OptiCli.Mcp` on CMS 12 (12.12.1 / CMS UI 12.16.1 or newer, .NET 8 or newer),
  `OptiCli.Mcp.Cms13` on CMS 13 (13.0.0 or newer); see [MCP server for editors](#mcp-server-for-editors).
- SQL Server on the local machine (including LocalDB and containers), or a remote SQL Server / Azure SQL
  development database.
- Not supported:
  - CMS 11, Commerce 13 and older, Commerce's orders, customers and promotions, Forms data and search indexes;
  - writes to test and staging databases; production only through a sealed plan the user runs (see
    [Writing to production](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/databases.md#writing-to-production)).

## Known limitations

- Rules that live only in code are read from the C# sources, not evaluated:
  - `[AllowedTypes]` is parsed. Editor descriptors and metadata extenders that change allowed types at runtime are
    not; a `uiHint` flags the property. The MCP module's `get_content_type` does give them: it runs in the site and
    asks the CMS UI's own metadata.
  - Validation attributes are only pointed at (file and line).
  - URLs a site rewrites in code (custom segments, partial routing) show as the plain content path.
  - `type` lists views, but not controllers.
- Content types are matched to C# classes by GUID or name with a lightweight source scan, not a compiler. Unusual
  declarations may be missed.
- `serve` runs the existing build output; it doesn't build unless `--build` is given.
- Output field names may still change before 1.0.

## Development

[docs/development.md](https://github.com/Epinova/opticli/blob/main/docs/development.md) describes the projects, how to build and test, the test sites and how to
release. Issues and pull requests are welcome. Please run the unit tests before sending a change. When a change touches
reads, also run the integration test against a site you have.

## Licence

[Mozilla Public License 2.0](https://github.com/Epinova/opticli/blob/main/LICENSE).
