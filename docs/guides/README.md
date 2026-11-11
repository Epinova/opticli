# Guides

Task-based how-to guides, published on the landing page at https://epinova.github.io/opticli/guides/. The
[README](../../README.md) stays the reference; a guide walks through one task from start to finish and links to the
README for the details.

| Folder | Audience |
|---|---|
| [editors/](editors/) | CMS editors working on content with an AI assistant through the MCP module |
| [developers/](developers/) | developers using the opticli CLI, usually with a coding agent |
| [mcp/](mcp/) | developers installing, configuring and extending the MCP module in a site |

## Format

Each guide is one Markdown file that starts with front matter:

```
---
title: Translate a page with your assistant
summary: One sentence saying what the reader gets done.
audience: editors
order: 2
written-for: 0.30.0
---
```

- `audience` is the folder name; `order` sorts the guides within it on the index page.
- `written-for` is the opticli version the guide was last checked against. Set it to the version being prepared
  whenever you update a guide.
- No top-level `#` heading (the title comes from the front matter) and no raw HTML. Use `##` and `###` headings,
  paragraphs, lists, tables, fenced code blocks and blockquotes (`> **Note:** …`).
- Fenced code blocks name their language: `bash`, `json`, `csharp`, `ts`, `text`, and `prompt` for something you type
  to an assistant.
- Link to the README as `https://github.com/Epinova/opticli#<heading-anchor>` and to other guides by relative path
  (`../mcp/custom-tools.md`); the site build rewrites those to the guide pages.
- Examples use the Alloy sample site and made-up hosts. Never a client's name, site or data.

Keeping the guides current is part of every change; see [AGENTS.md](../../AGENTS.md).
