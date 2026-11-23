# Development

```sh
dotnet build opticli.slnx       # warnings are errors
dotnet test opticli.slnx
cd npm && npm ci && npm test    # the npm package, against a stand-in opticli
```

The EPiServer packages come from Optimizely's public NuGet feed, which [nuget.config](../nuget.config) already lists.
Building needs the .NET 10 SDK and the .NET 8 runtime (CI installs both SDKs): the site agent, `OptiCli.Cms` and `OptiCli.Agent.Tests` build twice, for
CMS 12 (net8.0, `EPiServer.CMS.Core` 12.0.3) and CMS 13 (net10.0, 13.0.0, with `CMS13` defined), and CMS-specific code
stays in their `Compat/` adapter files. The MCP module builds the same way, as two packages (`OptiCli.Mcp` for CMS 12
from 12.12.1, `OptiCli.Mcp.Cms13` for CMS 13), and `OptiCli.Mcp.Tests` runs against each.

| Project | What it is |
|---|---|
| `src/OptiCli` | the `opticli` command line: commands, options, help text |
| `src/OptiCli.Core` | everything the CLI does: discovery, connection resolution and safety, SQL readers, decoders, output, `serve` |
| `src/OptiCli.Agent` | the site agent, loaded into the site process; built for CMS 12 and CMS 13, both copied into the tool package (`agent/cms12/`, `agent/cms13/`) |
| `src/OptiCli.Agent.Commerce` | the Commerce agent: the site agent's routes below `/commerce` (catalog refs by code, prices, warehouses, stock, markets). Built for Commerce 14 (net8.0, `EPiServer.Commerce.Core` 14.0.3) and 15 (net10.0, 15.0.0), each copied next to the site agent of its major; the site agent loads it only into a site whose `.deps.json` has `EPiServer.Commerce.Core` (`OPTICLI_COMMERCE=off` in the site's environment turns it off) |
| `src/OptiCli.Cms` | the content operations (read, create, draft, publish, ...) the site agent and the MCP module run, for a developer or a signed-in editor; source-linked into both |
| `src/OptiCli.Mcp` | the MCP server for editors, the `OptiCli.Mcp` package (CMS 12): OAuth authorization server, tools, connections page |
| `src/OptiCli.Mcp.Cms13` | the `OptiCli.Mcp.Cms13` package: the same sources built for CMS 13 (net10.0, `CMS13` defined), with the same assembly name and public API |
| `src/OptiCli.Protocol` | request and response types shared by the CLI, the site agent and the MCP module (source-linked) |
| `skill/` | the coding-agent skill, embedded in `opticli.dll` |
| `npm/` | the `@epinova/opticli` package: a typed Node.js API and Playwright fixtures (see [The npm package](#the-npm-package)) |
| `mcp-extension/` | the Claude Desktop extension for the MCP module (`opticli-cms.mcpb`), attached to each GitHub release |
| `docs/` | the reference pages and the how-to guides on the landing page |
| `tests/` | unit tests for the CLI (`OptiCli.Tests`), Core, the site agent and the MCP module (both on both CMS majors); `OptiCli.Integration` and `OptiCli.Mcp.Integration`, tests against a real site; `fixtures/`, the test sites' scripts |

The unit tests use generic fixtures and need no database or site, also for CMS 13 (one, `LocalDbTests`, needs LocalDB
and skips itself elsewhere). The integration test is an oracle. It samples content
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
| `OPTICLI_IT_PLAN` | a plan whose content is always compared, on top of the sample (the edge-case plan below); an absolute path, as the tests run in their build folder |

Some differences the database can't reproduce by design, such as URL segments a site drops in code. Those are listed
with the reason in `tests/OptiCli.Integration/Comparison/KnownDifferences.cs`; any other mismatch fails the run.

## The edge-case site

A sample site lacks much of what real sites have: fetch-data pages, a site whose start page is under another site's (CMS 12 only: CMS
13 refuses it),
simple addresses on several sites, culture-specific properties in shared and local blocks, personalized ContentAreas,
an approval sequence, language fallback settings, a media type for PDF files, three sites whose hosts the site host
tests change (and put back), CMS projects (one with an item marked Ready to publish and one that isn't), and a scheduled
job of its own. `tests/fixtures/edge-cases/` builds them from an Alloy
site (`dotnet new epi-alloy-mvc`) without changing it. `setup.sh` copies the site and its database, adds
`EdgeCasesFixture.cs` (the extra content types, plus a startup module for what a plan can't create) and
`JobsFixture.cs`, `UsersFixture.cs`, `OrphansFixture.cs`, `../projects/ProjectsFixture.cs` and `Cms12Fixture.cs` (on
CMS 13 `Cms13Fixture.cs`: the CMS-specific parts, such as the hosts sites), and applies `edge-cases.plan.json`. At
startup the fixture makes the projects "Edge project" (the approval root's draft) and "Edge campaign" (two pages of its
own, one draft marked ready and one not). `ProjectsFixture.cs` is a site handler that runs during a project publish: it
makes and publishes a page next to each published "opticli-pp-notify ..." page, and fails the first publish of an
"opticli-pp-flaky ..." page as CMS 12's task executor does, which the project tests use. `JobsFixture.cs` has "opticli test job", a manual, stoppable job
that writes a status message a second for 3 steps (the number in `App_Data/opticli-job-steps`, if it exists) and fails
when `App_Data/opticli-job-fail` exists; the `jobs` tests run it. `OrphansFixture.cs` leaves what removed code leaves behind: the page types `EdgeRemovedPage` (whose class doesn't
exist, used by one page), `EdgeTrashedPage` (its only page is in the recycle bin) and `EdgeRemovedEmptyPage` (used by
nothing, with a property `Teaser` whose block type `EdgeRemovedBlock` has no class either), the EdgePage properties
`EdgeRemovedText` (one value) and `EdgeRemovedEmptyText` (none) that its class doesn't declare, and `EdgeAdminPage`, a
type made in admin mode. It points `OPTICLI_REMOVALS_FILE` at the site's `App_Data/opticli-removals.jsonl`, so the
tests' removals stay out of your own `removals.jsonl`. They are made at the second start, once the plan's content exists, and again at every start
(the CMS removes an unused type whose class is gone when the site starts); the tests that remove them make them again
with `POST /opticli-fixture/orphans`, which the fixture answers on loopback only. `UsersFixture.cs` makes the user `edge-fixture-user` at startup (WebEditors, no password, not
made by opticli), which the `users` tests check `users remove` refuses; they also sign in on `/util/login` as a user
they add. With `OPTICLI_FIXTURE_SCHEDULER=on` in the environment
of `serve`, it also turns the scheduler on in the site's own configuration, as a real site has it (Alloy turns it off in
Development), to see that `serve` turns it off again and `serve --scheduler` doesn't. Run `setup.sh` again to update
the content: the plan is applied with `--update-existing`, and the database copy is kept unless `FRESH=1`.

```sh
SQLCMDPASSWORD=... tests/fixtures/edge-cases/setup.sh path/to/Alloy path/to/AlloyEdge alloy alloy-edge
OPTICLI_IT_PROJECT=path/to/AlloyEdge \
OPTICLI_IT_PLAN=$PWD/tests/fixtures/edge-cases/edge-cases.plan.json \
dotnet test tests/OptiCli.Integration
```

The integration tests run one at a time, since the write tests make and remove scratch content on the same site.
Tests of someone else's draft have the fixture save a version again as another user, or mark it ready as that user
with `&ready=1` (`POST /opticli-fixture/hand-over`, loopback only), through the CMS. The project tests turn the approval
sequence's "require a comment" on and off again with `POST /opticli-fixture/approval-comment?required=1|0` (loopback
only). Their test of a site with projects turned off runs only against a site served with
`EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=false` and `EPiServer__CmsUI__ProjectUI__ProjectGadgetEnabled=false`
(`--filter "FullyQualifiedName~Projects_are_refused"`); elsewhere it checks that projects work.

How a write makes a new block in a ContentArea follows the site's edit UI (`UIOptions.InlineBlocksInContentAreaEnabled`,
`serve --status` shows it as `inlineBlocks`): `InlineBlockWriteTests` run where the site makes inline blocks (CMS 13's
default), `SharedAreaBlockIntegrationTests` where it makes shared blocks in the page's "For this page" folder (CMS 12's
default), and each is skipped, saying how to restart the site, on a site in the other mode. The tests don't restart it:
run the suite once per mode, serving the site with the setting through `serve`'s environment. Inline blocks a page
already has (as another tool may put on a site whose edit UI makes none) come from the fixture's
`POST /opticli-fixture/inline-block`, and `GET /opticli-fixture/inline-blocks` says the mode (loopback only).

