// Listed, never run: playwright-config.test.mjs checks which projects the config made.
import { expect, test } from '@playwright/test';

test('the front page is in the project language', async ({ page }, testInfo) => {
  await page.goto(`/${testInfo.project.name}/`);
  await expect(page.locator('html')).toHaveAttribute('lang', testInfo.project.name);
});
