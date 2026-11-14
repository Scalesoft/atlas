import { expect, test, type Page } from '@playwright/test';
import { startEditing, setLocked } from './editing';

// Real editor, Monaco and collaboration. Never change the imported fixture itself.
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.beforeEach(() => test.skip(
  process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !process.env.ATLAS_E2E_PASSWORD || !process.env.ATLAS_E2E_SOURCE_PAGE_ID,
  'Requires an isolated acceptance wiki and a long imported source page.',
));
let fixtureId: string | undefined;
let fixtureSpaceId: string | undefined;
test.afterEach(async ({ page, context }) => {
  await page.close();
  if (!fixtureId) return;
  const snapshot = await (await context.request.get(`/api/pages/${fixtureId}`)).json();
  const tree = await (await context.request.get(`/api/spaces/${fixtureSpaceId}/tree`)).json();
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const response = await context.request.post(`/api/pages/${fixtureId}/trash`, {
    headers: { 'X-CSRF-TOKEN': csrf.token },
    data: { expectedVersion: snapshot.version, expectedTreeVersion: tree.version, includeSubtree: false },
  });
  expect(response.ok()).toBe(true);
  fixtureId = undefined;
});

const switchButton = (page: Page, name: 'Dokument' | 'Zdroj') => page.locator('.source-switch').getByRole('button', { name, exact: true });
const switchTop = async (page: Page) => Math.round((await page.locator('.source-switch').boundingBox())!.y);

/** The document is the shown view: the source is gone and the text flows with the page, in no pane of its own. */
async function documentFlowsWithPage(page: Page) {
  await expect(page.locator('.source-pane')).toHaveCount(0);
  await expect(switchButton(page, 'Dokument')).toHaveAttribute('aria-pressed', 'true');
  const layout = await page.locator('.document-pane').evaluate(pane => {
    const editor = pane.querySelector<HTMLElement>('.ProseMirror')!;
    return { shown: getComputedStyle(pane).display !== 'none', overflow: getComputedStyle(pane).overflowY, fitsDocument: pane.clientHeight >= editor.offsetHeight };
  });
  expect(layout).toEqual({ shown: true, overflow: 'visible', fitsDocument: true });
}

/** The source is the shown view: the one switch, where it stood, the document hidden behind it and the caret in Monaco. */
async function sourceShown(page: Page, top: number) {
  await expect(page.locator('.source-pane .monaco-editor').first()).toBeVisible();
  await expect(switchButton(page, 'Zdroj')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.source-switch')).toHaveCount(1);
  await expect(page.locator('.document-pane')).toBeHidden();
  expect(await switchTop(page)).toBe(top);
  await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('.md-source-editor'))).toBe(true);
  // The pane ends inside the window: the source scrolls in Monaco, not with the page.
  expect(await page.locator('.source-pane').evaluate(pane => pane.getBoundingClientRect().bottom <= window.innerHeight + 1)).toBe(true);
}

test('source round trips restore page scrolling, document content and the same live editor', async ({ page, context }) => {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, password) => {
    (element as HTMLInputElement).value = password!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  const originalResponse = await context.request.get(`/api/pages/${process.env.ATLAS_E2E_SOURCE_PAGE_ID}`);
  expect(originalResponse.ok()).toBe(true);
  const original = await originalResponse.json();
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const copied = await context.request.post('/api/pages', {
    headers: { 'X-CSRF-TOKEN': csrf.token },
    data: { spaceId: original.spaceId, parentId: null, title: `Source navigation audit ${Date.now()}`, contentJson: original.contentJson, markdown: '' },
  });
  expect(copied.ok()).toBe(true);
  const id = (await copied.json()).id;
  fixtureId = id;
  fixtureSpaceId = original.spaceId;
  await page.goto(`/#/pages/${id}`);
  await startEditing(page);
  const editor = page.locator('.document-editor-surface .ProseMirror');
  await expect(editor).toHaveAttribute('contenteditable', 'true');
  const originalEditor = await editor.elementHandle();
  // textContent includes off-screen paragraphs skipped by the reader's content-visibility optimization.
  const originalText = await editor.textContent();
  expect((await editor.boundingBox())!.height).toBeGreaterThan(1000);

  // The switch never moves: one press shows the other view, with no second header and nothing to close.
  const top = await switchTop(page);

  for (let round = 0; round < 2; round++) {
    // By pointer first, then by the keyboard alone (Enter on the switch, Esc in the source).
    if (round === 0) await switchButton(page, 'Zdroj').click();
    else { await switchButton(page, 'Zdroj').focus(); await page.keyboard.press('Enter'); }
    await sourceShown(page, top);
    if (round === 0) await switchButton(page, 'Dokument').click();
    else await page.keyboard.press('Escape');
    await documentFlowsWithPage(page);
    expect(await switchTop(page)).toBe(top);
    // Back in the text: the same live editor, with the caret in it.
    expect(await editor.evaluate((element, before) => element === before, originalEditor)).toBe(true);
    await expect(editor).toBeFocused();
    expect(await editor.textContent() === originalText).toBe(true);

    // Wheel over the article must scroll the host, not a leftover fixed-height pane.
    await editor.locator('p').first().scrollIntoViewIfNeeded();
    const before = await page.evaluate(() => window.scrollY);
    const bounds = (await editor.locator('p').first().boundingBox())!;
    await page.mouse.move(bounds.x + 30, bounds.y + 10);
    await page.mouse.wheel(0, 250);
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(before);
    await page.evaluate(() => window.scrollTo(0, 0));
  }

  // Leaving editing directly from the source must also show the document again.
  await switchButton(page, 'Zdroj').click();
  await sourceShown(page, top);
  await setLocked(page, true);
  await expect(page.locator('.source-pane')).toHaveCount(0);
  await expect(page.locator('.document-pane')).toBeVisible();
  expect(await editor.textContent() === originalText).toBe(true);
});
