import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OptiCli } from '../dist/lib/index.js';
import { Cms } from '../dist/lib/playwright/index.js';
import { fake, fakeCommand, ok } from './fake.mjs';

const sandbox = '100';
const cms = () => new Cms(new OptiCli({ command: fakeCommand }), sandbox);
const deletes = (calls) => calls().filter((c) => c.args[0] === 'delete').map((c) => c.args[1]);

test('a page goes below the sandbox and to the recycle bin after the test, with what was made below it', async () => {
  const { calls } = fake([
    { match: 'create 100', envelope: ok({ ref: '7', url: '/en/sandbox/a/' }) },
    { match: 'create 7', envelope: ok({ ref: '8', url: '/en/sandbox/a/b/' }) },
    { match: 'delete', envelope: ok({ ref: '7' }) },
  ]);
  const c = cms();

  const page = await c.page('StandardPage', 'A');
  await c.page('StandardPage', 'B', {}, { parent: page.ref });
  await c.cleanup();

  assert.deepEqual(deletes(calls), ['7']);
  assert.ok(calls()[0].args.includes('--publish'));
});

test('keep leaves a page on the site', async () => {
  const { calls } = fake([{ match: 'create 5', envelope: ok({ ref: '9', url: '/en/kept/' }) }]);
  const c = cms();

  await c.page('StandardPage', 'Kept', {}, { parent: '5', keep: true });
  await c.cleanup();

  assert.deepEqual(deletes(calls), []);
  assert.ok(!calls()[0].args.includes('keep'));
});

