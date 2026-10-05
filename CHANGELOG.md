# Changelog

Every release is on [nuget.org](https://www.nuget.org/packages/OptiCli). After updating, run `opticli skill install`
again to update the skill.

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
