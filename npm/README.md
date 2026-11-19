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
| `httpsPort` | `OPTICLI_HTTPS_PORT`, else `"httpsPort"` in opticli's user config | That HTTPS address's port (`serve --https-port`, implies `https`): fixed, so site hosts with the port (`alloy.localhost:5001`) are right on every run. A taken port fails setup. |
| `build` | `OPTICLI_BUILD=1` | Build the site before starting it (`serve --build`). |
| `keepSite` | `OPTICLI_KEEP_SITE=1` | Leave a site that setup started running. |
| `opticli` | see below | `command`, `project`, `cwd`, `profile`. |

### Fixtures

| Fixture | Scope | |
|---|---|---|
| `seed` | worker | `refs` and `urls` of the plan's steps, by `id`; `baseURL`; `sites`. A URL is a path on `baseURL` (`/en/fixtures/`), except for content on another site that the served site answers on a host of its own: then that site's address (`http://alloy-sv.localhost:5199/sv/news/`, see [Several sites](#several-sites)). `sites` maps each such site's name to where to open it. |
| `cms` | test | Content for one test: `page(type, name, properties?, { parent?, publish?, keep?, inProject?, ... })` (published by default, unless it goes into a project or is scheduled with `publishAt`; returns what `create` does and `url`, as `seed.urls` gives it), `set`, `publish`, `apply(plan, { keep?, ... })`, `project(name, { keep? })`. What `page` and `apply` create goes to the recycle bin after the test, and the projects the test made are deleted, unless `keep`. See [Projects in tests](#projects-in-tests). |
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
| `cms.project()`, a project a plan of `cms.apply()` created | Deleted after the test, after its content. With `keep: true`, stays. A project that was there before always stays. |
| `cms.set()`, `cms.publish()` | Stay: they change content the test didn't make. On seeded content the next run's seed puts it back, but tests in other workers of the same run see the change. |
| The sandbox | Goes when the worker ends, with everything below it, whatever made it. |
| The `opticli` fixture, the Node.js client | Stay: they never remove anything (except below the sandbox, as above). |

`keep: true` needs a parent outside the sandbox and outside content the test made, since that goes with everything
below it; `cms` refuses it there before saving anything. A step of `cms.apply()` that found its content already there
(`updateExisting`) leaves it, with or without `keep`.

Deleted content goes to the recycle bin, as in the CMS: nothing is deleted for good. On a local database that is
usually fine; empty the recycle bin in the CMS now and then.
On a site that makes a new block in a ContentArea a shared block in the page's "For this page" folder (`opticli serve
--status`: `inlineBlocks: shared`), the blocks a test page's areas made stay in that folder, outside the recycle bin,
until the page is deleted for good there, as the CMS keeps a page's assets.

### A database built from scratch

A database the CMS created itself has no sites and only `en` and `sv` enabled. The seed plan can add both: a
`language` step before the content in that language, and a `site` step after its start page (setup applies with
`updateExisting`, so later runs leave the site as it is). Give the hosts the ports the site runs on, fixed with
`opticli serve`'s port and `httpsPort`:

```ts
export const seedPlan = {
  guidNamespace: '3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10',
  operations: [
    { op: 'language', lang: 'no' },
    { op: 'create', id: 'home', parent: '1', type: 'StartPage', name: 'Hjem', lang: 'no' },
    { op: 'site', name: 'Alloy', startPage: '$home', hosts: ['localhost:5199', 'alloy-en.localhost:5199@en', 'https://localhost:5443'] },
  ],
} as const satisfies Plan;
```

The site gets an assets folder of its own (`SysSiteAssets` below the start page, at `/siteassets/`), as one made in
admin mode does; `siteAssets: 'global'` keeps it on the global assets folder.

See opticli's [A database built from scratch](https://github.com/epinova/opticli#a-database-built-from-scratch).

### Several sites

`opticli serve` runs the site on one address, and `baseURL` is that address: a path on it opens the site that answers
it, the one with the host `localhost:<port>` (or `*`). Another site answers on a host of its own. Browsers send
`*.localhost` names to this machine, so a site with the host `alloy-sv.localhost:5199`, on a port `serve` listens on
(its HTTP port, or `httpsPort`), needs no hosts file. Setup gives content on such a site its own address:

```ts
test('the Swedish site has its own start page', async ({ page, seed }) => {
  await page.goto(seed.urls.svArticle!); // http://alloy-sv.localhost:5199/sv/nyheter/
  await page.goto(seed.sites['Alloy Sverige']!); // http://alloy-sv.localhost:5199/
});
```

Content on the site at `baseURL` keeps its path, and so does content whose site has only hosts that don't answer here
(a production name, another port). A plan that creates its sites after their content still gives the content its own
site's address: after a `site` step, opticli reports every step's URL as the sites have it at the end. Each site is a
separate origin for the browser, with cookies of its own.

## Node.js

```ts
import { OptiCli, OptiCliError } from '@epinova/opticli';

const cli = new OptiCli({ project: '../src/Site' });
const page = await cli.create('/en/', 'StandardPage', 'Hello', { MainBody: '<p>Hi</p>' }, { publish: true });
console.log(page.url); // /en/hello/

try {
  await cli.get('999999');
} catch (e) {
  if (e instanceof OptiCliError && e.code === 'not_found') console.log(e.hint);
}
```

The client changes what you tell it to and removes nothing on its own, so it suits scripts that make content to keep.
Every call runs opticli once (about a second, most of it .NET starting): fine for setup, and a plan (`apply`) does many
changes in one call.

### The commands as methods

Each command a test or script needs is a method, generated from opticli's own description of its commands, so it
takes what the command takes and returns what it prints (`data` of its JSON):

- The name is the command's, in camelCase: `get`, `languages`, `whereUsed`, `blockCreate`, `projectsPublish`,
  `jobsLog`.
- Arguments are parameters, in the order `opticli <command> --help` gives them, optional ones too; flags are an
  options object named after them (`--include-draft` → `includeDraft`, a repeatable flag an array). When an optional
  argument is left out, the options may come first: `cli.whereUsed({ type: 'TeaserBlock' })`. After the options, a
  run options object: `{ timeoutMs, cwd, input }`.
- The return type is the command's JSON result. Where an option changes what it prints, the type follows it.
- Your editor shows each command's and option's help.

```ts
await cli.languages({ all: true });                  // LanguageInfo[]
await cli.find({ type: 'ArticlePage', status: 'draft' }); // DraftItem[]
await cli.whereUsed('123', { pages: true });         // PageUsageRow[]
await cli.projects();                                // ProjectInfo[]
await cli.projects('Spring campaign');               // ProjectItemInfo[]: one project's items
await cli.serve({ status: true });                   // AgentStatus
await cli.serve({ stop: true });                     // ServeStopOutput
await cli.publish('123', { dryRun: true });          // WriteOutput

for await (const article of cli.findAll({ type: 'ArticlePage' })) console.log(article.name); // every page
```

A command that lists gives one page (`limit`, then `cursor`); its `...All` iterator (`findAll`, `childrenAll`,
`searchAll`, ...) runs it again for each next page and yields every item.

What's generated: reading content (`get`, `children`, `tree`, `ancestors`, `find`, `search`, `resolve`, `url`,
`versions`, `drafts`, `history`, `whereUsed`, `access`, `trash`), content types and settings (`types`, `type`,
`allowedIn`, `languages`, `sites`, `categories`, `visitorGroups`, `displayTemplates`), writes (`set`, `create`,
`area`, `blockCreate`, `composition`, `translate`, `upload`, `publish`, `unpublish`, `move`, `delete`, `restore`,
`discard`, `apply`, `access` with a change, `languagesEnable`, `languagesDisable`), projects (`projects`,
`projectsCreate`, `projectsReady`, `projectsRemove`, `projectsPublish`, `projectsReactivate`, `projectsDelete`), jobs
(`jobs`, `jobsLog`, `jobsRun`, `jobsStop`, `jobsSet`), users (`usersAdd`, `usersRemove`, `usersRoles`), `serve` and
`sql`. Commands for the terminal or for setting up (`db`, `doctor`, `sync`, `export`, `drift`, `ide`, `env`, ...)
aren't: `run(['drift'])` runs any command and returns its envelope (`data`, `meta`).

Three are written by hand, to take more than the command line can:

- `create(parent, type, name, properties?, options?)` and `set(ref, properties, options?)` take the property values as
  an object (`--values`). `create` returns `CreatedContent`, with the new content's `ref`, `version`, `guid`, `type`
  and `name`; `set` returns `SavedContent`, whose `ref` is always there.
- `apply(plan, options?)` takes a plan object (sent on stdin) or a plan file's path; `baseDir` is where the plan's files
  are. For a plan written inline, `result.created` is keyed by its step ids, and each step of `result.operations` has
  its command's result once you check its `op`:

```ts
const run = await cli.apply({
  operations: [
    { op: 'create', id: 'news', parent: '/en/', type: 'StandardPage', name: 'News' },
    { op: 'move', ref: '$news', to: '/en/archive/' },
  ],
});
run.created.news; // its ref
for (const step of run.operations) if (step.op === 'move') console.log(step.result?.previousParent);
```

### `sql<Row>`: when no command answers

`sql(query)` runs a read-only query (opticli refuses anything else) and gives the rows with their columns as SQL names
them. You say what a row is; opticli can't check it, and the CMS's tables change between versions, so prefer a
command where one answers:

```ts
const { rows } = await cli.sql<{ LanguageID: string; Name: string | null }>('SELECT LanguageID, Name FROM tblLanguageBranch');
```

### `OptiCliSync`: where you can't await

`OptiCliSync` has the same methods, `create`, `set` and `apply` too, and `runSync()` for any other command; each
blocks until opticli answers. Use it where nothing can be awaited, such as `playwright.config`, and the async client
everywhere else. A Playwright project per language the site has enabled:

```ts
// playwright.config.ts
import { defineConfig } from '@playwright/test';
import { OptiCliSync } from '@epinova/opticli';

const languages = new OptiCliSync().languages().filter((language) => language.enabled);

export default defineConfig({
  projects: languages.map((language) => ({ name: language.code, use: { locale: language.code } })),
});
```

Playwright loads the config in every worker too, so the call runs once per process (about a second each).

### Errors

A command that fails throws `OptiCliError` with opticli's `code` (`not_found`, `validation`, `conflict`, ...),
`exitCode`, `hint`, `details` and `version`; see opticli's
[exit codes](https://github.com/epinova/opticli#output-and-exit-codes). A command that doesn't answer within
`timeoutMs` (120 seconds; longer for `serve` starting a site, `apply` and `jobsRun`) is stopped and throws `code`
`timeout`. Calls that can't be right throw a `TypeError` before opticli runs: an option the command doesn't have (also
from plain JavaScript, where TypeScript doesn't check), a value of the wrong kind, too many arguments.

### Projects in tests

opticli puts drafts into the CMS's projects (see opticli's
[CMS projects](https://github.com/epinova/opticli#cms-projects)), and a test can check what editors see in one:

```ts
test('the campaign page waits in its project', async ({ cms, opticli }) => {
  await cms.project('Spring campaign test');
  const page = await cms.page('StandardPage', 'Spring campaign', { MainBody: '<p>Soon</p>' }, { inProject: 'Spring campaign test', ready: true });

  const items = await opticli.projects('Spring campaign test'); // ProjectItemInfo[]
  expect(items.map((item) => item.ref)).toContain(page.ref);
});
```

- `inProject` (a project's id or name) on `create`, `set`, `cms.page()` and `cms.set()` puts the draft in that project,
  and `ready: true` marks it Ready to publish. `cms.page()` doesn't publish a page that goes into a project.
- A plan's `project` (or a step's) does the same for its save steps, and creates the project when it doesn't exist.
- `cms.project(name)` makes a project for the test. A project the test made, with `cms.project()` or through a plan
  that created it, is deleted after the test, after the test's content, unless `keep: true`. A project that was there
  before is never deleted. Deleting a project leaves its items' versions as they are.
- The client itself (`opticli`, the Node.js client) deletes no project on its own.

### Upgrading from 0.33

0.34 generated the client from the CLI, and some names and types changed with it; the
[CHANGELOG](https://github.com/epinova/opticli/blob/main/CHANGELOG.md) lists every one. What most code meets:

- `serveStatus()` and `serveStop()` are `serve({ status: true })` and `serve({ stop: true })`; `serve()` returns
  `AgentStatus`, not the envelope.
- Renamed result types: `ContentItem` → `ContentDocument`, `WriteResult` → `WriteOutput` (`SavedContent` from `set`,
  `CreatedContent` from `create`), `ApplyResult` → `AppliedPlan`, `ServeResult` → `AgentStatus`, `UrlResult` →
  `ContentUrls`, `DeleteResult` → `MoveOutput`, `ChangedProperty` → `PropertyChange`. `WriteOptions` is
  `CreateCallOptions` or `SetCallOptions`.
- An option a command doesn't have throws a `TypeError` instead of being ignored.
- In a plan, an `area` step's display option is `display`, and a `publish` or `discard` step's `version` a number:
  opticli refused the old spellings already.

### Which opticli, which site

| | |
|---|---|
| `command` option or `OPTICLI` | The opticli to run: an executable, or a checkout's `opticli.dll` (run with `dotnet`). Otherwise `dotnet tool run opticli` when a tool manifest above the working directory has it, else `opticli` on PATH. |
| `project` option or `OPTICLI_PROJECT` | The site's `.csproj` or folder, relative to `cwd` (default: the working directory). Otherwise opticli finds it from the working directory. |
| `profile` option or `OPTICLI_PROFILE` | A launch profile (`--profile`), passed to every call: it picks the database, and `serve` runs the site with the profile's environment variables too (except those `serve` sets itself). The workers use the same. |

`apply` runs in `baseDir` (`planDir` in setup), so a tool manifest must be in or above that folder too.

## Licence

MPL-2.0, as opticli.
