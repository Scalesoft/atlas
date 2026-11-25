import { expect, test } from '@playwright/test';

test('settings sections stay in the application left panel around its breakpoint', async ({ page }) => {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const json = path === '/api/setup/status' ? { version: '2.6.0-dev+ui-fixture', databaseReady: true, requiresSetup: false }
      : path === '/api/auth/me' ? { id: 'layout-user', username: 'layout', displayName: 'Ukázkový účet', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'teal', mode: 'light' } }
      : path === '/api/spaces' ? []
      : path === '/api/profile/account' ? { displayName: 'Ukázkový účet', username: 'layout', version: '1' }
      : undefined;
    await route.fulfill({ status: json === undefined ? 404 : 200, json: json ?? {} });
  });
  await page.setViewportSize({ width: 1024, height: 900 });
  await page.goto('/#/profile/account');
  const shell = page.locator('.settings-shell');
  await expect(shell).toHaveAttribute('data-rail-outside', 'true');
  const rail = page.locator('#atlas-settings-rail');
  const sections = rail.locator('.settings-nav-link[aria-current="page"]');
  for (const width of [1024, 900, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    // No "Sekce" bar or sheet: the sections are in the left panel, a drawer below 1025 px, like the rest of the app.
    await expect(shell.getByRole('button', { name: /^Sekce/ })).toHaveCount(0);
    await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
    await expect(sections).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(sections).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await expect(sections).toBeVisible();
});

test('settings rows contain long metadata and wrapping actions at their actual container width', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 900 });
  await page.goto('/?gallery=1#/gallery/layouts');
  const example = page.locator('[data-responsive-settings]');
  for (const width of ['1000', '480', '280']) {
    await page.getByLabel('Šířka ukázky nastavení').selectOption(width);
    await expect.poll(() => example.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    expect(await example.locator('.settings-list-row-text').evaluate(el => el.clientWidth)).toBeGreaterThan(100);
    expect(await example.locator('.settings-list-row-meta').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    for (const label of await example.locator('.status-badge > span').all()) {
      expect(await label.evaluate(el => el.clientWidth)).toBeGreaterThan(80);
    }
    const actions = example.locator('.settings-list-row-actions');
    const bounds = await example.boundingBox();
    for (const button of await actions.getByRole('button').all()) {
      const rect = await button.boundingBox();
      expect(rect!.x).toBeGreaterThanOrEqual(bounds!.x);
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(bounds!.x + bounds!.width);
    }
  }
});

test('library tabs keep the underline on their row and scroll focused tabs into view in a narrow desktop container', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 900 });
  await page.goto('/?gallery=1#/gallery/refresh');
  const example = page.locator('[data-refresh-example="LibraryDirectoryExample"]');
  await example.getByLabel('Šířka ukázky knihovny').selectOption('480px');
  const nav = example.getByRole('navigation', { name: 'Části knihovny' });
  const links = nav.getByRole('link');
  await expect(links).toHaveCount(5);
  await page.evaluate(() => document.fonts.ready);
  expect(await nav.evaluate(el => el.scrollWidth)).toBeGreaterThan(await nav.evaluate(el => el.clientWidth));
  const tops = await links.evaluateAll(elements => elements.map(el => el.getBoundingClientRect().top));
  expect(Math.max(...tops) - Math.min(...tops)).toBeLessThan(1);
  await links.first().focus();
  for (let i = 0; i < 4; i++) await page.keyboard.press('Tab');
  await expect(links.last()).toBeFocused();
  const navBox = await nav.boundingBox(), lastBox = await links.last().boundingBox();
  expect(lastBox!.x + lastBox!.width).toBeLessThanOrEqual(navBox!.x + navBox!.width + 1);
  await page.keyboard.press('Enter');
  await expect(links.last()).toHaveAttribute('aria-current', 'page');
  await expect.poll(async () => {
    const line = await nav.locator('.library-tabs-indicator').boundingBox();
    const link = await links.last().boundingBox();
    return Math.abs(line!.x - link!.x);
  }).toBeLessThan(1);
});
