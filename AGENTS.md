# Notes for coding agents

opticli is a .NET global tool (`src/OptiCli`), a site agent loaded into the site (`src/OptiCli.Agent`), an MCP module for
editors (`src/OptiCli.Mcp`, `src/OptiCli.Mcp.Cms13`) and an npm package for Playwright (`npm/`). [docs/development.md](docs/development.md) describes the projects, how to build and test, and how to release.

## When you add or change functionality, update the docs in the same change

A feature isn't done until everything that describes it says the same thing. Check each of these and update what the
change touches:

- **[docs/reference/](docs/reference/)**: the reference, one page per area (`cli/` for the CLI, `mcp/` for the MCP
  module). Commands, flags, options, output fields, error codes, limits.
- **[README.md](README.md)**: the quickstart and the documentation index. Update it when a change alters what it shows
  (install, quick start, the summaries, supported versions, known limitations) or adds a reference page. It is also the
  NuGet packages' page, so its links are absolute (`https://github.com/Epinova/opticli/blob/main/...`).
- **[skill/SKILL.md](skill/SKILL.md)**: what a coding agent is taught about the CLI. Update it when a command, flag or
  output an agent relies on changes.
- **[CHANGELOG.md](CHANGELOG.md)**: one entry per release, under the version being prepared.
- **[docs/guides/](docs/guides/)**: the how-to guides on the landing page, for editors (`editors/`), CLI developers
  (`developers/`) and MCP integrators (`mcp/`). When a change alters a step, command, option, tool, prompt or result
  that a guide shows, update that guide, and set its `written-for` to the version being prepared. When a new feature is
  a task someone will want to do from start to finish, consider a new guide, and say so if you don't write one.

To find the guides a change touches, search them for the command, option, tool or field you changed:

```sh
grep -rn -e 'sync pull' -e 'AllowedRedirectHosts' docs/guides/
```

Guides link to the reference for full flag and option tables instead of repeating them, so a flag rename usually only
needs the reference; a changed workflow needs the guide too. [docs/guides/README.md](docs/guides/README.md) describes
the guide format.

## Keep it generic

Examples, fixtures, tests, docs and commit messages use the Alloy sample site and made-up hosts such as
`alloy.example.com`. Never name a client, a client's site, its host names or its data. Epinova may be named as the
maker.
