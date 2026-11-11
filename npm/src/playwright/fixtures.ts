// Playwright fixtures:
// - `opticli` (worker): the CLI, run as setup ran it.
// - `seed` (worker): refs and URLs of the seed plan's content.
// - `sandbox` (worker): with the `opticliSandbox` option, a page per worker that test content goes below; it goes to the
//   recycle bin when the worker ends, with whatever a failed test left behind.
// - `cms` (test): writes that are undone after the test.
// - `baseURL`: the site opticli serves.
import { test as base } from '@playwright/test';
import { OptiCli, OptiCliError, type WriteOptions } from '../opticli.ts';
import type { ApplyResult, ContentRef, Plan, PlanIds, PlanStep, Properties, WriteResult } from '../types.ts';
import { readSeedState, type SeedState } from './setup.ts';
import { browseUrl } from './urls.ts';

/** The `opticliSandbox` option: the page each worker makes for its tests' content. */
export interface SandboxOptions {
  /** Where: a ref, or "$id" of a step in the seed plan. */
  parent: ContentRef;
  /** A page type its parent allows, that allows the test pages below it. */
  type: string;
  /** Values for it, such as `{ PageVisibleInMenu: false }` to keep it out of menus. */
  properties?: Properties;
}

/** The options to set in playwright.config's `use`: `defineConfig<{}, OptiCliWorkerOptions>({ use: { opticliSandbox: ... } })`. */
export type OptiCliWorkerOptions = { opticliSandbox?: SandboxOptions };

export interface CreatedPage extends WriteResult {
  /**
   * The page's URL, for `page.goto`: a path on `baseURL`, or, below another site the served site answers on a host of
   * its own, that site's address (as `seed.urls`). For a draft, where it will be once published.
   */
  url: string;
}

/** Leave the content on the site after the test: `cms.page(..., { keep: true, parent })`. */
export interface KeepOption {
  /**
   * Don't remove what this call creates after the test. It needs a parent outside the worker's sandbox and outside
   * content the test made, which both go after the test (or worker) with everything below them.
   */
  keep?: boolean;
}

/** Writes that a test's content gets, undone after the test unless `keep`. */
export class Cms {
  private readonly created: ContentRef[] = [];

  constructor(
    readonly opticli: OptiCli,
    /** Where {@link page} creates content unless given a parent: the worker's sandbox. */
    readonly parent: ContentRef | undefined,
    /** The addresses the served site listens on (`seed.addresses`), to open a page on its own site's host. */
    readonly addresses: readonly string[] = [],
  ) {}

  /** Creates a page, published unless `publish: false`, and removes it after the test unless `keep`. */
  async page(
    type: string,
    name: string,
    properties: Properties = {},
    { parent: given, keep, ...options }: WriteOptions & KeepOption & { parent?: ContentRef } = {},
  ): Promise<CreatedPage> {
    const parent = given ?? this.parent;
    if (!parent) throw new Error('cms.page() needs a parent: pass { parent }, or set the opticliSandbox option in playwright.config.');
    if (keep) this.checkKeep('cms.page()', [parent]);
    const result = await this.opticli.create(parent, type, name, properties, { ...options, publish: options.publish ?? true });
    if (!keep) this.track(result.ref, parent);
    if (!result.url) throw new Error(`${type} ${result.ref} has no URL: is ${type} a page type with a template?`);
    return { ...result, url: browseUrl(result.url, result.canonicalUrl, this.addresses)! };
  }

  /** Saves a draft of the change unless `publish`. On seeded content, the next run's seed puts it back. */
  set(ref: ContentRef, properties: Properties, options: WriteOptions = {}): Promise<WriteResult> {
    return this.opticli.set(ref, properties, options);
  }

  /** Publishes the latest version, or the version `ref` names (`123_456`). */
  publish(ref: ContentRef): Promise<WriteResult> {
    return this.opticli.publish(ref);
  }

