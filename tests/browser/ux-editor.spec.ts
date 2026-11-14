// UX review cycle 1, area editor (findings ED-n). Keyboard-only
// journeys in the page editor against a Development instance: `ATLAS_URL=http://localhost:<vite> npx playwright test ux-editor`.
// Pages go to the space „UX editor“ with titles „UX-editor …“; the run moves them to the trash at the end.
import { expect, test, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { startEditing, PAGE_TEXT, LIVE_EDITOR } from './editing';

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.describe.configure({ timeout: 120_000 });

const created: string[] = [];

async function signIn(page: Page) {
  await page.goto('/');
  const developer = page.getByRole('button', { name: 'Přihlásit jako vývojář' });
  // Only a Development instance offers the developer sign-in.
  test.skip(!(await developer.waitFor({ timeout: 10_000 }).then(() => true, () => false)), 'Needs a Development instance with the developer sign-in.');
  await developer.click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ }).first()).toBeVisible();
}

async function send(context: BrowserContext, method: 'post', path: string, data?: unknown) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const response = await context.request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
  expect(response.status(), await response.text()).toBeLessThan(300);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}

/** A new empty page in „UX editor“, opened for editing with the caret in its text. */
async function editNewPage(page: Page, name: string, content: unknown[] = [{ type: 'paragraph' }], plugins: unknown[] = [], focused = true) {
  const context = page.context();
  const spaces = await (await context.request.get('/api/spaces')).json() as { id: string; name: string }[];
  const space = spaces.find(item => item.name === 'UX editor') ?? await send(context, 'post', '/api/spaces', { name: 'UX editor', icon: 'code-xml', color: 'blue' });
  const { id } = await send(context, 'post', '/api/pages', { spaceId: space.id, parentId: null, title: `UX-editor ${name} ${Date.now()}`, markdown: '',
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, ...plugins.length ? { plugins } : {}, document: { type: 'doc', content } }) });
  created.push(id);
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator('[data-edit-lock]')).toBeVisible();
  if (focused) { await page.locator('.document-body .ProseMirror:not(.editor-hydrating *)').first().focus(); await page.keyboard.press('Enter'); } else await startEditing(page);
  const editor = page.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]');
  if (focused) await expect(editor).toBeFocused(); else await expect(editor).toBeVisible({ timeout: 20_000 });
  // The collaborative document must be synchronized before typing reaches it.
  await expect(page.getByText('Čekáme na synchronizaci')).toHaveCount(0);
  return editor;
}

type Probe = { selection: string; node?: string; blocks: string[] };
const probe = (editor: Locator) => editor.evaluate((element): Probe => {
  const editor = (element as unknown as { editor: { state: { selection: { constructor: { name: string }; node?: { type: { name: string } } }; doc: { content: { content: { type: { name: string }; textContent: string }[] } } } } }).editor;
  const { selection, doc } = editor.state;
  return { selection: selection.constructor.name, node: selection.node?.type.name,
    blocks: doc.content.content.map(node => node.textContent || node.type.name) };
});

async function slash(page: Page, query: string) {
  await page.keyboard.type(`/${query}`);
  const menu = page.getByTestId('slash-menu');
  await expect(menu).toBeVisible();
  return menu;
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

test('ED-1: the page source opens its Monaco editor (the editor dist keeps worker options a host Vite can rebundle)', async ({ page }) => {
  await signIn(page);
  const failed: string[] = [];
  page.on('response', response => { if (response.status() >= 500 && /sourceEditor|monacoSetup/.test(response.url())) failed.push(response.url()); });
  const editor = await editNewPage(page, 'zdroj');
  await page.keyboard.type('Ahoj');
  await page.getByRole('button', { name: 'Zdroj', exact: true }).click();
  await expect(page.locator('.monaco-editor').first()).toBeVisible();
  expect(failed).toEqual([]);
  await expect(editor).toBeAttached();
});

test('ED-2: block keys move, duplicate and delete a selected component (a chart) like a paragraph', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'bloky');
  await page.keyboard.type('Nahoře');
  await page.keyboard.press('Enter');
  const menu = await slash(page, 'graf');
  await expect(menu.getByRole('option').first()).toContainText('Graf');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await probe(editor)).node).toBe('chart');
  await page.keyboard.press('Control+Shift+ArrowUp');
  await expect.poll(async () => (await probe(editor)).blocks.slice(0, 2)).toEqual(['chart', 'Nahoře']);
  expect((await probe(editor)).node).toBe('chart');
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await expect.poll(async () => (await probe(editor)).blocks.slice(0, 3)).toEqual(['chart', 'chart', 'Nahoře']);
  await page.keyboard.press('Control+Shift+Backspace');
  await expect.poll(async () => (await probe(editor)).blocks.slice(0, 2)).toEqual(['chart', 'Nahoře']);
});

