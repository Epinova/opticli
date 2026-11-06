// Playwright's globalSetup: start the site, check opticli's version, apply the seed plan, and hand what the workers need
// to them through the environment. It returns the teardown, which Playwright runs after the tests.
import { compareVersions, OptiCli, type OptiCliOptions } from '../opticli.ts';
import type { ContentRef, Plan } from '../types.ts';
import { requiredOpticli } from '../version.ts';

export interface SetupOptions {
  /**
   * Content the tests read, applied with `--update-existing` on every run: the first run creates it, later ones only
   * repair what changed. Give it a `guidNamespace`, and an `id` on every create, block and upload.
   */
  plan?: Plan;
  /** Where the plan's files (`upload`, `"@file"` values) are; they must be inside it. Default: the working directory. */
  planDir?: string;
  /** Publish what the plan saves. Default: true. */
  publish?: boolean;
  /** Build the site before starting it (`serve --build`). Default: `OPTICLI_BUILD=1`. */
  build?: boolean;
  /** Leave a site that setup started running after the tests. Default: `OPTICLI_KEEP_SITE=1`. */
  keepSite?: boolean;
  /** How to run opticli, and which project. */
  opticli?: Pick<OptiCliOptions, 'command' | 'project' | 'cwd'>;
}

/** What setup hands the workers, in {@link stateVariable}. */
export interface SeedState<Id extends string = string> {
  /** Where the browser reaches the site. */
  baseURL: string;
  /** How setup ran opticli, so the workers run the same. */
  opticli: { command: readonly string[]; project?: string; cwd?: string };
  /** The plan's step `id` → content ref. */
  refs: Record<Id, ContentRef>;
  /** The plan's step `id` → its URL, host-relative, for content that has one (pages, media). */
  urls: Partial<Record<Id, string>>;
}

/** The environment variable that carries the {@link SeedState} from setup to the workers. */
export const stateVariable = 'OPTICLI_PLAYWRIGHT_STATE';

/**
 * Playwright's `globalSetup`:
 *
 * ```ts
 * // global-setup.ts
 * export default defineSetup({ plan: seedPlan, planDir: import.meta.dirname });
 * ```
 *
 * It reuses a site that runs already (`opticli serve`, IDE mode) and leaves it running; a site it starts, it stops
 * after the tests.
 */
export function defineSetup(options: SetupOptions = {}): () => Promise<() => Promise<void>> {
  return async () => {
    const opticli = new OptiCli({
      ...options.opticli,
      onWarnings: (warnings, args) => console.warn(`opticli ${args[0]}: ${warnings.join(' ')}`),
    });

    const before = await opticli.serveStatus();
    const served = await opticli.serve({ build: options.build ?? process.env.OPTICLI_BUILD === '1' });
    checkVersions(served.meta.version, served.data.agent?.version);
    const baseURL = served.data.browseUrl ?? served.data.url;
    if (!baseURL) throw new Error(`opticli serve gave no URL: ${JSON.stringify(served.data)}`);

    const state: SeedState = {
      baseURL,
      opticli: { command: opticli.command, project: opticli.project, cwd: opticli.cwd },
      refs: {},
      urls: {},
    };
    if (options.plan) {
      const applied = await opticli.apply(options.plan, {
        baseDir: options.planDir,
        updateExisting: true,
        publish: options.publish ?? true,
      });
      state.refs = applied.created;
      for (const step of applied.operations) {
        if (step.id && step.result?.url) state.urls[step.id] = step.result.url;
      }
      const changed = applied.operations.filter((o) => o.status !== 'unchanged').length;
      console.log(`opticli: seed plan ${changed ? `changed ${changed} of ${applied.operations.length} steps` : 'unchanged'}`);
    }
    process.env[stateVariable] = JSON.stringify(state);

    const startedSite = before.state !== 'running';
    return async () => {
      if (startedSite && !(options.keepSite ?? process.env.OPTICLI_KEEP_SITE === '1')) await opticli.serveStop();
    };
  };
}

/** The workers' copy of what setup did. */
export function readSeedState<Id extends string = string>(): SeedState<Id> {
  const json = process.env[stateVariable];
  if (!json) {
    throw new Error("No opticli setup ran: set globalSetup in playwright.config to a file that exports defineSetup() from '@epinova/opticli/playwright'.");
  }
  return JSON.parse(json) as SeedState<Id>;
}

function checkVersions(cli: string, agent: string | undefined): void {
  const upgrade = 'dotnet tool update -g OptiCli (or dotnet tool update OptiCli where a tool manifest pins it)';
  if (compareVersions(cli, requiredOpticli) < 0) {
    throw new Error(`This package needs opticli ${requiredOpticli} or newer; ${cli} runs. Upgrade it: ${upgrade}.`);
  }
  if (agent && compareVersions(agent, requiredOpticli) < 0) {
    throw new Error(
      `The site runs the site agent of opticli ${agent}, older than ${requiredOpticli}. ` +
        'Restart it (opticli serve --stop, or restart it in the IDE) so it runs the agent of the installed opticli.',
    );
  }
}
