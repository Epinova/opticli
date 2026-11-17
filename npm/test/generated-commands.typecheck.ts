// Type-checked by `npm run typecheck`, never run: calls to the generated commands (src/generated) as code using the
// package would make them, sync and async, with the result type each must have. A line after `// @ts-expect-error` must
// not compile; if it does, the check fails.
import type { Properties } from '../src/types.ts';
import { OptiCliCommands, OptiCliCommandsSync, type CommandRunner, type CommandRunnerSync } from '../src/generated/commands.ts';
import type {
  AccessOutput,
  AccessView,
  AgentStatus,
  ContentDocument,
  ContentIdentity,
  DraftItem,
  LanguageInfo,
  LanguagesOutput,
  PageUsageRow,
  PlanRun,
  ProjectDeletePreview,
  ProjectInfo,
  ProjectItemInfo,
  ProjectResult,
  RemoveLanguageOutput,
  SealResult,
  ServeLogs,
  ServeStopOutput,
  SqlResult,
  SqlValue,
  TimedItem,
  TypeUsageRow,
  Usage,
  VersionStatus,
  WriteOutput,
} from '../src/generated/types.ts';

/** True when A and B are the same type (not only assignable). */
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
function expect<T extends true>(_: T): void {}

declare const runner: CommandRunner;
declare const runnerSync: CommandRunnerSync;
const cli = new OptiCliCommands(runner);
const sync = new OptiCliCommandsSync(runnerSync);

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
  await cli.get('1', { db: 'staging' });

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

  const usages = await cli.whereUsed({ ref: '123' });
  expect<Equal<typeof usages, Usage[]>>(true);
  const pages = await cli.whereUsed({ ref: '123', pages: true });
  expect<Equal<typeof pages, PageUsageRow[]>>(true);
  const instances = await cli.whereUsed({ type: 'TeaserBlock' });
  expect<Equal<typeof instances, TypeUsageRow[]>>(true);

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

  const projects = await cli.projects();
  expect<Equal<typeof projects, ProjectInfo[]>>(true);
  const items = await cli.projects({ project: 'Spring campaign' });
  expect<Equal<typeof items, ProjectItemInfo[]>>(true);
  const preview = await cli.projectsDelete('12', { dryRun: true });
  expect<Equal<typeof preview, ProjectDeletePreview>>(true);
  const deleted = await cli.projectsDelete('12');
  expect<Equal<typeof deleted, ProjectResult>>(true);
  // An open value set: the CMS's statuses complete, another string still compiles.
  if (projects[0]?.status === 'active' || projects[0]?.status === 'someNewStatus') return;

  // Writes.
  const values: Properties = { Heading: 'Hello', MainArea: [{ ref: '123' }], Link: { href: '/en/' } };
  const saved = await cli.set('123', { values, publish: true });
  expect<Equal<typeof saved, WriteOutput>>(true);
  const known: VersionStatus | (string & {}) | undefined = saved.status;
  if (saved.status === 'published' || saved.status === 'anything the site says') void known;
  await cli.set('123', { properties: ['Heading=Hello'], inProject: 'Spring campaign', ready: true });
  const created = await cli.create('1', { name: 'News', type: 'ArticlePage', values: { Heading: 'Hi' } });
  expect<Equal<typeof created, WriteOutput>>(true);
  // @ts-expect-error: create needs a name
  await cli.create('1', { type: 'ArticlePage' });
  const removed = await cli.translate('123', { lang: 'sv', remove: true, dryRun: true });
  expect<Equal<typeof removed, RemoveLanguageOutput>>(true);
  const translated = await cli.translate('123', { lang: 'sv', values: { Heading: 'Hej' } });
  expect<Equal<typeof translated, WriteOutput>>(true);

  const run = await cli.apply('-', { dryRun: true }, { input: '{"operations": []}' });
  expect<Equal<typeof run, PlanRun>>(true);
  const seal = await cli.apply('plan.json', { sealFor: 'prod' });
  expect<Equal<typeof seal, SealResult>>(true);
  const step = run.operations[0];
  if (step?.status === 'saved' || step?.status === 'notRun') void step;
  // @ts-expect-error: a step's status is one PlanStepStatus
  if (step?.status === 'done') return;
  // @ts-expect-error: --seal isn't generated (only the user runs a sealed plan)
  await cli.apply('plan.json', { seal: 'abc' });

  // sql: rows typed by the caller.
  const branches = await cli.sql<{ LanguageID: string; Enabled: boolean }>('SELECT LanguageID, Enabled FROM tblLanguageBranch');
  expect<Equal<typeof branches, SqlResult<{ LanguageID: string; Enabled: boolean }>>>(true);
  branches.rows[0]?.LanguageID.toLowerCase();
  // @ts-expect-error: not a column the caller named
  branches.rows[0]?.Name;
  const any = await cli.sql('SELECT 1 AS One');
  expect<Equal<(typeof any.rows)[number], Record<string, SqlValue>>>(true);
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
  const rights = sync.access('1', { inherit: true });
  expect<Equal<typeof rights, AccessOutput>>(true);
  const rows = sync.sql<{ Code: string }>('SELECT 1');
  expect<Equal<typeof rows.rows, { Code: string }[]>>(true);
  // @ts-expect-error: a sync call isn't a promise
  sync.get('1').then(() => {});
}
