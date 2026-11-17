// The generated commands (src/generated/commands.ts): the command line each method makes from its arguments and
// options, run through a stand-in client (the abstract classes extended with a `run` that records it).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OptiCliCommands, OptiCliCommandsSync } from '../dist/lib/generated/commands.js';

/** Clients that record each call and answer with the next of `pages` (an envelope's data and meta), else `{}`. */
function recorder(pages = []) {
  const calls = [];
  const answer = (args, options) => {
    calls.push({ args, options });
    return pages.shift() ?? { data: {}, meta: {} };
  };
  class Async extends OptiCliCommands {
    async run(args, options) {
      return answer(args, options);
    }
  }
  class Sync extends OptiCliCommandsSync {
    runSync(args, options) {
      return answer(args, options);
    }
  }
  return { calls, async: new Async(), sync: new Sync() };
}

test('arguments, then options: a value after its flag, a flag for true, nothing for false', async () => {
  const { calls, async: cli } = recorder();

  await cli.get('123', { lang: 'sv', full: true, expand: false, version: 'latest' });

  assert.deepEqual(calls[0].args, ['get', '123', '--lang', 'sv', '--version', 'latest', '--full']);
});

test('the data is what the method returns', async () => {
  const { async: cli, sync } = recorder([{ data: { ref: '123' }, meta: {} }, { data: [{ code: 'en' }], meta: {} }]);

  assert.deepEqual(await cli.get('123'), { ref: '123' });
  assert.deepEqual(sync.languages(), [{ code: 'en' }]);
});

test('a three-state flag passes false explicitly, and nothing when left out', async () => {
  const { calls, async: cli } = recorder();

  await cli.serve({ scheduler: false, https: true });
  await cli.serve({ scheduler: true });
  await cli.serve({});

  assert.deepEqual(calls.map((c) => c.args), [['serve', '--https', '--scheduler', 'false'], ['serve', '--scheduler'], ['serve']]);
});

test('a repeated option repeats its flag; a number is a string', async () => {
  const { calls, async: cli } = recorder([{ data: [], meta: {} }]);

  await cli.find({ type: 'ArticlePage', where: ['Heading~news', 'Name=About'], limit: 5 });

  assert.deepEqual(calls[0].args, ['find', '--type', 'ArticlePage', '--where', 'Heading~news', '--where', 'Name=About', '--limit', '5']);
});

test('--values is the properties object as JSON', async () => {
  const { calls, async: cli } = recorder();
  const values = { Heading: 'Hello', MainArea: [{ ref: '123' }], Link: { href: '/en/' } };

  await cli.translate('123', { lang: 'sv', values, dryRun: true });

  assert.deepEqual(calls[0].args, ['translate', '123', '--lang', 'sv', '--values', JSON.stringify(values), '--dry-run']);
});

test('an optional argument stays positional, before every option', async () => {
  const { calls, async: cli } = recorder([{}, {}, { data: [], meta: {} }, { data: [], meta: {} }, { data: [], meta: {} }]);

  await cli.translate('123', ['Heading=Hej', 'Intro=Hi'], { lang: 'sv' });
  await cli.area('1', 'MainArea', 'add', ['123'], { dryRun: true });
  await cli.projects('Spring campaign');
  await cli.whereUsed('123', { pages: true });
  await cli.jobsLog('Empty trash');

  assert.deepEqual(calls.map((c) => c.args), [
    ['translate', '123', 'Heading=Hej', 'Intro=Hi', '--lang', 'sv'],
    ['area', '1', 'MainArea', 'add', '123', '--dry-run'],
    ['projects', 'Spring campaign'],
    ['where-used', '123', '--pages'],
    ['jobs', 'log', 'Empty trash'],
  ]);
});

test('left out, an optional argument gives its place to the options object', async () => {
  const list = () => ({ data: [], meta: {} });
  const { calls, async: cli, sync } = recorder([list(), list(), list(), list(), {}, list()]);

  await cli.whereUsed({ type: 'TeaserBlock' }, { timeoutMs: 5000 });
  await cli.whereUsed(undefined, { pages: true });
  await cli.projects();
  sync.projects({ limit: 5 });
  sync.translate('123', { lang: 'sv' }, { cwd: '/site' });
  for (const _ of sync.jobsLogAll({ failed: true })) break;

  assert.deepEqual(calls.map((c) => c.args), [
    ['where-used', '--type', 'TeaserBlock'],
    ['where-used', '--pages'],
    ['projects'],
    ['projects', '--limit', '5'],
    ['translate', '123', '--lang', 'sv'],
    ['jobs', 'log', '--failed'],
  ]);
  assert.deepEqual(calls[0].options, { timeoutMs: 5000 });
  assert.deepEqual(calls[4].options, { cwd: '/site' });
});

