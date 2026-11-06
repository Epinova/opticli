// A typed wrapper around the opticli CLI. Every call runs `opticli <command> --json`, parses the JSON envelope it prints
// on stdout, and returns `data`, or throws an OptiCliError for `ok: false`. What opticli checks (the local database, drift,
// property names, plans) stays in the CLI: this module only runs it.
import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import type {
  ApplyResult,
  ContentItem,
  ContentRef,
  DeleteResult,
  Envelope,
  ErrorBody,
  ErrorCode,
  OkEnvelope,
  Plan,
  Properties,
  ServeResult,
  UrlResult,
  WriteResult,
} from './types.ts';

export interface OptiCliOptions {
  /**
   * How to run opticli, as the program and its first arguments. Default: `$OPTICLI` (an executable, or a checkout's
   * `opticli.dll`), else `dotnet tool run opticli` when a local tool manifest (`.config/dotnet-tools.json`) above the
   * working directory has opticli, else `opticli` on PATH.
   */
  command?: readonly string[];
  /** The site's project (`--project`): its `.csproj` or folder. Default: `$OPTICLI_PROJECT`, else opticli's discovery from the working directory. */
  project?: string;
  /** Working directory for every call (except `apply`'s `baseDir`). Default: the process's. */
  cwd?: string;
  /** Called with `meta.warnings` of every successful command. */
  onWarnings?: (warnings: string[], args: readonly string[]) => void;
}

export interface RunOptions {
  /** Text for stdin (`apply -`). */
  input?: string;
  /** Working directory for this call. */
  cwd?: string;
  timeoutMs?: number;
}

export interface WriteOptions {
  publish?: boolean;
  dryRun?: boolean;
  lang?: string;
}

/** opticli answered `ok: false`, or didn't answer with JSON (`code` `internal`). */
export class OptiCliError extends Error {
  readonly code: ErrorCode;
  readonly hint?: string;
  readonly details?: Record<string, unknown>;
  readonly exitCode: number | null;
  /** The opticli version that failed, when it says (0.27.0 and newer). */
  readonly version?: string;
  readonly args: readonly string[];

  constructor(error: ErrorBody, exitCode: number | null, args: readonly string[], version?: string) {
    super(`opticli ${args[0] ?? ''}: ${error.code}: ${error.message}${error.hint ? `\nHint: ${error.hint}` : ''}`);
    this.name = 'OptiCliError';
    this.code = error.code;
    this.hint = error.hint;
    this.details = error.details;
    this.exitCode = exitCode;
    this.version = version;
    this.args = args;
  }
}

export class OptiCli {
  readonly command: readonly string[];
  readonly project?: string;
  readonly cwd?: string;
  private readonly onWarnings?: OptiCliOptions['onWarnings'];

  constructor(options: OptiCliOptions = {}) {
    this.cwd = options.cwd;
    this.command = options.command ?? resolveCommand(options.cwd ?? process.cwd());
    this.project = options.project ?? (process.env.OPTICLI_PROJECT || undefined);
    this.onWarnings = options.onWarnings;
  }

  /** Runs any opticli command with `--json` and returns its envelope; throws OptiCliError when `ok` is false. */
  async run<T>(args: readonly string[], options: RunOptions = {}): Promise<OkEnvelope<T>> {
    const full = [...args, '--json', ...(this.project ? ['--project', this.project] : [])];
    const { stdout, stderr, exitCode } = await this.exec(full, options);
    let envelope: Envelope<T>;
    try {
      envelope = JSON.parse(stdout) as Envelope<T>;
    } catch {
      const output = (stderr || stdout).trim().slice(0, 2000);
      throw new OptiCliError({ code: 'internal', message: `no JSON on stdout (exit ${exitCode}): ${output}` }, exitCode, args);
    }
    if (!envelope.ok) throw new OptiCliError(envelope.error, exitCode, args, envelope.meta?.version);
    if (envelope.meta?.warnings?.length) this.onWarnings?.(envelope.meta.warnings, args);
    return envelope;
  }

  private async data<T>(args: readonly string[], options?: RunOptions): Promise<T> {
    return (await this.run<T>(args, options)).data;
  }

  // serve

  /** Starts the site with the site agent in the background (`serve`), or returns the one that runs. */
  serve(options: { build?: boolean; port?: number; https?: boolean; timeoutSeconds?: number } = {}): Promise<OkEnvelope<ServeResult>> {
    const timeout = options.timeoutSeconds ?? 180;
    const args = ['serve', '--timeout', String(timeout)];
    if (options.build) args.push('--build');
    if (options.port) args.push('--port', String(options.port));
    if (options.https) args.push('--https');
    // A build comes first, and may take minutes.
    return this.run(args, { timeoutMs: (timeout + 600) * 1000 });
  }

  serveStatus(): Promise<ServeResult> {
    return this.data(['serve', '--status']);
  }

  serveStop(): Promise<ServeResult> {
    return this.data(['serve', '--stop']);
  }

  // reads

  get(ref: ContentRef, options: { lang?: string; version?: 'published' | 'latest' | (string & {}); full?: boolean } = {}): Promise<ContentItem> {
    const args = ['get', ref];
    if (options.lang) args.push('--lang', options.lang);
    if (options.version) args.push('--version', options.version);
    if (options.full) args.push('--full');
    return this.data(args);
  }

