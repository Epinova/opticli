---
name: opticli
description: Inspect and change content of an Optimizely CMS 12 (EPiServer) site the user develops locally (against its local or development database) with the opticli CLI instead of hand-written SQL or guessing from code. Use when you need to know what CMS content exists (pages, blocks, media, folders), what a page or block contains (properties, ContentArea items, rich text), which page type or block type something is and which C# class and Razor view render it, where a block or page is used, which content a URL shows, what drafts and versions exist, or when the user asks you to create or edit CMS content (set properties, add a block to a ContentArea, create a page or block, translate, publish) in their development site, or to point the sites of a restored database at localhost (their host names). Also for scheduled jobs: which exist, whether a job (an import, a sync) ran and how it ended, and running or rescheduling one. And for the recycle bin: what was deleted, by whom, and bringing it back; and a local login for a restored database.
opticli-version: 0.13.0
---

# opticli: Optimizely CMS content from the command line

opticli reads an Optimizely CMS 12 site's database directly (fixed, read-only queries) and writes through the
CMS itself inside the site running on this machine. Run it from anywhere in the site's repository; it finds the CMS
project and its **development database** on its own: a local one automatically, a remote one (e.g. in Azure) only
after the user has chosen it (see "Which database" below).

**Code or opticli?** The C# code says what *can* exist (content type classes, properties, `[AllowedTypes]`, views).
The database says what *does* exist (content items, their values, where blocks are placed, URLs, drafts). Use
opticli for the second kind of question, and to find the code: `opticli type <Name>` gives the class file, views and
per-property `allowedTypes`; `opticli allowed-in <Type>` answers "where may this block go" across all types.

This file was written for opticli 0.13.0 (`opticli --version`). Longer material (every command and option, value
syntax, plan files, output fields, troubleshooting) is in [reference.md](reference.md): read it when you write, or
when a command below doesn't cover your question.

## Basics

- Invoke as `opticli <command>`. If it is not on PATH, ask the user how they run it (it installs as a .NET global tool).
- Output is compact JSON when stdout is not a terminal (always the case for you): `{"ok":true,"data":...,"meta":{...}}`.
  List commands accept `--jsonl` (one item per line), which is handy for line-by-line processing (`jq -c`, `grep`).
- Lists return 50 items. **Always check `meta.next`**: if present there is more (pass it as `--cursor <next>` or raise
  `--limit`, e.g. `--limit 1000`). There is no total count. `types` is sorted by name; `--sort instances` for most used.
- Every item carries `ref`, `guid`, `type`, `name`, `status`, `language` (absent for language-invariant content such as
  media and folders) and `url` when it has one. Any `ref` can be passed back to another command.
- `url` is site-relative. With several sites (`opticli sites`) the same path can exist on each: use `opticli url <ref>`
  for the absolute URL and site, and pass absolute URLs as refs.
- Long strings are cut at 300 characters (`truncated: true, length: N`); `get --fields A,B` or `--full` gives them whole.
- `opticli <command> --help` has every option and an example. Dates are UTC (`...Z`).

## Refs

