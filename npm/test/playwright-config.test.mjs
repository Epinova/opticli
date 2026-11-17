// The tester's case end to end: Playwright loads a playwright.config that builds its projects from
// `new OptiCliSync().languages()`, here against the stand-in opticli.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { test } from 'node:test';
import { fake, ok } from './fake.mjs';

test('a playwright.config gets a project per enabled language from the sync client', () => {
  const { calls, dir } = fake([
    {
      match: 'languages',
      envelope: ok(
        [
          { id: 1, code: 'en', name: 'English', enabled: true, sortIndex: 10, contentItems: 40, masterOf: ['Alloy'] },
          { id: 2, code: 'sv', name: 'Svenska', enabled: true, sortIndex: 20, contentItems: 12, masterOf: [] },
          { id: 3, code: 'no', name: 'Norsk', enabled: false, sortIndex: 30, contentItems: 0, masterOf: [] },
        ],
        { source: 'db' },
      ),
    },
  ]);
  const playwright = join(import.meta.dirname, '..', 'node_modules', '@playwright', 'test', 'cli.js');
  const config = join(import.meta.dirname, 'playwright-languages', 'playwright.config.ts');

  const run = spawnSync(process.execPath, [playwright, 'test', '--config', config, '--list'], {
    encoding: 'utf8',
    env: { ...process.env, OPTICLI: join(import.meta.dirname, 'fake-opticli.mjs'), PLAYWRIGHT_OUTPUT_DIR: join(dir, 'results') },
    timeout: 60_000,
  });

  assert.equal(run.status, 0, run.stderr || run.stdout);
  assert.match(run.stdout, /\[en\] › front-page\.spec\.ts/);
  assert.match(run.stdout, /\[sv\] › front-page\.spec\.ts/);
  assert.doesNotMatch(run.stdout, /\[no\]/);
  assert.deepEqual(calls()[0].args, ['languages', '--json']);
});