  /** Every language branch's path and absolute URL. Write results have `url` already. */
  url(ref: ContentRef): Promise<UrlResult> {
    return this.data(['url', ref]);
  }

  // writes

  create(parent: ContentRef, type: string, name: string, properties: Properties = {}, options: WriteOptions = {}): Promise<WriteResult> {
    return this.data(['create', parent, '--type', type, '--name', name, ...values(properties), ...writeFlags(options)]);
  }

  /** Saves a draft of the change, unless `publish`. */
  set(ref: ContentRef, properties: Properties, options: WriteOptions & { name?: string; from?: string } = {}): Promise<WriteResult> {
    const args = ['set', ref, ...values(properties), ...writeFlags(options)];
    if (options.name) args.push('--name', options.name);
    if (options.from) args.push('--from', options.from);
    return this.data(args);
  }

  /** Publishes the latest version, or the version `ref` names (`123_456`). */
  publish(ref: ContentRef, options: { includeDraft?: boolean; dryRun?: boolean } = {}): Promise<WriteResult> {
    const args = ['publish', ref];
    if (options.includeDraft) args.push('--include-draft');
    if (options.dryRun) args.push('--dry-run');
    return this.data(args);
  }

  /** Moves content, with everything below it, to the recycle bin. */
  delete(ref: ContentRef, options: { ignoreReferences?: boolean; dryRun?: boolean } = {}): Promise<DeleteResult> {
    const args = ['delete', ref];
    if (options.ignoreReferences) args.push('--ignore-references');
    if (options.dryRun) args.push('--dry-run');
    return this.data(args);
  }

  /**
   * Runs a plan, sent on stdin. Its files (`upload`, `"@file"` values) are relative to `baseDir` and must be inside it.
   * With the plan's `guidNamespace` and `updateExisting`, running it again only changes what differs.
   */
  apply(plan: Plan, options: { baseDir?: string; updateExisting?: boolean; publish?: boolean; dryRun?: boolean } = {}): Promise<ApplyResult> {
    const args = ['apply', '-'];
    if (options.updateExisting) args.push('--update-existing');
    if (options.publish) args.push('--publish');
    if (options.dryRun) args.push('--dry-run');
    return this.data(args, { input: JSON.stringify(plan), cwd: options.baseDir, timeoutMs: 600_000 });
  }

  private exec(args: readonly string[], options: RunOptions): Promise<{ stdout: string; stderr: string; exitCode: number | null }> {
    const [program, ...prefix] = this.command;
    return new Promise((resolvePromise, reject) => {
      const child = spawn(program, [...prefix, ...args], {
        cwd: options.cwd ?? this.cwd,
        stdio: ['pipe', 'pipe', 'pipe'],
        timeout: options.timeoutMs ?? 120_000,
      });
      let stdout = '';
      let stderr = '';
      child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
      child.stderr.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));
      child.on('error', (error: NodeJS.ErrnoException) =>
        reject(
          error.code === 'ENOENT'
            ? new Error(`Couldn't run ${this.command.join(' ')}: install opticli (dotnet tool install -g OptiCli), or set OPTICLI to it.`, { cause: error })
            : error,
        ),
      );
      child.on('close', (exitCode) => resolvePromise({ stdout, stderr, exitCode }));
      child.stdin.on('error', () => {}); // a CLI that exits before reading stdin
      child.stdin.end(options.input ?? '');
    });
  }
}

/** The command that runs opticli from `cwd`: see {@link OptiCliOptions.command}. */
export function resolveCommand(cwd: string, env: NodeJS.ProcessEnv = process.env): string[] {
  const bin = env.OPTICLI;
  if (bin) return bin.endsWith('.dll') ? ['dotnet', bin] : [bin];
  return findToolManifest(cwd) ? ['dotnet', 'tool', 'run', 'opticli'] : ['opticli'];
}

/** The nearest local tool manifest above `cwd` that has opticli, as `dotnet tool run` finds it. */
export function findToolManifest(cwd: string): string | undefined {
  for (let dir = resolve(cwd); ; dir = dirname(dir)) {
    for (const file of [join(dir, '.config', 'dotnet-tools.json'), join(dir, 'dotnet-tools.json')]) {
      if (!existsSync(file)) continue;
      try {
        const tools = (JSON.parse(readFileSync(file, 'utf8')) as { tools?: Record<string, { commands?: string[] }> }).tools ?? {};
        if (Object.values(tools).some((tool) => tool.commands?.includes('opticli'))) return file;
      } catch {
        // Not JSON: dotnet would fail on it too. Keep looking above.
      }
    }
    if (dirname(dir) === dir) return undefined;
  }
}

/** Compares `major.minor.patch` versions, ignoring a prerelease suffix: negative, zero or positive. */
export function compareVersions(a: string, b: string): number {
  const parts = (v: string) => v.split(/[-+]/)[0].split('.').map((n) => Number.parseInt(n, 10) || 0);
  const [x, y] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) {
    if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) - (y[i] ?? 0);
  }
  return 0;
}

function values(properties: Properties): string[] {
  return Object.keys(properties).length ? ['--values', JSON.stringify(properties)] : [];
}

function writeFlags(options: WriteOptions): string[] {
  const flags: string[] = [];
  if (options.publish) flags.push('--publish');
  if (options.dryRun) flags.push('--dry-run');
  if (options.lang) flags.push('--lang', options.lang);
  return flags;
}
