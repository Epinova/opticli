---
title: Read the site from Node.js scripts and playwright.config
summary: Call opticli's commands as typed methods from a Node.js script, and build Playwright projects from the site's own data, such as one project per enabled language.
audience: developers
order: 7
written-for: 0.34.0
---

A test suite or a build script often needs to know something about the site before it starts: which languages are
enabled, which pages use a block, which pages of a type exist. The npm package `@epinova/opticli` has a method for
each opticli command a script needs, typed by what the command prints, and a blocking client for the one place that
can't wait for an answer: `playwright.config`. By the end of this guide you have a script that reads the site, a
Playwright config with a project per enabled language, and the escape hatches for what no command answers.

## Before you start

- opticli at the npm package's version or newer (`opticli --version`), and the package installed: `npm install -D
  @epinova/opticli`. [Seed CMS content for Playwright tests](playwright-seed-content.md) shows how to pin opticli
  with a tool manifest.
- The site's repository, with a local database opticli finds (`opticli doctor`). Reads need no running site; writes
  need `opticli serve`.
- Node.js 18 or newer, and TypeScript for the types (plain JavaScript works too). The examples use the Alloy sample
  site's types.

## Steps

### 1. Call a command

Each command is a method named after it in camelCase. Its arguments are parameters in the CLI's order, and its flags
an options object named after them:

```ts
// scripts/report.ts
import { OptiCli } from '@epinova/opticli';

const cli = new OptiCli({ project: '../src/Alloy' });

const languages = await cli.languages(); // LanguageInfo[]: the enabled ones; { all: true } adds the rest
console.log(languages.map((language) => language.code));

const articles = await cli.find({ type: 'ArticlePage', lang: 'en' }); // ContentIdentity[]
for (const usage of await cli.whereUsed('/en/about-us/', { pages: true })) {
  console.log(`${usage.name} (${usage.ref}) shows it in ${usage.property}`);
}
console.log(`${articles.length} articles`);
```

`opticli <command> --help` and your editor's hover show the same help for each method and option. Where an option
changes what the command prints, the return type follows it: `cli.find({ type: 'ArticlePage', status: 'draft' })`
gives `DraftItem[]`, `cli.serve({ status: true })` gives `AgentStatus`. An option the command doesn't have throws a
`TypeError` before opticli runs.

Writes are methods too (`cli.create()`, `cli.set()`, `cli.apply()`, `cli.projectsReady()`, ...), and need the site
running: `await cli.serve()` starts it, or reuses the one that runs. They change the database for good; in tests,
the `cms` fixture undoes them afterwards (see [Seed CMS content for Playwright tests](playwright-seed-content.md)).

### 2. Read every page of a list

A command that lists gives one page at a time. Its `...All` method follows the pages and yields every item:

```ts
let count = 0;
for await (const article of cli.findAll({ type: 'ArticlePage' })) {
  if (article.status === 'published') count++;
}
console.log(`${count} published articles`);
```

### 3. Build Playwright projects from the site

`playwright.config` can't await, so use `OptiCliSync`: the same methods, each blocking until opticli answers. A
project per language the site has enabled (`languages()` lists only those):

```ts
// playwright.config.ts
import { defineConfig } from '@playwright/test';
import { OptiCliSync } from '@epinova/opticli';

const languages = new OptiCliSync().languages();

export default defineConfig({
  testDir: './e2e',
  projects: languages.map((language) => ({ name: language.code, use: { locale: language.code } })),
});
```

A test reads its project's language from `testInfo.project.name` (or `locale`), and `npx playwright test --list`
shows a project per language. Playwright loads the config in its main process and again in every worker, so the
call runs once per process, about a second each. Everywhere else, use the async `OptiCli`.

### 4. Ask the database when no command answers

`sql<Row>()` runs a read-only query (opticli refuses anything that writes) and types the rows as you say they are. The
columns are as SQL names them:

```ts
const { rows } = await cli.sql<{ LanguageID: string; Name: string | null }>(
  'SELECT LanguageID, Name FROM tblLanguageBranch WHERE Enabled = 1',
);
```

opticli can't check your row type, and the CMS's tables change between versions, so look for a command first. A
command that isn't a method (`drift`, `doctor`, ...) runs with `run(['drift'])` (`runSync` on `OptiCliSync`), which
returns the JSON envelope.

### 5. Handle errors

A command that fails throws `OptiCliError`, with opticli's `code`, `exitCode`, `hint` and `details`:

```ts
import { OptiCliError } from '@epinova/opticli';

try {
  await cli.get('/en/no-such-page/');
} catch (e) {
  if (e instanceof OptiCliError && e.code === 'not_found') console.log(e.hint);
  else throw e;
}
```

A command that doesn't answer within two minutes is stopped and throws `code` `timeout`. Pass a longer `timeoutMs`
in the run options, after the options object: `cli.search('Alloy', {}, { timeoutMs: 300_000 })`.

## If something goes wrong

| What you see | What to do |
|---|---|
| `Couldn't run opticli: install opticli ...` | opticli isn't on PATH or in a tool manifest above the working directory. Install it, or set `OPTICLI` to it. |
| `needs_selection` (exit 6) | opticli doesn't know the development database yet. Run `opticli db use <id>` once at your terminal. |
| `TypeError: opticli find has no option '...'` | The option isn't one of the command's: check `opticli find --help`. |
| `playwright.config` takes seconds to load | Each Playwright process runs the blocking call once. Read the data in the global setup instead if it doesn't decide the projects. |
| A method you need isn't there | Commands for the terminal or for setting up aren't generated. Use `run([...])`. |

## Related

- [The npm package's README](https://github.com/Epinova/opticli/blob/main/npm/README.md#nodejs)
- [Seed CMS content for Playwright tests](playwright-seed-content.md)
- [Find content and the code behind it](find-content-and-code.md)
- [Tests with Playwright and Node.js](https://github.com/Epinova/opticli#tests-with-playwright-and-nodejs)
