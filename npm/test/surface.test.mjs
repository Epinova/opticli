// The public surface (exported names, class methods) against the checked-in list: see surface.mjs.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { checkedIn, surface } from './surface.mjs';

test('the public surface is the checked-in list: nothing renamed or removed by accident', () => {
  const now = new Set(surface());
  const listed = new Set(checkedIn());
  const removed = [...listed].filter((n) => !now.has(n));
  const added = [...now].filter((n) => !listed.has(n));

  assert.ok(
    removed.length === 0 && added.length === 0,
    `The package's names changed. Removed (a breaking change: say so in CHANGELOG.md): ${removed.join(', ') || 'none'}. ` +
      `Added: ${added.join(', ') || 'none'}. If that is meant, run \`node test/surface.mjs --write\` and commit test/surface.txt.`,
  );
});