test('undefined followed by more values is the optional argument left out, as the types say', async () => {
  const list = () => ({ data: [], meta: {} });
  const { calls, async: cli, sync } = recorder([list(), list(), list(), list(), list(), list(), {}, list()]);
  const maybeRef = undefined;

  // A possibly-undefined argument in its place: left out when undefined, given otherwise.
  await cli.whereUsed(maybeRef, { pages: true });
  await cli.whereUsed('123', { pages: true });
  // Run options with nothing else: undefined for the argument and for the options object.
  await cli.whereUsed(undefined, undefined, { timeoutMs: 5000 });
  sync.projects(undefined, undefined, { cwd: '/site' });
  // The options object first, then run options.
  await cli.jobsLog({ failed: true }, { timeoutMs: 7000 });
  for await (const _ of cli.whereUsedAll(maybeRef, { type: 'TeaserBlock' }, { timeoutMs: 3000 })) break;
  sync.projectsReady('12', undefined, { all: true });
  await cli.area('1', 'MainArea', 'add', undefined, { dryRun: true });

  assert.deepEqual(calls.map((c) => c.args), [
    ['where-used', '--pages'],
    ['where-used', '123', '--pages'],
    ['where-used'],
    ['projects'],
    ['jobs', 'log', '--failed'],
    ['where-used', '--type', 'TeaserBlock'],
    ['projects', 'ready', '12', '--all'],
    ['area', '1', 'MainArea', 'add', '--dry-run'],
  ]);
  assert.deepEqual(calls.slice(2, 6).map((c) => c.options), [{ timeoutMs: 5000 }, { cwd: '/site' }, { timeoutMs: 7000 }, { timeoutMs: 3000 }]);

  // undefined then run options: the run options are read as the options object, which the types refuse too.
  await assert.rejects(() => cli.whereUsed(undefined, { timeoutMs: 5000 }), { name: 'TypeError', message: "opticli where-used has no option 'timeoutMs'." });
  assert.throws(() => sync.jobsLog(undefined, { cwd: '/site' }), { name: 'TypeError', message: "opticli jobs log has no option 'cwd'." });
  assert.equal(calls.length, 8);
});

test('arguments that are missing, of the wrong kind or too many are a TypeError before anything runs', async () => {
  const { calls, async: cli, sync } = recorder();

  await assert.rejects(() => cli.whereUsed('1', 'pages'), { name: 'TypeError', message: 'opticli where-used: expected the options object, not "pages".' });
  await assert.rejects(() => cli.projects('a', {}, {}, {}), { name: 'TypeError', message: 'opticli projects: too many arguments.' });
  assert.throws(() => sync.translate({ lang: 'sv' }), { name: 'TypeError', message: 'opticli translate: argument 1 must be a string, not {"lang":"sv"}.' });
  assert.throws(() => sync.projectsReady('12', '123'), TypeError);
  assert.equal(calls.length, 0);
});

test('a required list argument is spread; a subcommand is its words', async () => {
  const { calls, async: cli } = recorder();

  await cli.languagesEnable(['sv', 'no'], { dryRun: true });
  await cli.blockCreate({ type: 'TeaserBlock', name: 'Teaser', for: '123' });
  await cli.projectsReady('12', { all: true, requestApproval: true, comment: 'Campaign texts' });
  await cli.projectsRemove('12', ['123', '456']);

  assert.deepEqual(calls.map((c) => c.args), [
    ['languages', 'enable', 'sv', 'no', '--dry-run'],
    ['block', 'create', '--type', 'TeaserBlock', '--name', 'Teaser', '--for', '123'],
    ['projects', 'ready', '12', '--all', '--request-approval', '--comment', 'Campaign texts'],
    ['projects', 'remove', '12', '123', '456'],
  ]);
});

test('the options every command takes: another database', async () => {
  const { calls, async: cli } = recorder();

  await cli.get('1', { db: 'staging' });
  await cli.sql('SELECT 1', { connectionName: 'Other', limit: 10 });

  assert.deepEqual(calls.map((c) => c.args), [
    ['get', '1', '--db', 'staging'],
    ['sql', 'SELECT 1', '--limit', '10', '--connection-name', 'Other'],
  ]);
});