test('ED-3: Escape, then Tab leaves the editor from a list item too, as the help says', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'seznam');
  await page.keyboard.type('- jedna');
  await page.keyboard.press('Enter');
  await page.keyboard.type('dvě');
  await page.keyboard.press('Escape');
  await page.keyboard.press('Tab');
  await expect(editor).not.toBeFocused();
  // The item was not nested.
  await expect(editor.locator('ul ul')).toHaveCount(0);
});

test('ED-4: Ctrl+Shift+digit sets a heading on any layout and the help names those keys', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'nadpisy');
  await page.keyboard.type('Nadpis');
  await page.keyboard.press('Control+Shift+Digit2');
  await expect(editor.locator('h2')).toHaveText('Nadpis');
  await page.keyboard.press('Control+Shift+Digit0');
  await expect(editor.locator('h2')).toHaveCount(0);
  await page.keyboard.press('Control+/');
  const help = page.getByRole('dialog', { name: 'Klávesové zkratky' });
  await expect(help).toContainText('Ctrl Shift 2');
  await expect(help).not.toContainText('Ctrl Alt 2');
  await page.keyboard.press('Escape');
  await expect(editor).toBeFocused();
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');
  const menu = await slash(page, 'nadpis 3');
  await expect(menu.getByRole('option').first()).toContainText('Ctrl+Shift+3');
});

test('ED-5: typing a slash item\'s whole name is no search argument („/úkol z youtracku“)', async ({ page }) => {
  await signIn(page);
  await editNewPage(page, 'youtrack');
  const menu = await slash(page, 'úkol z youtracku');
  const first = menu.getByRole('option').first();
  test.skip(!(await first.textContent())?.includes('YouTrack'), 'The YouTrack module is not enabled here.');
  await expect(first).not.toContainText('vyhledat');
});

test('ED-6: every block of the slash menu says what it is for', async ({ page }) => {
  await signIn(page);
  await editNewPage(page, 'popisy');
  for (const [query, label] of [['graf', 'Graf'], ['diagram', 'Diagram'], ['api', 'API'], ['matematick', 'Matematický vzorec'], ['obsah dok', 'Obsah dokumentu']]) {
    const menu = await slash(page, query);
    const row = menu.getByRole('option').filter({ hasText: label }).first();
    await expect(row).toBeVisible();
    // Title and a second line (description) under it.
    expect((await row.innerText()).split('\n').filter(line => line.trim()).length, label).toBeGreaterThan(1);
    await page.keyboard.press('Escape');
    for (let index = 0; index <= query.length; index++) await page.keyboard.press('Backspace');
  }
});

test('ED-8: the selection toolbar stays over the page text, not over the sidebar', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'bublina');
  await page.keyboard.type('Krátký výběr');
  await page.keyboard.press('Shift+Home');
  const bubble = page.getByRole('group', { name: 'Formátování textu' });
  await expect(bubble).toBeVisible();
  await expect.poll(async () => Math.round((await bubble.boundingBox())!.x)).toBeGreaterThanOrEqual(Math.round((await page.locator('.md-editor').first().boundingBox())!.x));
  await expect(editor).toBeFocused();
});

test('ED-9: inline arguments pick the kind of a box, a chart and a diagram („/box tip“, „/graf koláč“, „/diagram sekvence“)', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'argumenty');
  const json = () => editor.evaluate(element => JSON.stringify((element as unknown as { editor: { getJSON(): unknown } }).editor.getJSON()));
  for (const [query, row, inserted] of [['box tip', 'Informační box: Tip', '"variant":"tip"'], ['graf koláč', 'Graf: Koláčový', '"kind":"pie"'],
    ['diagram sekvence', 'Sekvenční diagram', '"source":"sequenceDiagram']]) {
    const menu = await slash(page, query);
    await expect(menu.getByRole('option').first()).toContainText(row);
    await page.keyboard.press('Enter');
    await expect.poll(json).toContain(inserted);
    await page.keyboard.press('Control+a');
    await page.keyboard.press('Backspace');
    await expect.poll(json).not.toContain(inserted);
  }
});