```sh
opticli serve --stop --project /demo/AlloyEdge
EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled=true opticli serve --project /demo/AlloyEdge    # inline; false: shared
OPTICLI_IT_PROJECT=/demo/AlloyEdge OPTICLI_IT_PLAN=$PWD/tests/fixtures/edge-cases/edge-cases.plan.json dotnet test tests/OptiCli.Integration
```

`drift.sh` in the same folder runs the shared-database scenario on copies of the edge-case site and its database. It
reaches the copy through a host name instead of a loopback name (`<hostname>.localhost` resolves to the loopback
address, but counts as remote), so `serve` runs in shared mode. It then changes the copy's code and checks each step:
nothing differs, local ahead, the database ahead, and an EF Core migration that `serve` refuses. Against that copy,
`DriftTests` checks that writes stop on drift until its fingerprint confirms it. It runs on CMS 12 and CMS 13 copies
alike.

```sh
SQLCMDPASSWORD=... tests/fixtures/edge-cases/drift.sh path/to/AlloyEdge path/to/AlloyDrift
```

`drift.sh` keeps opticli's config and state for the copy in `<copy>/.opticli` and chooses the copy's database there,
so start the copy and run `DriftTests` with the same `XDG_CONFIG_HOME` and `XDG_STATE_HOME` (the script prints both
commands at the end; the site needs `serve --allow-pending-migrations`). Its databases default to CMS 12's; on CMS 13
name them: `drift.sh path/to/AlloyEdge13 path/to/AlloyDrift13 alloy-edge13 alloy-edge13-shared`.

