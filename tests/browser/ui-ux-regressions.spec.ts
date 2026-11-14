import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { startEditing, expectSaved, PAGE_TEXT, LIVE_EDITOR } from './editing';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

async function signIn(page: Page) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin');
  await page.getByLabel('Heslo', { exact: true }).fill(process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}

async function createDocument(context: BrowserContext) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const headers = { 'X-CSRF-TOKEN': csrf.token };
  const spaces = await (await context.request.get('/api/spaces')).json();
  const marker = `ux${Date.now()}${Math.random().toString(36).slice(2, 8)}`;
  const title = `Jak připravit nové pracovní prostředí a získat přístupy ke všem týmovým službám ${marker}`;
  const response = await context.request.post('/api/pages', { headers, data: {
    spaceId: spaces[0].id, title, parentId: null,
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [
        { type: 'paragraph', content: [{ type: 'text', text: 'Ověřte přístupy a připravte pracovní prostředí.' }] },
      ],
    } }), markdown: 'Ověřte přístupy a připravte pracovní prostředí.',
  } });
  expect(response.status()).toBe(200);
  const document = await response.json();
  expect((await context.request.post(`/api/pages/${document.id}/publish`, {
    headers, data: { expectedVersion: document.version },
  })).status()).toBe(200);
  return { ...document, title, marker };
}

test.beforeEach(async ({ page }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the initialized development acceptance wiki.');
  await signIn(page);
});

test('search results remain clickable with a reduced visual viewport', async ({ page, context }) => {
  const document = await createDocument(context);
  await page.getByRole('button', { name: /^Hledat ve wiki/ }).click();
  const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  await search.getByRole('combobox', { name: 'Hledat publikované stránky', exact: true }).fill(`"${document.marker}"`);
  const result = search.getByRole('option').filter({ hasText: document.title });
  await expect(result).toBeVisible();
  await page.setViewportSize({ width: 390, height: 420 });
  await search.locator('.search-content').evaluate(el => { el.scrollTop = el.scrollHeight; });
  expect(await result.evaluate(el => {
    const r = el.getBoundingClientRect();
    const hit = window.document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
    return hit !== null && (hit === el || el.contains(hit));
  })).toBe(true);
  await result.click();
  await expect(page.locator('.document-header h1')).toHaveText(document.title);
});

test('account administration keeps horizontal scrolling inside its table', async ({ page }) => {
  await page.getByRole('button', { name: 'Správa wiki', exact: true }).click();
  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Lidé(\s|$)/ }).click();
  await expect(page.getByRole('heading', { name: 'Lidé', exact: true })).toBeVisible();
  for (const width of [320, 390, 640]) {
    await page.setViewportSize({ width, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    await expect(page.getByRole('heading', { name: 'Lidé', exact: true })).toBeVisible();
  }
});

test('long title and offline feedback preserve the draft across reconnect', async ({ page, context }) => {
  const document = await createDocument(context);
  await page.goto(`/#/pages/${document.id}`);
  await startEditing(page);
  await page.setViewportSize({ width: 390, height: 844 });
  const title = page.getByRole('textbox', { name: 'Název stránky', exact: true });
  expect(await title.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  const changedTitle = document.title + ' – upraveno';
  await title.fill(changedTitle);
  await context.setOffline(true);
  try {
    await expect(page.locator('[data-page-save-state]')).toContainText('Offline, uloží se po připojení', { timeout: 1000 });
  } finally {
    await context.setOffline(false);
  }
  await expect(page.locator(LIVE_EDITOR)).toBeVisible({ timeout: 30_000 });
  await expect(title).toHaveValue(changedTitle);
  await expectSaved(page);
  await page.reload();
  await expect(page.locator('.document-header h1')).toHaveText(changedTitle);
});

test('browsing history retains the editor and restoring creates a draft without republishing', async ({ page, context }) => {
  const document = await createDocument(context);
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const revisedTitle = `Novější koncept ${document.marker}`;
  const saved = await context.request.put(`/api/pages/${document.id}`, {
    headers: { 'X-CSRF-TOKEN': csrf.token },
    data: { title: revisedTitle, contentJson: document.contentJson, markdown: 'Ověřte přístupy a připravte pracovní prostředí.', expectedVersion: document.version },
  });
  expect(saved.status()).toBe(200);
  const draft = await saved.json();
  await page.goto(`/#/pages/${document.id}`);
  const editor = page.locator(PAGE_TEXT);
  await expect(editor).toBeVisible();
  await editor.evaluate(el => el.setAttribute('data-history-instance', 'retained'));
  const openHistory = async () => {
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await page.getByRole('button', { name: 'Historie', exact: true }).click();
    return page.getByRole('dialog', { name: revisedTitle, exact: true });
  };
  let history = await openHistory();
  await expect(history.getByRole('navigation', { name: 'Uložené verze stránky', exact: true })).toContainText('Verze 2');
  await history.getByRole('button', { name: 'Zavřít historii', exact: true }).click();
  await expect(editor).toHaveAttribute('data-history-instance', 'retained');
  history = await openHistory();
  await history.getByRole('button', { name: /^Verze 1 / }).click();
  const preview = history.getByRole('region', { name: 'Vybraná historická verze', exact: true });
  await expect(preview.getByRole('heading', { name: document.title, exact: true })).toBeVisible();
  await expect(preview.locator('[contenteditable="true"]')).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => window.document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  const restored = page.waitForResponse(response => response.url().endsWith(`/api/pages/${document.id}/restore`) && response.request().method() === 'POST');
  await history.getByRole('button', { name: 'Obnovit jako koncept', exact: true }).click();
  expect((await restored).status()).toBe(200);
  await expect(history).not.toBeVisible();
  await expect(page.locator('.document-status')).toContainText('Historická verze byla obnovena jako koncept');
  const current = await (await context.request.get(`/api/pages/${document.id}`)).json();
  expect(current.version).toBe(draft.version + 1);
  expect(current.publishedVersion).toBe(document.version);
  expect(current.title).toBe(document.title);
});
