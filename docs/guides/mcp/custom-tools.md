---
title: Write your first site-specific tool
summary: Add a read tool and a write tool of your own to the MCP module, so editors' assistants can find Alloy product pages without a teaser and fill them in as drafts.
audience: mcp
order: 2
written-for: 0.30.0
---

The module's own tools work on any content, one item at a time. A tool of your own can know your site: which page
types matter, where they live, what "done" looks like. In this guide you add two tools to an Alloy site. The read
tool, `list_products_missing_teaser`, lists product pages (Alloy Track, Alloy Plan, Alloy Meet) that have no teaser
text of their own. The write tool, `fill_product_teasers`, writes the teasers the assistant drafted, as drafts, after a
dry run the editor has seen. Both run as the signed-in editor, with their access rights and the module's audit log.

## Before you start

- The module installed and working in your site: see [Install the MCP module in your site](install-the-module.md).
- opticli 0.30.0 or newer: `AddOptiCliMcp()` returns the builder you add tools to from that version on.
- An Alloy site with its product pages, and an editor account you can connect Claude Code with.

## Build the tools

### 1. Write the read tool

A read tool is an ordinary MCP C# SDK tool class. Each tool method needs `[McpServerTool]` with a `Name` and
`ReadOnly = true`. Leave `[McpServerToolType]` off the class: the SDK's `WithToolsFromAssembly()` would offer it on
the site's other MCP servers too.

```csharp
using System.ComponentModel;
using EPiServer;
using EPiServer.Core;
using ModelContextProtocol.Server;
using OptiCli.Mcp;
// plus your Alloy project's namespace for ProductPage

public sealed class ProductTeaserTools(IOptiCliEditor editor, IContentLoader loader)
{
    [McpServerTool(Name = "list_products_missing_teaser", Title = "Product pages without a teaser", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the product pages below a page that have no teaser text of their own. Read each one with get_content before writing a teaser for it.")]
    public IReadOnlyList<ContentDescription> ListProductsMissingTeaser(
        [Description("The id of the page to look below, e.g. the Products page.")] int below)
    {
        var root = editor.Load<PageData>(new ContentReference(below));
        // Everything below, as the CMS finds it: no access checks yet. Sorted, so the order is stable.
        var links = loader.GetDescendents(root.ContentLink.ToReferenceWithoutVersion()).OrderBy(link => link.ID);
        var missing = new List<ContentDescription>();
        foreach (var found in editor.Readable(links).OfType<ProductPage>())
        {
            // The newest version, as the write tool sees it: a draft may have a teaser already.
            var page = editor.Load<ProductPage>(found.ContentLink.ToReferenceWithoutVersion(), latest: true);
            if (!HasOwnTeaser(page))
            {
                missing.Add(editor.Describe(page));
            }
        }
        return missing;
    }

    // Alloy's TeaserText getter falls back to MetaDescription, so read the stored value instead.
    internal static bool HasOwnTeaser(PageData page) =>
        !string.IsNullOrWhiteSpace(page.Property["TeaserText"]?.Value as string);
}
```

Three things make this safe. `IContentLoader` checks no access rights, so every reference it finds goes through
`editor.Readable`, which keeps only what the editor may read. Content they can't read must not show up in a result, a
count or an error. `editor.Load` loads as `get_content` does and answers `not_found` for content that is missing or
hidden alike. And `editor.Describe` gives each page the fields the module's own tools give, with `url` and `editUrl`,
so the assistant can go on with `get_content` and hand the editor a link.

> **Note:** `GetDescendents` reads the whole subtree. That is fine for Alloy's few product pages. On a large site, find
> the candidates with your search index or a narrower query, then pass them through `editor.Readable` the same way.

### 2. Write the write tool

A write tool never saves anything itself. It returns a `ContentPlan`, and the module checks that plan with a dry run,
shows it to the user through the assistant, and saves it as drafts. The arguments are a record whose properties the
assistant sees as the tool's arguments, in camelCase.

