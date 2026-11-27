// The clients: OptiCli (async) and OptiCliSync (blocking). Each call runs `opticli <command> --json`, parses the JSON
// envelope it prints on stdout, and returns `data`, or throws an OptiCliError for `ok: false`. What opticli checks (the
// local database, drift, property names, plans) stays in the CLI: this module only runs it.
//
// One method per command is generated (generated/commands.ts, the classes these extend); written here by hand are the
// ones that add something to the command: `create` and `set` take the properties as an object, `apply` a typed plan.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import type { Envelope, OkEnvelope } from './envelope.ts';
import { argv, OptiCliCommands, OptiCliCommandsSync, optionsOf } from './generated/commands.ts';
import type { ApplyOptions, CreateOptions, RunOptions, SetOptions } from './generated/options.ts';
import type { ErrorBody, ErrorCode, SealResult, WriteOutput } from './generated/types.ts';
import type { AppliedPlan, CreatedContent, SavedContent } from './results.ts';
import type { ContentRef, Plan, Properties } from './types.ts';

export interface OptiCliOptions {
  /**
   * How to run opticli, as the program and its first arguments. Default: `$OPTICLI` (an executable, or a checkout's
   * `opticli.dll`), else `dotnet tool run opticli` when a local tool manifest (`.config/dotnet-tools.json`) above the
   * working directory has opticli, else `opticli` on PATH.
   */
  command?: readonly string[];
  /**
   * The site's project (`--project`): its `.csproj` or folder, relative to `cwd`. Default: `$OPTICLI_PROJECT`, else
   * opticli's discovery from the working directory.
   */
  project?: string;
  /** Working directory for every call (except `apply`'s `baseDir`). Default: the process's. */
  cwd?: string;
  /**
   * The launch profile (`--profile`) every call passes: it picks the database, and `serve` runs the site with the
   * profile's environment variables too. Default: `$OPTICLI_PROFILE`, else none.
   */
  profile?: string;
  /** Called with `meta.warnings` of every successful command. */
  onWarnings?: (warnings: string[], args: readonly string[]) => void;
  /**
   * How long every call waits for opticli, in milliseconds, unless the call's own `timeoutMs` (the run options, after
   * the options object) says otherwise. Default: 120 s, and 10 minutes for `apply`.
   */
  timeoutMs?: number;
}

/** `create`'s options: its flags, except the type, name and values, which are parameters. */
export type CreateCallOptions = Omit<CreateOptions, 'type' | 'name' | 'values'>;

/** `set`'s options: its flags, except the values, which are a parameter. */
export type SetCallOptions = Omit<SetOptions, 'values'>;

/** `apply`'s options: its flags, and where the plan's files are. */
export interface ApplyCallOptions extends ApplyOptions {
  /** The folder the plan's files (`upload`, `"@file"` values) are relative to, and must be inside. Default: `cwd`. */
  baseDir?: string;
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

/**
 * opticli, run as a child process per call. Every command is a method (`cli.get('123')`, `cli.languages()`,
 * `cli.whereUsed('123', { pages: true })`), with its arguments as parameters and its flags as an options object;
 * `run` runs any other.
 */
export class OptiCli extends OptiCliCommands {
  readonly command: readonly string[];
  readonly project?: string;
  readonly cwd?: string;
  readonly profile?: string;
  /** The constructor's `timeoutMs`: how long a call waits unless it says otherwise. */
  readonly timeoutMs?: number;
  readonly #onWarnings?: OptiCliOptions['onWarnings'];

  constructor(options: OptiCliOptions = {}) {
    super();
    const s = settings(options);
    this.command = s.command;
    this.project = s.project;
    this.cwd = s.cwd;
    this.profile = s.profile;
    this.timeoutMs = s.timeoutMs;
    this.#onWarnings = s.onWarnings;
  }

