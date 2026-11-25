import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { setLocked } from './editing';

test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });
const username = process.env.ATLAS_E2E_USER, password = process.env.ATLAS_E2E_PASSWORD;
test.skip(!username || !password || process.env.ATLAS_E2E_ALLOW_TAGS !== '1',
  'Opt in with ATLAS_E2E_ALLOW_TAGS=1 and a dedicated account; creates and trashes synthetic pages.');
let state: Awaited<ReturnType<BrowserContext['storageState']>>;
let fixture: { id: string; spaceId: string; version: number; title: string } | undefined;
let headers: Record<string, string>;

test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext();
  try {
    const page = await context.newPage(); await page.goto('/');
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username!);
    await page.getByLabel('Heslo', { exact: true }).fill(password!);
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    state = await context.storageState();
  } finally { await context.close(); }
});
test.beforeEach(async ({ context, page }) => {
  fixture = undefined; await context.addCookies(state.cookies);
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  headers = { 'X-CSRF-TOKEN': csrf.token };
  const spaces = await (await context.request.get('/api/spaces')).json();
  const space = spaces.find((s: { role: string }) => s.role === 'Admin' || s.role === 'Writer');
  expect(space, 'A writable test space is required').toBeTruthy();
  const title = `Tagy – browserová přejímka ${Date.now()}`;
  const response = await context.request.post('/api/pages', { headers, data: {
    spaceId: space.id, parentId: null, title, markdown: '## Přehled\n\nTestovací stránka pro ověření tagů.',
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
      { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Přehled' }] },
      { type: 'paragraph', content: [{ type: 'text', text: 'Testovací stránka pro ověření tagů.' }] },
    ] } }),
  } });
  expect(response.ok()).toBe(true); fixture = await response.json();
  await page.goto(`/#/pages/${fixture!.id}`);
  await expect(page.locator('.document-header h1')).toHaveText(title);
  await expect(page.getByRole('button', { name: 'Přidat tagy', exact: true })).toBeVisible();
});
test.afterEach(async ({ context }) => {
  if (!fixture) return;
  const pageResponse = await context.request.get(`/api/pages/${fixture.id}`);
  expect(pageResponse.ok(), 'Fixture must remain accessible for cleanup').toBe(true);
  const document = await pageResponse.json();
  const tree = await (await context.request.get(`/api/spaces/${document.spaceId}/tree`)).json();
  const trashed = await context.request.post(`/api/pages/${document.id}/trash`, { headers, data: {
    expectedVersion: document.version, expectedTreeVersion: tree.version, includeSubtree: false,
  } });
  expect(trashed.ok(), 'Trash the synthetic page after the test').toBe(true);
});
// Tags save as they go (P5): every change is its own PUT, and Esc only closes the dialog.
const tagsSaved = (page: Page) => page.waitForResponse(r => r.url().endsWith(`/api/pages/${fixture!.id}/tags`) && r.request().method() === 'PUT');
async function addTag(page: Page, name: string) {
  const dialog = page.getByRole('dialog', { name: /^(Přidat|Upravit) tagy$/ });
  const input = dialog.getByRole('combobox');
  const saved = tagsSaved(page);
  await input.fill(name); await input.press('Enter');
  expect((await saved).ok()).toBe(true);
  await expect(dialog.getByRole('button', { name: `Odebrat tag ${name}`, exact: true })).toBeVisible();
}
async function closeTags(page: Page) {
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: /^(Přidat|Upravit) tagy$/ })).toBeHidden();
}
test('multiple tags persist, can be searched and removed, and fit desktop and mobile', async ({ page, context }, info) => {
  const first = `Integrace ${fixture!.id.slice(0, 8)}`, second = `Návody ${fixture!.id.slice(0, 8)}`;
  await page.getByRole('button', { name: 'Přidat tagy', exact: true }).click();
  await addTag(page, first); await addTag(page, second);
  await page.screenshot({ path: info.outputPath('page-tags-editor-desktop.png'), animations: 'disabled' });
  await closeTags(page); await page.reload();
  const chips = page.locator('.document-header .page-tag-link');
  await expect(chips).toHaveCount(2);
  await expect(chips.filter({ hasText: first })).toBeVisible();
  const document = await (await context.request.get(`/api/pages/${fixture!.id}`)).json();
  expect(document.version, 'Tag metadata must not create an article revision').toBe(fixture!.version);

  await chips.filter({ hasText: first }).click();
  const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  await expect(search.locator('.selected-search-tag')).toContainText(first);
  await expect(search.locator('.search-result').filter({ hasText: fixture!.title })).toBeVisible();
  await page.reload();
  await expect(search.locator('.selected-search-tag')).toContainText(first);
  await search.locator('.search-result').filter({ hasText: fixture!.title }).click();
  await expect(search).toBeHidden();
  await page.keyboard.press('ControlOrMeta+k');
  await search.getByRole('combobox', { name: /^Hledat ve wiki – / }).fill(second);
  const suggestion = search.getByRole('region', { name: 'Nalezené tagy' }).getByRole('button', { name: `Stránky s tagem ${second}`, exact: true });
  await expect(suggestion).toBeVisible(); await suggestion.click();
  await expect(search.locator('.selected-search-tag')).toContainText(second);
  await search.locator('.search-result').filter({ hasText: fixture!.title }).click();

  for (const width of [1920, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1080 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    for (const chip of await chips.all()) {
      const box = await chip.boundingBox(); expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(width);
    }
  }
  // A phone offers the tag editor only while editing (board 13); the lock in the bottom bar unlocks the page.
  await setLocked(page, false);
  await page.getByRole('button', { name: 'Upravit tagy', exact: true }).click();
  const editor = page.getByRole('dialog', { name: /^(Přidat|Upravit) tagy$/ });
  const box = await editor.boundingBox(); expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: info.outputPath('page-tags-editor-mobile.png'), animations: 'disabled' });
  const removed = tagsSaved(page);
  await editor.getByRole('button', { name: `Odebrat tag ${second}`, exact: true }).click();
  expect((await removed).ok()).toBe(true);
  await closeTags(page); await page.reload();
  await expect(chips).toHaveCount(1); await expect(chips).toContainText(first);
  await info.attach('tag-state.json', { body: JSON.stringify(await (await context.request.get(`/api/pages/${fixture!.id}/tags`)).json()), contentType: 'application/json' });
});
test('a stale tag editor reports the conflict, keeps the other saved update and shows the current tags', async ({ page, context }) => {
  await page.getByRole('button', { name: 'Přidat tagy', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: /^(Přidat|Upravit) tagy$/ });
  // A colleague saves while this editor is open on the old tags.
  const other = await context.request.put(`/api/pages/${fixture!.id}/tags`, { headers, data: { names: ['Úprava kolegy'], expectedTagIds: [] } });
  expect(other.ok()).toBe(true);
  const conflict = tagsSaved(page);
  const input = dialog.getByRole('combobox');
  await input.fill('Moje úprava'); await input.press('Enter');
  expect((await conflict).status()).toBe(409);
  await expect(dialog.getByRole('alert')).toBeVisible();
  // The refused tag is taken back and the current tags stand in the dialog: nothing to reload by hand.
  await expect(dialog.getByRole('button', { name: 'Odebrat tag Úprava kolegy', exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Odebrat tag Moje úprava', exact: true })).toHaveCount(0);
  const stored = await (await context.request.get(`/api/pages/${fixture!.id}/tags`)).json();
  expect(stored.tags.map((tag: { name: string }) => tag.name)).toEqual(['Úprava kolegy']);
  await closeTags(page);
});