test('ED-10 + PR-9: Escape leaves a new component for the text after it; Tab stays inside its edit panel', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'komponenta');
  await slash(page, 'graf');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await probe(editor)).node).toBe('chart');
  await page.keyboard.press('Escape');
  await expect.poll(async () => (await probe(editor)).selection).toBe('TextSelection');
  await page.keyboard.type('Pod grafem');
  await expect.poll(async () => (await probe(editor)).blocks).toEqual(['chart', 'Pod grafem']);
  await page.keyboard.press('Home');
  await page.keyboard.press('ArrowUp');
  await expect.poll(async () => (await probe(editor)).node).toBe('chart');
  await page.keyboard.press('Enter'); // the selected chart opens its panel in place
  const panel = page.getByRole('dialog');
  await expect(panel).toBeVisible();
  await page.keyboard.press('Shift+Tab');
  await expect.poll(() => panel.evaluate(element => element.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Tab');
  await expect.poll(() => panel.evaluate(element => element.contains(document.activeElement))).toBe(true);
});

test('ED-7: Escape from a component\'s workspace (API) returns focus to the document, not to the page body', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'api');
  await page.keyboard.type('Nahoře');
  await page.keyboard.press('Enter');
  await slash(page, 'api');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await probe(editor)).node).toBe('openapi');
  await page.keyboard.press('Enter'); // the selected block opens its YAML view in place
  await expect(page.getByRole('tab', { name: 'YAML' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(editor).toBeFocused();
});

test('ED-16: Escape, then Tab leaves past the whole editor, not to a table inside the text', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'odchod');
  await page.keyboard.type('Nad tabulkou');
  await page.keyboard.press('Enter');
  await slash(page, 'tabulka 2x2');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Control+Home');
  await page.keyboard.press('Escape');
  await page.keyboard.press('Tab');
  await expect(editor).not.toBeFocused();
  expect(await editor.evaluate(element => element.contains(document.activeElement))).toBe(false);
});

test('ED-17: editing a page that starts with a component puts the caret in the text, not on the component', async ({ page }) => {
  await signIn(page);
  const chart = { type: 'chart', attrs: { data: { version: 1, title: 'Graf', kind: 'bar', grid: [['Období', 'Hodnota'], ['Leden', '12']], source: null, sourceStatus: null } } };
  const editor = await editNewPage(page, 'zacatek', [chart, { type: 'paragraph', content: [{ type: 'text', text: 'Pod grafem' }] }], [{ id: 'charts', version: 1 }]);
  await expect.poll(async () => (await probe(editor)).selection).toBe('TextSelection');
  await page.keyboard.type('X');
  await expect.poll(async () => (await probe(editor)).blocks).toEqual(['chart', 'XPod grafem']);
});

// UX cycle 2 (EU2-n)
const gif = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';
const textOf = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] });

test('EU2-1: the page-link list is diacritics-insensitive and puts word-start hits first, the first one preselected', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now();
  const context = page.context();
  const spaces = await (await context.request.get('/api/spaces')).json() as { id: string; name: string }[];
  const space = spaces.find(item => item.name === 'UX editor') ?? await send(context, 'post', '/api/spaces', { name: 'UX editor', icon: 'code-xml', color: 'blue' });
  for (const title of [`Žabka ${stamp}`, `Příručka lékaře UX ${stamp}`]) {
    const { id } = await send(context, 'post', '/api/pages', { spaceId: space.id, parentId: null, title, markdown: '',
      contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } }) });
    created.push(id);
  }
  await editNewPage(page, 'odkaz');
  await page.keyboard.type(`[[ZABKA ${stamp}`);
  const options = page.getByRole('option');
  await expect(options.first()).toBeVisible();
  await expect(options.first()).toContainText('Žabka');
  await expect(options.first()).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  await expect(page.locator(`${PAGE_TEXT} a, ${PAGE_TEXT} [data-page-id]`).filter({ hasText: 'Žabka' }).first()).toBeVisible();
});

test('EU2-2: image size and alignment apply on the click, without „Použít“', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'obrazek', [{ type: 'image', attrs: { src: gif, alt: 'Bod' } }, { type: 'paragraph' }]);
  await editor.locator('img').first().click();
  await page.getByRole('button', { name: 'Velikost a zarovnání' }).click();
  const panel = page.locator('.md-image-panel-layout');
  await expect(panel.getByRole('button', { name: 'Použít' })).toHaveCount(0);
  await panel.getByRole('button', { name: '25 %' }).click();
  await expect.poll(() => editor.locator('.md-image-viewport').first().evaluate(element => (element as HTMLElement).style.width)).toBe('25%');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'Na střed' }).click();
  await expect(panel.getByRole('button', { name: 'Na střed' })).toHaveAttribute('aria-pressed', 'true');
});

