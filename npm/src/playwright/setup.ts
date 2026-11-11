// Playwright's globalSetup: start the site, check opticli's version, apply the seed plan, and hand what the workers need
// to them through the environment. It returns the teardown, which Playwright runs after the tests.
import { compareVersions, OptiCli, type OptiCliOptions } from '../opticli.ts';
import type { ApplyResult, ContentRef, OkEnvelope, Plan, ServeResult } from '../types.ts';
import { requiredOpticli } from '../version.ts';
import { browseUrl, siteUrl } from './urls.ts';

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
  /**
   * Also serve the site on `https://localhost:<port>` with the ASP.NET Core development certificate (`serve --https`),
   * and browse it there: for a site that redirects HTTP to HTTPS. Default: `OPTICLI_HTTPS=1`, else `"https"` in
   * opticli's user config. A site that runs already is used as it is: one without an HTTPS address fails setup.
   */
  https?: boolean;
  /**
   * The port of that HTTPS address (`serve --https-port`, implies `https`), so site hosts with the port stay right from run
   * to run. Default: `OPTICLI_HTTPS_PORT`, else `"httpsPort"` in opticli's user config, else the next free port.
   */
  httpsPort?: number;
  /** Build the site before starting it (`serve --build`). Default: `OPTICLI_BUILD=1`. */
  build?: boolean;
  /** Leave a site that setup started running after the tests, or after setup failed. Default: `OPTICLI_KEEP_SITE=1`. */
  keepSite?: boolean;
  /** How to run opticli, and which project. */
  opticli?: Pick<OptiCliOptions, 'command' | 'project' | 'cwd' | 'profile'>;
}

/** What setup hands the workers, in {@link stateVariable}. */
export interface SeedState<Id extends string = string> {
  /** Where the browser reaches the site. */
  baseURL: string;
  /** How setup ran opticli, so the workers run the same. */
  opticli: { command: readonly string[]; project?: string; cwd?: string; profile?: string };
  /** The plan's step `id` → content ref. */
  refs: Record<Id, ContentRef>;
  /**
   * The plan's step `id` → its URL, for content that has one (pages, media): a path on {@link baseURL}, or, for content
   * on another site that the served site answers on a host of its own (`alloy-sv.localhost:5199`), that site's address.
   */
  urls: Partial<Record<Id, string>>;
  /**
   * Each site's name (`opticli sites`) → where to open it: {@link baseURL} for the site on the served address itself, its
   * own address for a site on another `*.localhost` host on a port `opticli serve` listens on. Sites whose hosts don't
   * answer here (production names, other ports) are left out.
   */
  sites: Record<string, string>;
  /** The addresses the served site listens on (HTTP, and HTTPS with `https`), to tell which hosts it answers. */
  addresses: string[];
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
    const startedSite = before.state !== 'running';
    const keepSite = options.keepSite ?? process.env.OPTICLI_KEEP_SITE === '1';
    const teardown = async () => {
      if (startedSite && !keepSite) await opticli.serveStop();
    };

    const httpsPort = options.httpsPort ?? (process.env.OPTICLI_HTTPS_PORT ? Number(process.env.OPTICLI_HTTPS_PORT) : undefined);
    if (httpsPort !== undefined && !(Number.isInteger(httpsPort) && httpsPort > 0 && httpsPort < 65536)) {
      throw new Error(`opticli: httpsPort (OPTICLI_HTTPS_PORT) must be a port number, not ${process.env.OPTICLI_HTTPS_PORT ?? httpsPort}.`);
    }
    const https = httpsPort !== undefined || (options.https ?? (process.env.OPTICLI_HTTPS ? process.env.OPTICLI_HTTPS === '1' : undefined));
    const served = await opticli.serve({ build: options.build ?? process.env.OPTICLI_BUILD === '1', https, httpsPort });
    try {
      if (https && !startedSite && served.data.mode !== 'ide' && !served.data.browseUrl) {
        throw new Error(
          `opticli: the site that runs already (${served.data.url}) has no HTTPS address, and setup doesn't stop a site it didn't start. ` +
            'Stop it (opticli serve --stop) and run the tests again, or start it with opticli serve --https.',
        );
      }
      process.env[stateVariable] = JSON.stringify(await seed(opticli, served, options));
    } catch (error) {
      // Playwright runs no teardown when setup fails: stop the site setup started here.
      await teardown().catch((stopError) => console.warn(`opticli: couldn't stop the site: ${stopError}`));
      throw error;
    }
    return teardown;
  };
}

async function seed(opticli: OptiCli, served: OkEnvelope<ServeResult>, options: SetupOptions): Promise<SeedState> {
  checkVersions(served.meta.version, served.data.agent?.version);
  const baseURL = served.data.browseUrl ?? served.data.url;
  if (!baseURL) throw new Error(`opticli serve gave no URL: ${JSON.stringify(served.data)}`);

  const state: SeedState = {
    baseURL,
    opticli: { command: opticli.command, project: opticli.project, cwd: opticli.cwd, profile: opticli.profile },
    refs: {},
    urls: {},
    sites: {},
    addresses: [served.data.url, served.data.browseUrl].filter((a): a is string => !!a),
  };
  if (options.plan) {
    const applied = await opticli.apply(options.plan, {
      baseDir: options.planDir,
      updateExisting: true,
      publish: options.publish ?? true,
    });
    state.refs = applied.created;
    for (const step of applied.operations) {
      const url = browseUrl(step.result?.url, step.result?.canonicalUrl, state.addresses);
      if (step.id && url) state.urls[step.id] = url;
    }
    const changed = applied.operations.filter((o) => o.status !== 'unchanged');
    console.log(
      changed.length
        ? `opticli: seed plan changed ${changed.length} of ${applied.operations.length} steps: ${changed.map(describeStep).join('; ')}`
        : 'opticli: seed plan unchanged',
    );
  }
  state.sites = await sites(opticli, baseURL, state.addresses);
  return state;
}

/** Where to open each site; none when opticli can't list them (an older one says why in a warning). */
async function sites(opticli: OptiCli, baseURL: string, addresses: readonly string[]): Promise<Record<string, string>> {
  try {
    const listed = await opticli.sites();
    const result: Record<string, string> = {};
    for (const site of listed) {
      const url = siteUrl(site.url, baseURL, addresses);
      if (url) result[site.name] = url;
    }
    return result;
  } catch (error) {
    console.warn(`opticli: couldn't list the sites, so seed.sites is empty: ${error instanceof Error ? error.message : error}`);
    return {};
  }
}

/** A step that changed, for the log: what it is, and which properties it changed. */
function describeStep(step: ApplyResult['operations'][number]): string {
  const name = `${step.id ?? `#${step.index}`} (${step.op}${step.status === 'saved' ? '' : `, ${step.status}`})`;
  // A site step's changes are lines of text; a content step's are properties.
  const properties = (step.result?.changes ?? []).map((c) => (typeof c === 'string' ? c : c.property)).filter(Boolean);
  return properties.length ? `${name}: ${properties.join(', ')}` : name;
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