  /** Runs any opticli command with `--json` and returns its envelope; throws OptiCliError when `ok` is false. */
  async run<T>(args: readonly string[], options: RunOptions = {}): Promise<OkEnvelope<T>> {
    const { stdout, stderr, exitCode } = await exec(this.command, fullArgs(this, args), options.cwd ?? this.cwd, { ...options, timeoutMs: options.timeoutMs ?? this.timeoutMs });
    return envelope<T>(stdout, stderr, exitCode, args, this.#onWarnings);
  }

  /**
   * Creates content below `parent` (`opticli create`): a draft, unless `publish`. `properties` are its values as an
   * object (sent as `--values`). Writes: needs the site running (`opticli serve`).
   */
  create(parent: ContentRef, type: string, name: string, properties: Properties | undefined, options: CreateCallOptions & { dryRun: true }, call?: RunOptions): Promise<WriteOutput>;
  create(parent: ContentRef, type: string, name: string, properties?: Properties, options?: CreateCallOptions & { dryRun?: false }, call?: RunOptions): Promise<CreatedContent>;
  create(parent: ContentRef, type: string, name: string, properties?: Properties, options?: CreateCallOptions, call?: RunOptions): Promise<WriteOutput>;
  async create(parent: ContentRef, type: string, name: string, properties: Properties = {}, options: CreateCallOptions = {}, call?: RunOptions): Promise<WriteOutput> {
    return (await this.run<WriteOutput>(createArgs(parent, type, name, properties, options), call)).data;
  }

  /**
   * Saves a draft of the change (`opticli set`), unless `publish`. `properties` are the values as an object (sent as
   * `--values`). Writes: needs the site running (`opticli serve`).
   */
  async set(ref: ContentRef, properties: Properties, options: SetCallOptions = {}, call?: RunOptions): Promise<SavedContent> {
    return (await this.run<SavedContent>(setArgs(ref, properties, options), call)).data;
  }

  /**
   * Runs a plan (`opticli apply`): an object, sent on stdin, or a plan file's path. Its files (`upload`, `"@file"`
   * values) are relative to `baseDir` and must be inside it. With the plan's `guidNamespace` and `updateExisting`,
   * running it again only changes what differs. A plan's `project` (or a step's) puts the drafts in a project, which it
   * creates if no unpublished project has the name. Writes: needs the site running (`opticli serve`).
   */
  apply<const P extends Plan>(plan: P | string, options: ApplyCallOptions & { sealFor: string }, call?: RunOptions): Promise<SealResult>;
  apply<const P extends Plan>(plan: P | string, options: ApplyCallOptions & { dryRun: true }, call?: RunOptions): Promise<AppliedPlan>;
  apply<const P extends Plan>(plan: P, options?: ApplyCallOptions & { sealFor?: undefined }, call?: RunOptions): Promise<AppliedPlan<P>>;
  apply(plan: string, options?: ApplyCallOptions & { sealFor?: undefined }, call?: RunOptions): Promise<AppliedPlan>;
  apply(plan: Plan | string, options?: ApplyCallOptions, call?: RunOptions): Promise<AppliedPlan | SealResult>;
  async apply(plan: Plan | string, options: ApplyCallOptions = {}, call?: RunOptions): Promise<AppliedPlan | SealResult> {
    const [args, run] = applyArgs(plan, options, call, this.timeoutMs);
    return (await this.run<AppliedPlan | SealResult>(args, run)).data;
  }
}

/**
 * opticli, blocking: the same methods as OptiCli, each waiting for opticli to exit (a read takes about a second, a
 * write longer) while nothing else in the process runs. For code that can't await, such as building `projects` in a
 * `playwright.config` (`new OptiCliSync().languages()`); everywhere else, use OptiCli.
 */
export class OptiCliSync extends OptiCliCommandsSync {
  readonly command: readonly string[];
  readonly project?: string;
  readonly cwd?: string;
  readonly profile?: string;
  /** The constructor's `timeoutMs`: how long a call waits unless it says otherwise. */
  readonly timeoutMs?: number;
  readonly #onWarnings?: OptiCliOptions['onWarnings'];

  constructor(options: OptiCliOptions = {}) {
    super();
    const s = settings(options);
    this.command = s.command;
    this.project = s.project;
    this.cwd = s.cwd;
    this.profile = s.profile;
    this.timeoutMs = s.timeoutMs;
    this.#onWarnings = s.onWarnings;
  }

