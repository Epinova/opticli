# opticli for Node.js and Playwright

Typed access to [opticli](https://github.com/epinova/opticli) from Node.js, and Playwright fixtures that use it: start
an Optimizely CMS 12 or 13 site, seed the content the tests need, give each test content of its own, and clean up
after. Writes go through the CMS (versions, cache, events), so a page shows a change at once.

## Install

```bash
npm install -D @epinova/opticli
```

The package runs the opticli CLI; it doesn't install it. Its version is the opticli version it was released with, and it
needs that opticli or newer: pin it in
the site's repository with a [local tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use),
so CI gets the same version:

```bash
dotnet new tool-manifest
dotnet tool install OptiCli
```

or install it globally (`dotnet tool install -g OptiCli`). opticli's own rules apply: the site runs against your local
or chosen development database (`opticli doctor` shows which), never production.

## Playwright

A seed plan: content every test can read. It is an [opticli plan](https://github.com/epinova/opticli/blob/main/skill/reference.md#plans-apply)
that setup applies with `--update-existing` on every run, so the first run creates it and later runs only repair what
changed. Give it a fixed `guidNamespace` and an `id` on each step that creates something.

```ts
// e2e/seed.ts
import type { Plan } from '@epinova/opticli';

export const seedPlan = {
  guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61', // any GUID; keep it
  operations: [
    { op: 'create', id: 'root', parent: '/en/', type: 'StandardPage', name: 'Test fixtures', properties: { PageVisibleInMenu: false } },
    { op: 'upload', id: 'image', file: 'files/teaser.png', for: '$root' },
    { op: 'create', id: 'article', parent: '$root', type: 'ArticlePage', name: 'Seeded article', properties: { MainBody: '<p>Hello</p>' } },
  ],
} as const satisfies Plan;
```

```ts
// e2e/global-setup.ts
import { defineSetup } from '@epinova/opticli/playwright';
import { seedPlan } from './seed.ts';

export default defineSetup({ plan: seedPlan, planDir: import.meta.dirname });
```

```ts
// playwright.config.ts
import { defineConfig } from '@playwright/test';
import type { OptiCliWorkerOptions } from '@epinova/opticli/playwright';

export default defineConfig<{}, OptiCliWorkerOptions>({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  use: {
    // Each worker's test content goes below a hidden page of its own, below the seed plan's root.
    opticliSandbox: { parent: '$root', type: 'StandardPage', properties: { PageVisibleInMenu: false } },
  },
});
```

```ts
// e2e/fixtures.ts: seed.refs and seed.urls typed by the plan's step ids
import { createTest } from '@epinova/opticli/playwright';
import type { seedPlan } from './seed.ts';

export const test = createTest<typeof seedPlan>();
export { expect } from '@epinova/opticli/playwright';
```

```ts
// e2e/article.spec.ts
import { expect, test } from './fixtures.ts';

test('the seeded article', async ({ page, seed }) => {
  await page.goto(seed.urls.article!);
  await expect(page.getByText('Hello')).toBeVisible();
});

test('a draft is not visible until it is published', async ({ page, cms }) => {
  const created = await cms.page('StandardPage', 'Draft test', { MainBody: '<p>Published text</p>' });
  const draft = await cms.set(created.ref, { MainBody: '<p>Draft text</p>' });

  await page.goto(created.url);
  await expect(page.getByText('Draft text')).toHaveCount(0);

  await cms.publish(draft.version!);
  await page.reload();
  await expect(page.getByText('Draft text')).toBeVisible();
});
```

### What setup does

`defineSetup()` is the `globalSetup`. It starts the site with `opticli serve` (or reuses one that runs already: `serve`,
or the site your IDE runs in IDE mode), checks that opticli and the site's agent are new enough, applies the seed plan,
and points `baseURL` at the site. After the tests it stops a site it started, and leaves one that ran before. The seeded
content stays for the next run.

Setup logs what the seed plan did: `unchanged`, or which steps changed and which of their properties. A step that
changes on every run has a value the CMS stores differently from how the plan gives it; write it as the CMS stores it
(the log's step, `opticli get`).

For a site that redirects HTTP to HTTPS, set `https: true`. The address is `https://localhost:<port>` with the ASP.NET
Core development certificate, which a browser may not trust: add `ignoreHTTPSErrors: true` to `use` in
playwright.config, or trust it (`dotnet dev-certs https --trust`).

| Option | Default | |
|---|---|---|
| `plan` | none | The seed plan. |
| `planDir` | the working directory | Where the plan's files are (`upload`, `"@file"` values); they must be inside it. |
| `publish` | `true` | Publish what the plan saves. |
| `https` | `OPTICLI_HTTPS=1`, else `"https"` in opticli's user config | Also serve the site on `https://localhost:<port>` (`serve --https`) and browse it there, for a site that redirects HTTP to HTTPS. A site that runs already without an HTTPS address fails setup: stop it first. |
| `build` | `OPTICLI_BUILD=1` | Build the site before starting it (`serve --build`). |
| `keepSite` | `OPTICLI_KEEP_SITE=1` | Leave a site that setup started running. |
| `opticli` | see below | `command`, `project`, `cwd`. |

### Fixtures

| Fixture | Scope | |
|---|---|---|
| `seed` | worker | `refs` and `urls` of the plan's steps, by `id`; `baseURL`. The URLs are paths on `baseURL` (`/en/fixtures/`), never the site's own hosts, which don't answer while `opticli serve` runs it. |
| `cms` | test | Content for one test: `page(type, name, properties?, { parent?, publish?, keep? })` (published by default; returns `ref`, `version` and `url`), `set`, `publish`, `apply(plan, { keep?, ... })`. What `page` and `apply` create goes to the recycle bin after the test, unless `keep`. |
| `sandbox` | worker | With the `opticliSandbox` option: the ref of the worker's own page, where `cms.page()` creates content. It goes to the recycle bin when the worker ends, with anything a failed test left. |
| `opticli` | worker | The `OptiCli` client, for anything else. |
| `baseURL` | test | The site `opticli serve` runs. |

`opticliSandbox`: `parent` (a ref, or `$id` of a seed step), `type` (a page type the parent allows and that allows your
test pages), `properties`. Without it, pass `{ parent }` to `cms.page()`.

### What stays, and what goes

| | After the test run |
|---|---|
| The seed plan | Stays. The next run repairs what changed. |
| `cms.page()`, `cms.apply()` | Go after the test. With `keep: true`, stay. |
| `cms.set()`, `cms.publish()` | Stay: they change content the test didn't make. On seeded content the next run's seed puts it back, but tests in other workers of the same run see the change. |
| The sandbox | Goes when the worker ends, with everything below it, whatever made it. |
| The `opticli` fixture, the Node.js client | Stay: they never remove anything (except below the sandbox, as above). |

`keep: true` needs a parent outside the sandbox and outside content the test made, since that goes with everything
below it; `cms` refuses it there before saving anything. A step of `cms.apply()` that found its content already there
(`updateExisting`) leaves it, with or without `keep`.

Deleted content goes to the recycle bin, as in the CMS: nothing is deleted for good. On a local database that is
usually fine; empty the recycle bin in the CMS now and then.

## Node.js

```ts
import { OptiCli, OptiCliError } from '@epinova/opticli';

const cms = new OptiCli({ project: '../src/Site' });
const page = await cms.create('/en/', 'StandardPage', 'Hello', { MainBody: '<p>Hi</p>' }, { publish: true });
console.log(page.url); // /en/hello/

try {
  await cms.get('999999');
} catch (e) {
  if (e instanceof OptiCliError && e.code === 'not_found') console.log(e.hint);
}
```

The client changes what you tell it to and removes nothing on its own, so it suits scripts that make content to keep.
Typed methods: `serve`, `serveStatus`, `serveStop`, `get`, `url`, `create`, `set`, `publish`, `delete`, `apply`.
`run(args)` runs any other command and returns its JSON envelope. A failure throws `OptiCliError` with opticli's `code`,
`exitCode`, `hint`, `details` and `version`; see opticli's
[exit codes](https://github.com/epinova/opticli#output-and-exit-codes).

Every call runs opticli once (about a second, most of it .NET starting): fine for setup, and a plan (`apply`) does many
changes in one call.

### Which opticli, which site

| | |
|---|---|
| `command` option or `OPTICLI` | The opticli to run: an executable, or a checkout's `opticli.dll` (run with `dotnet`). Otherwise `dotnet tool run opticli` when a tool manifest above the working directory has it, else `opticli` on PATH. |
| `project` option or `OPTICLI_PROJECT` | The site's `.csproj` or folder, relative to `cwd` (default: the working directory). Otherwise opticli finds it from the working directory. |

`apply` runs in `baseDir` (`planDir` in setup), so a tool manifest must be in or above that folder too.

## Licence

MPL-2.0, as opticli.