Wherever a command takes `<ref>`: a content id (`123`), a specific version (`123_456`, from `versions`), a content
GUID, or a URL/path (`/en/about/`, `https://www.example.com/en/about/`). `1` is the root of all content.
Content from a content provider (e.g. DAM images) shows as `63__provider`: pass it back as is in property values,
ContentAreas and links, but it can't be read with `get` (it isn't in the database). Its id is local to this database;
use the `guid` in anything that must work in other environments.
Add `--lang <code>` to choose a language branch (default: the item's master language, or the language of the URL).

## The commands you'll use most

| Question | Command |
|---|---|
| Is opticli set up, which DB, is the site running? | `opticli doctor` |
| Which sites, hosts and start pages exist? | `opticli sites` |
| Point a restored database's sites at localhost (only when asked) | `opticli sites primary "Site A=localhost:5001" --dry-run` (needs `serve`) |
| Which page/block types exist, how many items each? | `opticli types --kind block --sort instances` (`page`, `media`, `folder`) |
| A type's properties, C# class file and views | `opticli type ArticlePage` |
| Types or properties left behind by removed code | `opticli types --orphaned`; `opticli type <Name>` (`existsOnModel: false`, `values`) |
| Which ContentAreas/references accept a type | `opticli allowed-in TeaserBlock --kind page` |
| Everything in one item, decoded | `opticli get 123` (`--fields Heading,MainArea`, `--lang en`) |
| Which content a URL shows | `opticli resolve https://www.example.com/en/news/` |
| An item's URL(s) | `opticli url 123` |
| What's below an item | `opticli tree 123 --depth 1` / `opticli children 123` |
| Items of a type, filtered | `opticli find --type ArticlePage --where Heading~news --under /en/` |
| Where a block, page or file is used | `opticli where-used 456` (`--pages`: follow nested blocks up to the pages) |
| Text anywhere in names or text properties | `opticli search "opening hours" --in strings` |
| Who may read or edit an item (access rights) | `opticli access 123` (`inherited`, `from`: where they come from) |
| Version history / unpublished work | `opticli versions 123` / `opticli drafts --since 2024-06-01 --kind page` |
| Who moved, deleted or published an item, and when | `opticli history 123` (`--since 30d`) |
| Content waiting to go live / expired | `opticli find --type ArticlePage --status scheduled` (`expired`) |
| Which categories / visitor groups exist | `opticli categories` / `opticli visitor-groups` |
| What was deleted, and where it goes back | `opticli trash` (`--since 7d`, `--by`); `opticli restore <ref> --dry-run` (needs `serve`; only when asked) |
| What differs between this build and a shared database? | `opticli drift` (needs `serve`) |
| Which scheduled jobs exist, which are overdue or failed last time? | `opticli jobs` (`--failed`) |
| Did a job (an import, a sync) run, and how did it end? | `opticli jobs log "<job name>"` (`--failed --since 1d` for every job) |
| Run a job now (only when asked) | `opticli jobs run "<job name>"` (needs `serve`; waits for it) |
| Sign in locally to a restored database (only when asked) | `opticli users add <name> --dry-run` (needs `serve`); `opticli users roles` |
| Anything else (read-only) | `opticli sql "SELECT TOP 10 ... FROM tblContent ..."` |

## Recipes

- **What renders this URL?** `opticli resolve <url>` gives the ref and `type`; `opticli type <type>` gives
  `classes[].file` and `views[].file` (paths relative to `sourceRoot`). Views are matched by file name and `@model`,
  so teaser and partial views are listed next to the page template (`matchedBy: fileName` is usually the template).
  Controllers and view components are not listed: grep the code for the class name (e.g. `PageController<ArticlePage>`).
- **Which types can hold block X?** `opticli allowed-in X --kind page` reads `[AllowedTypes]` from the C# code for
  every ContentArea and reference property: `allowed: explicit` (the attribute names X or a base class, see
  `matchedBy`) or `any` (a ContentArea without the attribute). A row with a `uiHint` has an editor descriptor that may
  change the allowed types at runtime: read that descriptor before trusting `any`. `--explicit` drops the `any` rows.
  What is actually placed there: `opticli find --type <PageType> --where <Area>=<blockRef>`.
- **Where is a block used, and which pages changed last?** `opticli where-used <ref> --pages` follows owners that are
  blocks (list blocks, nested blocks) up to the pages, newest `saved` first, with the blocks in between in `via`.
  For a whole block type, `opticli find --type TeaserBlock --limit 1000 --jsonl` lists the instances; run
  `where-used --pages` per instance (one call each, so start with the few you need).
- **What's unpublished?** `opticli drafts --since <yyyy-MM-dd>` (with `--kind page`, `--type`, `--by <user>`,
  `--lang`); each row names the newest draft version (`get <that ref>` shows its values). `checkedOut` = being edited,
  `checkedIn` = ready to publish; a `drafts` count in the thousands means an import or integration job saves versions.
- **Just the metadata of an item** (`saved`, `changedBy`, `status`): `opticli get <ref> --fields name`. Without
  `--version`, `get` shows the published version, or the latest draft when the branch was never published.

## Which database

- A command fails with `needs_selection` (exit 6) when opticli doesn't know the project's development database yet,
  or the saved choice no longer matches the configuration. `error.details.choices` lists the options
  (`n`, `id`, `server`, `database`, `local`, `from`).
  1. Show the user the choices (server, database, from) and **ask which one is their development database**. Use
     your question tool if you have one. Never choose for them, even when one looks obvious.
  2. Run `opticli db use <id>` with the id of the one they picked, then rerun the original command.
- `opticli db list` shows every connection string and the current choice; `opticli db forget` clears it.
- `meta.database` appears when the database in use is remote. `development: true` means it is the one the user chose.
- `--db <id|name>` uses another database for one run. Only pass it when the user asked for that environment; the
  response then warns that it isn't the development database. Never use `--db`/`--connection` to get around a
  refusal.

## Reading results and errors

- Failures: `{"ok":false,"error":{"code","message","hint"}}` on stdout, and a non-zero exit code. **Read the hint**: it
  says what to do next (a "Did you mean ...?" for mistyped types, properties or commands; which option to add).
- Exit codes: `0` ok, `1` usage (bad arguments), `2` not found, `3` refused by a safety rule, `4` database or site
  not reachable, `5` write conflict, validation failure (`error.details` lists every issue) or `drift` (see Rules),
  `6` the user must choose the development database (see "Which database"), `7` a job `jobs run` ran didn't succeed
  (`error.details.status`, `message`), `130` interrupted (Ctrl+C). `timeout` (exit 4) from `jobs run --timeout`: the
  job still runs.