test('an option the command lacks is a TypeError before anything runs, an inherited name too', async () => {
  const { calls, async: cli, sync } = recorder();

  await assert.rejects(() => cli.get('1', { nope: true }), { name: 'TypeError', message: "opticli get has no option 'nope'." });
  await assert.rejects(() => cli.get('1', { toString: 'x' }), { name: 'TypeError', message: "opticli get has no option 'toString'." });
  assert.throws(() => sync.serve({ foreground: true }), TypeError);
  assert.throws(() => sync.jobsRun('Empty trash', { constructor: true }), TypeError);
  assert.equal(calls.length, 0);
});

test('the hand-written commands have no generated method', () => {
  for (const name of ['create', 'set', 'apply']) {
    assert.equal(name in OptiCliCommands.prototype, false, name);
    assert.equal(name in OptiCliCommandsSync.prototype, false, name);
  }
});

test("the run options reach run; serve's start and jobs run wait longer by default", async () => {
  const { calls, async: cli, sync } = recorder();

  await cli.usersAdd('editor', { passwordStdin: true }, { input: 'secret', cwd: '/site' });
  await cli.serve({ timeout: 60 });
  await cli.serve({ status: true }, { timeoutMs: 5000 });
  await cli.jobsRun('Empty trash', { timeout: 30 });
  await cli.get('1');
  await cli.serve();
  // Status, logs and stop start nothing: the runner's default.
  await cli.serve({ status: true });
  sync.serve({ logs: true, tail: 20 });
  await cli.serve({ stop: true }, { cwd: '/site' });

  assert.deepEqual(calls[0], { args: ['users', 'add', 'editor', '--password-stdin'], options: { input: 'secret', cwd: '/site' } });
  assert.equal(calls[1].options.timeoutMs, (60 + 600) * 1000);
  assert.equal(calls[2].options.timeoutMs, 5000);
  assert.equal(calls[3].options.timeoutMs, 90_000);
  assert.equal(calls[4].options, undefined);
  assert.equal(calls[5].options.timeoutMs, (180 + 600) * 1000);
  assert.deepEqual(calls.slice(6).map((c) => c.options), [{ timeoutMs: undefined }, { timeoutMs: undefined }, { cwd: '/site', timeoutMs: undefined }]);
});

test('a list iterator follows meta.next with --cursor until there is none', async () => {
  const pages = () => [
    { data: [{ ref: '1' }, { ref: '2' }], meta: { next: 'c2' } },
    { data: [{ ref: '3' }], meta: { next: 'c3' } },
    { data: [], meta: {} },
  ];
  const asyncRun = recorder(pages());
  const syncRun = recorder(pages());

  const found = [];
  for await (const row of asyncRun.async.findAll({ type: 'ArticlePage', limit: 2 })) found.push(row.ref);
  const foundSync = [...syncRun.sync.findAll({ type: 'ArticlePage', limit: 2 })].map((row) => row.ref);

  assert.deepEqual(found, ['1', '2', '3']);
  assert.deepEqual(foundSync, found);
  assert.deepEqual(asyncRun.calls.map((c) => c.args), [
    ['find', '--type', 'ArticlePage', '--limit', '2'],
    ['find', '--type', 'ArticlePage', '--limit', '2', '--cursor', 'c2'],
    ['find', '--type', 'ArticlePage', '--limit', '2', '--cursor', 'c3'],
  ]);
  assert.deepEqual(syncRun.calls.map((c) => c.args), asyncRun.calls.map((c) => c.args));
});

test('a list iterator stops with an error when a cursor comes back', async () => {
  const pages = () => [
    { data: [{ ref: '1' }], meta: { next: 'c2' } },
    { data: [{ ref: '2' }], meta: { next: 'c3' } },
    { data: [{ ref: '3' }], meta: { next: 'c2' } },
  ];
  const asyncRun = recorder(pages());
  const syncRun = recorder(pages());

  const found = [];
  await assert.rejects(async () => {
    for await (const row of asyncRun.async.findAll({ type: 'ArticlePage' })) found.push(row.ref);
  }, /gave the cursor 'c2' a second time/);
  assert.throws(() => [...syncRun.sync.findAll({ type: 'ArticlePage' })], /gave the cursor 'c2' a second time/);

  assert.deepEqual(found, ['1', '2', '3']);
  assert.equal(asyncRun.calls.length, 3);
  assert.equal(syncRun.calls.length, 3);
});

test('the sync class makes the same command lines', () => {
  const { calls, sync } = recorder();

  sync.access('123', { grant: ['Authenticated=Read'], revoke: ['Everyone'], dryRun: true });
  sync.translate('123', { lang: 'sv', remove: true });

  assert.deepEqual(calls.map((c) => c.args), [
    ['access', '123', '--grant', 'Authenticated=Read', '--revoke', 'Everyone', '--dry-run'],
    ['translate', '123', '--lang', 'sv', '--remove'],
  ]);
});
