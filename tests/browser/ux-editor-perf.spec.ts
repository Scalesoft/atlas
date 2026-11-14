// UX review cycle 2, area editor-perf (findings EP2-n).
// `ATLAS_URL=http://localhost:<vite> ATLAS_UX_DEV_LOGIN=1 npx playwright test &`. Own pages go to the space
// „UX editor-perf“ and to the trash at the end. ATLAS_REAL=1 adds the typing budget over the biggest real pages: run it against a
// COPY of the real database (it types and discards on real pages), never the shared instance.
import { expect, test, type Page } from '@playwright/test';
import { LIVE_EDITOR } from "./editing";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.describe.configure({ timeout: 180_000 });
test.skip(process.env.ATLAS_UX_DEV_LOGIN !== '1', 'Needs a Development instance with the developer sign-in (ATLAS_UX_DEV_LOGIN=1).');

const created: string[] = [];

async function signIn(page: Page) {
  await page.goto('/');
  await page.getByRole('button', { name: 'Přihlásit jako vývojář' }).click();
  const onboarding = page.getByRole('button', { name: 'Otevřít wiki' });
  const search = page.getByRole('button', { name: /^Hledat ve wiki/ }).first();
  await expect(onboarding.or(search)).toBeVisible();
  if (await onboarding.isVisible()) await onboarding.click();
  await expect(search).toBeVisible();
}

async function send(page: Page, method: 'post' | 'put', path: string, data?: unknown) {
  const csrf = await (await page.context().request.get('/api/auth/csrf')).json();
  return page.context().request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
}

async function json(page: Page, method: 'post' | 'put', path: string, data?: unknown) {
  const response = await send(page, method, path, data);
  expect(response.status(), await response.text()).toBeLessThan(300);
  return response.json();
}

const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] });
const code = (text: string) => ({ type: 'codeBlock', attrs: { language: 'typescript' }, content: [{ type: 'text', text }] });

async function newPage(page: Page, name: string, content: unknown[]) {
  const spaces = await (await page.context().request.get('/api/spaces')).json() as { id: string; name: string }[];
  const space = spaces.find(item => item.name === 'UX editor-perf') ?? await json(page, 'post', '/api/spaces', { name: 'UX editor-perf', icon: 'code-xml', color: 'blue' });
  const { id } = await json(page, 'post', '/api/pages', { spaceId: space.id, parentId: null, title: `UX-editor-perf ${name} ${Date.now()}`, markdown: '',
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content } }) });
  created.push(id);
  return id as string;
}

const liveEditor = (page: Page) => page.locator(LIVE_EDITOR);

/** Enter in the page's focused text; resolves once the collaborative document is synchronized and holds the caret. */
async function edit(page: Page) {
  await expect(page.locator('[data-edit-lock]')).toBeVisible();
  await page.locator('.document-body .ProseMirror:not(.editor-hydrating *)').first().focus();
  await page.keyboard.press('Enter');
  await expect(liveEditor(page)).toBeFocused({ timeout: 60_000 });
  await expect(page.locator('.editor-hydrating')).toHaveCount(0, { timeout: 60_000 });
  await expect(page.getByText('Čekáme na synchronizaci')).toHaveCount(0);
}

/** Leaves the page (no question is asked; the edits save by themselves). */
async function discard(page: Page) {
  await page.evaluate(() => { location.hash = '#/'; });
}

test.afterAll(async ({ browser }) => {
  if (!created.length) return;
  const page = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  await signIn(page);
  for (const id of created) {
    const { spaceId, version } = await (await page.context().request.get(`/api/pages/${id}`)).json();
    const tree = await (await page.context().request.get(`/api/spaces/${spaceId}/tree`)).json();
    await send(page, 'post', `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: version });
  }
  await page.close();
});

test('EP2-6: Enter right after opening a page edits it even when code highlighting arrives late', async ({ page }) => {
  await signIn(page);
  const id = await newPage(page, 'pozdní zvýraznění', [paragraph('Úvod'), code('const value = 1;')]);
  // A fresh tab loads the highlighter with the editor chunk; held back, it arrives after E (as on a slow connection).
  const tab = await page.context().newPage();
  await tab.route(/lowlight-/, async route => { await new Promise(resolve => setTimeout(resolve, 2_500)); await route.continue(); });
  await tab.goto(`/#/pages/${id}`);
  await expect(tab.locator('[data-edit-lock]')).toBeVisible();
  await tab.locator('.document-body .ProseMirror:not(.editor-hydrating *)').first().focus();
  await tab.keyboard.press('Enter');
  await expect(liveEditor(tab)).toBeVisible({ timeout: 30_000 });
  await expect(tab.getByText('Dokument se nepodařilo otevřít')).toHaveCount(0);
  await expect(tab.locator('.tt-code-block .hljs-keyword').first()).toBeVisible();
  await tab.keyboard.type('x');
  await expect(tab.locator('[data-page-save-state]')).toBeVisible({ timeout: 15_000 });
  await discard(tab);
  await tab.close();
});

test('EP2-9: typing right after opening saves by itself', async ({ page }) => {
  await signIn(page);
  const id = await newPage(page, 'ctrl s', [paragraph('Začátek')]);
  await page.goto(`/#/pages/${id}`);
  await edit(page);
  await page.keyboard.press('End');
  const typed = ` uloženo-${Date.now()}`;
  await page.keyboard.type(typed);
  await expect.poll(async () => (await (await page.context().request.get(`/api/pages/${id}`)).json()).contentJson, { timeout: 15_000 }).toContain(typed);
  await expect(liveEditor(page)).toBeFocused();
});

test('ED-13 + ED-14: Zdroj right after typing waits for the sync quietly and puts the caret into the source', async ({ page }) => {
  await signIn(page);
  const id = await newPage(page, 'zdroj', [paragraph('Začátek')]);
  await page.goto(`/#/pages/${id}`);
  await edit(page);
  await page.keyboard.press('End');
  await page.keyboard.type(' a hned zdroj');
  await page.getByRole('button', { name: 'Zdroj', exact: true }).click();
  await expect(page.locator('.md-source-editor')).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('.md-source-editor')), { timeout: 30_000 }).toBe(true);
  await expect(page.getByText('Počkejte na připojení')).toHaveCount(0);
  await expect(page.locator('.md-source-editor')).toContainText('a hned zdroj');
});

