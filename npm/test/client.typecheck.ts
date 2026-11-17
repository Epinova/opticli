// Type-checked by `npm run typecheck`, never run: calls to the clients (OptiCli, OptiCliSync) as code using the package
// would make them, with the result type each must have. A line after `// @ts-expect-error` must not compile; if it
// does, the check fails.
import {
  OptiCli,
  OptiCliCommands,
  OptiCliSync,
  type AccessOutput,
  type AccessView,
  type AgentStatus,
  type AppliedPlan,
  type AppliedStep,
  type ContentDocument,
  type ContentIdentity,
  type CreatedContent,
  type DraftItem,
  type JobLogView,
  type LanguageInfo,
  type LanguagesOutput,
  type PageUsageRow,
  type Plan,
  type PlanProjectStatus,
  type ProjectDeletePreview,
  type ProjectInfo,
  type ProjectItemInfo,
  type ProjectResult,
  type Properties,
  type RemoveLanguageOutput,
  type RunOptions,
  type RunResult,
  type SavedContent,
  type SealResult,
  type ServeLogs,
  type ServeStopOutput,
  type SqlResult,
  type SqlValue,
  type TimedItem,
  type TypeUsageRow,
  type Usage,
  type VersionStatus,
  type WriteOutput,
} from '../src/index.ts';

/** True when A and B are the same type (not only assignable). */
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
function expect<T extends true>(_: T): void {}

const cli = new OptiCli({ project: 'src/Alloy' });
const sync = new OptiCliSync({ project: 'src/Alloy' });

