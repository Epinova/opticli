---
title: Seed CMS content for Playwright tests
summary: Use the @epinova/opticli npm package to start the site, seed the content your Playwright tests read, and give each test pages of its own that are cleaned up afterwards.
audience: developers
order: 3
written-for: 0.31.0
---

End-to-end tests on an Optimizely site usually depend on content someone made by hand, and break when an editor
changes it. With the npm package `@epinova/opticli`, the tests bring their own content: a global setup starts the
site with `opticli serve` and applies a seed plan, each worker gets a hidden sandbox page, and each test creates the
pages it needs through the CMS and has them moved to the recycle bin when it ends. By the end of this guide you have
a seed plan, a global setup, a config and typed fixtures, and tests that read seeded content or make their own.

## Before you start

- opticli at the npm package's version or newer: the package shares its version with the opticli it was released with.
- The site's repository, built, with a local database opticli finds (`opticli doctor`). The tests write to it.
- Node.js 18 or newer and `@playwright/test` 1.40 or newer. The examples use the Alloy sample site's types.

## Steps

### 1. Install the package and pin opticli

```bash
npm install -D @epinova/opticli
dotnet new tool-manifest
dotnet tool install OptiCli
```

The package runs the opticli CLI but doesn't install it. A local tool manifest pins the version in the repository, so
CI runs the same one; the package uses `dotnet tool run opticli` when a manifest above the working directory has it,
else `opticli` on your PATH. If the Playwright folder isn't inside the site project, tell it where the site is with
`OPTICLI_PROJECT=../src/Alloy` (or the `opticli: { project }` option in step 3).

### 2. Write the seed plan

The seed plan is the content every test may read. It is an ordinary opticli plan. A fixed `guidNamespace` and an `id`
on every step that creates something give the content the same GUIDs on every machine, so the plan can run on every
test run and only repair what changed.

```ts
// e2e/seed.ts
import type { Plan } from '@epinova/opticli';

export const seedPlan = {
  guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61', // any GUID; keep it
  operations: [
    { op: 'create', id: 'root', parent: '/en/', type: 'StandardPage', name: 'Test fixtures', properties: { PageVisibleInMenu: false } },
    { op: 'upload', id: 'image', file: 'files/teaser.png', for: '$root' },
    { op: 'create', id: 'article', parent: '$root', type: 'StandardPage', name: 'Seeded article', properties: { MainBody: '<p>Hello</p>' } },
    { op: 'block', id: 'teaser', type: 'TeaserBlock', name: 'Seeded teaser', for: '$article',
      properties: { Heading: 'Seeded teaser', Text: 'A shared block from the seed plan.', Image: '$image' } },
    { op: 'area', ref: '$article', property: 'MainContentArea', action: 'add', item: '$teaser' },
  ],
} as const satisfies Plan;
```

A database the CMS created from scratch has no sites and few languages; the plan can add them first, see
[Build a test database from scratch](build-a-test-database.md). `$root` refers to what the step with `id: 'root'` made. `as const satisfies Plan` checks the steps against the plan
type and keeps the ids as literals, which step 4 uses. Files the plan uploads are relative to `planDir`.

### 3. Add the global setup and the config

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
    // Each worker's test pages go below a hidden page of its own, below the seed plan's root.
    opticliSandbox: { parent: '$root', type: 'StandardPage', properties: { PageVisibleInMenu: false } },
  },
});
```

`defineSetup()` starts the site with `opticli serve`, or reuses one that already runs (also the site your IDE runs in
IDE mode). It checks that opticli and the site's agent are new enough, applies the seed plan with `--update-existing`
(published, unless `publish: false`) and points `baseURL` at the site. After the tests it stops a site it started.
For a site that redirects to HTTPS, pass `https: true`, or `httpsPort: 5443` for a fixed port (site hosts carry it).
To run against a launch profile's database and variables, pass `opticli: { profile: 'E2E' }`. `import.meta.dirname` needs Node.js 20.11 or newer; on older
versions use `dirname(fileURLToPath(import.meta.url))`. The other options are in the
[package README](https://github.com/Epinova/opticli/blob/main/npm/README.md#what-setup-does).

### 4. Type the fixtures by your plan

```ts
// e2e/fixtures.ts
import { createTest } from '@epinova/opticli/playwright';
import type { seedPlan } from './seed.ts';