## The Commerce test sites

`tests/fixtures/commerce/setup.sh [12|13|both]` builds two Commerce sites from Optimizely's template (`dotnet new
epi-commerce-empty`): `/demo/Commerce12` (CMS 12, Commerce 14.48.0, net8.0) and `/demo/Commerce13` (CMS 13, Commerce
15.5.0, net10.0), each on two empty databases of its own, the CMS's and Commerce's (`opticli-commerce12` and
`opticli-commerce12-ecf`, `opticli-commerce13` and `opticli-commerce13-ecf`), which the CMS and Commerce fill at the first
start. `CommerceFixture.cs` gives both a small content model: a start page with `Catalog` and `FeaturedProduct`
references, an image media type, and a catalog model (`FashionNode`, `FashionProduct` with a `MainImage`,
`FashionVariant`, `FashionBundle`). There is no content: the tests make theirs. `FRESH=1` makes both again from nothing
(it drops the databases, and refuses any not named `opticli-commerce...`); the comment at the top lists the versions,
folders, databases and ports.

The Commerce tests are `[CommerceSiteFact]`s: they run where `OPTICLI_IT_PROJECT` is a site whose restored project has
`EPiServer.Commerce.Core`, and skip elsewhere. Run them alone (the other integration tests need the edge-case site's
content), with the project path as `serve` was given it:

```sh
SQLCMDPASSWORD=... FRESH=1 tests/fixtures/commerce/setup.sh
opticli serve --project /demo/Commerce12
OPTICLI_IT_PROJECT=/demo/Commerce12 dotnet test tests/OptiCli.Integration --filter "FullyQualifiedName~Commerce"
```

## The CMS 13 test sites

`tests/fixtures/cms13/setup.sh [fresh|upgraded|both]` (default both) builds two CMS 13 Alloy sites: a fresh one (`dotnet new epi-alloy-mvc`, EPiServer.Templates
2.0.1, on an empty database the CMS fills at its first start; `/demo/Alloy13`, `alloy13` by default) and an upgraded one
(the same code on a copy of a CMS 12 Alloy site's database and media, which CMS 13 upgrades at its first start;
`/demo/Alloy13Up`, `alloy13-upgraded`). The CMS 12 site and database it copies are only read. Both get
`VisualBuilderFixture.cs`: an experience type, section types, element types, a contract, display templates, and content
made through the CMS's API: two experiences (one with a Swedish branch), a shared element, blueprints, and a published
and a draft content variation. Run it again to update the fixture; `FRESH=1` makes everything again, and the comment at
the top of the script lists the settings (`CMS_VERSION`, the folders, databases and ports).

The CMS 13 edge-case site is `tests/fixtures/edge-cases/setup.sh` run from the fresh CMS 13 site, and has the Visual
Builder fixture too. The integration tests run on all of them (`VisualBuilderTests` and `VisualBuilderWriteTests` where
the fixture is, the rest everywhere):

```sh
SQLCMDPASSWORD=... FRESH=1 tests/fixtures/cms13/setup.sh
SQLCMDPASSWORD=... FRESH=1 tests/fixtures/edge-cases/setup.sh /demo/Alloy13 /demo/AlloyEdge13 alloy13 alloy-edge13
(cd /demo/AlloyEdge13 && opticli serve)
OPTICLI_IT_PROJECT=/demo/AlloyEdge13 OPTICLI_IT_PLAN=$PWD/tests/fixtures/edge-cases/edge-cases.plan.json dotnet test tests/OptiCli.Integration
```

Scripts in `tests/fixtures/cms13/` run whole command sets against one site and save each output, with a `summary.tsv`:
`read-battery.sh <site> <out>` every read command (`compare-battery.py <a> <b>` compares two runs, e.g. an upgraded
CMS 13 site with the CMS 12 site it came from, or a CMS 12 site before and after a change), `write-battery.sh` the
content writes, and `vb-write-battery.sh` Visual Builder writes. The write batteries start and stop `serve` themselves,
check that the site renders what they wrote, and leave their content in the recycle bin.

## The content sync fixtures

`tests/fixtures/sync/` makes databases to export from and to, as copies of an edge-case site's database (`alloy-edge*`
only; the base database and the site's own blobs are only read). `make-source.sh` makes the source,
`alloy-edge-source`, and changes it through opticli the way editors change another environment (`source-changes.plan.json`:
new pages with a cycle between two of them, a block in a "For this page" folder, media, an edited page, a draft, a
move, a page in the recycle bin, a new language branch; on CMS 13 also a Visual Builder experience). `round-trip.sh`
exports part of it and applies the plan to a fresh copy of the base database whose ids differ from the source's, then
compares every item and language branch the plan writes in both databases (`get --full --all-properties`, ids,
versions and who saved when aside; publish dates included), and applies it again, which must change nothing.
`sync-pull.sh` tests `sync pull` on copies of its own (`alloy-edge-sync-source` and `alloy-edge-sync-target`, ids
shifted): the baseline (a page in the target's recycle bin restored), then make-source.sh's changes and
`sync-changes.plan.json`'s (a folder in a "For this page" folder, a page scheduled for 2030, a page of a type the target
lacks) pulled with `--delete` and compared item by item as round-trip.sh compares them, a second pull that applies
nothing, a conflict and `--overwrite-local`, a local change kept, a local trash that is a conflict until
`--overwrite-local` restores it, a language branch made in both, and a local draft behind values found the same. They stop the sites they start, unless told to keep them; `round-trip.sh` drops its target unless
`KEEP_TARGET=1`, `sync-pull.sh` both copies unless `KEEP=1`. Each script's comment lists its modes and settings
(`OPTICLI` picks the opticli to run, e.g. a wrapper around this checkout's build).

```sh
SQLCMDPASSWORD=... tests/fixtures/sync/round-trip.sh                       # CMS 12: /demo/AlloyEdge, alloy-edge
SQLCMDPASSWORD=... tests/fixtures/sync/round-trip.sh /demo/AlloyEdge13 alloy-edge13 alloy-edge-13-source alloy-edge-13-target
SQLCMDPASSWORD=... tests/fixtures/sync/sync-pull.sh                        # CMS 12
SQLCMDPASSWORD=... tests/fixtures/sync/sync-pull.sh /demo/AlloyEdge13 alloy-edge13 alloy-edge-13-sync-source alloy-edge-13-sync-target
```

## The MCP test site

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

It also adds `McpCustomToolsFixture.cs`, for the [custom tools](reference/mcp/tools.md#custom-tools) tests: test-only tools of the site's own,
all named `fixture_...`, chained on `AddOptiCliMcp(...)` with `WithTools`, `WithWriteTool` and `WithInstructions` (read
tools that load with `IContentLoader` and filter with the editor, and write tools that update, create, move and trash
Alloy's pages and blocks); a server of the site's own at `/site-mcp`, which registers the read tools' class too; and a
clock a request can move forward in Development (header `X-Opticli-Fixture-Clock`), so the plan token's expiry can be
tested without waiting for it.

Run from a CMS 13 edge-case site (`setup.sh` reads the CMS version from its project), it references
`src/OptiCli.Mcp.Cms13` instead and adds `McpVisualBuilderFixture.cs`: Visual Builder experiences with inline and shared
sections and elements, some of them readable by administrators only, and an element type with properties the edit UI
hides or locks. The integration tests take the edit UI's path from the site's CMS version, and their Visual Builder tests
run only there:

```sh
SQLCMDPASSWORD=... FRESH=1 MCP_PORT=5200 tests/fixtures/mcp/setup.sh path/to/AlloyEdge13 path/to/AlloyMcp13 alloy-edge13 alloy-mcp13
```

`tests/OptiCli.Mcp.Integration` connects to it as Claude does, with the official MCP SDK's client: discovery from the
401, registration, the CMS login form and the consent page (both scripted), then tool calls. It covers sign-in, the
role gate and CIMD; the module's 401 untouched by the site's error pages, and the site's named route working beside
the module; that hidden content is the same `not_found` as missing content, also when it is named in a reference or
rich text, and that a search doesn't count it; return addresses only on Claude's hosts or loopback, and metadata that
isn't cached; drafts in the editor's name, publish refused without Publish rights, approval sequences, `baseVersion`
conflicts, uploads, images shown (a large one scaled down by the site's image service), `editUrl`, `previewUrl` (a draft shown to the editor signed in, and to nobody else), the assistant's preview links (on the default instance; none, and no preview path, where they are turned off), and `resolve_url`; review requests without publishing (none where no sequence applies),
content type access rights, restores refused, a branch not published before its master, `list_children` paging; script
in rich text and links refused, allowed types an editor descriptor sets given by `get_content_type` and kept to on
writes, properties the edit UI hides or locks (also by an editor descriptor, or a tab only
administrators see) left out and refused, a language only administrators may edit refused to an editor, live content
not moved below another approval sequence; links as markup refused, what a property already has written back while
anything new is refused, SVG files with script and HTML files not uploaded, locks inside local blocks and block lists;
parallel refreshes, refresh token rotation and reuse within and after the grace period, fewer scopes on a refresh,
revocation on the connections page, and a removed role or a disabled account caught at the next refresh (which changes
the test site's own database and changes it back). For the site's own tools: hidden content kept out of their results
and plans, dry runs and runs with planned content, a run that stops midway, a token refused after a change, a replay,
another editor's or tool's use, or 30 minutes, the published-version rule, `MaxPlanSteps`, the scopes and gates, the
per-step audit lines, and none of them at `/site-mcp`. CMS projects (`ProjectTests`): making, filling, marking ready
and publishing them, a colleague's ready item, the publish scope, and site tools' plans into a project. `McpFixture.cs` also adds `RestrictedBlock`, a block type only
WebAdmins may create; `McpFieldsBlock`, with a property not shown in edit mode, a read-only one, one an editor
descriptor locks for all but WebAdmins and one on a tab that needs Administer, plus rich text, link, URL, ContentArea
and reference properties (one ContentArea with allowed types an editor descriptor sets for its UIHint), a local block and a block list (`McpInnerBlock`, with a property locked for all but
WebAdmins and one on the tab that needs Administer); an "opticli write-back fixture" block with a video's `<iframe>` and
an sms: link; the language `de`, enabled and editable by administrators only; and a folder "opticli find fixture" in the
global assets with one folder editors see and one only administrators see; save handlers of the site's own that file a
page named "opticli-filed …" under an "opticli archive" page they create and publish, and clear a teaser text, for the
`siteChanges` tests; and an "opticli inert fixture" block with script inside a `<textarea>`. `setup.sh` gives the site high `RateLimits`,
as the tests sign in many times a minute, and a `RefreshTokenReuseGrace` of 3 seconds, so the reuse test needn't wait a
minute. The tests are skipped unless the site is configured, and delete the content they create:

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

The site's edit UI makes a new block in a ContentArea inline or as a shared block in the page's "For this page" folder
(CMS 13's and CMS 12's defaults), and the module follows it. `InlineBlockTests` run in the first mode,
`SharedAreaBlockTests` mostly in the second, each skipped on a site in the other mode (the edge-case fixture's
`/opticli-fixture/inline-blocks` says which). Run the suite once per mode, restarting the default instance with
`serve.sh --inline-blocks on|off`:

```sh
tests/fixtures/mcp/serve.sh path/to/AlloyMcp --port 5180 --inline-blocks on     # off: shared blocks
OPTICLI_MCP_IT_URL=http://127.0.0.1:5180 OPTICLI_MCP_IT_USERS=... dotnet test tests/OptiCli.Mcp.Integration
```

The assistant's preview links are on by default, so the default instance runs their tests (a draft's link opened
with no cookie, refused with another path, a query, another spelling, another host or another method, still the
version after publishing, gone in the recycle bin, after a revocation and once the editor can't read the version, and
the CMS's redirect to its login never passed on). The test for a site that turned them off (no link, no preview path)
needs a third instance, started with `OptiCli__Mcp__AllowPreviewLinks=false` (`serve.sh --no-preview-links`), and
`OPTICLI_MCP_IT_NO_PREVIEW_LINKS_URL`:

```sh
tests/fixtures/mcp/serve.sh path/to/AlloyMcp --port 5182 --no-preview-links
OPTICLI_MCP_IT_NO_PREVIEW_LINKS_URL=http://127.0.0.1:5182 OPTICLI_MCP_IT_URL=... OPTICLI_MCP_IT_USERS=... dotnet test tests/OptiCli.Mcp.Integration
tests/fixtures/mcp/serve.sh path/to/AlloyMcp --port 5182 --stop
```

A CMS 13 site served with `EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=true` needs
`OPTICLI_MCP_IT_EDITOR_UI=projectMode`. `tests/fixtures/projects/check-mcp.sh [--all] [--project-mode] <site>:<port> ...`
rebuilds MCP test sites with this checkout's module and fixtures and runs `ProjectTests` (or, with `--all`, everything)
on `<port>` and on `<port>+1` with publishing off.

## The schema output tests

`opticli schema --json` (a hidden command) describes every command: its arguments, options and, as JSON Schema, the
JSON result it prints, per mode where an option changes it. The npm package's types are generated from it (see
[The npm package](#the-npm-package) below). `SchemaOutputTests` in `tests/OptiCli.Integration` checks that the CLI
prints what it declares: it runs this checkout's `opticli.dll` as a process (`src/OptiCli/bin/<the test's
configuration>/net8.0/`, built first, or `OPTICLI_IT_CLI`) and validates each JSON result against the schema of the
same build, with JsonSchema.Net (JSON Schema 2020-12, `uuid` and `date-time` formats asserted), picking the mode as
opticli does. Errors are checked against `ErrorBody`, the envelope against the npm package's, and so are the npm
package's own types where the schema is open (`get`'s property values, `sql`'s cells, a plan step's result by its op,
what `create` and `set` promise). A mismatch fails the test, naming the command line, the place and the value.

- `The_validator_fails_output_that_differs_from_the_declared_type` needs no site: it shows that a wrong type, an
  undeclared or missing property, a value outside a closed set or a bad format fails. CI runs only this one; the
  others skip there.
- `Reads_print_what_the_schema_declares`: every read command in every mode, with refs the site's own output gives, on
  any site, running or not. It fails for a mode that printed nothing, except where the site has nothing to show (the
  log names them). It runs `serve --stop` only on a stopped site.
- `Writes_print_what_the_schema_declares`: every write, dry run and real, on the edge-case site with its agent running.
  Its content goes to the recycle bin; projects, users, hosts and languages it adds are removed again.
- `Local_commands_print_what_the_schema_declares`: `db`, `sites primary` and `apply --seal-for` with a user config of
  their own (the serve state is the real one, so a running agent is found); `ide` and `env` on a copy of the site's
  `.csproj` and `appsettings*.json` with a state folder of its own; `skill install` with a home folder of its own. Your
  own config, state and site stay as they are.

| Variable | Meaning |
|---|---|
| `OPTICLI_IT_PROJECT` | the site, as for the other integration tests |
| `OPTICLI_IT_CLI` | the opticli to run and take the schema from: an `opticli.dll` or a command (default: this checkout's build) |
| `OPTICLI_IT_SEAL_DB` | another local database on the site's SQL Server with the same content model (a copy of the site's database), read only, that the local test's `apply --seal-for` names as production; the site's own can't be, since a role belongs to a server and database. Without it, that check is left out |
| `OPTICLI_IT_SCHEMA_LOG` | append every command line run, with its mode, to this file |

opticli keys a site's serve state by the project path as you give it, so start the site and run the tests with the
same path: through a symbolic link and through its target, they are two sites on one database, and the tests don't
see the agent the other path started.

```sh
dotnet test tests/OptiCli.Integration --filter "FullyQualifiedName~The_validator"     # no site
OPTICLI_IT_PROJECT=/demo/Alloy dotnet test tests/OptiCli.Integration --filter "FullyQualifiedName~SchemaOutputTests.Reads"
opticli serve --project /demo/AlloyEdge13                                             # the same path as below
OPTICLI_IT_PROJECT=/demo/AlloyEdge13 OPTICLI_IT_SEAL_DB=alloy-edge13-copy OPTICLI_IT_SCHEMA_LOG=/tmp/schema.log \
  dotnet test tests/OptiCli.Integration --filter "FullyQualifiedName~SchemaOutputTests"