export async function asyncCalls(): Promise<void> {
  // serve: a result per mode.
  const started = await cli.serve({ build: true, scheduler: false });
  expect<Equal<typeof started, AgentStatus>>(true);
  const status = await cli.serve({ status: true });
  expect<Equal<typeof status, AgentStatus>>(true);
  const logs = await cli.serve({ logs: true, tail: 40 });
  expect<Equal<typeof logs, ServeLogs>>(true);
  const stopped = await cli.serve({ stop: true });
  expect<Equal<typeof stopped, ServeStopOutput>>(true);
  const flag: boolean = Math.random() > 0.5;
  const either = await cli.serve({ logs: flag });
  expect<Equal<typeof either, AgentStatus | ServeLogs | ServeStopOutput>>(true);
  // A known value set: on or off, nothing else.
  if (status.scheduler === 'on') status.scheduler satisfies 'on';
  // @ts-expect-error: the scheduler is on or off
  if (status.scheduler === 'maybe') return;
  // @ts-expect-error: --foreground isn't generated (it runs until stopped)
  await cli.serve({ foreground: true });

  // Reads.
  const page = await cli.get('/en/about/', { lang: 'sv', fields: 'Heading,MainArea', version: 'latest' });
  expect<Equal<typeof page, ContentDocument>>(true);
  const heading = page.properties.Heading?.value;
  if (typeof heading === 'string') heading.toUpperCase();
  // @ts-expect-error: no such option
  await cli.get('1', { nope: true });
  // @ts-expect-error: the client sets the project itself
  await cli.get('1', { project: 'Alloy.csproj' });
  // @ts-expect-error: the output format isn't an option
  await cli.get('1', { text: true });
  // @ts-expect-error: the ref is required
  await cli.get({ lang: 'sv' });
  await cli.get('1', { db: 'staging' }, { timeoutMs: 5000 });

  const rows = await cli.find({ type: 'ArticlePage', where: ['Heading~news'] });
  expect<Equal<typeof rows, ContentIdentity[]>>(true);
  const drafts = await cli.find({ type: 'ArticlePage', status: 'draft' });
  expect<Equal<typeof drafts, DraftItem[]>>(true);
  const scheduled = await cli.find({ type: 'ArticlePage', status: 'scheduled' });
  expect<Equal<typeof scheduled, TimedItem[]>>(true);
  // @ts-expect-error: --type is required
  await cli.find({ status: 'draft' });
  // @ts-expect-error: not a status find knows
  await cli.find({ type: 'ArticlePage', status: 'bogus' });
  for await (const row of cli.findAll({ type: 'ArticlePage', status: 'draft' })) {
    expect<Equal<typeof row, DraftItem>>(true);
  }
  // @ts-expect-error: the iterator pages itself
  cli.findAll({ type: 'ArticlePage', cursor: 'abc' });

  // An optional argument stays positional, as on the command line; left out, the options object takes its place.
  const usages = await cli.whereUsed('123');
  expect<Equal<typeof usages, Usage[]>>(true);
  const pages = await cli.whereUsed('123', { pages: true });
  expect<Equal<typeof pages, PageUsageRow[]>>(true);
  const instances = await cli.whereUsed({ type: 'TeaserBlock' });
  expect<Equal<typeof instances, TypeUsageRow[]>>(true);
  const maybe: string | undefined = Math.random() > 0.5 ? '123' : undefined;
  const either2 = await cli.whereUsed(maybe, { pages: true });
  expect<Equal<typeof either2, PageUsageRow[]>>(true);
  for await (const row of cli.whereUsedAll('123', { pages: true })) {
    expect<Equal<typeof row, PageUsageRow>>(true);
  }
  // @ts-expect-error: the ref is an argument now, not an option
  await cli.whereUsed({ ref: '123' });
  const log = await cli.jobsLog('Empty trash', { failed: true });
  expect<Equal<typeof log, JobLogView[]>>(true);
  await cli.jobsLog({ limit: 5 });

  // access reads without a change option, and writes with one.
  const rights = await cli.access('123');
  expect<Equal<typeof rights, AccessView>>(true);
  const changed = await cli.access('123', { grant: ['Authenticated=Read'], dryRun: true });
  expect<Equal<typeof changed, AccessOutput>>(true);
  // @ts-expect-error: only a change has before and after
  rights.after;

  const languages = await cli.languages({ all: true });
  expect<Equal<typeof languages, LanguageInfo[]>>(true);
  const enabled = await cli.languagesEnable(['sv', 'no'], { dryRun: true });
  expect<Equal<typeof enabled, LanguagesOutput>>(true);
  // @ts-expect-error: the codes are an array
  await cli.languagesEnable('sv');

  // projects: the list, or one project's items, by its argument.
  const projects = await cli.projects();
  expect<Equal<typeof projects, ProjectInfo[]>>(true);
  const items = await cli.projects('Spring campaign');
  expect<Equal<typeof items, ProjectItemInfo[]>>(true);
  const either3 = await cli.projects(maybe);
  expect<Equal<typeof either3, ProjectItemInfo[] | ProjectInfo[]>>(true);
  const listed = await cli.projects({ limit: 10 });
  expect<Equal<typeof listed, ProjectInfo[]>>(true);
  const made = await cli.projectsCreate('Spring campaign');
  expect<Equal<typeof made, ProjectResult>>(true);
  await cli.projectsReady('Spring campaign', ['123', '456'], { requestApproval: true });
  await cli.projectsReady('Spring campaign', { all: true });
  await cli.projectsPublish('Spring campaign', { dryRun: true });
  const preview = await cli.projectsDelete('12', { dryRun: true });
  expect<Equal<typeof preview, ProjectDeletePreview>>(true);
  const deleted = await cli.projectsDelete('12');
  expect<Equal<typeof deleted, ProjectResult>>(true);
  // An open value set: the CMS's statuses complete, another string still compiles.
  if (projects[0]?.status === 'active' || projects[0]?.status === 'someNewStatus') return;

  // Writes: create and set are hand-written, with the properties as an object.
  const values: Properties = { Heading: 'Hello', MainArea: [{ ref: '123' }], Link: { href: '/en/' } };
  const saved = await cli.set('123', values, { publish: true });
  expect<Equal<typeof saved, SavedContent>>(true);
  saved.ref.toUpperCase();
  const known: VersionStatus | (string & {}) | undefined = saved.status;
  if (saved.status === 'published' || saved.status === 'anything the site says') void known;
  await cli.set('123', { Heading: 'Hello' }, { inProject: 'Spring campaign', ready: true, requestApproval: true });
  // @ts-expect-error: the values are the second parameter, not an option
  await cli.set('123', {}, { values });
  const created = await cli.create('1', 'ArticlePage', 'News', { Heading: 'Hi' }, { lang: 'en', inProject: 'Spring campaign' });
  expect<Equal<typeof created, CreatedContent>>(true);
  created.ref.toUpperCase();
  created.version.toUpperCase();
  const planned = await cli.create('1', 'ArticlePage', 'News', undefined, { dryRun: true });
  expect<Equal<typeof planned, WriteOutput>>(true);
  // @ts-expect-error: a dry-run create saves nothing, so it has no ref for sure
  planned.ref.toUpperCase();
  // @ts-expect-error: the type and name are parameters
  await cli.create('1', 'ArticlePage', 'News', {}, { name: 'Other' });
  const removed = await cli.translate('123', { lang: 'sv', remove: true, dryRun: true });
  expect<Equal<typeof removed, RemoveLanguageOutput>>(true);
  const translated = await cli.translate('123', { lang: 'sv', values: { Heading: 'Hej' } });
  expect<Equal<typeof translated, WriteOutput>>(true);
  await cli.translate('123', ['Heading=Hej'], { lang: 'sv' });
  const published = await cli.publish('123_456');
  expect<Equal<typeof published, WriteOutput>>(true);

  // apply: a typed plan, with its ids in `created`, a project, and each step's result typed by its op.
  const plan = {
    guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61',
    project: 'Spring campaign',
    operations: [
      { op: 'create', id: 'news', parent: '1', type: 'ArticlePage', name: 'News', ready: true },
      { op: 'block', id: 'teaser', type: 'TeaserBlock', name: 'Teaser', for: '$news', project: null },
      { op: 'set', ref: '123', properties: { Heading: 'x' }, project: 12 },
    ],
  } as const satisfies Plan;
  const run = await cli.apply(plan, { updateExisting: true, baseDir: 'seed' });
  expect<Equal<typeof run, AppliedPlan<typeof plan>>>(true);
  run.created.news.toUpperCase();
  run.created.teaser.toUpperCase();
  // @ts-expect-error: no step has that id
  run.created.other;
  const project = run.projects?.[0];
  if (project) expect<Equal<typeof project.status, PlanProjectStatus>>(true);
  const step: AppliedStep | undefined = run.operations[0];
  if (step?.op === 'create') step.result?.changes.map((c) => c.property);
  if (step?.op === 'move') step.result?.descendants.toFixed();
  // @ts-expect-error: a move's result has no url
  if (step?.op === 'move') step.result?.url.toUpperCase();
  // Before narrowing, any output's property reads as possibly undefined.
  const url = step?.result?.url;
  expect<Equal<typeof url, string | undefined>>(true);
  const child = run.operations.find((o) => o.id === 'news')!.result!.url!;
  child.toUpperCase();
  if (step?.status === 'saved' || step?.status === 'notRun') void step;
  // @ts-expect-error: a step's status is one PlanStepStatus
  if (step?.status === 'done') return;
  // Written inline, the plan's ids are still known (a const type parameter).
  const inline = await cli.apply({ operations: [{ op: 'create', id: 'page', parent: '1', type: 'StandardPage', name: 'P' }] });
  inline.created.page.toUpperCase();
  const dry = await cli.apply(plan, { dryRun: true });
  expect<Equal<typeof dry, AppliedPlan>>(true);
  const fromFile = await cli.apply('seed/plan.json', { publish: true });
  expect<Equal<typeof fromFile, AppliedPlan>>(true);
  const seal = await cli.apply('plan.json', { sealFor: 'prod' });
  expect<Equal<typeof seal, SealResult>>(true);
  // @ts-expect-error: --seal isn't an option (only the user runs a sealed plan)
  await cli.apply('plan.json', { seal: 'abc' });

  // sql: rows typed by the caller.
  const branches = await cli.sql<{ LanguageID: string; Enabled: boolean }>('SELECT LanguageID, Enabled FROM tblLanguageBranch');
  expect<Equal<typeof branches, SqlResult<{ LanguageID: string; Enabled: boolean }>>>(true);
  branches.rows[0]?.LanguageID.toLowerCase();
  // @ts-expect-error: not a column the caller named
  branches.rows[0]?.Name;
  const any = await cli.sql('SELECT 1 AS One');
  expect<Equal<(typeof any.rows)[number], Record<string, SqlValue>>>(true);

  // run: any command, as the envelope.
  const envelope = await cli.run<{ variables: Record<string, string> }>(['env', '--format', 'json']);
  envelope.meta.version.toUpperCase();
  envelope.data.variables.x;
}