export const test = createTest<typeof seedPlan>();
export { expect, OptiCliError } from '@epinova/opticli/playwright';
```

`seed.refs` and `seed.urls` are now keyed by the plan's step ids: `seed.urls.article` compiles, `seed.urls.artcle`
doesn't.

### 5. Write the tests

```ts
// e2e/article.spec.ts
import { expect, test } from './fixtures.ts';

test('the seeded article shows its teaser', async ({ page, seed }) => {
  await page.goto(seed.urls.article!);
  await expect(page.getByRole('heading', { name: 'Seeded teaser' })).toBeVisible();
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

Seeded content costs nothing per test. `cms.page()` creates a published page in the worker's sandbox and returns its
`ref`, `version` and `url`, a path on `baseURL` (on a solution with several sites, a page on another site's own
`*.localhost` host gets that site's address, as `seed.urls` does, and `seed.sites` names each site's address). `cms.set()` saves a draft unless you pass `publish: true`, and
`cms.apply()` runs a whole plan in one call. What `cms.page()` and `cms.apply()` create goes to the recycle bin after
the test, also when it fails; pass `keep: true` with a parent outside the sandbox to leave it. `cms.set()` and
`cms.publish()` on seeded content stay until the next run's seed puts them back, and other workers see them meanwhile.

### 6. Test the errors too

A failed write throws `OptiCliError` with opticli's `code`, `exitCode`, `hint` and `details`, so a test can tell a
missing page from values the CMS refused:

```ts
test('a property typo fails before anything is saved', async ({ cms }) => {
  const error = await cms.page('StandardPage', 'Typo', { MainBdy: '<p>x</p>' }).catch((e: unknown) => e);
  expect(error).toBeInstanceOf(OptiCliError);
  expect((error as OptiCliError).code).toBe('usage');
  expect((error as OptiCliError).hint).toContain('MainBody');
});
```

`code` is typed (`not_found`, `validation`, `conflict`, ...), with the exit codes from
[Output and exit codes](https://github.com/Epinova/opticli#output-and-exit-codes).

### 7. Run it

```bash
npx playwright test
```

Before the tests, setup logs what the seed plan did. The first run lists the steps it saved; later runs print
`opticli: seed plan unchanged`, or which steps and properties they put back. `OPTICLI_BUILD=1` builds the site before starting it, and `OPTICLI_KEEP_SITE=1` leaves a site setup started
running for the next run. Each call to opticli takes about a second, most of it .NET starting, so prefer one
`cms.apply()` over many single writes.

## If something goes wrong

| What you see | What to do |
|---|---|
| `This package needs opticli X or newer` | Update opticli: `dotnet tool update OptiCli` where the manifest pins it, else `dotnet tool update -g OptiCli`. |
| `The site runs the site agent of opticli X, older than ...` | A site from an older opticli is still running. `opticli serve --stop`, or restart it in the IDE. |
| `No opticli setup ran` | `globalSetup` in playwright.config doesn't point at the file that exports `defineSetup()`. |
| `needs_selection` (exit 6) from setup | opticli doesn't know the development database yet. Run `opticli db use <id>` once at your terminal. |
| A seed step changes on every run | The plan gives a value differently from how the CMS stores it. The setup log names the step and property; write it as `opticli get` shows it. |
| `validation` (exit 5) | The CMS refused the values, for example a required property left out (`TeaserBlock` needs `Heading`, `Text` and `Image`). `details` lists every issue. |
| Setup fails over HTTPS | A site that already runs without an HTTPS address can't be switched. `opticli serve --stop`, then run the tests with `https: true`. |

## Related

- [The npm package's README](https://github.com/Epinova/opticli/blob/main/npm/README.md)
- [Tests with Playwright and Node.js](https://github.com/Epinova/opticli#tests-with-playwright-and-nodejs)
- [Plan format](https://github.com/Epinova/opticli/blob/main/skill/reference.md#plans-apply)
- [Build a test database from scratch](build-a-test-database.md)
- [Find content and the code behind it](find-content-and-code.md)
- [Work on a restored production database](restored-production-database.md)
