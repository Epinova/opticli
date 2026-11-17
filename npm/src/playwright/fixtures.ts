// Playwright fixtures:
// - `opticli` (worker): the CLI, run as setup ran it.
// - `seed` (worker): refs and URLs of the seed plan's content.
// - `sandbox` (worker): with the `opticliSandbox` option, a page per worker that test content goes below; it goes to the
//   recycle bin when the worker ends, with whatever a failed test left behind.
// - `cms` (test): writes that are undone after the test.
// - `baseURL`: the site opticli serves.
import { test as base } from '@playwright/test';
import type { ProjectResult, WriteOutput } from '../generated/types.ts';
import type { PublishOptions } from '../generated/options.ts';
import { OptiCli, OptiCliError, type ApplyCallOptions, type CreateCallOptions, type SetCallOptions } from '../opticli.ts';
import type { AppliedPlan, CreatedContent, SavedContent } from '../results.ts';
import type { ContentRef, Plan, PlanIds, PlanStep, Properties } from '../types.ts';
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

export interface CreatedPage extends CreatedContent {
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

/** `cms.page`'s options: `create`'s (but a dry run, which makes no page), where, and whether to keep it. */
export type PageOptions = Omit<CreateCallOptions, 'dryRun'> & KeepOption & { parent?: ContentRef };

/** Writes that a test's content gets, undone after the test unless `keep`. */
export class Cms {
  private readonly created: ContentRef[] = [];
  /** Projects this test made (by id), deleted after its content. */
  private readonly projects: number[] = [];

  constructor(
    readonly opticli: OptiCli,
    /** Where {@link page} creates content unless given a parent: the worker's sandbox. */
    readonly parent: ContentRef | undefined,
    /** The addresses the served site listens on (`seed.addresses`), to open a page on its own site's host. */
    readonly addresses: readonly string[] = [],
  ) {}

  /**
   * Creates a page and removes it after the test unless `keep`. Published, unless `publish: false`, it is scheduled
   * (`publishAt`), or it goes into a project (`inProject`), whose publish puts it live.
   */
  async page(type: string, name: string, properties: Properties = {}, { parent: given, keep, ...options }: PageOptions = {}): Promise<CreatedPage> {
    const parent = given ?? this.parent;
    if (!parent) throw new Error('cms.page() needs a parent: pass { parent }, or set the opticliSandbox option in playwright.config.');
    if (keep) this.checkKeep('cms.page()', [parent]);
    // opticli refuses --publish with --publish-at or --in-project.
    const publish = options.publish ?? !(options.inProject || options.publishAt);
    const result = await this.opticli.create(parent, type, name, properties, { ...options, publish });
    if (!keep) this.track(result.ref, parent);
    if (!result.url) throw new Error(`${type} ${result.ref} has no URL: is ${type} a page type with a template?`);
    return { ...result, url: browseUrl(result.url, result.canonicalUrl, this.addresses)! };
  }

  /**
   * Saves a draft of the change unless `publish` (or puts it in a project: `inProject`). On seeded content, the next
   * run's seed puts it back.
   */
  set(ref: ContentRef, properties: Properties, options: SetCallOptions = {}): Promise<SavedContent> {
    return this.opticli.set(ref, properties, options);
  }

  /** Publishes the latest version, or the version `ref` names (`123_456`). */
  publish(ref: ContentRef, options: PublishOptions = {}): Promise<WriteOutput> {
    return this.opticli.publish(ref, options);
  }

  /**
   * Runs a plan; what it creates is removed after the test unless `keep`: its content, and a project it made (the plan's
   * `project` named one that didn't exist). Content that was there before (a step that found it with `updateExisting`)
   * and projects that were there before stay.
   */
  async apply<const P extends Plan>(plan: P, { keep, ...options }: Omit<ApplyCallOptions, 'sealFor'> & KeepOption = {}): Promise<AppliedPlan<P>> {
    if (keep) {
      const parents = plan.operations.map((step) => (isCreate(step) ? (step.parent ?? step.for) : undefined));
      this.checkKeep('cms.apply()', parents.filter((p): p is ContentRef => p !== undefined));
    }
    const result = (await this.opticli.apply(plan, options)) as AppliedPlan<P>;
    if (keep) return result;
    const created = result.created as Record<string, ContentRef>;
    for (const step of result.operations) {
      const planned = plan.operations[step.index];
      const existing = (step.op === 'create' || step.op === 'block' || step.op === 'upload') && step.result?.existing;
      if (step.id && created[step.id] && isCreate(planned) && !existing) {
        this.track(created[step.id], planned.parent ?? planned.for);
      }
    }
    for (const project of result.projects ?? []) {
      if (project.status === 'created' && project.id !== undefined) this.projects.push(project.id);
    }
    return result;
  }

  /**
   * Creates a project for the test's drafts (`cms.page(..., { inProject: name })`, a plan's `project`), deleted after
   * the test unless `keep`. Deleting a project leaves its items' versions as they are.
   */
  async project(name: string, { keep }: KeepOption = {}): Promise<ProjectResult> {
    const result = await this.opticli.projectsCreate(name);
    if (!keep) this.projects.push(result.project.id);
    return result;
  }

  /** Deletes what the test made, newest first, then the projects it made. Content below other tracked content goes with it. */
  async cleanup(): Promise<void> {
    const errors: unknown[] = [];
    const gone = (e: unknown) => e instanceof OptiCliError && e.code === 'not_found';
    for (const ref of [...this.created].reverse()) {
      try {
        await this.opticli.delete(ref, { ignoreReferences: true });
      } catch (e) {
        if (!gone(e)) errors.push(e);
      }
    }
    for (const id of [...this.projects].reverse()) {
      try {
        await this.opticli.projectsDelete(String(id));
      } catch (e) {
        if (!gone(e)) errors.push(e);
      }
    }
    this.created.length = 0;
    this.projects.length = 0;
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
