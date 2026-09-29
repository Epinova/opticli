---
name: opticli
description: Inspect and change content of an Optimizely CMS 12 (EPiServer) site the user develops locally (against its local or development database) with the opticli CLI instead of hand-written SQL or guessing from code. Use when you need to know what CMS content exists (pages, blocks, media, folders), what a page or block contains (properties, ContentArea items, rich text), which page type or block type something is and which C# class and Razor view render it, where a block or page is used, which content a URL shows, what drafts and versions exist, or when the user asks you to create or edit CMS content (set properties, add a block to a ContentArea, create a page or block, translate, publish) in their development site.
opticli-version: 0.3.0
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

This file was written for opticli 0.3.0 (`opticli --version`). Longer material (every command and option, value
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
Add `--lang <code>` to choose a language branch (default: the item's master language, or the language of the URL).

## The commands you'll use most

| Question | Command |
|---|---|
| Is opticli set up, which DB, is the site running? | `opticli doctor` |
| Which sites, hosts and start pages exist? | `opticli sites` |
| Which page/block types exist, how many items each? | `opticli types --kind block --sort instances` (`page`, `media`, `folder`) |
| A type's properties, C# class file and views | `opticli type ArticlePage` |
| Which ContentAreas/references accept a type | `opticli allowed-in TeaserBlock --kind page` |
| Everything in one item, decoded | `opticli get 123` (`--fields Heading,MainArea`, `--lang en`) |
| Which content a URL shows | `opticli resolve https://www.example.com/en/news/` |
| An item's URL(s) | `opticli url 123` |
| What's below an item | `opticli tree 123 --depth 1` / `opticli children 123` |
| Items of a type, filtered | `opticli find --type ArticlePage --where Heading~news --under /en/` |
| Where a block, page or file is used | `opticli where-used 456` (`--pages`: follow nested blocks up to the pages) |
| Text anywhere in names or text properties | `opticli search "opening hours" --in strings` |
| Version history / unpublished work | `opticli versions 123` / `opticli drafts --since 2024-06-01 --kind page` |
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
  not reachable, `5` write conflict or validation failure (`error.details` lists every issue), `6` the user must
  choose the development database (see "Which database").
- `meta.warnings` means the result is valid but you should know something (e.g. a search was capped).
- Statuses: `published`, `checkedOut` (draft), `checkedIn` (ready to publish), `previouslyPublished`,
  `delayedPublish` (scheduled), `awaitingApproval`, `rejected`. Deleted items (in the recycle bin) say `deleted: true`.
- When something fails oddly, run `opticli doctor` and read its `warnings`.

## Changing content (only when the user asked for a change)

Writes go through the CMS in the running site, are saved as the user `opticli`, and create a **new draft version**
unless `--publish` is passed. The new draft becomes the primary draft, which edit mode opens. Workflow:

1. `opticli serve` - starts the local site in the background with the opticli site agent (takes 30-60 s; `serve --status`,
   `serve --logs --tail 40` if it fails). Only start it when you are about to write. Against a remote development
   database it warns that the database is shared: tell the user that the writes will reach it.
2. Dry-run first: add `--dry-run` to see the validated before/after without saving.
3. Run the write: `opticli set 123 Heading="New title"`, `opticli area 123 MainArea add 456`,
   `opticli block create --type TeaserBlock --name "Teaser" --for 123`, `opticli create 123 --type ArticlePage --name News`.
4. Verify: `opticli get <ref>` (the draft: `get <version ref from the output>` or `--version latest`) and
   `opticli versions <ref> --limit 3`.
5. `opticli serve --stop` when you're done writing.

Structured values (ContentArea items, links, lists) go in `--values '{"MainArea":[{"ref":"456"}]}'`; several related
writes can run as one validated plan with `opticli apply plan.json`. Syntax for both: [reference.md](reference.md).

## Rules

- Never pass `--publish` and never run `publish` unless the user explicitly asked for the change to go live.
- Never run `delete` unless the user explicitly asked to delete that content. (It only moves content to the recycle
  bin; undo with `opticli move <ref> --to <previousParent>`. Start pages, site and asset roots, and anything that
  contains them, are refused.)
- Always `--dry-run` a multi-step `apply` plan first, and a single write when you are unsure of its effect.
- Start `serve` only when a write is needed, and stop it (`opticli serve --stop`) when you are done.
- Never run `opticli db use` or pass `--db`/`--connection`/`OPTICLI_DB` on your own initiative: which database is
  the development one, and whether to touch any other, is the user's decision. Writes to anything but the development
  database are refused (exit 3). Don't try to work around a refusal; tell the user.
- Don't write to the CMS database with SQL; `sql` is read-only by design.
- Report what you changed: the refs and version refs every write prints.