  /** Runs any opticli command with `--json` and returns its envelope, blocking; throws OptiCliError when `ok` is false. */
  runSync<T>(args: readonly string[], options: RunOptions = {}): OkEnvelope<T> {
    const { stdout, stderr, exitCode } = execSync(this.command, fullArgs(this, args), options.cwd ?? this.cwd, { ...options, timeoutMs: options.timeoutMs ?? this.timeoutMs });
    return envelope<T>(stdout, stderr, exitCode, args, this.#onWarnings);
  }

  /** As `OptiCli.create`, blocking. */
  create(parent: ContentRef, type: string, name: string, properties: Properties | undefined, options: CreateCallOptions & { dryRun: true }, call?: RunOptions): WriteOutput;
  create(parent: ContentRef, type: string, name: string, properties?: Properties, options?: CreateCallOptions & { dryRun?: false }, call?: RunOptions): CreatedContent;
  create(parent: ContentRef, type: string, name: string, properties?: Properties, options?: CreateCallOptions, call?: RunOptions): WriteOutput;
  create(parent: ContentRef, type: string, name: string, properties: Properties = {}, options: CreateCallOptions = {}, call?: RunOptions): WriteOutput {
    return this.runSync<WriteOutput>(createArgs(parent, type, name, properties, options), call).data;
  }

  /** As `OptiCli.set`, blocking. */
  set(ref: ContentRef, properties: Properties, options: SetCallOptions = {}, call?: RunOptions): SavedContent {
    return this.runSync<SavedContent>(setArgs(ref, properties, options), call).data;
  }

  /** As `OptiCli.apply`, blocking. */
  apply<const P extends Plan>(plan: P | string, options: ApplyCallOptions & { sealFor: string }, call?: RunOptions): SealResult;
  apply<const P extends Plan>(plan: P | string, options: ApplyCallOptions & { dryRun: true }, call?: RunOptions): AppliedPlan;
  apply<const P extends Plan>(plan: P, options?: ApplyCallOptions & { sealFor?: undefined }, call?: RunOptions): AppliedPlan<P>;
  apply(plan: string, options?: ApplyCallOptions & { sealFor?: undefined }, call?: RunOptions): AppliedPlan;
  apply(plan: Plan | string, options?: ApplyCallOptions, call?: RunOptions): AppliedPlan | SealResult;
  apply(plan: Plan | string, options: ApplyCallOptions = {}, call?: RunOptions): AppliedPlan | SealResult {
    const [args, run] = applyArgs(plan, options, call, this.timeoutMs);
    return this.runSync<AppliedPlan | SealResult>(args, run).data;
  }
}

// The command lines of the hand-written methods, the same for both clients.

function createArgs(parent: ContentRef, type: string, name: string, properties: Properties, options: CreateCallOptions): string[] {
  return argv(['create'], [parent], { ...options, type, name, ...values(properties) }, optionsOf('create'));
}

function setArgs(ref: ContentRef, properties: Properties, options: SetCallOptions): string[] {
  return argv(['set'], [ref], { ...options, ...values(properties) }, optionsOf('set'));
}

/** `apply`'s command line and run options; it waits the call's timeoutMs, else the client's, else 10 minutes. */
function applyArgs(plan: Plan | string, { baseDir, ...options }: ApplyCallOptions, call: RunOptions = {}, clientTimeoutMs?: number): [string[], RunOptions] {
  const file = typeof plan === 'string';
  const args = argv(['apply'], [file ? plan : '-'], options, optionsOf('apply'));
  return [args, { ...call, input: file ? call.input : JSON.stringify(plan), cwd: baseDir ?? call.cwd, timeoutMs: call.timeoutMs ?? clientTimeoutMs ?? 600_000 }];
}

/** `--values` for a non-empty properties object. */
function values(properties: Properties): { values?: Properties } {
  return Object.keys(properties).length ? { values: properties } : {};
}

// Running opticli, for both clients.

interface Settings {
  command: readonly string[];
  project?: string;
  cwd?: string;
  profile?: string;
  timeoutMs?: number;
  onWarnings?: OptiCliOptions['onWarnings'];
}

function settings(options: OptiCliOptions): Settings {
  // Absolute: opticli resolves --project against its working directory, and apply runs in the plan's folder.
  const project = options.project ?? (process.env.OPTICLI_PROJECT || undefined);
  return {
    command: options.command ?? resolveCommand(options.cwd ?? process.cwd()),
    project: project ? resolve(options.cwd ?? process.cwd(), project) : undefined,
    cwd: options.cwd,
    profile: options.profile ?? (process.env.OPTICLI_PROFILE || undefined),
    timeoutMs: options.timeoutMs,
    onWarnings: options.onWarnings,
  };
}

/** The command line with what every call passes: `--json`, the project and the profile. */
function fullArgs(settings: Pick<Settings, 'project' | 'profile'>, args: readonly string[]): string[] {
  return [...args, '--json', ...(settings.project ? ['--project', settings.project] : []), ...(settings.profile ? ['--profile', settings.profile] : [])];
}

/** The envelope opticli printed; throws OptiCliError when it is `ok: false` or not JSON. */
function envelope<T>(stdout: string, stderr: string, exitCode: number | null, args: readonly string[], onWarnings: Settings['onWarnings']): OkEnvelope<T> {
  let parsed: Envelope<T>;
  try {
    parsed = JSON.parse(stdout) as Envelope<T>;
  } catch {
    const output = (stderr || stdout).trim().slice(0, 2000);
    throw new OptiCliError({ code: 'internal', message: `no JSON on stdout (exit ${exitCode}): ${output}` }, exitCode, args);
  }
  if (!parsed.ok) throw new OptiCliError(parsed.error, exitCode, args, parsed.meta?.version);
  if (parsed.meta?.warnings?.length) onWarnings?.(parsed.meta.warnings, args);
  return parsed;
}

interface Exited {
  stdout: string;
  stderr: string;
  exitCode: number | null;
}

const defaultTimeoutMs = 120_000;

function timedOut(args: readonly string[], timeoutMs: number): OptiCliError {
  const message = `no answer within ${timeoutMs / 1000} s, so this opticli process was stopped. A site that opticli serve runs keeps running, and a write may have been saved there anyway: read it before you run it again`;
  const hint = "For a command that takes longer, give a longer timeoutMs: the client's (new OptiCli({ timeoutMs })), or the call's (the run options, after the options object).";
  return new OptiCliError({ code: 'timeout', message, hint }, null, args);
}

function notInstalled(command: readonly string[], error: Error): Error {
  return new Error(`Couldn't run ${command.join(' ')}: install opticli (dotnet tool install -g OptiCli), or set OPTICLI to it.`, { cause: error });
}

function exec(command: readonly string[], args: readonly string[], cwd: string | undefined, options: RunOptions): Promise<Exited> {
  const [program, ...prefix] = command;
  const timeoutMs = options.timeoutMs ?? defaultTimeoutMs;
  return new Promise((resolvePromise, reject) => {
    // The timer is ours, not spawn's `timeout`: spawn's outlives a program that never started, and keeps Node running.
    let timer: NodeJS.Timeout | undefined;
    let settled = false;
    const settle = (done: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      done();
    };
    const child = spawn(program, [...prefix, ...args], { cwd, stdio: ['pipe', 'pipe', 'pipe'] });
    timer = setTimeout(() => {
      child.kill();
      settle(() => reject(timedOut(args, timeoutMs)));
    }, timeoutMs);
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
    child.stderr.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));
    child.on('error', (error: NodeJS.ErrnoException) => settle(() => reject(error.code === 'ENOENT' ? notInstalled(command, error) : error)));
    child.on('close', (exitCode) => settle(() => resolvePromise({ stdout, stderr, exitCode })));
    child.stdin.on('error', () => {}); // a CLI that exits before reading stdin
    child.stdin.end(options.input ?? '');
  });
}

function execSync(command: readonly string[], args: readonly string[], cwd: string | undefined, options: RunOptions): Exited {
  const [program, ...prefix] = command;
  const timeoutMs = options.timeoutMs ?? defaultTimeoutMs;
  const result = spawnSync(program, [...prefix, ...args], {
    cwd,
    input: options.input ?? '',
    encoding: 'utf8',
    timeout: timeoutMs,
    killSignal: 'SIGTERM',
    // A large list or plan result is still one envelope.
    maxBuffer: 256 * 1024 * 1024,
  });
  const error = result.error as NodeJS.ErrnoException | undefined;
  if (error?.code === 'ETIMEDOUT') throw timedOut(args, timeoutMs);
  if (error?.code === 'ENOENT') throw notInstalled(command, error);
  if (error) throw error;
  return { stdout: result.stdout, stderr: result.stderr, exitCode: result.status };
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