test('EP2-8: publishing outside editing is refused over a closed window’s unsaved live edits', async ({ page, browser }) => {
  await signIn(page);
  const id = await newPage(page, 'živé úpravy', [paragraph('Katalog')]);
  const first = await (await page.context().request.get(`/api/pages/${id}`)).json();
  await json(page, 'post', `/api/pages/${id}/publish`, { expectedVersion: first.version });
  // A saved draft to publish, before the live edits (a save without a session resets the shared log).
  const saved = await json(page, 'put', `/api/pages/${id}`, { title: first.title, contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [paragraph('Katalog v2')] } }), markdown: 'Katalog v2', expectedVersion: first.version });
  // Another window edits and closes without saving: its edits live only in the shared log.
  const other = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const tab = await other.newPage();
  await signIn(tab);
  await tab.goto(`/#/pages/${id}`);
  await edit(tab);
  await tab.keyboard.press('End');
  await tab.keyboard.type(' závislosti a směry');
  await expect(tab.locator('[data-page-save-state]')).toBeVisible({ timeout: 15_000 });
  await other.close();
  // Publikovat změny from reading, as the web sends it.
  const refused = await send(page, 'post', `/api/pages/${id}/publish`, { expectedVersion: saved.version, refuseUnsavedLiveEdits: true });
  expect(refused.status()).toBe(409);
  expect((await refused.json()).code).toBe('collaboration_unsaved');
});

test.describe('typing over the biggest real pages', () => {
  test.skip(process.env.ATLAS_REAL !== '1', 'Set ATLAS_REAL=1 and ATLAS_URL to an instance over a COPY of the real-data database.');

  // Median keydown-to-next-paint (Event Timing) over 22 keys typed at the end of the page. On this machine before cycle 2 the biggest page
  // (703 kB of JSON, 20 tables) took 104–136 ms a key and a 275 kB one 56–96 ms; after it 64–88 ms and 24–64 ms.
  const budget = (rank: number) => (rank === 0 ? 100 : 80);

  test('EP2-1…5: typing stays under the budget on the four biggest pages', async ({ page }) => {
    await signIn(page);
    const request = page.context().request;
    const spaces = await (await request.get('/api/spaces')).json() as { id: string }[];
    const ids: string[] = [];
    for (const space of spaces) {
      const tree = await (await request.get(`/api/spaces/${space.id}/tree`)).json() as { items?: { id: string; type?: string }[] };
      ids.push(...(tree.items ?? []).filter(item => !item.type || item.type === 'page').map(item => item.id));
    }
    const sizes: { id: string; bytes: number }[] = [];
    for (let start = 0; start < ids.length; start += 16) {
      await Promise.all(ids.slice(start, start + 16).map(async id => {
        const response = await request.get(`/api/pages/${id}`);
        expect(response.ok()).toBe(true);
        sizes.push({ id, bytes: ((await response.json()).contentJson as string).length });
      }));
    }
    const biggest = sizes.sort((a, b) => b.bytes - a.bytes).slice(0, 4);
    expect(biggest.length).toBe(4);
    const results: string[] = [];
    for (const [rank, { id, bytes }] of biggest.entries()) {
      await page.goto(`/#/pages/${id}`);
      await edit(page);
      await liveEditor(page).evaluate(element => {
        const editor = (element as unknown as { editor: { state: { doc: { content: { size: number } } }; commands: { setTextSelection(at: number): void }; view: { focus(): void } } }).editor;
        editor.commands.setTextSelection(editor.state.doc.content.size - 1);
        editor.view.focus();
      });
      await page.evaluate(() => {
        const w = window as unknown as { __latency: number[]; __observer?: PerformanceObserver };
        w.__latency = [];
        w.__observer?.disconnect();
        w.__observer = new PerformanceObserver(list => { for (const entry of list.getEntries()) if (entry.name === 'keydown') w.__latency.push(entry.duration); });
        w.__observer.observe({ type: 'event', durationThreshold: 16, buffered: false } as PerformanceObserverInit);
      });
      for (const key of 'abcdefghijklmnopqrstuv') { await page.keyboard.press(key); await page.waitForTimeout(120); }
      await page.waitForTimeout(500);
      const latency = (await page.evaluate(() => (window as unknown as { __latency: number[] }).__latency)).sort((a, b) => a - b);
      // Keys answered within one frame report no entry (the 16 ms threshold); they count as 16 ms.
      while (latency.length < 22) latency.unshift(16);
      const median = latency[Math.floor(latency.length / 2)]!;
      results.push(`${Math.round(bytes / 1000)} kB: median ${median} ms (budget ${budget(rank)})`);
      await discard(page);
      expect.soft(median, results.at(-1)).toBeLessThanOrEqual(budget(rank));
    }
    test.info().annotations.push({ type: 'typing', description: results.join('; ') });
    console.log(results.join('\n'));
  });
});
