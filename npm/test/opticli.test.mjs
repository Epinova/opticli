import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { compareVersions, findToolManifest, OptiCli, OptiCliError, OptiCliSync, resolveCommand } from '../dist/lib/index.js';
import { fake, fakeCommand, ok } from './fake.mjs';

test('a command runs with --json and the project, and returns its data', async () => {
  const { calls } = fake([{ match: 'get 123', envelope: ok({ ref: '123', name: 'News' }, { source: 'db' }) }]);
  const cli = new OptiCli({ command: fakeCommand, project: '/sites/alloy' });

  const item = await cli.get('123', { lang: 'sv' });

  assert.deepEqual(item, { ref: '123', name: 'News' });
  assert.deepEqual(calls()[0].args, ['get', '123', '--lang', 'sv', '--json', '--project', '/sites/alloy']);
});

test('an error envelope throws OptiCliError with its code, exit code, hint, details and version', async () => {
  fake([
    {
      match: 'delete 5',
      exit: 5,
      envelope: { ok: false, meta: { version: '0.27.0' }, error: { code: 'conflict', message: 'Referenced.', hint: 'Pass --ignore-references.', details: { reason: 'referenced' } } },
    },
  ]);

  const error = await new OptiCli({ command: fakeCommand }).delete('5').catch((e) => e);

  assert.ok(error instanceof OptiCliError);
  assert.equal(error.code, 'conflict');
  assert.equal(error.exitCode, 5);
  assert.equal(error.hint, 'Pass --ignore-references.');
  assert.deepEqual(error.details, { reason: 'referenced' });
  assert.equal(error.version, '0.27.0');
  assert.match(error.message, /delete: conflict: Referenced\.\nHint: Pass --ignore-references\./);
});

test('output that is not JSON is an internal error with what the CLI said', async () => {
  fake([{ match: 'get', exit: 1, raw: '', stderr: 'Unhandled exception: boom' }]);

  const error = await new OptiCli({ command: fakeCommand }).get('1').catch((e) => e);

  assert.ok(error instanceof OptiCliError);
  assert.equal(error.code, 'internal');
  assert.match(error.message, /exit 1.*boom/);
});

test('writes send properties as --values JSON, and their flags', async () => {
  const { calls } = fake([{ match: 'create', envelope: ok({ ref: '9', saved: true, url: '/en/hello/' }) }]);
  const cli = new OptiCli({ command: fakeCommand });

  const result = await cli.create('/en/', 'StandardPage', 'Hello', { MainBody: '<p>Hi</p>', MainContentArea: [{ ref: '7' }] }, { publish: true, lang: 'en' });

  assert.equal(result.url, '/en/hello/');
  const args = calls()[0].args;
  assert.deepEqual(args.slice(0, 6), ['create', '/en/', '--type', 'StandardPage', '--name', 'Hello']);
  assert.deepEqual(JSON.parse(args[args.indexOf('--values') + 1]), { MainBody: '<p>Hi</p>', MainContentArea: [{ ref: '7' }] });
  assert.ok(args.includes('--publish'));
  assert.deepEqual(args.slice(args.indexOf('--lang'), args.indexOf('--lang') + 2), ['--lang', 'en']);
});

test('a write without properties sends no --values', async () => {
  const { calls } = fake([{ match: 'set', envelope: ok({ ref: '9' }) }]);

  await new OptiCli({ command: fakeCommand }).set('9', {}, { name: 'Renamed' });

  assert.deepEqual(calls()[0].args, ['set', '9', '--name', 'Renamed', '--json']);
});