export function syncCalls(): void {
  // A playwright.config: one project per enabled language.
  const languages = sync.languages();
  expect<Equal<typeof languages, LanguageInfo[]>>(true);
  const projects = languages.filter((l) => l.enabled).map((l) => ({ name: l.code }));
  void projects;

  const status = sync.serve({ status: true });
  expect<Equal<typeof status, AgentStatus>>(true);
  const page = sync.get('1');
  expect<Equal<typeof page, ContentDocument>>(true);
  const drafts = sync.find({ type: 'ArticlePage', status: 'draft' });
  expect<Equal<typeof drafts, DraftItem[]>>(true);
  for (const row of sync.findAll({ type: 'ArticlePage' })) {
    expect<Equal<typeof row, ContentIdentity>>(true);
  }
  const items = sync.projects('Spring campaign');
  expect<Equal<typeof items, ProjectItemInfo[]>>(true);
  const instances = sync.whereUsed({ type: 'TeaserBlock' });
  expect<Equal<typeof instances, TypeUsageRow[]>>(true);
  const rights = sync.access('1', { inherit: true });
  expect<Equal<typeof rights, AccessOutput>>(true);
  const rows = sync.sql<{ Code: string }>('SELECT 1');
  expect<Equal<typeof rows.rows, { Code: string }[]>>(true);
  const created = sync.create('1', 'ArticlePage', 'News', { Heading: 'Hi' }, { publish: true });
  expect<Equal<typeof created, CreatedContent>>(true);
  const saved = sync.set('1', { Heading: 'Hi' });
  expect<Equal<typeof saved, SavedContent>>(true);
  const run = sync.apply({ operations: [{ op: 'create', id: 'page', parent: '1', type: 'StandardPage', name: 'P' }] });
  run.created.page.toUpperCase();
  const envelope = sync.runSync<LanguageInfo[]>(['languages']);
  expect<Equal<typeof envelope.data, LanguageInfo[]>>(true);
  // @ts-expect-error: a sync call isn't a promise
  sync.get('1').then(() => {});
}

// A stand-in client: the generated commands over any `run`, as a test double might be written.
export class Recorder extends OptiCliCommands {
  readonly calls: string[][] = [];
  async run<T>(args: readonly string[], _options?: RunOptions): Promise<RunResult<T>> {
    this.calls.push([...args]);
    return { data: [] as T };
  }
}
