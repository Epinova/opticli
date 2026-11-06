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
