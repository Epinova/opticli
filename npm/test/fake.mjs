// Sets up fake-opticli.mjs for one test: its responses, and the calls it got.
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const fakeCommand = [process.execPath, join(import.meta.dirname, 'fake-opticli.mjs')];

export function fake(responses) {
  const dir = mkdtempSync(join(tmpdir(), 'opticli-fake-'));
  const log = join(dir, 'calls.jsonl');
  writeFileSync(log, '');
  writeFileSync(join(dir, 'responses.json'), JSON.stringify(responses));
  process.env.FAKE_OPTICLI_RESPONSES = join(dir, 'responses.json');
  process.env.FAKE_OPTICLI_LOG = log;
  return {
    dir,
    calls: () => readFileSync(log, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line)),
  };
}

export const ok = (data, meta = {}) => ({ ok: true, data, meta: { source: 'agent', version: '9.9.9', ...meta } });
