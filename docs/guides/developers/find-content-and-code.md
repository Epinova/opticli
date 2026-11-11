---
title: Find content and the code behind it
summary: Go from a URL or a name to the content item, its C# class and Razor view, and every page that uses it.
audience: developers
order: 2
written-for: 0.30.0
---

You are looking at a block on a page and want to know what it is, which C# class and Razor view render it, and what
else breaks if you change it. opticli answers that from the database and your source tree, without clicking through
the edit UI or writing SQL. By the end of this guide you can go from a URL or a name to the content, its type, its
class and views, and every page that uses it, and you can hand the whole question to your coding agent.

## Before you start

- opticli 0.30.0 or newer, run from anywhere inside the site's repository.
- A database opticli can read: `opticli doctor` shows which one. Nothing needs to be running; all of this is reads.
- For the agent part: `opticli skill install` (or `opticli skill install --repo` to share it through git).

## Steps

### 1. Start from a URL

```bash
opticli resolve http://localhost:5000/en/alloy-plan/
```

```json
{"ref":"8","type":"ProductPage","name":"Alloy Plan","language":"en","status":"published","url":"/en/alloy-plan/",
 "site":"Alloy","host":"localhost:5000","languageSource":"path","matchedBy":"path"}
```

`ref` is what every other command takes. A production URL works too when the database has that host, and a bare path
(`/en/alloy-plan/`) uses the site with the `*` host. Every command that takes a `<ref>` also takes the URL directly,
so `opticli get /en/alloy-plan/` works as well.

### 2. Or start from a name

When you know the type, filter on the name:

```bash
opticli find --type TeaserBlock --where Name~Plan
```

When you don't, search names (or text, with `--in strings`):

```bash
opticli search "Alloy Plan" --in names
```

Each row has `ref`, `type`, `name` and `status`. Lists stop at 50 items: if `meta.next` is there, pass it as
`--cursor` or raise `--limit`.

### 3. Read the item

```bash
opticli get 5 --fields MainContentArea
```

On a page, a ContentArea lists its items with `ref`, `type` and `name`, so this is how you get from "the teaser on the
start page" to the block's ref. An item with `"inline": true` and no `ref` is an inline block, stored in that area
rather than as content of its own. Then read the block:

```bash
opticli get 31
```

`get` decodes every property: rich text with its links, references with the name and type of what they point at,
nested blocks. `--version latest` shows the newest draft instead of the published version, and `--lang sv` another
language branch.

### 4. Find the class and the views

```bash
opticli type TeaserBlock
```

```json
{"name":"TeaserBlock","kind":"block","instances":6,
 "properties":[{"name":"Heading","type":"String","cultureSpecific":true,"required":true,"source":"Models/Blocks/TeaserBlock.cs:18"}],
 "sourceRoot":"/home/you/alloy",
 "classes":[{"file":"Models/Blocks/TeaserBlock.cs","line":11,"matchedBy":"guid","baseTypes":["SiteBlockData"]}],
 "views":[{"file":"Views/Shared/Blocks/TeaserBlock.cshtml","matchedBy":"fileName"}]}
```

Paths are relative to `sourceRoot`, the solution folder. Views are matched by file name and by `@model`, so partial and teaser views show up
next to the page template; `matchedBy: fileName` is usually the main one. Each property has the `file:line` that
declares it, and `allowedTypes` where `[AllowedTypes]` restricts it. Controllers and view components aren't listed:
search the code for the class name, for example `PageController<ProductPage>`.

To ask the reverse, which properties accept this type at all, run:

```bash
opticli allowed-in TeaserBlock --kind page
```

### 5. See where it is used

```bash
opticli where-used 31
opticli where-used 31 --pages
```

Each row names the owner (`ref`, `type`, `name`, `status`, `saved`, `changedBy`), the `property` it sits in
(`MainContentArea`, or `MainContentArea[2].Link` inside an inline block) and the `kind` of reference: ContentArea,
reference, link, rich-text link or a block in rich text. `--pages` follows owners that are blocks up to the pages
that show them, newest save first, with the blocks in between in `via`. Deleted owners come last with `deleted: true`.

For every block of the type at once, most used first and unused ones last:

```bash
opticli where-used --type TeaserBlock
```

### 6. Hand it to your coding agent

With the skill installed, the agent knows these commands and chains them itself. Ask the question as you would ask a
colleague:

```prompt
On http://localhost:5000/en/ there is a teaser that says "Alloy Plan". What is it, which C# class and Razor view render
it, and on which pages is that same block used?
```

```prompt
I want to add a property to TeaserBlock. Which pages show teasers today, which ones were edited most recently, and
which ContentAreas allow the type?
```

The agent answers with names next to refs ("Alloy Plan", block 31) and the file paths from `opticli type`. It only
reads for questions like these; it changes content only when you ask it to.

## If something goes wrong

| What you see | What to do |
|---|---|
| `not_found` (exit 2) on `resolve` | The host or path isn't in the database. Check `opticli sites` for the hosts; after a restore, see [Work on a restored production database](restored-production-database.md). |
| `not_found` (exit 2) on `type` or `find --type` | The type name is wrong. The hint suggests the closest names; `opticli types --kind block` lists them all. |
| `needs_selection` (exit 6) | opticli doesn't know the development database yet. Run `opticli db list` and `opticli db use <id>`. |
| `classes` or `views` empty | opticli scans the C# and Razor files in the solution folder. A type or view that comes from a NuGet package isn't there to find. |
| A block you expected is missing from `where-used` | The value scan reads each branch's published values (the latest draft if it was never published); only the CMS's link index covers other saved versions. If it was placed in a newer draft, `opticli drafts` and `opticli get <ref> --version latest` show it. |

## Related

- [Commands: Reading](https://github.com/Epinova/opticli#reading)
- [Using opticli with coding agents](https://github.com/Epinova/opticli#using-opticli-with-coding-agents)
- [Output and exit codes](https://github.com/Epinova/opticli#output-and-exit-codes)
- [Work on a restored production database](restored-production-database.md)
- [Seed content for Playwright tests](playwright-seed-content.md)
