// Playwright fixtures:
// - `opticli` (worker): the CLI, run as setup ran it.
// - `seed` (worker): refs and URLs of the seed plan's content.
// - `sandbox` (worker): with the `opticliSandbox` option, a page per worker that test content goes below; it goes to the
//   recycle bin when the worker ends, with whatever a failed test left behind.
// - `cms` (test): writes that are undone after the test.
// - `baseURL`: the site opticli serves.
import { test as base } from '@playwright/test';
import { OptiCli, OptiCliError, type WriteOptions } from '../opticli.ts';
import type { ApplyResult, ContentRef, Plan, PlanIds, Properties, WriteResult } from '../types.ts';
import { readSeedState, type SeedState } from './setup.ts';

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
  /** The page's URL (host-relative), for `page.goto`; for a draft, where it will be once published. */
  url: string;
}

/** Writes that a test's content gets, undone after the test. */
export class Cms {
  private readonly created: ContentRef[] = [];

  constructor(
    readonly opticli: OptiCli,
    /** Where {@link page} creates content unless given a parent: the worker's sandbox. */
    readonly parent: ContentRef | undefined,
  ) {}

  /** Creates a page, published unless `publish: false`, and removes it after the test. */
  async page(type: string, name: string, properties: Properties = {}, options: WriteOptions & { parent?: ContentRef } = {}): Promise<CreatedPage> {
    const parent = options.parent ?? this.parent;
    if (!parent) throw new Error('cms.page() needs a parent: pass { parent }, or set the opticliSandbox option in playwright.config.');
    const result = await this.opticli.create(parent, type, name, properties, { ...options, publish: options.publish ?? true });
    this.track(result.ref, parent);
    if (!result.url) throw new Error(`${type} ${result.ref} has no URL: is ${type} a page type with a template?`);
    return result as CreatedPage;
  }

  /** Saves a draft of the change unless `publish`. On seeded content, the next run's seed puts it back. */
  set(ref: ContentRef, properties: Properties, options: WriteOptions = {}): Promise<WriteResult> {
    return this.opticli.set(ref, properties, options);
  }

  /** Publishes the latest version, or the version `ref` names (`123_456`). */
  publish(ref: ContentRef): Promise<WriteResult> {
    return this.opticli.publish(ref);
  }

  /** Runs a plan; what it creates below existing content is removed after the test. */
  async apply(plan: Plan, options: Parameters<OptiCli['apply']>[1] = {}): Promise<ApplyResult> {
    const result = await this.opticli.apply(plan, options);
    for (const step of plan.operations) {
      if ((step.op === 'create' || step.op === 'block' || step.op === 'upload') && step.id && result.created[step.id]) {
        this.track(result.created[step.id], step.parent ?? step.for);
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

    cms: async ({ opticli, sandbox }, use) => {
      const cms = new Cms(opticli, sandbox);
      await use(cms);
      await cms.cleanup();
    },
  });
}