- `meta.warnings` means the result is valid but you should know something (e.g. a search was capped).
- Statuses: `published`, `checkedOut` (draft), `checkedIn` (ready to publish), `previouslyPublished`,
  `delayedPublish` (scheduled), `awaitingApproval`, `rejected`. Deleted items (in the recycle bin) say `deleted: true`.
- When something fails oddly, run `opticli doctor` and read its `warnings`.

## Changing content (only when the user asked for a change)

Writes go through the CMS in the running site, are saved as the user `opticli`, and create a **new draft version**
unless `--publish` is passed. The new draft becomes the primary draft, which edit mode opens. `set` and `area` base it
on the latest version (a conflict, exit 5, if someone saves a newer one meanwhile); `--from published` bases it on the
published version instead, leaving newer drafts out. Workflow:

1. `opticli serve` - starts the local site in the background with the opticli site agent (takes 30-60 s; `serve --status`,
   `serve --logs --tail 40` if it fails). Only start it when you are about to write. Against a remote development
   database it warns that the database is shared: tell the user that the writes will reach it. There it also says
   when this build and the database differ (`drift:` in `meta.warnings`): writes then stop until the user confirms.
2. Dry-run first: add `--dry-run` to see the validated before/after without saving.
3. Run the write: `opticli set 123 Heading="New title"`, `opticli area 123 MainArea add 456`,
   `opticli block create --type TeaserBlock --name "Teaser" --for 123`, `opticli create 123 --type ArticlePage --name News`,
   `opticli upload report.pdf --for 123` (a file as media; then reference its `ref` in a property).
4. Verify: `opticli get <ref>` (the draft: `get <version ref from the output>` or `--version latest`) and
   `opticli versions <ref> --limit 3`.
5. `opticli serve --stop` when you're done writing.

Structured values (ContentArea items, links, lists) go in `--values '{"MainArea":[{"ref":"456"}]}'`; several related
writes can run as one validated plan with `opticli apply plan.json`. A plan with `"guidNamespace"` can be run again
with `apply --update-existing` (always `--dry-run` it first). Syntax for all of this: [reference.md](reference.md).

Besides the content type's properties, writes take a few built-in settings: `Name`, `PageURLSegment`,
`PageVisibleInMenu`, `StartPublish`/`StopPublish`, `Category`, and on pages `ChildSortOrder`, `SortIndex`,
`SimpleAddress` and `Shortcut` (a menu item that links elsewhere). A list page that shows its children in the wrong
order usually needs its `ChildSortOrder` (e.g. `PublishedDescending`), not a code change.

## Rules