```csharp
using System.ComponentModel;
using EPiServer.Core;
using OptiCli.Mcp;
// plus your Alloy project's namespace for ProductPage

public sealed record ProductTeaser(
    [property: Description("The product page's id, as list_products_missing_teaser gives it.")] int Id,
    [property: Description("The teaser text: one or two plain sentences, no markup.")] string Text);

public sealed record FillProductTeasersArgs(
    [property: Description("A teaser for each product page to fill, one per page.")] IReadOnlyList<ProductTeaser> Teasers);

[OptiCliWriteTool("fill_product_teasers", Title = "Fill product teasers", Kinds = ContentPlanKinds.Update)]
[Description("Writes a teaser text on product pages that have none of their own. Pages that already have one are left as they are.")]
public sealed class FillProductTeasersTool(IOptiCliEditor editor) : IOptiCliWriteTool<FillProductTeasersArgs>
{
    public Task<ContentPlan> PlanAsync(FillProductTeasersArgs args, CancellationToken cancellationToken)
    {
        // Alloy has no nullable reference types, so the list and its texts can still arrive as null.
        if (args.Teasers is null || args.Teasers.Count == 0 || args.Teasers.Any(t => t is null || string.IsNullOrWhiteSpace(t.Text)))
        {
            throw new OptiCliToolException("usage", "Every teaser needs a page id and a text.", "Give at least one page with a teaser text.");
        }
        if (args.Teasers.GroupBy(t => t.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new OptiCliToolException("usage", $"Page {twice.Key} is in teasers more than once.", "Give each page once.");
        }

        var plan = new ContentPlan();
        foreach (var teaser in args.Teasers.OrderBy(t => t.Id))
        {
            // The latest version, draft included: an update based on an older one is a conflict.
            var page = editor.Load<ProductPage>(new ContentReference(teaser.Id), latest: true);
            if (ProductTeaserTools.HasOwnTeaser(page))
            {
                continue;   // already done: no step, so a new dry run after a stopped run shows only what is left
            }
            plan.Update(page, p => p.Set("TeaserText", teaser.Text.Trim()));
        }
        return Task.FromResult(plan);
    }
}
```

The ids come from the assistant, which already knows them, so `editor.Load` is the right call here: it answers
`not_found` for a page the editor can't read, and `usage` for one that isn't a `ProductPage`. Duplicates are refused
up front because a plan that updates the same content twice is a fault of the site, which the assistant only sees as
`internal`. `Kinds` declares which steps the plan may have; add `ContentPlanKinds.Create` before you call
`plan.Create`, for example.

The module calls `PlanAsync` twice: once for the dry run and once for the real run, and refuses the real run if the
two plans differ. So plan deterministically. Sort, never add timestamps or new GUIDs, and plan from the content's
current state.

### 3. Register both tools

Chain them on the builder `AddOptiCliMcp` returns, and add a line of instructions that tells the assistant how they fit
together:

```csharp
services.AddOptiCliMcp()
    .WithTools<ProductTeaserTools>()
    .WithWriteTool<FillProductTeasersTool>()
    .WithInstructions("To fill missing teasers on product pages: list them with list_products_missing_teaser, read each with get_content, write a teaser from its content, and save them with fill_product_teasers.");
```

The module checks names, attributes and argument types at startup. A mistake makes `WithTools`, `WithWriteTool` or
`MapOptiCliMcp()` throw an `InvalidOperationException` that names the class and method, so run the site once before
anything else.

## Name and describe them well

The assistant picks tools by name and description, so these are your user interface.

- **Name** a tool for what it does on your site, in the module's style: `list_products_missing_teaser`, not `search`
  or `products`. Names are 1 to 128 letters, digits, `_`, `-` and `.`, and can't be one of the module's own
  (`get_content`, `update_content`, `delete_content` and the rest, whether offered or not).
