import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { setLocked } from './editing';

test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });
const username = process.env.ATLAS_E2E_USER, password = process.env.ATLAS_E2E_PASSWORD;
test.skip(!username || !password || process.env.ATLAS_E2E_ALLOW_ICONS !== '1', 'Opt in with ATLAS_E2E_ALLOW_ICONS=1 and a writable test account. Creates and trashes synthetic pages.');
let state: Awaited<ReturnType<BrowserContext['storageState']>>;
let fixtures: { id: string; title: string; version: number; spaceId: string }[] = [];
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
  fixtures = []; await context.addCookies(state.cookies);
  const csrf = await (await context.request.get('/api/auth/csrf')).json(); headers = { 'X-CSRF-TOKEN': csrf.token };
  const spaces = await (await context.request.get('/api/spaces')).json();
  const space = spaces.find((s: { role: string }) => s.role === 'Admin' || s.role === 'Writer');
  expect(space).toBeTruthy();
  for (const title of [`Dokumentace · ikony ${Date.now()}`, 'Podřízená testovací stránka']) {
    const result = await context.request.post('/api/pages', { headers, data: {
      spaceId: space.id, parentId: fixtures[0]?.id ?? null, title, markdown: '## Přehled\n\nUkázka pro ověření ikon.',
      contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
        { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Přehled' }] },
        { type: 'paragraph', content: [{ type: 'text', text: 'Ukázka pro ověření ikon.' }] },
      ] } }),
    } });
    expect(result.ok()).toBe(true); fixtures.push(await result.json());
  }
  await page.goto(`/#/pages/${fixtures[0]!.id}`);
  await expect(page.getByRole('button', { name: 'Přidat ikonu', exact: true })).toBeVisible();
});
test.afterEach(async ({ context }) => {
  for (const fixture of [...fixtures].reverse()) {
    const current = await (await context.request.get(`/api/pages/${fixture.id}`)).json();
    const tree = await (await context.request.get(`/api/spaces/${fixture.spaceId}/tree`)).json();
    const result = await context.request.post(`/api/pages/${fixture.id}/trash`, { headers, data: { expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false } });
    expect(result.ok(), 'Remove synthetic icon fixtures').toBe(true);
  }
});
// The picker's icon grid is a region named „Ikona stránky“ too; the picker itself is the one with the save button.
const picker = (page: Page) => page.getByRole('region', { name: 'Ikona stránky', exact: true })
  .filter({ has: page.getByRole('button', { name: 'Použít', exact: true }) });
async function save(page: Page) {
  const response = page.waitForResponse(r => r.url().endsWith(`/api/pages/${fixtures[0]!.id}/decoration`) && r.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Použít', exact: true }).click();
  expect((await response).ok()).toBe(true);
  await expect(picker(page)).toBeHidden();
}
test('a parent page icon and color persist in heading and tree and can be removed on mobile', async ({ page, context }, info) => {
  const fixture = fixtures[0]!;
  const titleIcon = page.locator('.document-header h1 .page-decoration-icon');
  const treeLink = page.locator(`.tree-page[href$="/pages/${fixture.id}"]`);
  const treeIcon = treeLink.locator('.page-decoration-icon');
  await page.getByRole('button', { name: 'Přidat ikonu', exact: true }).click();
  // The picker unfolds in place under the title (no dialog since 2026-09-28).
  const dialog = picker(page);
  // The search over the whole icon set opens under „Další ikony (N)“; the option „Složka“ is the icon folder.
  await dialog.getByRole('button', { name: /^Další ikony \(/ }).click();
  await dialog.getByRole('searchbox', { name: 'Hledat ikonu' }).fill('slozka');
  await dialog.getByRole('listbox', { name: 'Ikony' }).locator('[data-value="folder"]').click();
  await dialog.getByRole('radio', { name: 'Modrá · jemná', exact: true }).check();
  await dialog.getByRole('searchbox', { name: 'Hledat ikonu' }).fill('');
  await page.screenshot({ path: info.outputPath('page-icon-picker-desktop.png'), animations: 'disabled' });
  await save(page);
  await expect(titleIcon).toHaveAttribute('data-icon', 'folder'); await expect(treeIcon).toHaveAttribute('data-color', 'blue-2');
  const toggle = page.getByRole('button', { name: `Sbalit ${fixture.title}`, exact: true });
  await toggle.click(); await expect(page.getByRole('button', { name: `Rozbalit ${fixture.title}`, exact: true })).toBeVisible();
  await page.reload(); await expect(titleIcon).toHaveAttribute('data-color', 'blue-2'); await expect(treeIcon).toHaveAttribute('data-icon', 'folder');
  const current = await (await context.request.get(`/api/pages/${fixture.id}`)).json();
  expect(current.version, 'Icon changes do not create content revisions').toBe(fixture.version);

  // A phone offers the icon editor only while editing (board 13); the lock in the bottom bar unlocks the page.
  const editOnPhone = () => setLocked(page, false);
  await page.setViewportSize({ width: 390, height: 844 });
  await editOnPhone();
  await page.getByRole('button', { name: 'Změnit ikonu', exact: true }).click();
  // Tones other than the ten quick colours are in the palette.
  await dialog.getByRole('button', { name: 'Další barvy a tóny (60)', exact: true }).click();
  await dialog.getByRole('radio', { name: 'Fialová · sytá', exact: true }).check();
  const bounds = (await dialog.boundingBox())!;
  expect(bounds.x).toBeGreaterThanOrEqual(0); expect(bounds.x + bounds.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: info.outputPath('page-icon-picker-mobile.png'), animations: 'disabled' });
  await save(page); await page.reload(); await expect(titleIcon).toHaveAttribute('data-color', 'violet-3');
  await editOnPhone();
  await page.getByRole('button', { name: 'Změnit ikonu', exact: true }).click();
  await dialog.getByRole('button', { name: 'Bez ikony', exact: true }).click(); await save(page);
  await page.reload(); await expect(titleIcon).toHaveCount(0);
  await page.setViewportSize({ width: 1440, height: 900 }); await expect(treeIcon).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Přidat ikonu', exact: true })).toBeVisible();
});
test('a conflicting icon edit preserves the other writer choice and offers a reload', async ({ page, context }) => {
  await page.getByRole('button', { name: 'Přidat ikonu', exact: true }).click();
  const dialog = picker(page);
  await dialog.getByRole('radio', { name: 'Složka', exact: true }).check();
  const other = await context.request.put(`/api/pages/${fixtures[0]!.id}/decoration`, { headers, data: { icon: 'star', color: 'amber', expectedRevisionId: null } });
  expect(other.ok()).toBe(true);
  const failed = page.waitForResponse(r => r.url().endsWith('/decoration') && r.request().method() === 'PUT');
  await dialog.getByRole('button', { name: 'Použít', exact: true }).click(); expect((await failed).status()).toBe(409);
  await expect(dialog.getByRole('alert')).toBeVisible(); await expect(dialog.getByRole('button', { name: 'Použít', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Načíst aktuální ikonu', exact: true }).click();
  await expect(dialog.getByRole('radio', { name: 'Hvězda', exact: true })).toBeChecked();
  await dialog.getByRole('button', { name: 'Zrušit', exact: true }).click();
  await expect(page.locator('.document-header h1 .page-decoration-icon')).toHaveAttribute('data-icon', 'star');
});
