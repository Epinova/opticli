// opticli/playwright: start the site, seed content and clean up in Playwright tests.
import { createTest } from './fixtures.ts';

export { Cms, createTest } from './fixtures.ts';
export type { CreatedPage, OptiCliWorkerOptions, SandboxOptions } from './fixtures.ts';
export { defineSetup, readSeedState, stateVariable } from './setup.ts';
export type { SeedState, SetupOptions } from './setup.ts';
export { OptiCliError } from '../opticli.ts';
export { expect } from '@playwright/test';

/** The fixtures, with `seed.refs` keyed by any string; `createTest<typeof plan>()` types them by the plan's ids. */
export const test = createTest();