  /**
   * Runs a plan; what it creates is removed after the test unless `keep`. Content that was there before (a step
   * that found it with `updateExisting`) stays.
   */
  async apply(plan: Plan, { keep, ...options }: Parameters<OptiCli['apply']>[1] & KeepOption = {}): Promise<ApplyResult> {
    if (keep) {
      const parents = plan.operations.map((step) => (isCreate(step) ? (step.parent ?? step.for) : undefined));
      this.checkKeep('cms.apply()', parents.filter((p): p is ContentRef => p !== undefined));
    }
    const result = await this.opticli.apply(plan, options);
    if (keep) return result;
    for (const step of result.operations) {
      const planned = plan.operations[step.index];
      if (step.id && result.created[step.id] && isCreate(planned) && !step.result?.existing) {
        this.track(result.created[step.id], planned.parent ?? planned.for);
      }
    }
    return result;
  }

  /** Deletes what the test made, newest first. Content below other tracked content goes with it. */
  async cleanup(): Promise<void> {
    const errors: unknown[] = [];
    for (const ref of [...this.created].reverse()) {
      try {
        await this.opticli.delete(ref, { ignoreReferences: true });
      } catch (e) {
        if (!(e instanceof OptiCliError && e.code === 'not_found')) errors.push(e);
      }
    }
    this.created.length = 0;
    if (errors.length) throw new AggregateError(errors, `opticli cleanup: ${errors.length} delete(s) failed`);
  }

  private track(ref: ContentRef, parent: ContentRef | undefined): void {
    // Below tracked or planned ("$id") content: deleting the top item takes it along.
    if (parent && (parent.startsWith('$') || this.created.includes(parent))) return;
    this.created.push(ref);
  }

  /** Refuses `keep` below content that goes after the test or worker, before anything is saved. */
  private checkKeep(call: string, parents: readonly ContentRef[]): void {
    for (const parent of parents) {
      if (parent === this.parent) {
        throw new Error(`${call} with keep: true needs a parent outside the worker's sandbox, which goes when the worker ends.`);
      }
      if (this.created.includes(parent)) {
        throw new Error(`${call} with keep: true can't go below ${parent}, which this test made and which goes after it.`);
      }
    }
  }
}

function isCreate(step: PlanStep | undefined): step is Extract<PlanStep, { op: 'create' | 'block' | 'upload' }> {
  return step?.op === 'create' || step?.op === 'block' || step?.op === 'upload';
}

type TestFixtures = { cms: Cms };

type WorkerFixtures<Id extends string> = {
  opticli: OptiCli;
  seed: SeedState<Id>;
  sandbox: ContentRef | undefined;
  opticliSandbox: SandboxOptions | undefined;
};

/**
 * The fixtures, with `seed.refs` and `seed.urls` typed by the seed plan's step ids:
 * `export const test = createTest<typeof seedPlan>();` (the plan written `as const satisfies Plan`).
 */
export function createTest<P extends Plan = Plan>() {
  type Id = PlanIds<P> extends never ? string : PlanIds<P>;
  return base.extend<TestFixtures, WorkerFixtures<Id>>({
    opticliSandbox: [undefined, { option: true, scope: 'worker' }],

    seed: [async ({}, use) => use(readSeedState<Id>()), { scope: 'worker' }],

    opticli: [async ({ seed }, use) => use(new OptiCli({ ...seed.opticli, command: [...seed.opticli.command] })), { scope: 'worker' }],

    sandbox: [
      async ({ opticli, seed, opticliSandbox }, use, workerInfo) => {
        if (!opticliSandbox) return use(undefined);
        const parent = opticliSandbox.parent.startsWith('$')
          ? (seed.refs as Record<string, ContentRef>)[opticliSandbox.parent.slice(1)]
          : opticliSandbox.parent;
        if (!parent) throw new Error(`opticliSandbox.parent ${opticliSandbox.parent} is no step id of the seed plan.`);
        const name = `Playwright worker ${workerInfo.workerIndex} ${new Date().toISOString()}`;
        const page = await opticli.create(parent, opticliSandbox.type, name, opticliSandbox.properties ?? {}, { publish: true });
        await use(page.ref);
        await opticli.delete(page.ref, { ignoreReferences: true });
      },
      { scope: 'worker' },
    ],

    baseURL: async ({ seed }, use) => use(seed.baseURL),

    cms: async ({ opticli, sandbox, seed }, use) => {
      const cms = new Cms(opticli, sandbox, seed.addresses ?? []);
      await use(cms);
      await cms.cleanup();
    },
  });
}