test('EU2-3: the toolbar of an image or a code block hides no line of the text above it', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'lista', [textOf('Text nad komponentami'), { type: 'image', attrs: { src: gif, alt: 'Bod' } },
    textOf('Mezi nimi'), { type: 'codeBlock', attrs: { language: 'json' }, content: [{ type: 'text', text: '{ "a": 1 }' }] }]);
  const overlaps = async (above: string) => {
    const toolbar = page.locator('.md-context-toolbar:not([hidden])').first();
    await expect(toolbar).toBeVisible();
    const [bar, text] = [await toolbar.boundingBox(), await editor.getByText(above, { exact: true }).boundingBox()];
    return !!bar && !!text && bar.y < text.y + text.height && bar.y + bar.height > text.y;
  };
  await editor.locator('img').first().click();
  expect(await overlaps('Text nad komponentami')).toBe(false);
  await editor.locator('pre').first().click();
  expect(await overlaps('Mezi nimi')).toBe(false);
});

// UX review cycle 5 (ED5-n).
const cell = (text: string) => ({ type: 'tableCell', content: [textOf(text)] });
const tableOf = (rows: number) => ({ type: 'table', content: Array.from({ length: rows }, (_, row) => ({ type: 'tableRow', content: [cell(`A${row}`), cell(`B${row}`)] })) });

const docText = (editor: Locator) => editor.evaluate(element => (element as unknown as { editor: { state: { doc: { textContent: string } } } }).editor.state.doc.textContent);

test('ED5-4: two writers on one page see each other, and Zpět takes back only your own typing', async ({ page, browser }) => {
  await signIn(page);
  const editorA = await editNewPage(page, 'spoluprace', [textOf('Základ')]);
  const other = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const pageB = await other.newPage();
  await signIn(pageB);
  await pageB.goto(page.url());
  await startEditing(pageB);
  const editorB = pageB.locator(LIVE_EDITOR);
  await expect(editorB).toBeVisible();
  await expect(pageB.getByText('Čekáme na synchronizaci')).toHaveCount(0);
  await expect.poll(() => docText(editorB)).toContain('Základ');
  await editorA.getByText('Základ').click();
  await page.keyboard.press('End');
  await page.keyboard.type(' Alfa');
  await expect.poll(() => docText(editorB)).toContain('Základ Alfa');
  await editorB.getByText('Základ').click();
  await pageB.keyboard.press('Control+Home');
  await pageB.keyboard.type('Beta ');
  await expect.poll(() => docText(editorA)).toContain('Beta Základ Alfa');
  await page.keyboard.press('Control+z');
  await expect.poll(() => docText(editorA)).toContain('Beta Základ');
  await expect.poll(() => docText(editorA)).not.toContain('Alfa');
  await expect.poll(() => docText(editorB)).not.toContain('Alfa');
  await expect.poll(() => docText(editorB)).toContain('Beta Základ');
  await other.close();
});

test('ED5-3 (PG4-8): the table toolbar of a page that opens with a long table stays off the page header at 390 px', async ({ page }) => {
  await signIn(page);
  await page.setViewportSize({ width: 390, height: 844 });
  const rows = Array.from({ length: 40 }, (_, row) => ({ type: 'tableRow', content: [cell(`A${row}`), cell(`B${row}`), cell(`C${row}`)] }));
  const editor = await editNewPage(page, 'dlouha', [{ type: 'table', content: rows }], [], false);
  await editor.locator('td').nth(4).click();
  const bar = page.getByTestId('table-menu');
  await expect(bar).toBeVisible();
  const [box, tags] = [await bar.boundingBox(), await page.getByText('Přidat tagy').boundingBox()];
  expect(box && tags && box.y < tags.y + tags.height && box.y + box.height > tags.y, `${JSON.stringify(box)} over ${JSON.stringify(tags)}`).toBe(false);
});

test('ED5-2: typing in a paragraph does not rebuild the rails of the tables on the page', async ({ page }) => {
  await signIn(page);
  const editor = await editNewPage(page, 'koleje', [textOf('Nahoře'), ...Array.from({ length: 6 }, () => tableOf(3)), textOf('Dole')]);
  await expect(page.getByTestId('table-controls').first()).toBeAttached();
  await editor.getByText('Nahoře', { exact: true }).click();
  await page.keyboard.press('End');
  await page.waitForTimeout(600);
  // Every rail rebuild rewrites the chips under .tt-table-controls; typing text elsewhere must not.
  await page.evaluate(() => {
    const w = window as unknown as { __rails: number };
    w.__rails = 0;
    const observer = new MutationObserver(records => { w.__rails += records.length; });
    document.querySelectorAll('.tt-table-controls').forEach(element => observer.observe(element, { childList: true, subtree: true, attributes: true }));
  });
  await page.keyboard.type('abcdef', { delay: 40 });
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => (window as unknown as { __rails: number }).__rails)).toBe(0);
});