test('keep in the sandbox, or below content the test made, is refused before anything is saved', async () => {
  const { calls } = fake([{ match: 'create 100', envelope: ok({ ref: '7', url: '/en/sandbox/a/' }) }]);
  const c = cms();

  await assert.rejects(c.page('StandardPage', 'Kept', {}, { keep: true }), /outside the worker's sandbox/);
  await assert.rejects(c.apply({ operations: [{ op: 'create', parent: sandbox, type: 'StandardPage', name: 'X' }] }, { keep: true }), /outside the worker's sandbox/);
  assert.equal(calls().length, 0);

  const made = await c.page('StandardPage', 'A');
  await assert.rejects(c.page('StandardPage', 'Kept', {}, { parent: made.ref, keep: true }), /which this test made/);
  assert.equal(calls().length, 1);
});

test('apply removes what it made, and leaves what was there before', async () => {
  const { calls } = fake([
    {
      match: 'apply',
      envelope: ok({
        dryRun: false,
        created: { section: '20', child: '21', shared: '5' },
        operations: [
          { index: 0, op: 'create', id: 'section', status: 'saved', result: { ref: '20' } },
          { index: 1, op: 'create', id: 'child', status: 'saved', result: { ref: '21' } },
          { index: 2, op: 'create', id: 'shared', status: 'unchanged', result: { ref: '5', existing: true } },
        ],
      }),
    },
    { match: 'delete', envelope: ok({ ref: '20' }) },
  ]);
  const c = cms();

  await c.apply(
    {
      guidNamespace: '0d6a8a5e-2f7b-4c1e-9b0a-5c3f1e7d2a61',
      operations: [
        { op: 'create', id: 'section', parent: sandbox, type: 'StandardPage', name: 'Section' },
        { op: 'create', id: 'child', parent: '$section', type: 'StandardPage', name: 'Child' },
        { op: 'create', id: 'shared', parent: '/en/', type: 'StandardPage', name: 'Shared' },
      ],
    },
    { updateExisting: true },
  );
  await c.cleanup();

  assert.deepEqual(deletes(calls), ['20']);
});

test('apply with keep removes nothing', async () => {
  const { calls } = fake([
    {
      match: 'apply',
      envelope: ok({ dryRun: false, created: { a: '30' }, operations: [{ index: 0, op: 'create', id: 'a', status: 'saved', result: { ref: '30' } }] }),
    },
  ]);
  const c = cms();

  await c.apply({ operations: [{ op: 'create', id: 'a', parent: '/en/', type: 'StandardPage', name: 'A' }] }, { keep: true });
  await c.cleanup();

  assert.deepEqual(deletes(calls), []);
});

test('a page on another site the served one answers gets that site address, one on the served address its path', async () => {
  fake([
    { match: 'create 100', envelope: ok({ ref: '7', url: '/en/sandbox/a/', canonicalUrl: 'http://localhost:5199/en/sandbox/a/' }) },
    { match: 'create 50', envelope: ok({ ref: '8', url: '/sv/b/', canonicalUrl: 'http://alloy-sv.localhost:5199/sv/b/' }) },
    { match: 'delete', envelope: ok({ ref: '7' }) },
  ]);
  const c = new Cms(new OptiCli({ command: fakeCommand }), sandbox, ['http://127.0.0.1:5199']);

  assert.equal((await c.page('StandardPage', 'A')).url, '/en/sandbox/a/');
  assert.equal((await c.page('StandardPage', 'B', {}, { parent: '50' })).url, 'http://alloy-sv.localhost:5199/sv/b/');
  await c.cleanup();
});

const projectDeletes = (calls) => calls().filter((c) => c.args[0] === 'projects' && c.args[1] === 'delete').map((c) => c.args[2]);

test('a page into a project is a draft there, unless told to publish', async () => {
  const { calls } = fake([
    { match: 'create 100', envelope: ok({ ref: '7', url: '/en/sandbox/a/' }) },
    { match: 'set 7', envelope: ok({ ref: '7' }) },
  ]);
  const c = cms();

  await c.page('StandardPage', 'A', {}, { inProject: 'Spring campaign', ready: true, requestApproval: true });
  await c.set('7', { Heading: 'x' }, { inProject: 'Spring campaign' });

  const [create, set] = calls().map((x) => x.args);
  assert.ok(!create.includes('--publish'));
  assert.deepEqual(create.slice(-5), ['--request-approval', '--in-project', 'Spring campaign', '--ready', '--json']);
  assert.ok(set.includes('--in-project'));
});

test("apply removes a project the plan made after its content, and never one that was there", async () => {
  const { calls } = fake([
    {
      match: 'apply',
      envelope: ok({
        dryRun: false,
        created: { a: '30' },
        operations: [{ index: 0, op: 'create', id: 'a', status: 'saved', result: { ref: '30' } }],
        projects: [
          { id: 4, name: 'Test campaign', status: 'created' },
          { id: 2, name: 'Spring campaign', status: 'existing' },
        ],
      }),
    },
    { match: 'delete', envelope: ok({ ref: '30' }) },
    { match: 'projects delete', envelope: ok({ project: { id: 4, name: 'Test campaign', status: 'active' }, dryRun: false, valid: true, deleted: true }) },
  ]);
  const c = cms();

  await c.apply({
    project: 'Test campaign',
    operations: [
      { op: 'create', id: 'a', parent: sandbox, type: 'StandardPage', name: 'A' },
      { op: 'set', ref: '5', properties: { Heading: 'x' }, project: 'Spring campaign' },
    ],
  });
  await c.cleanup();

  const order = calls().map((x) => x.args.slice(0, 2).join(' '));
  assert.deepEqual(deletes(calls), ['30']);
  assert.deepEqual(projectDeletes(calls), ['4']);
  assert.ok(order.indexOf('delete 30') < order.indexOf('projects delete'));
});

test('apply with keep, or a dry run, removes no project', async () => {
  const projects = (status) => [{ name: 'Test campaign', status, ...(status === 'created' ? { id: 4 } : {}) }];
  const { calls } = fake([
    { match: 'apply - --dry-run', envelope: ok({ dryRun: true, created: {}, operations: [], projects: projects('toCreate') }) },
    { match: 'apply', envelope: ok({ dryRun: false, created: {}, operations: [], projects: projects('created') }) },
  ]);
  const c = cms();
  const plan = { project: 'Test campaign', operations: [{ op: 'set', ref: '5', properties: { Heading: 'x' } }] };

  await c.apply(plan, { dryRun: true });
  await c.apply(plan, { keep: true });
  await c.cleanup();

  assert.deepEqual(projectDeletes(calls), []);
});

test('cms.project makes a project the test removes, unless keep', async () => {
  const made = (id, name) => ok({ project: { id, name, status: 'active' }, dryRun: false, valid: true, created: true });
  const { calls } = fake([
    { match: 'projects create Test campaign', envelope: made(4, 'Test campaign') },
    { match: 'projects create Kept', envelope: made(5, 'Kept') },
    { match: 'projects delete 4', exit: 2, envelope: { ok: false, error: { code: 'not_found', message: 'gone' } } },
  ]);
  const c = cms();

  const project = await c.project('Test campaign');
  await c.project('Kept', { keep: true });
  await c.cleanup();

  assert.equal(project.project.id, 4);
  assert.deepEqual(projectDeletes(calls), ['4']);
});
