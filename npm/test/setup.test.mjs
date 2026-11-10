import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { requiredOpticli } from '../dist/lib/index.js';
import { defineSetup, readSeedState, stateVariable } from '../dist/lib/playwright/index.js';
import { fake, fakeCommand, ok } from './fake.mjs';

const running = { state: 'running', message: '', url: 'http://127.0.0.1:5199', agent: { version: requiredOpticli } };
const plan = {
  guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61',
  operations: [
    { op: 'create', id: 'root', parent: '/en/', type: 'StandardPage', name: 'Fixtures' },
    { op: 'block', id: 'teaser', type: 'TeaserBlock', name: 'Teaser', for: '$root' },
  ],
};
const applied = ok({
  dryRun: false,
  created: { root: '10', teaser: '11' },
  operations: [
    { index: 0, op: 'create', id: 'root', status: 'saved', result: { ref: '10', url: '/en/fixtures/' } },
    { index: 1, op: 'block', id: 'teaser', status: 'unchanged', result: { ref: '11' } },
  ],
});

afterEach(() => delete process.env[stateVariable]);

test('setup starts the site, seeds the plan, hands the workers its state, and stops the site it started', async () => {
  const { calls, dir } = fake([
    { match: 'serve --status', envelope: ok({ state: 'stopped', message: '' }, { version: requiredOpticli }) },
    { match: 'serve --stop', envelope: ok({ stopped: true }, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok(running, { version: requiredOpticli }) },
    { match: 'apply', envelope: applied },
  ]);

  const teardown = await defineSetup({ plan, planDir: dir, opticli: { command: fakeCommand, project: '/sites/alloy' } })();

  const state = readSeedState();
  assert.equal(state.baseURL, 'http://127.0.0.1:5199');
  assert.deepEqual(state.refs, { root: '10', teaser: '11' });
  assert.deepEqual(state.urls, { root: '/en/fixtures/' });
  assert.deepEqual(state.opticli, { command: fakeCommand, project: '/sites/alloy' });
  const apply = calls().find((c) => c.args[0] === 'apply');
  assert.deepEqual(apply.args.slice(0, 4), ['apply', '-', '--update-existing', '--publish']);
  assert.equal(apply.cwd, dir);

  await teardown();
  assert.deepEqual(calls().at(-1).args.slice(0, 2), ['serve', '--stop']);
});

test('a site that ran before setup keeps running', async () => {
  const { calls } = fake([
    { match: 'serve --status', envelope: ok(running, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok(running, { version: requiredOpticli }) },
  ]);

  const teardown = await defineSetup({ opticli: { command: fakeCommand } })();
  await teardown();

  assert.ok(!calls().some((c) => c.args.includes('--stop')));
});

test('an opticli older than the package stops setup with how to upgrade', async () => {
  fake([
    { match: 'serve --status', envelope: ok(running, { version: '0.1.0' }) },
    { match: 'serve', envelope: ok(running, { version: '0.1.0' }) },
  ]);

  await assert.rejects(defineSetup({ opticli: { command: fakeCommand } })(), /needs opticli .* 0\.1\.0 runs\. Upgrade it: dotnet tool update -g OptiCli/);
});

test('a site that runs an older agent stops setup with how to restart it', async () => {
  const old = { ...running, agent: { version: '0.1.0' } };
  fake([
    { match: 'serve --status', envelope: ok(old, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok(old, { version: requiredOpticli }) },
  ]);

  await assert.rejects(defineSetup({ opticli: { command: fakeCommand } })(), /site agent of opticli 0\.1\.0.*opticli serve --stop/);
});

test('a seed plan that fails stops the site setup started', async () => {
  const { calls, dir } = fake([
    { match: 'serve --status', envelope: ok({ state: 'stopped', message: '' }, { version: requiredOpticli }) },
    { match: 'serve --stop', envelope: ok({ stopped: true }, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok(running, { version: requiredOpticli }) },
    { match: 'apply', exit: 5, envelope: { ok: false, error: { code: 'validation', message: 'MainBody is required.' } } },
  ]);

  await assert.rejects(defineSetup({ plan, planDir: dir, opticli: { command: fakeCommand } })(), /validation: MainBody is required/);

  assert.deepEqual(calls().at(-1).args.slice(0, 2), ['serve', '--stop']);
});

test('a failed setup leaves the site running with keepSite', async () => {
  const { calls } = fake([
    { match: 'serve --status', envelope: ok({ state: 'stopped', message: '' }, { version: '0.1.0' }) },
    { match: 'serve --stop', envelope: ok({ stopped: true }, { version: '0.1.0' }) },
    { match: 'serve', envelope: ok(running, { version: '0.1.0' }) },
  ]);

  await assert.rejects(defineSetup({ keepSite: true, opticli: { command: fakeCommand } })(), /needs opticli/);

  assert.ok(!calls().some((c) => c.args.includes('--stop')));
});

test('workers without setup say what to configure', () => {
  assert.throws(() => readSeedState(), /globalSetup/);
});

test('https: true serves the site with HTTPS and browses it there', async () => {
  const { calls } = fake([
    { match: 'serve --status', envelope: ok({ state: 'stopped', message: '' }, { version: requiredOpticli }) },
    { match: 'serve --stop', envelope: ok({ stopped: true }, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok({ ...running, mode: 'background', browseUrl: 'https://localhost:5200' }, { version: requiredOpticli }) },
  ]);

  const teardown = await defineSetup({ https: true, opticli: { command: fakeCommand } })();

  assert.equal(readSeedState().baseURL, 'https://localhost:5200');
  assert.ok(calls().find((c) => c.args[0] === 'serve' && c.args[1] === '--timeout').args.includes('--https'));
  await teardown();
});

test('https: true refuses a site that runs already without an HTTPS address, and leaves it running', async () => {
  const { calls } = fake([
    { match: 'serve --status', envelope: ok({ ...running, mode: 'background' }, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok({ ...running, mode: 'background' }, { version: requiredOpticli }) },
  ]);

  await assert.rejects(defineSetup({ https: true, opticli: { command: fakeCommand } })(), /no HTTPS address/);
  assert.ok(!calls().some((c) => c.args.includes('--stop')));
  assert.equal(process.env[stateVariable], undefined);
});

test('the log names the steps the seed plan changed', async (t) => {
  fake([
    { match: 'serve --status', envelope: ok(running, { version: requiredOpticli }) },
    { match: 'serve', envelope: ok(running, { version: requiredOpticli }) },
    {
      match: 'apply',
      envelope: ok({
        dryRun: false,
        created: { root: '10', teaser: '11' },
        operations: [
          { index: 0, op: 'create', id: 'root', status: 'saved', result: { ref: '10', changes: [{ property: 'MainBody' }] } },
          { index: 1, op: 'block', id: 'teaser', status: 'unchanged', result: { ref: '11' } },
          { index: 2, op: 'area', status: 'saved', result: { ref: '10', changes: [{ property: 'MainContentArea' }] } },
        ],
      }),
    },
  ]);
  const log = t.mock.method(console, 'log', () => {});

  await defineSetup({ plan, opticli: { command: fakeCommand } })();

  assert.equal(log.mock.calls.at(-1).arguments[0], 'opticli: seed plan changed 2 of 3 steps: root (create): MainBody; #2 (area): MainContentArea');
});
