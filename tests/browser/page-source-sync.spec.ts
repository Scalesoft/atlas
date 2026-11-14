import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { startEditing, expectSaved, PAGE_TEXT, LIVE_EDITOR } from './editing';

// These tests copy a page inside an explicitly enabled disposable wiki. Imported content and credentials stay in memory.
test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });
const username = process.env.ATLAS_E2E_USER;
const password = process.env.ATLAS_E2E_PASSWORD;
const sourcePageId = process.env.ATLAS_E2E_SOURCE_PAGE_ID;
test.skip(!username || !password || !sourcePageId || process.env.ATLAS_E2E_ALLOW_COLLAB !== '1',
  'Requires an isolated wiki, ATLAS_E2E_USER/PASSWORD/SOURCE_PAGE_ID and ATLAS_E2E_ALLOW_COLLAB=1.');

interface PageSnapshot { id: string; spaceId: string; title: string; version: number; contentJson: string }
let state: Awaited<ReturnType<BrowserContext['storageState']>>;
let fixture: PageSnapshot | undefined;

async function mutate(context: BrowserContext, path: string, data: unknown) {
  const csrf = await context.request.get('/api/auth/csrf');
  expect(csrf.status()).toBe(200);
  return context.request.post(path, { data, headers: { 'X-CSRF-TOKEN': (await csrf.json()).token } });
}

async function readPage(context: BrowserContext, id: string): Promise<PageSnapshot> {
  const response = await context.request.get(`/api/pages/${id}`);
  expect(response.status()).toBe(200);
  return response.json();
}

function containsText(snapshot: PageSnapshot, marker: string) {
  // Assert only on a unique public test marker, so a failed assertion never prints the copied article.
  return snapshot.contentJson.includes(marker);
}

async function expectDocumentMarker(page: Page, marker: string, present = true) {
  await expect.poll(async () => (await page.locator(PAGE_TEXT).textContent())?.includes(marker) ?? false,
    { message: 'The public document retains the expected test marker.' }).toBe(present);
}

test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  try {
    const page = await context.newPage();
    await page.goto('/');
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username!);
    const field = page.getByLabel('Heslo', { exact: true });
    await expect(field).toBeVisible();
    await field.evaluate((element, value) => {
      (element as HTMLInputElement).value = value;
      element.dispatchEvent(new Event('input', { bubbles: true }));
    }, password!);
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    state = await context.storageState();
  } finally { await context.close(); }
});

test.beforeEach(async ({ context, page }) => {
  fixture = undefined;
  await context.addCookies(state.cookies);
  const original = await readPage(context, sourcePageId!);
  const created = await mutate(context, '/api/pages', {
    spaceId: original.spaceId, parentId: null, title: `Source sync fixture ${Date.now()}`,
    contentJson: original.contentJson, markdown: '',
  });
  expect(created.status()).toBe(200);
  fixture = await created.json();
  await page.goto(`/#/pages/${fixture!.id}`);
  await expect(page.getByRole('textbox', { name: 'Název stránky', exact: true })).toHaveValue(fixture!.title);
});

test.afterEach(async ({ context }) => {
  // Close live sessions first; trash is reversible and uses the same public version checks as the application.
  await Promise.all(context.pages().map(page => page.close()));
  if (!fixture) return;
  const current = await readPage(context, fixture.id);
  const treeResponse = await context.request.get(`/api/spaces/${fixture.spaceId}/tree`);
  expect(treeResponse.status()).toBe(200);
  const tree = await treeResponse.json();
  const trashed = await mutate(context, `/api/pages/${fixture.id}/trash`, {
    expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false,
  });
  expect(trashed.status()).toBe(200);
});

async function appendDocument(page: Page, marker: string) {
  const document = page.locator(LIVE_EDITOR);
  await document.focus();
  await document.press('ControlOrMeta+End');
  await document.press('Enter');
  await page.keyboard.insertText(marker);
  await expect(page.locator('[data-page-save-state]')).toBeVisible();
}