```

The reads take about 2 minutes, the writes 1 to 1.5, the local commands seconds.

## The npm package

`npm/` is the `@epinova/opticli` package: `npm ci`, then `npm run typecheck` (the package, and `test/*.typecheck.ts`,
which pin result types and the calls that mustn't compile) and `npm test` (it builds, then tests against a stand-in
opticli, `test/fake-opticli.mjs`).

Its command methods, options and result types (`npm/src/generated/`) are generated from `opticli schema --json` by
`npm/scripts/generate.mjs`; which commands are in is its `include` list, and what C# can't say is in
`npm/scripts/overrides.mjs`. After changing a command, an option or an output record, build the CLI, regenerate and
commit the result:

```sh
dotnet build src/OptiCli/OptiCli.csproj
cd npm && npm run generate      # the newest build in src/OptiCli/bin; OPTICLI=path/to/opticli.dll for another
```

CI's npm job builds the CLI, regenerates, and fails when `npm/src/generated` differs from what is committed (an
untracked file too). The generated files carry no version, so a release doesn't change them.

`test/surface.txt` lists the package's public surface: every exported name, the public members of its classes, the
properties of its interfaces, and what its type aliases resolve to (the values of a union, each `PlanStep` op's
fields). `surface.test.mjs` fails when it changes, so nothing is renamed or removed by accident. After a deliberate
change, `npm run build && node test/surface.mjs --write`, commit `test/surface.txt`, and list a removal in the
CHANGELOG as a breaking change.

## CI and contributing

[CI](../.github/workflows/ci.yml) runs the unit tests on Linux, Windows and macOS for every push to `main` and every pull
request, packs and checks the NuGet packages and the Claude Desktop extension, and checks the npm package (above). On
Windows it starts SQL Server Express LocalDB (the image's; it installs Microsoft's MSI, checked against a pinned SHA-256,
only when the image lacks it), and `LocalDbTests` must pass there (`OPTICLI_REQUIRE_LOCALDB` turns its skip into a
failure).
Issues and pull requests are welcome. Please run the unit tests before sending a change. When a change touches
reads, also run the integration test against a site you have.

## Releasing

Set the new version as `<Version>` in [Directory.Build.props](../Directory.Build.props) and as `opticli-version` in
[skill/SKILL.md](../skill/SKILL.md) (and in its sentence "This file was written for opticli ..."), and add the release to
[CHANGELOG.md](../CHANGELOG.md) under a heading `## X.Y.Z (date)`. `SkillTests` fails when either version in SKILL.md
differs. npm/package.json and mcp-extension/manifest.json stay at 0.0.0: their builds take the version from
Directory.Build.props. The npm package's generated files carry no version: regenerate them only when a command
changed, which CI checks anyway. Commit on `main` and push it, then push a matching tag:

```sh
git tag v0.23.1 && git push origin v0.23.1
```

The [release workflow](../.github/workflows/release.yml) checks that the tag matches both versions and is on `main`,
runs the unit tests, checks that the CLI package holds both site agent builds and starts, and publishes it to nuget.org together
with `OptiCli.Mcp` and `OptiCli.Mcp.Cms13` at the same version (trusted publishing, environment `nuget`). Then it
creates the GitHub release, with the version's CHANGELOG section as its notes (the job fails without a heading
`## X.Y.Z (date)`) and the Claude Desktop extension `opticli-cms.mcpb` attached.

The npm package `@epinova/opticli` ([npm/](../npm)), in the npm organization `epinova`, follows with the same version,
through npm trusted publishing, once the repository variable `NPM_PUBLISH` is `true`. Trusted publishing needs the package to exist, so the first version is published by hand:

1. Release the version as above. With `NPM_PUBLISH` unset, the workflow skips the npm job.
2. From the tag: `cd npm && npm ci && npm test`, then `npm login` (as a member of `epinova` who may publish) and
   `npm publish ./dist --access public` (two-factor authentication on).
3. On npmjs.com, in the package's settings, add a trusted publisher: GitHub Actions, repository `Epinova/opticli` (as GitHub spells it: npm compares it, and `repository` in npm/package.json, case-sensitively),
   workflow `release.yml`, environment `npm`.
4. `gh variable set NPM_PUBLISH --body true`. From the next tag on, the workflow publishes the package; a version already
   on npm is left as it is.
