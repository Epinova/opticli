# Changelog

Every release is on [nuget.org](https://www.nuget.org/packages/OptiCli). After updating, run `opticli skill install`
again to update the skill.

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