test('apply sends the plan on stdin and runs in its folder', async () => {
  const { calls, dir } = fake([{ match: 'apply', envelope: ok({ dryRun: false, operations: [], created: {} }) }]);
  const plan = { guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61', operations: [{ op: 'upload', id: 'img', file: 'a.png', parent: '3' }] };

  await new OptiCli({ command: fakeCommand }).apply(plan, { baseDir: dir, updateExisting: true, publish: true });

  const call = calls()[0];
  assert.deepEqual(call.args, ['apply', '-', '--update-existing', '--publish', '--json']);
  assert.deepEqual(JSON.parse(call.stdin), plan);
  assert.equal(call.cwd, dir);
});

test('writes into a project: inProject, ready and requestApproval are their flags', async () => {
  const { calls } = fake([
    { match: 'create', envelope: ok({ ref: '9', project: { id: 4, name: 'Spring campaign', item: 'added', ready: true } }) },
    { match: 'set', envelope: ok({ ref: '9' }) },
  ]);
  const cli = new OptiCli({ command: fakeCommand });

  const created = await cli.create('/en/', 'StandardPage', 'Hello', {}, { inProject: 'Spring campaign', ready: true });
  await cli.set('9', { Heading: 'Hi' }, { inProject: '4', requestApproval: true, dryRun: true });

  assert.equal(created.project.item, 'added');
  assert.deepEqual(calls()[0].args, ['create', '/en/', '--type', 'StandardPage', '--name', 'Hello', '--in-project', 'Spring campaign', '--ready', '--json']);
  assert.deepEqual(calls()[1].args, ['set', '9', '--values', '{"Heading":"Hi"}', '--dry-run', '--request-approval', '--in-project', '4', '--json']);
});

test("a hand-written write refuses an option its command lacks, as the generated ones do", async () => {
  const { calls } = fake([]);
  const cli = new OptiCli({ command: fakeCommand });

  await assert.rejects(() => cli.create('/en/', 'StandardPage', 'Hello', {}, { pubish: true }), { name: 'TypeError', message: "opticli create has no option 'pubish'." });
  await assert.rejects(() => cli.apply({ operations: [] }, { seal: 'abc' }), { name: 'TypeError', message: "opticli apply has no option 'seal'." });
  assert.equal(calls().length, 0);
});

test("apply sends a plan's project with it, and runs a plan file by its path", async () => {
  const projects = [{ id: 4, name: 'Spring campaign', status: 'created', undo: 'opticli projects delete 4' }];
  const { calls, dir } = fake([{ match: 'apply', envelope: ok({ dryRun: false, operations: [], created: { news: '9' }, projects }) }]);
  const cli = new OptiCli({ command: fakeCommand });
  const plan = { project: 'Spring campaign', operations: [{ op: 'create', id: 'news', parent: '1', type: 'ArticlePage', name: 'News', ready: true }] };

  const run = await cli.apply(plan);
  await cli.apply('plan.json', { dryRun: true, baseDir: dir }, { timeoutMs: 1000 });

  assert.deepEqual(run.projects, projects);
  assert.equal(run.created.news, '9');
  assert.deepEqual(JSON.parse(calls()[0].stdin), plan);
  assert.deepEqual(calls()[1].args, ['apply', 'plan.json', '--dry-run', '--json']);
  assert.equal(calls()[1].stdin, '');
  assert.equal(calls()[1].cwd, dir);
});

test('sql returns the columns and rows as opticli prints them', async () => {
  const data = { columns: ['LanguageID', 'Enabled'], rows: [{ LanguageID: 'en', Enabled: true }], rowCount: 1, rolledBack: true };
  const { calls } = fake([{ match: 'sql', envelope: ok(data, { source: 'db' }) }]);

  const result = await new OptiCli({ command: fakeCommand }).sql('SELECT LanguageID, Enabled FROM tblLanguageBranch', { limit: 10 });

  assert.deepEqual(result, data);
  assert.deepEqual(calls()[0].args, ['sql', 'SELECT LanguageID, Enabled FROM tblLanguageBranch', '--limit', '10', '--json']);
});

test('a value starting with @ reaches opticli as it is', async () => {
  const { calls } = fake([{ match: 'search', envelope: ok([]) }]);

  await new OptiCli({ command: fakeCommand }).search('@alloy');

  assert.deepEqual(calls()[0].args, ['search', '@alloy', '--json']);
});

test('the sync client runs the same command lines, and runSync returns the envelope', () => {
  const { calls } = fake([
    { match: 'languages', envelope: ok([{ code: 'en', enabled: true }], { source: 'db', next: 'c2' }) },
    { match: 'projects Spring', envelope: ok([{ ref: '9' }]) },
    { match: 'create', envelope: ok({ ref: '9' }) },
    { match: 'apply', envelope: ok({ dryRun: false, operations: [], created: {} }) },
  ]);
  const sync = new OptiCliSync({ command: fakeCommand, project: '/sites/alloy', profile: 'Alloy' });

  const envelope = sync.runSync(['languages']);
  const items = sync.projects('Spring campaign');
  sync.create('1', 'StandardPage', 'Hello', { MainBody: '<p>Hi</p>' }, { publish: true });
  sync.apply({ operations: [] }, { dryRun: true });

  assert.deepEqual(envelope.data, [{ code: 'en', enabled: true }]);
  assert.equal(envelope.meta.next, 'c2');
  assert.deepEqual(items, [{ ref: '9' }]);
  const flags = ['--json', '--project', '/sites/alloy', '--profile', 'Alloy'];
  assert.deepEqual(calls().map((c) => c.args), [
    ['languages', ...flags],
    ['projects', 'Spring campaign', ...flags],
    ['create', '1', '--type', 'StandardPage', '--name', 'Hello', '--values', '{"MainBody":"<p>Hi</p>"}', '--publish', ...flags],
    ['apply', '-', '--dry-run', ...flags],
  ]);
  assert.deepEqual(JSON.parse(calls()[3].stdin), { operations: [] });
});

test('the sync client throws OptiCliError, warns, and stops a command that runs too long', () => {
  fake([
    { match: 'get 5', exit: 2, envelope: { ok: false, meta: { version: '0.34.0' }, error: { code: 'not_found', message: 'No content 5.' } } },
    { match: 'get 1', envelope: ok({ ref: '1' }, { warnings: ['The database is remote.'] }) },
    { match: 'get 2', delayMs: 5_000, envelope: ok({ ref: '2' }) },
  ]);
  const seen = [];
  const sync = new OptiCliSync({ command: fakeCommand, onWarnings: (w) => seen.push(...w) });

  const error = (() => {
    try {
      sync.get('5');
    } catch (e) {
      return e;
    }
  })();
  sync.get('1');
  const late = (() => {
    try {
      sync.runSync(['get', '2'], { timeoutMs: 300 });
    } catch (e) {
      return e;
    }
  })();

  assert.ok(error instanceof OptiCliError);
  assert.equal(error.code, 'not_found');
  assert.equal(error.exitCode, 2);
  assert.equal(error.version, '0.34.0');
  assert.deepEqual(seen, ['The database is remote.']);
  assert.ok(late instanceof OptiCliError);
  assert.equal(late.code, 'timeout');
});

test('a missing opticli says how to install it, in the sync client too', () => {
  assert.throws(() => new OptiCliSync({ command: ['opticli-that-does-not-exist'] }).get('1'), /dotnet tool install -g OptiCli/);
});

test('warnings go to onWarnings', async () => {
  fake([{ match: 'get', envelope: ok({ ref: '1' }, { warnings: ['The database is remote.'] }) }]);
  const seen = [];

  await new OptiCli({ command: fakeCommand, onWarnings: (w, args) => seen.push([w, args[0]]) }).get('1');

  assert.deepEqual(seen, [[['The database is remote.'], 'get']]);
});

test('a missing opticli says how to install it', async () => {
  const error = await new OptiCli({ command: ['opticli-that-does-not-exist'] }).get('1').catch((e) => e);

  assert.match(error.message, /dotnet tool install -g OptiCli/);
});

test('a missing opticli leaves nothing that keeps Node running', () => {
  const script = `import { OptiCli } from ${JSON.stringify(new URL('../dist/lib/index.js', import.meta.url).href)};
    await new OptiCli({ command: ['opticli-that-does-not-exist'] }).get('1').catch(() => {});`;

  const child = spawnSync(process.execPath, ['--input-type=module', '-e', script], { timeout: 10_000 });

  assert.equal(child.error, undefined, 'the process should exit on its own');
  assert.equal(child.status, 0);
});

test('a command that runs too long is stopped, as a timeout', async () => {
  fake([{ match: 'get', delayMs: 5_000, envelope: ok({ ref: '1' }) }]);

  const error = await new OptiCli({ command: fakeCommand }).run(['get', '1'], { timeoutMs: 300 }).catch((e) => e);

  assert.ok(error instanceof OptiCliError);
  assert.equal(error.code, 'timeout');
  assert.match(error.message, /no answer within 0\.3 s/);
  // Only this process was stopped: the message mustn't read as if the site was.
  assert.match(error.message, /this opticli process was stopped\. A site that opticli serve runs keeps running/);
});

test("the client's timeoutMs is every call's, apply's too, unless the call gives its own", async () => {
  fake([
    { match: 'get', delayMs: 5_000, envelope: ok({ ref: '1' }) },
    { match: 'apply', delayMs: 5_000, envelope: ok({ dryRun: true, operations: [], created: {} }) },
  ]);
  const cli = new OptiCli({ command: fakeCommand, timeoutMs: 300 });
  assert.equal(cli.timeoutMs, 300);

  const get = await cli.get('1').catch((e) => e);
  assert.equal(get.code, 'timeout');
  assert.match(get.message, /no answer within 0\.3 s/);

  const apply = await cli.apply({ operations: [] }, { dryRun: true }).catch((e) => e);
  assert.equal(apply.code, 'timeout');
  assert.match(apply.message, /no answer within 0\.3 s/);

  const own = await cli.run(['get', '1'], { timeoutMs: 200 }).catch((e) => e);
  assert.match(own.message, /no answer within 0\.2 s/);

  assert.throws(() => new OptiCliSync({ command: fakeCommand, timeoutMs: 300 }).runSync(['get', '1']), (e) => e.code === 'timeout' && /within 0\.3 s/.test(e.message));
});

test('a relative project is resolved against cwd, so apply in the plan folder finds the same site', async () => {
  const { calls, dir } = fake([{ match: 'apply', envelope: ok({ dryRun: false, operations: [], created: {} }) }]);
  const cli = new OptiCli({ command: fakeCommand, cwd: '/repo/tests', project: '../src/Site' });

  await cli.apply({ operations: [] }, { baseDir: dir });

  assert.equal(cli.project, '/repo/src/Site');
  assert.deepEqual(calls()[0].args.slice(-2), ['--project', '/repo/src/Site']);
});

test('the command: OPTICLI first (a dll runs with dotnet), then a tool manifest, then opticli on PATH', () => {
  const root = mkdtempSync(join(tmpdir(), 'opticli-manifest-'));
  const nested = join(root, 'tests', 'e2e');
  mkdirSync(join(root, '.config'), { recursive: true });
  mkdirSync(nested, { recursive: true });

  assert.deepEqual(resolveCommand(nested, { OPTICLI: '/x/opticli.dll' }), ['dotnet', '/x/opticli.dll']);
  assert.deepEqual(resolveCommand(nested, { OPTICLI: '/x/opticli' }), ['/x/opticli']);
  assert.deepEqual(resolveCommand(nested, {}), ['opticli']);

  writeFileSync(join(root, '.config', 'dotnet-tools.json'), JSON.stringify({ version: 1, tools: { dotnetsay: { commands: ['dotnetsay'] } } }));
  assert.equal(findToolManifest(nested), undefined);

  writeFileSync(join(root, '.config', 'dotnet-tools.json'), JSON.stringify({ version: 1, tools: { opticli: { version: '0.27.0', commands: ['opticli'] } } }));
  assert.equal(findToolManifest(nested), join(root, '.config', 'dotnet-tools.json'));
  assert.deepEqual(resolveCommand(nested, {}), ['dotnet', 'tool', 'run', 'opticli']);
});

test('versions compare by number, without the prerelease suffix', () => {
  assert.ok(compareVersions('0.27.0', '0.26.9') > 0);
  assert.ok(compareVersions('0.10.0', '0.9.0') > 0);
  assert.equal(compareVersions('0.27.0-preview', '0.27.0'), 0);
  assert.ok(compareVersions('0.26.0', '0.27.0') < 0);
});