const switchButton = (page: Page, name: 'Dokument' | 'Zdroj') => page.locator('.source-switch').getByRole('button', { name, exact: true });

async function openSource(page: Page) {
  await switchButton(page, 'Zdroj').click();
  const panel = page.getByRole('region', { name: 'Zdroj stránky', exact: true });
  await expect(panel.getByRole('textbox', { name: 'Zdroj stránky', exact: true })).toBeVisible();
  return panel;
}

/** „Dokument“ with a clean source: the page's own text is back at once and takes input. */
async function backToDocument(page: Page) {
  await switchButton(page, 'Dokument').click();
  await expect(page.locator('.source-pane')).toHaveCount(0);
  await expect(page.locator(PAGE_TEXT)).toHaveAttribute('contenteditable', 'true');
}

async function appendSource(page: Page, marker: string) {
  const input = page.getByRole('textbox', { name: 'Zdroj stránky', exact: true });
  await input.focus();
  await input.press('ControlOrMeta+End');
  await input.press('Enter');
  await input.press('Enter');
  await page.keyboard.insertText(marker);
}

async function expectSourceMarker(page: Page, marker: string) {
  const input = page.getByRole('textbox', { name: 'Zdroj stránky', exact: true });
  await expect.poll(async () => {
    await input.focus();
    await input.press('ControlOrMeta+End');
    const visibleSource = await page.locator('.md-source-panel .view-lines').textContent();
    return visibleSource?.replace(/\s+/g, ' ').includes(marker) ?? false;
  }, { message: 'The public source editor retains the test marker.' }).toBe(true);
}

test('document edits survive a local source draft, the question before leaving it, Apply and reload', async ({ page, context }) => {
  const documentMarker = 'Source-sync document addition';
  const sourceMarker = 'Source-sync local source addition';
  await startEditing(page);
  await appendDocument(page, documentMarker);
  const panel = await openSource(page);
  expect(containsText(await readPage(context, fixture!.id), documentMarker)).toBe(true);
  await appendSource(page, sourceMarker);
  await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeEnabled();

  // „Dokument“ (and Esc) over an unsaved draft asks first; the draft never reaches the document or the server unapplied.
  const question = page.getByRole('alertdialog', { name: 'Rozpracované změny zdroje', exact: true });
  await switchButton(page, 'Dokument').click();
  await expect(question).toBeVisible();
  await question.getByRole('button', { name: 'Pokračovat v úpravách', exact: true }).click();
  await page.getByRole('textbox', { name: 'Zdroj stránky', exact: true }).press('Escape');
  await expect(question).toBeVisible();
  await question.getByRole('button', { name: 'Pokračovat v úpravách', exact: true }).click();
  await expect(question).toBeHidden();
  await expect(switchButton(page, 'Zdroj')).toHaveAttribute('aria-pressed', 'true');
  await expectDocumentMarker(page, documentMarker);
  await expectDocumentMarker(page, sourceMarker, false);
  expect(containsText(await readPage(context, fixture!.id), sourceMarker)).toBe(false);

  await expectSourceMarker(page, sourceMarker);
  const saved = page.waitForResponse(response => response.url().endsWith(`/api/pages/${fixture!.id}/source`) && response.request().method() === 'PUT');
  await panel.getByRole('button', { name: 'Použít', exact: true }).click();
  expect((await saved).status()).toBe(200);
  await expect.poll(async () => containsText(await readPage(context, fixture!.id), sourceMarker)).toBe(true);
  await page.reload();
  await expectDocumentMarker(page, documentMarker);
  await expectDocumentMarker(page, sourceMarker);
});

