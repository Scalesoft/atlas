// UX review cycle 4, area page (findings PG4-n): the page editor, its header
// and the Projector blocks in it. `ATLAS_URL=http://localhost:<vite> ATLAS_UX_DEV_LOGIN=1 npx playwright test ux-page`.
// Pages go to the space „UX page“ with titles „UX-page …“; the run moves them to the trash at the end.
import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { startEditing } from './editing';

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.describe.configure({ timeout: 120_000 });

const created: string[] = [];
const rich = (content: unknown[], plugins: unknown[] = []) =>
  JSON.stringify({ format: 'atlas-rich', version: 1, ...plugins.length ? { plugins } : {}, document: { type: 'doc', content } });
const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] });

async function signIn(page: Page) {
  await page.goto('/');
  const developer = page.getByRole('button', { name: 'Přihlásit jako vývojář' });
  test.skip(!(await developer.waitFor({ timeout: 10_000 }).then(() => true, () => false)), 'Needs a Development instance with the developer sign-in.');
  await developer.click();
  await expect(page.locator('.workspace h1').first()).toBeAttached();
}

async function send(context: BrowserContext, method: 'post', path: string, data?: unknown) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const response = await context.request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
  expect(response.status(), await response.text()).toBeLessThan(300);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}

async function createPage(context: BrowserContext, title: string, contentJson = rich([{ type: 'paragraph' }])): Promise<string> {
  const spaces = await (await context.request.get('/api/spaces')).json() as { id: string; name: string }[];
  const space = spaces.find(item => item.name === 'UX page') ?? await send(context, 'post', '/api/spaces', { name: 'UX page', icon: 'pen-line', color: 'blue' });
  const { id } = await send(context, 'post', '/api/pages', { spaceId: space.id, parentId: null, title, markdown: '', contentJson });
  created.push(id);
  return id;
}

async function edit(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator('[data-edit-lock]')).toBeVisible();
  await page.locator('.document-body .ProseMirror:not(.editor-hydrating *)').first().focus();
  await page.keyboard.press('Enter');
  const editor = page.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]');
  await expect(editor).toBeFocused();
  await expect(page.getByText('Čekáme na synchronizaci')).toHaveCount(0);
  return editor;
}

