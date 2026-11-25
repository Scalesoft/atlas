import { test, expect, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { publishPage, startEditing, sessionLease, PAGE_TEXT, LIVE_EDITOR } from './editing';
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 });
async function secret(field: Locator, value: string) {
  await expect(field).toBeVisible(); await field.evaluate((element, text) => { (element as HTMLInputElement).value = text; element.dispatchEvent(new Event('input', { bubbles: true })); }, value);
}
async function login(context: BrowserContext, username: string, password: string) {
  const page = await context.newPage(); await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username);
  await secret(page.getByLabel('Heslo', { exact: true }), password);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible(); return page;
}
async function mutate(context: BrowserContext, path: string, data: unknown, method: 'post' | 'put' = 'post') {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  return context.request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
}
type RichNode = { type?: string; text?: string; content?: RichNode[] };
/** Text of a page's rich document; the web page API (PageView) no longer returns the Markdown projection. */
function pageText(page: { contentJson: string }) {
  const text = (node: RichNode): string => node.text ?? (node.content ?? []).map(text).join(node.type === 'doc' ? String.fromCharCode(10) : '');
  return text(JSON.parse(page.contentJson).document);
}
async function append(page: Page, text: string) {
  const editor = page.locator(LIVE_EDITOR);
  await editor.click(); await editor.press('Control+End'); await editor.press('Enter'); await page.keyboard.insertText(text);
}
async function save(page: Page, id: string) {
  const accepted = page.waitForResponse(response => response.url().endsWith(`/api/pages/${id}`) && response.request().method() === 'PUT' && response.status() === 200);
  // The page stores itself after a moment (autosave), nothing to click.
  return (await accepted).json();
}
test('an ordinary writer moves a saved page and continues editing with a new generation and destination access', async ({ browser, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Only the explicitly authorized isolated installed acceptance wiki.');
  test.setTimeout(90_000);
  const stamp = Date.now(), password = process.env.ATLAS_E2E_PASSWORD!;
  const admin = await login(context, 'browser-admin', password);
  const sourceResponse = await mutate(context, '/api/spaces', { name: `Přesun zdroj ${stamp}`, icon: 'book-open', color: 'blue' });
  const targetResponse = await mutate(context, '/api/spaces', { name: `Přesun cíl ${stamp}`, icon: 'compass', color: 'green' });
  expect(sourceResponse.status()).toBe(200); expect(targetResponse.status()).toBe(200);
  const source = await sourceResponse.json(), target = await targetResponse.json();
  const username = `move-writer-${stamp}`;
  const activationResponse = await mutate(context, '/api/admin/users', { username, displayName: 'Autor přesunu', isWikiAdmin: false });
  expect(activationResponse.status()).toBe(201); const activation = await activationResponse.json();
  for (const space of [source, target]) expect((await mutate(context, `/api/spaces/${space.id}/access`, { userId: activation.user.id, role: 'Writer', expectedVersion: null }, 'put')).status()).toBe(204);
  const users = await (await context.request.get('/api/admin/users')).json();
  const reader = users.find((user: { username: string }) => user.username === 'browser-reader');
  expect(!!reader).toBe(true);
  expect((await mutate(context, `/api/spaces/${source.id}/access`, { userId: reader.id, role: 'Reader', expectedVersion: null }, 'put')).status()).toBe(204);
  const writerContext = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const readerContext = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  try {
    const writerPassword = `${password}-writer`;
    expect((await mutate(writerContext, '/api/auth/activate', { token: activation.token, newPassword: writerPassword })).status()).toBe(204);
    activation.token = '';
    const writer = await login(writerContext, username, writerPassword);
    const readerPage = await login(readerContext, 'browser-reader', process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`);
    expect((await (await writerContext.request.get('/api/auth/me')).json()).isWikiAdmin).toBe(false);
    const writerSpaces = await (await writerContext.request.get('/api/spaces')).json();
    for (const space of [source, target]) expect(writerSpaces.find((item: { id: string }) => item.id === space.id).role).toBe('Writer');
    const seed = 'Zveřejněný obsah před přesunem.';
    const creation = await mutate(writerContext, '/api/pages', { spaceId: source.id, parentId: null, title: `Přenášený článek ${stamp}`, contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: seed }] }] } }), markdown: seed });
    expect(creation.status()).toBe(200); const initial = await creation.json(), id = initial.id;
    expect((await mutate(writerContext, `/api/pages/${id}/publish`, { expectedVersion: 1 })).status()).toBe(200);
    // The writable page is the shared editor from the first frame; the join follows on its own.
    const joining = sessionLease(writer, id);
    await writer.goto(`/#/pages/${id}`);
    const originalLease = await joining;
    await startEditing(writer);
    await append(writer, 'Uloženo před přesunem.'); const checkpoint = await save(writer, id);
    expect(pageText(checkpoint)).toContain('Uloženo před přesunem.');
    expect((await readerContext.request.get(`/api/pages/${id}`)).status()).toBe(200);
    await writer.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await writer.getByRole('menuitem', { name: 'Přesunout…', exact: true }).click();
    // The organization dialog is the location only (no rename, no tabs); the cross-space move is a disclosure in it.
    const organization = writer.getByRole('dialog', { name: 'Uspořádat stránku', exact: true });
    await expect(organization).toContainText(initial.title);
    await organization.getByText('Přesun do jiného prostoru', { exact: true }).click();
    await organization.locator(`#organization-target-space [data-value="${target.id}"]`).click();
    await organization.getByRole('checkbox', { name: 'Potvrzuji změnu přístupových práv podle cílového prostoru', exact: true }).check();
    const moved = writer.waitForResponse(response => response.url().endsWith(`/api/pages/${id}/move-space`) && response.request().method() === 'POST');
    const rejoining = sessionLease(writer, id);
    await organization.getByRole('button', { name: 'Přesunout do vybraného prostoru', exact: true }).click();
    expect((await moved).status()).toBe(200);
    const newLease = await rejoining;
    expect(newLease.generation).not.toBe(originalLease.generation);
    await expect(organization).not.toBeVisible(); await expect(writer.locator(LIVE_EDITOR)).toBeVisible();
    await expect(writer.locator(PAGE_TEXT)).toContainText('Uloženo před přesunem.');
    const movedPage = await (await writerContext.request.get(`/api/pages/${id}`)).json();
    expect(movedPage.spaceId).toBe(target.id); expect(movedPage.version).toBe(checkpoint.version);
    expect((await readerContext.request.get(`/api/pages/${id}`)).status()).toBe(404);
    expect((await mutate(writerContext, `/api/pages/${id}`, { title: movedPage.title, contentJson: movedPage.contentJson, markdown: pageText(movedPage), expectedVersion: movedPage.version, collaborationGeneration: originalLease.generation }, 'put')).status()).toBe(409);
    expect((await mutate(context, `/api/spaces/${target.id}/access`, { userId: reader.id, role: 'Reader', expectedVersion: null }, 'put')).status()).toBe(204);
    const published = await (await readerContext.request.get(`/api/pages/${id}`)).json();
    expect(pageText(published)).toContain(seed); expect(pageText(published)).not.toContain('Uloženo před přesunem.');
    await append(writer, 'Nová generace po přesunu.'); const saved = await save(writer, id);
    expect(pageText(saved)).toContain('Nová generace po přesunu.'); expect(saved.spaceId).toBe(target.id);
    await publishPage(writer);
    await readerPage.goto(`/#/pages/${id}`); await readerPage.reload();
    await expect(readerPage.locator(PAGE_TEXT)).toContainText('Nová generace po přesunu.');
    await expect(readerPage.locator(PAGE_TEXT)).toContainText('Uloženo před přesunem.');
    await expect(readerPage.locator(PAGE_TEXT)).toHaveAttribute('contenteditable', 'false');
    await expect(readerPage.locator('.breadcrumbs')).toContainText(target.name);
    expect((await (await writerContext.request.get(`/api/spaces/${source.id}/tree`)).json()).items.some((item: { id: string }) => item.id === id)).toBe(false);
    expect((await (await writerContext.request.get(`/api/spaces/${target.id}/tree`)).json()).items.some((item: { id: string }) => item.id === id)).toBe(true);
    await admin.close();
  } finally { await writerContext.close(); await readerContext.close(); }
});