test('remote edits make a dirty source stale without losing either author’s work during recovery', async ({ page, context }) => {
  const localMarker = 'Source-sync recovered local addition';
  const remoteMarker = 'Source-sync remote writer addition';
  await startEditing(page);
  const panel = await openSource(page);
  await appendSource(page, localMarker);
  await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeEnabled();

  const remote = await context.newPage();
  await remote.goto(`/#/pages/${fixture!.id}`);
  await startEditing(remote);
  await appendDocument(remote, remoteMarker);
  await expect(panel.getByRole('alert').filter({ hasText: 'Stránka se mezitím změnila' })).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeDisabled();
  await expectSourceMarker(page, localMarker);
  await expectSaved(remote);
  const checkpoint = await readPage(context, fixture!.id);
  expect(containsText(checkpoint, remoteMarker)).toBe(true);
  expect(containsText(checkpoint, localMarker)).toBe(false);

  await panel.getByRole('button', { name: 'Načíst aktuální verzi', exact: true }).click();
  const confirmation = page.getByRole('alertdialog', { name: 'Načíst aktuální verzi', exact: true });
  await expect(confirmation).toBeVisible();
  await confirmation.getByRole('button', { name: 'Pokračovat v úpravách', exact: true }).click();
  await expectSourceMarker(page, localMarker);
  await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeDisabled();

  // The reader keeps their authored addition, then explicitly rebuilds it on the current document instead of overwriting a collaborator.
  await panel.getByRole('button', { name: 'Načíst aktuální verzi', exact: true }).click();
  await confirmation.getByRole('button', { name: 'Zahodit návrh a načíst', exact: true }).click();
  await backToDocument(page);
  await expectDocumentMarker(page, remoteMarker);
  await openSource(page);
  await appendSource(page, localMarker);
  await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeEnabled();
  // Whose tab saves the shared session's checkpoint first decides what Apply meets: this tab's own checkpoint (it goes before the
  // source) brings the current version and the source is saved; after the colleague's checkpoint the version here is old and the
  // server refuses it. Both keep both authors' work.
  const answered = page.waitForResponse(async response => {
    if (response.request().method() !== 'PUT') return false;
    if (response.url().endsWith(`/api/pages/${fixture!.id}/source`)) return true;
    return response.url().endsWith(`/api/pages/${fixture!.id}`) && response.status() >= 400
      && (await response.json()).code !== 'collaboration_pending';
  });
  await panel.getByRole('button', { name: 'Použít', exact: true }).click();
  const answer = await answered;
  if (answer.status() !== 200) {
    const conflict = answer;
    expect(conflict.status()).toBe(409);
    expect((await conflict.json()).code).toBe('version_conflict');
    await expect(panel.getByRole('alert').filter({ hasText: 'Stránku mezitím uložil někdo jiný' })).toBeVisible();
    await expectSourceMarker(page, localMarker);
    const afterConflict = await readPage(context, fixture!.id);
    expect(containsText(afterConflict, remoteMarker)).toBe(true);
    expect(containsText(afterConflict, localMarker)).toBe(false);

    // A version conflict refreshes the host version, but still requires the author to review the current source explicitly.
    await panel.getByRole('button', { name: 'Načíst aktuální verzi', exact: true }).click();
    await confirmation.getByRole('button', { name: 'Zahodit návrh a načíst', exact: true }).click();
    await backToDocument(page);
    await expectDocumentMarker(page, remoteMarker);
    await openSource(page);
    await appendSource(page, localMarker);
    await expect(panel.getByRole('button', { name: 'Použít', exact: true })).toBeEnabled();
    const recovered = page.waitForResponse(response => response.url().endsWith(`/api/pages/${fixture!.id}/source`) && response.request().method() === 'PUT');
    await panel.getByRole('button', { name: 'Použít', exact: true }).click();
    expect((await recovered).status()).toBe(200);
  }
  await page.reload();
  await expectDocumentMarker(page, remoteMarker);
  await expectDocumentMarker(page, localMarker);
  const persisted = await readPage(context, fixture!.id);
  expect(containsText(persisted, remoteMarker) && containsText(persisted, localMarker)).toBe(true);
  expect(persisted.version).toBeGreaterThan(checkpoint.version);
});