- **Describe** what the tool does and what to do with its result ("read each one with get_content"). Leave out the dry
  run workflow on a write tool: the module appends it to the description itself.
- **Describe every argument** and say where its value comes from ("as list_products_missing_teaser gives it").
- **Fail on purpose** with `OptiCliToolException(code, message, hint)`, using `usage`, `not_found`, `refused` or
  `conflict`. The assistant follows the hint. Anything else your tool throws is logged and reaches the assistant as
  `internal`, without its message, so never put content the editor can't read in a message.

## Try the dry run and confirm flow

Rebuild, connect Claude Code as an editor, and ask:

```prompt
Find the product pages below Products that have no teaser text. Read each one and write a teaser of at most two
sentences. Show me the changes before you save anything.
```

The assistant calls `list_products_missing_teaser`, reads each page, and then calls `fill_product_teasers` with
`dryRun: true`. The arguments it sends look like this (the ids are examples):

```json
{
  "teasers": [
    { "id": 12, "text": "Alloy Plan keeps every project on schedule, from first idea to delivery." },
    { "id": 14, "text": "Alloy Meet brings your team together, wherever they work." }
  ],
  "dryRun": true
}
```

The dry run checks every step as the editor (access rights, the edit UI's rules, validation, `baseVersion`) and
answers with each step's changes, `valid`, a `planToken` and `planTokenExpires`. A plan with a step that would be
refused gets no token. The assistant shows you the changes. When you agree, it calls the tool again with the same
arguments, `dryRun: false` and the token. The module plans again, refuses the run with a `conflict` if anything
changed, checks every step again, and saves them in order as drafts, each with an `editUrl` you can open in the CMS.

A token runs once and expires after 30 minutes. A run that fails at a step stops there and keeps the steps before it,
and the result lists what was saved, what failed and why, and what didn't run.

## Test it

- **As a restricted editor.** Take Read access on Alloy Meet away from your test editor and run the prompt again.
  Alloy Meet must not appear in the list, and the dry run must not mention it.
- **Change under the plan.** Do a dry run, then edit the teaser of one listed page in the CMS, then confirm. The run
  must be refused with a `conflict` and save nothing; a new dry run shows the page gone from the plan.
- **Run twice.** Confirm with the same token again. The second run is a `conflict`.
- **Check the audit log.** With `OptiCli.Mcp.Audit` at Information, each tool call has a line, and the real run has a
  line per step with its kind, the content and the outcome.
- **Unit test the planning.** `PlanAsync` is plain C#: test the argument checks with a stub `IOptiCliEditor`, and the
  number of steps with `plan.Count`. The steps' details are internal, so check them through a dry run on a test site.

## If something goes wrong

- **The site won't start, with an `InvalidOperationException` naming your class:** a missing `Name`, `ReadOnly`,
  `[Description]` or `Kinds`, or a name that is taken. The message says which.
- **Every update is a `conflict`:** the plan loads content without `latest: true`, so a newer draft, your own included,
  makes it stale. Load what you change with `editor.Load<T>(link, latest: true)`.
- **"The plan isn't the one the dry run showed" on every run:** the plan isn't deterministic. Look for unsorted
  results, timestamps or GUIDs.
- **The assistant gets `internal`:** your tool threw something other than `OptiCliToolException`, or planned a step
  of a kind it didn't declare. The site's log has the details.
- **A plan is refused for too many steps:** `MaxPlanSteps` (100 by default) caps a plan. Narrow the arguments; the
  hint tells the assistant to do the rest in further runs.

## Related

- [Install the MCP module in your site](install-the-module.md)
- [Take the MCP module to production](hardening.md)
- README: [Custom tools](https://github.com/Epinova/opticli#custom-tools), with every `ContentPlan` step (create,
  create in a "For this page" folder, move, trash) and the rules a tool must follow
- README: [Beside another MCP server](https://github.com/Epinova/opticli#beside-another-mcp-server), if the site runs
  one of its own