- Never pass `--publish` and never run `publish` unless the user explicitly asked for the change to go live.
- A publish puts the whole version live, including changes someone else saved since the published version. When
  there are any, it fails with `conflict` (exit 5) and `error.details.reason: "pendingDraft"` (a `--dry-run` reports
  `pendingDraft` with a warning). Show the user `details.draft` (`savedBy`, `saved`, `changes`) and **ask whether those changes
  should go live too**. Pass `--include-draft` (in a plan, `"includeDraft": true` on the step) only after they said
  yes; never on your own initiative. Drafts opticli saved itself don't need this. If they should stay out, don't drop
  your change or rewrite whole values to get around them: base it on the published version, `--from published` (a
  plan step's `"from": "published"`). Their draft then stays a draft and doesn't go live; the warning in
  `meta.warnings` lists what was left out, and the new draft becomes the one edit mode opens, so tell the user.
- Against a shared (remote) development database, a write stops when this build and the database differ: `drift`
  (exit 5). The database keeps what the deployed code made, so the write would run this build's code against content
  the deployed site serves. Show the user what differs (`error.details`, or `opticli drift`) and what it means:
  `local` ahead = this branch has changes that aren't deployed there; `database` ahead = that environment runs newer
  code than this checkout (pull); `unknown` = they differ and the database doesn't say which side changed. Ask whether
  to write with this build anyway. Pass `--accept-drift <details.fingerprint>` (a plan takes it once:
  `apply --accept-drift`) only after they said yes, never on your own initiative, and never with a fingerprint from an
  earlier answer: it stops counting when the differences change. Dry runs don't stop.
- `serve` refuses (exit 3) a build with EF Core migrations the shared database lacks (`details.reason:
  "pendingMigrations"`), or a CMS schema version the database can't run (`"schemaVersion"`). Tell the user; pass
  `--allow-pending-migrations` only when they say the site doesn't migrate that database at startup.
- Content with an approval sequence (`approval` in `get`) is never published directly: such a publish is refused
  (exit 3, `details.reason: "approvalSequence"`). Ask the user whether to send it for review, and only then pass
  `--request-approval`. opticli never approves or rejects; reviewers do that in the CMS.
- After a publish, `previouslyPublished` is the version that was live before; `opticli publish <ref> --version <id>`
  with it goes back. Without it, that was the first publish: `opticli unpublish <ref>` takes it offline again.
- Never run `unpublish`, `discard` or `translate --remove` unless the user asked for it. `discard` deletes a version
  for good: show the dry run's `changes` first; a version someone else saved needs `--include-draft`, only after the
  user said yes. `translate --remove` deletes every version of the branch: pass `--confirm` only after the user
  confirmed.
- Never run `delete` unless the user explicitly asked to delete that content. (It only moves content to the recycle
  bin; undo with `opticli restore <ref>`. Start pages, site and asset roots, and anything that contains them, are
  refused.) When other content references it, the delete stops (`conflict`,
  `details.reason: "referenced"`): show the user `details.references`, and pass `--ignore-references` only after they
  said to delete it anyway.
- Never run `restore` unless the user asked to bring that content back: content that was published is live again at
  once. Dry-run it first and tell the user where it goes (`parent`) and what comes along (`descendants`). It goes back
  below `originalParent` from `opticli trash`; when that is null, or in the recycle bin too, ask the user where it
  should go (`--to <parent>`), or restore that parent first. Restore what was deleted, not something below it.
- Never change access rights (`access` with `--grant`, `--user`, `--revoke`, `--break-inheritance`, `--inherit`) unless
  the user explicitly asked for it. They aren't versioned: report the `before` from the output, which is the only
  record of what they were. Root, start pages and asset roots, and changes that leave no role with Administer, are
  refused.
- Never change site hosts (`sites primary`, `sites host add|remove`) unless the user asked for it, and dry-run first.
  Which host goes with which site is the user's call: ports in the launch profile say nothing about which site is
  which, so ask rather than guess. Pass `--save` only when they want the mapping kept for the next restore (then
  `opticli sites primary --from-config`; `--forget <site>` drops a saved site that is gone). Site definitions aren't
  versioned: report the `changes` from the output. Tell the user to restart the site if it also runs elsewhere (their
  IDE), as `meta.warnings` says. Against a shared database they are refused (exit 3): don't work around it.
- Never run, stop or reschedule a scheduled job (`jobs run`, `jobs stop`, `jobs set`) unless the user asked for it. Jobs
  that aren't the CMS's own (the site's, add-ons') may reach external systems (imports, syncs, emails): `jobs run`
  warns that opticli doesn't know what they do. When "an import didn't run", start with `opticli jobs log "<job>"` (status, message, trigger) and
  `opticli jobs` (enabled, next run): `serve` keeps the site's scheduler off, so nothing runs on its schedule there,
  and only `jobs run` starts a job. `jobs run` waits and ends with the run's status and message; report them. Jobs that
  delete for good (emptying the recycle bin, trimming versions, truncating the change log, Commerce's expired carts,
  ...) are refused without `--allow-destructive`, by `jobs run` and by a `jobs set` that lets the scheduler run one:
  pass it only after the user confirmed that job by name. Never pass
  `serve --scheduler` on your own initiative: overdue jobs would all start. Schedules aren't versioned: report the
  `before` from `jobs set`.
- Never add or remove users (`users add`, `users remove`) unless the user asked for a local login. Don't pass
  `--password-stdin` with a password you made up: let opticli generate it, and give the user `passwordFile` (the path),
  never the password or the file's content. `users remove` only removes users opticli made; a refusal there or on a
  site without ASP.NET Identity (exit 3) is final: tell the user. Never list users or read the user tables (`sql
  --include-personal-data`) to find names; `users roles` has the counts.
- Always `--dry-run` a multi-step `apply` plan first, and a single write when you are unsure of its effect.
- A write that timed out (`unreachable`, "no response within") or a plan that stopped halfway (`details.partial`) may
  have saved more than it reports: check with `opticli versions <ref>` before running it again.
- Start `serve` only when a write is needed, and stop it (`opticli serve --stop`) when you are done.
- Never run `opticli db use` or pass `--db`/`--connection`/`OPTICLI_DB` on your own initiative: which database is
  the development one, and whether to touch any other, is the user's decision. Writes to anything but the development
  database are refused (exit 3). Don't try to work around a refusal; tell the user.
- Don't write to the CMS database with SQL; `sql` is read-only by design.
- Whenever you mention content to the user (in a report, a proposal or a question), give its name next to the ref,
  and the type or language when that tells items apart: "About us" (page 10, draft 10_1473), not 10 or 10_1473
  alone. Every item in a result carries `name`; for a bare id, `opticli get <ref> --fields name` looks it up. A
  version someone else saved: who and when (`savedBy`, `saved`), e.g. "the draft Kari saved on 14 April".
- Report what you changed that way: the refs and version refs every write prints, each with its content's name.
