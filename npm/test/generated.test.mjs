// The generated commands (src/generated/commands.ts): the command line each method makes from its arguments and
// options, run through a stand-in runner that records it.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OptiCliCommands, OptiCliCommandsSync } from '../dist/lib/generated/commands.js';

/** A runner that records each call and answers with the next of `pages` (an envelope's data and meta), else `{}`. */
function recorder(pages = []) {
  const calls = [];
  const answer = (args, options) => {
    calls.push({ args, options });
    return pages.shift() ?? { data: {}, meta: {} };
  };
  return {
    calls,
    async: new OptiCliCommands({ run: async (args, options) => answer(args, options) }),
    sync: new OptiCliCommandsSync({ runSync: (args, options) => answer(args, options) }),
  };
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

  await cli.set('123', { values, dryRun: true });

  assert.deepEqual(calls[0].args, ['set', '123', '--values', JSON.stringify(values), '--dry-run']);
});

test('an optional argument, given in the options, goes with the arguments before every option', async () => {
  const { calls, async: cli } = recorder([{}, {}, { data: [], meta: {} }, { data: [], meta: {} }]);

  await cli.set('123', { publish: true, properties: ['Heading=Hello', 'Intro=Hi'] });
  await cli.area('1', 'MainArea', 'add', { items: ['123'], dryRun: true });
  await cli.projects({ project: 'Spring campaign' });
  await cli.whereUsed({ ref: '123', pages: true });

  assert.deepEqual(calls.map((c) => c.args), [
    ['set', '123', 'Heading=Hello', 'Intro=Hi', '--publish'],
    ['area', '1', 'MainArea', 'add', '123', '--dry-run'],
    ['projects', 'Spring campaign'],
    ['where-used', '123', '--pages'],
  ]);
});

test('a required list argument is spread; a subcommand is its words', async () => {
  const { calls, async: cli } = recorder();

  await cli.languagesEnable(['sv', 'no'], { dryRun: true });
  await cli.blockCreate({ type: 'TeaserBlock', name: 'Teaser', for: '123' });
  await cli.projectsReady('12', { all: true, requestApproval: true, comment: 'Campaign texts' });

  assert.deepEqual(calls.map((c) => c.args), [
    ['languages', 'enable', 'sv', 'no', '--dry-run'],
    ['block', 'create', '--type', 'TeaserBlock', '--name', 'Teaser', '--for', '123'],
    ['projects', 'ready', '12', '--all', '--request-approval', '--comment', 'Campaign texts'],
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

test('an option the command lacks is a TypeError before anything runs', async () => {
  const { calls, async: cli, sync } = recorder();

  await assert.rejects(() => cli.get('1', { nope: true }), { name: 'TypeError', message: "opticli get has no option 'nope'." });
  assert.throws(() => sync.apply('plan.json', { seal: 'abc' }), { name: 'TypeError', message: "opticli apply has no option 'seal'." });
  assert.throws(() => sync.serve({ foreground: true }), TypeError);
  assert.equal(calls.length, 0);
});

test("the call's options reach the runner; serve, apply and jobs run wait longer by default", async () => {
  const { calls, async: cli } = recorder();

  await cli.apply('-', { dryRun: true }, { input: '{"operations": []}', cwd: '/plans' });
  await cli.serve({ timeout: 60 });
  await cli.serve({ status: true }, { timeoutMs: 5000 });
  await cli.jobsRun('Empty trash', { timeout: 30 });
  await cli.get('1');

  assert.deepEqual(calls[0], { args: ['apply', '-', '--dry-run'], options: { input: '{"operations": []}', cwd: '/plans', timeoutMs: 600_000 } });
  assert.equal(calls[1].options.timeoutMs, (60 + 600) * 1000);
  assert.equal(calls[2].options.timeoutMs, 5000);
  assert.equal(calls[3].options.timeoutMs, 90_000);
  assert.equal(calls[4].options, undefined);
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

test('the sync class makes the same command lines', () => {
  const { calls, sync } = recorder();

  sync.access('123', { grant: ['Authenticated=Read'], revoke: ['Everyone'], dryRun: true });
  sync.translate('123', { lang: 'sv', remove: true });

  assert.deepEqual(calls.map((c) => c.args), [
    ['access', '123', '--grant', 'Authenticated=Read', '--revoke', 'Everyone', '--dry-run'],
    ['translate', '123', '--lang', 'sv', '--remove'],
  ]);
});
