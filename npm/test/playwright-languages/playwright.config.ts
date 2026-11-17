// A tester's case: one Playwright project per language the site has enabled, read when the config loads. A
// playwright.config can't await, so it uses the blocking client. playwright-config.test.mjs lists its tests against the
// stand-in opticli (OPTICLI); in a site's own suite the import is '@epinova/opticli'.
import { defineConfig } from '@playwright/test';
import { OptiCliSync } from '../../src/index.ts';

const languages = new OptiCliSync().languages().filter((language) => language.enabled);

export default defineConfig({
  testDir: '.',
  outputDir: process.env.PLAYWRIGHT_OUTPUT_DIR,
  projects: languages.map((language) => ({ name: language.code, use: { locale: language.code } })),
});