test.afterAll(async ({ browser }) => {
  if (!created.length) return;
  const page = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  await signIn(page);
  const request = page.context().request;
  for (const id of created) {
    const { spaceId, version } = await (await request.get(`/api/pages/${id}`)).json();
    const tree = await (await request.get(`/api/spaces/${spaceId}/tree`)).json();
    await send(page.context(), 'post', `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: version });
  }
  await page.close();
});

test('PG4-1: the toolbar link field finds wiki pages, the first one preselected and Enter links it; an address stays an address', async ({ page }) => {
  await signIn(page);
  const word = `Kvazar${Date.now().toString(36)}`;
  const target = await createPage(page.context(), `UX-page ${word} cíl`);
  const id = await createPage(page.context(), `UX-page odkazy ${Date.now()}`, rich([paragraph('Viz modul a web.')]));
  const editor = await edit(page, id);
  const link = page.getByRole('toolbar', { name: 'Formátování' }).getByRole('button', { name: 'Odkaz', exact: true });

  await editor.getByText('Viz modul a web.').dblclick({ position: { x: 40, y: 8 } });
  await link.click();
  const field = page.getByRole('combobox', { name: 'Stránka nebo adresa' });
  await expect(field).toBeFocused();
  await field.fill(word.toLowerCase());
  const first = page.getByRole('listbox', { name: 'Stránky wiki' }).getByRole('option').first();
  await expect(first).toContainText(`UX-page ${word} cíl`);
  await expect(first).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  await expect(editor.locator(`a[href="#/pages/${target}"]`)).toHaveText('modul');
  await expect(editor).toBeFocused();

  await page.keyboard.press('End');
  await page.keyboard.press('ArrowLeft');
  for (let step = 0; step < 3; step++) await page.keyboard.press('Shift+ArrowLeft');
  await link.click();
  await field.fill('https://example.com/a');
  await expect(page.getByRole('listbox', { name: 'Stránky wiki' })).toHaveCount(0);
  await page.keyboard.press('Enter');
  await expect(editor.locator('a[href="https://example.com/a"]')).toHaveText('web');
});

test('PG4-1: from a keyboard selection, Alt+F10 enters the selection bubble and its Odkaz opens the page search', async ({ page }) => {
  await signIn(page);
  const word = `Pulsar${Date.now().toString(36)}`;
  const target = await createPage(page.context(), `UX-page ${word} cíl`);
  const id = await createPage(page.context(), `UX-page bublina ${Date.now()}`, rich([paragraph('Návod')]));
  const editor = await edit(page, id);
  await page.keyboard.press('Home');
  await page.keyboard.press('Shift+End');
  const bubble = page.getByRole('group', { name: 'Formátování textu' });
  await expect(bubble).toBeVisible();
  await page.keyboard.press('Alt+F10');
  await expect(bubble.getByRole('button').first()).toBeFocused();
  // Arrows move inside the bubble, as in the editor's context toolbars.
  for (let step = 0; step < 12 && !(await bubble.getByRole('button', { name: 'Odkaz', exact: true }).evaluate(button => button === document.activeElement)); step++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Enter');
  await page.keyboard.type(word);
  await expect(page.getByRole('listbox', { name: 'Stránky wiki' }).getByRole('option').first()).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  await expect(editor.locator(`a[href="#/pages/${target}"]`)).toHaveText('Návod');
  // Esc from the bubble returns to the text with the selection kept.
  await page.keyboard.press('Shift+Home');
  await expect(bubble).toBeVisible();
  await page.keyboard.press('Alt+F10');
  await expect(bubble.getByRole('button').first()).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(editor).toBeFocused();
});

test('PG4-4: a pasted board link becomes the board name with the board icon, as a page link does', async ({ page }) => {
  await signIn(page);
  const context = page.context();
  const id = await createPage(context, `UX-page tabule ${Date.now()}`);
  const { spaceId } = await (await context.request.get(`/api/pages/${id}`)).json();
  const board = await send(context, 'post', `/api/spaces/${spaceId}/boards`, { title: `UX-page tabule ${Date.now()}` });
  try {
    const editor = await edit(page, id);
    const url = new URL(`/#/boards/${board.id}`, page.url()).href;
    await editor.evaluate((element, text) => {
      const data = new DataTransfer();
      data.setData('text/plain', text);
      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }, url);
    await page.getByRole('menuitem', { name: /^Převést na/ }).click();
    await page.getByRole('button', { name: 'Použít' }).click();
    const link = editor.locator(`a[href="#/boards/${board.id}"]`);
    await expect(link).toHaveText(board.title);
    await expect(link.locator('svg[data-icon="layout-dashboard"]')).toBeVisible();
  } finally {
    const csrf = await (await context.request.get('/api/auth/csrf')).json();
    await context.request.delete(`/api/boards/${board.id}`, { headers: { 'X-CSRF-TOKEN': csrf.token } });
  }
});

test('PG4-6: after inserting an image from the slash menu the writer types on below it, the image stays', async ({ page }) => {
  // Listening from the start: the file chooser interception must be on before Enter, or headless Chromium cancels the
  // picker at once (a listener added just before Enter lost that race in ~1 run of 2).
  page.on('filechooser', chooser => void chooser.setFiles({ name: 'tečka.png', mimeType: 'image/png',
    buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/eL9GtQAAAABJRU5ErkJggg==', 'base64') }));
  await signIn(page);
  const editor = await edit(page, await createPage(page.context(), `UX-page obrázek ${Date.now()}`, rich([paragraph('Úvod'), { type: 'paragraph' }])));
  await page.keyboard.press('Control+End');
  await page.keyboard.type('/obrázek');
  await expect(page.getByTestId('slash-menu').getByRole('option').first()).toContainText('Obrázek');
  await page.keyboard.press('Enter');
  await expect(editor.locator('img')).toHaveCount(1);
  await page.keyboard.type('Pokračuji');
  await expect(editor.locator('img')).toHaveCount(1);
  await expect(editor.locator('p', { hasText: 'Pokračuji' })).toBeVisible();
});

test('PG4-5: on a phone the comment field does not promise Ctrl+Enter', async ({ browser }) => {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
  const page = await context.newPage();
  await signIn(page);
  const id = await createPage(context, `UX-page komentáře ${Date.now()}`, rich([paragraph('Text')]));
  await page.goto(`/#/pages/${id}`);
  const composer = page.locator('[data-comment-composer]');
  await composer.scrollIntoViewIfNeeded();
  await expect(composer).toBeVisible();
  await expect(composer.getByText('Ctrl+Enter odešle')).toBeHidden();
  // PG4-7: nor does the editor's footer promise Alt+F10.
  await startEditing(page);
  await expect(page.locator('.paper-footer .md-keyboard-hint')).toBeHidden();
  await context.close();
});
