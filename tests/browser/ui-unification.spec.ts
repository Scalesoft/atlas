import { test, expect, type Page } from '@playwright/test';
import { startEditing, PAGE_TEXT } from './editing';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Acceptance regressions for the UI unification (Task 4.2)
// and docs/ui-unification-audit-2026-09-07.md §6 ("Chování"). These run against whatever
// wiki instance ATLAS_URL points at (default https://localhost:7101) using the plain
// `test`/`test` development account, or any account supplied via env — never the
// synthetic `browser-admin` account the other specs in this directory provision, and
// never a hard-coded password.
//
// The whole file logs in exactly once (test.beforeAll below) and shares that session via
// storageState, to stay well under the ten-logins-per-minute limit the builtin auth
// guard enforces (see README.md "Pořadí průchodů").
const ATLAS_USER = process.env.ATLAS_E2E_USER;
const ATLAS_PASSWORD = process.env.ATLAS_E2E_PASSWORD;
const STORAGE_STATE = path.join(__dirname, '..', '..', 'artifacts', 'ui-audit-2026-09-07', 'ui-unification-state.json');

// test.use with trace/screenshot/video must stay top-level (Playwright forces a new
// worker otherwise), so this file skips itself outright rather than nesting the skip
// inside a describe block.
test.skip(!ATLAS_USER || !ATLAS_PASSWORD, 'Requires ATLAS_E2E_USER and ATLAS_E2E_PASSWORD.');
// Unlike the acceptance environment the other specs in this directory target (real,
// trusted certificate), the local dev instance this file is meant for serves the
// ASP.NET Core dev-certificate, which the OS/browser trusts but Node's own TLS
// validation (used by context.request) does not — hence ignoreHTTPSErrors here.
test.use({ trace: 'off', screenshot: 'off', video: 'off', storageState: STORAGE_STATE, ignoreHTTPSErrors: true });

test.describe('UI unification acceptance', () => {
  test.beforeAll(async ({ browser }) => {
    // Playwright applies the file's test.use() options (storageState included) to any
    // browser.newContext() made during a test/hook, so the very first run — before
    // STORAGE_STATE exists — must explicitly opt back out of it here.
    const context = await browser.newContext({ ignoreHTTPSErrors: true, storageState: undefined });
    const page = await context.newPage();
    await page.goto('/');
    // This context is always freshly created (storageState explicitly cleared above),
    // so the login form is always the first thing to render — no need to branch on it,
    // and branching on a same-tick `.count()` would race the SPA's own bootstrap.
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(ATLAS_USER!);
    await page.getByLabel('Heslo', { exact: true }).fill(ATLAS_PASSWORD!);
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    await context.storageState({ path: STORAGE_STATE });
    await context.close();
  });

  // Pages created through the API below are test fixtures, not content anyone should
  // keep around: trash every one of them once the whole file is done (see afterAll),
  // rather than leaving them behind in the shared dev instance on every run.
  const createdPageIds: string[] = [];

  async function createDocument(page: Page, opts: { title: string; markdown?: string; publish?: boolean; content?: Array<Record<string, unknown>> }) {
    const csrf = await (await page.context().request.get('/api/auth/csrf')).json();
    const headers = { 'X-CSRF-TOKEN': csrf.token };
    const spaces = await (await page.context().request.get('/api/spaces')).json();
    const markdown = opts.markdown ?? `Obsah stránky ${opts.title}.`;
    const created = await page.context().request.post('/api/pages', {
      headers,
      data: {
        spaceId: spaces[0].id, parentId: null, title: opts.title,
        markdown,
        contentJson: JSON.stringify({
          format: 'atlas-rich', version: 1,
          document: { type: 'doc', content: opts.content ?? [{ type: 'paragraph', content: [{ type: 'text', text: markdown }] }] },
        }),
      },
    });
    expect(created.status()).toBe(200);
    const document = await created.json();
    createdPageIds.push(document.id);
    if (opts.publish) {
      const published = await page.context().request.post(`/api/pages/${document.id}/publish`, {
        headers, data: { expectedVersion: document.version },
      });
      expect(published.status()).toBe(200);
    }
    return document;
  }

  test.afterAll(async ({ browser }) => {
    // There is no hard-delete endpoint for a page (by design — see PageOrganizationEndpoints);
    // move each fixture to trash instead, the same action a user takes to remove one.
    // Re-fetch each page's current version and its space's current tree version right
    // before trashing it: TrashAsync is optimistic-concurrency-checked against both, and
    // earlier tests in this file may have edited the page or moved others around it.
    if (!createdPageIds.length) return;
    const context = await browser.newContext({ ignoreHTTPSErrors: true, storageState: STORAGE_STATE });
    try {
      const page = await context.newPage();
      const csrf = await (await page.context().request.get('/api/auth/csrf')).json();
      const headers = { 'X-CSRF-TOKEN': csrf.token };
      for (const id of createdPageIds) {
        const pageResponse = await page.context().request.get(`/api/pages/${id}`);
        if (!pageResponse.ok()) continue; // already gone (e.g. trashed by a test itself)
        const document = await pageResponse.json();
        const tree = await (await page.context().request.get(`/api/spaces/${document.spaceId}/tree`)).json();
        await page.context().request.post(`/api/pages/${id}/trash`, {
          headers, data: { includeSubtree: false, expectedTreeVersion: tree.version, expectedVersion: document.version },
        });
      }
    } finally {
      await context.close();
    }
  });

  test('closing overlays: an open "…" menu and the Historie dialog both close on route navigation', async ({ page }) => {
    const first = await createDocument(page, { title: `Přehled sekce ${Date.now()}`, publish: true });
    const second = await createDocument(page, { title: `Detail sekce ${Date.now()}`, publish: true });
    await page.goto(`/#/pages/${first.id}`);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();

    // The "…" menu (ActionDisclosure) is a plain, non-modal popover: open it, then click
    // a page in the sidebar tree — a route change — and confirm the menu does not
    // survive it.
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await expect(page.locator('[role=menu]').and(page.locator(':visible'))).toHaveCount(1);
    const treeLink = page.getByRole('treeitem', { name: second.title, exact: true });
    await expect(treeLink).toBeVisible();
    await treeLink.click();
    await expect(page).toHaveURL(new RegExp(second.id));
    await expect(page.locator('[role=menu]').and(page.locator(':visible'))).toHaveCount(0);

    // The Historie dialog is a native modal <dialog> (showModal()), which makes the rest
    // of the page inert — so the route change that must close it here is a browser-back
    // navigation (still a real hashchange, the event AppDialog listens for) rather than a
    // click on now-covered page content.
    // ActionDisclosure stamps role="menuitem" on its slotted buttons, so items inside it
    // are found by that role, not "button" (which still matches the trigger itself).
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Historie', exact: true }).click();
    await expect(page.locator('dialog[open]')).toHaveCount(1);
    await page.goBack();
    await expect(page.locator('dialog[open]')).toHaveCount(0);
  });

  test('mobile document with a wide table has no page-level horizontal scroll and no outline rail', async ({ page }) => {
    // Own the rich table fixture: isolated Podman instances intentionally have no demo seed pages.
    // Markdown alone is insufficient because this API also stores the editor's authoritative JSON.
    const headings = Array.from({ length: 4 }, (_, index) => ({
      type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: `Kapitola ${index + 1}` }],
    }));
    const table = { type: 'table', content: Array.from({ length: 4 }, (_, row) => ({
      type: 'tableRow', content: Array.from({ length: 7 }, (_, column) => ({
        type: row === 0 ? 'tableHeader' : 'tableCell', attrs: { colspan: 1, rowspan: 1, colwidth: [160] },
        content: [{ type: 'paragraph', content: [{ type: 'text', text: `Konfigurace ${row + 1}/${column + 1}` }] }],
      })),
    })) };
    const document = await createDocument(page, {
      title: `Široká tabulka ${Date.now()}`, publish: true,
      markdown: '| A | B | C | D | E | F | G |\n| --- | --- | --- | --- | --- | --- | --- |\n| 1 | 2 | 3 | 4 | 5 | 6 | 7 |',
      content: [headings[0]!, table, ...headings.slice(1)],
    });
    await page.goto(`/#/pages/${document.id}`);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
    const hasWideTable = await page.evaluate((selector) =>
      Array.from(document.querySelectorAll(selector)).some((t) => (t.querySelector('tr')?.children.length ?? 0) >= 7), `${PAGE_TEXT} table`);
    expect(hasWideTable, 'expected the fixture to render its >=7 column table').toBe(true);

    await page.setViewportSize({ width: 390, height: 844 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
    // A phone keeps the outline folded into the „Na této stránce“ row (PageOutline, board 13): no list of headings and
    // no page dock over the text until the reader asks for it.
    await expect(page.locator('.page-outline-list').and(page.locator(':visible'))).toHaveCount(0);
    await expect(page.getByRole('complementary', { name: 'Boční panel stránky', exact: true })).toBeHidden();
  });

  test('offline while editing shows exactly one connection notice and the offline save state', async ({ page, context }) => {
    const document = await createDocument(page, { title: `Nabídka offline ${Date.now()}` });
    await page.goto(`/#/pages/${document.id}`);
    await startEditing(page);
    // Dirty the page first so the offline save state below is caused by going offline, not by "nothing changed yet".
    await page.getByRole('textbox', { name: 'Název stránky', exact: true }).fill(document.title + ' – upraveno');

    await context.setOffline(true);
    try {
      const notice = page.getByText(/spojení|připojení/i).and(page.locator(':visible'));
      await expect(notice).toHaveCount(1, { timeout: 15_000 });
      await expect(page.locator('[data-page-save-state]')).toContainText('Offline, uloží se po připojení');
    } finally {
      await context.setOffline(false);
    }
    await expect(page.getByText(/spojení|připojení/i).and(page.locator(':visible'))).toHaveCount(0, { timeout: 30_000 });
  });

  test('an idle editing session for 40s reports no "Server timeout" console errors', async ({ page }) => {
    test.setTimeout(90_000);
    const document = await createDocument(page, { title: `Idle relace ${Date.now()}` });
    const serverTimeoutErrors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && message.text().includes('Server timeout')) serverTimeoutErrors.push(message.text());
    });
    page.on('pageerror', (error) => {
      if (error.message.includes('Server timeout')) serverTimeoutErrors.push(error.message);
    });
    await page.goto(`/#/pages/${document.id}`);
    await startEditing(page);
    // Real timeouts are 15s/30s SignalR defaults; 40s covers a full disconnect-reconnect
    // cycle at roughly twice the shortest interval without taking minutes in CI.
    await page.waitForTimeout(40_000);
    expect(serverTimeoutErrors).toEqual([]);
  });

  test('vocabulary: home, a document and Správa wiki carry none of the forbidden terms', async ({ page }) => {
    const document = await createDocument(page, { title: `Slovník ${Date.now()}`, publish: true });
    const forbidden = /\b(WikiAdmin|Reader|Writer|Admin|Fullscreen|FILE|Choose File)\b/;

    await page.goto('/');
    expect(await page.locator('body').innerText()).not.toMatch(forbidden);

    await page.goto(`/#/pages/${document.id}`);
    await expect(page.locator('.document-header h1')).toHaveText(document.title);
    expect(await page.locator('body').innerText()).not.toMatch(forbidden);

    // The launcher includes the current backup warning in its accessible name when attention is needed.
    await page.getByRole('button', { name: /^Správa wiki(?:$|:)/ }).click();
    await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Lidé(\s|$)/ }).click();
    await expect(page.getByRole('heading', { name: 'Lidé', exact: true })).toBeVisible();
    expect(await page.locator('body').innerText()).not.toMatch(forbidden);
  });

  test('profile: an unsaved display-name edit prompts to discard when leaving the section, and staying keeps it', async ({ page }) => {
    await page.goto('/');
    // The profile launcher's accessible name is its whole button text (avatar initials +
    // display name + "Profil a vzhled"), so match it as a substring, not exactly.
    await page.getByRole('button', { name: /Profil a vzhled/ }).click();
    const name = page.getByLabel('Zobrazované jméno', { exact: true });
    await expect(name).toBeVisible();
    const original = await name.inputValue();
    await name.fill(original + ' upraveno');

    const navigation = page.getByRole('navigation', { name: 'Nastavení profilu', exact: true });
    await navigation.getByRole('link', { name: 'Zabezpečení', exact: true }).click();
    const guard = page.getByRole('dialog', { name: 'Neuložené změny', exact: true });
    await expect(guard).toBeVisible();

    await guard.getByRole('button', { name: 'Zůstat', exact: true }).click();
    await expect(guard).toBeHidden();
    await expect(name).toHaveValue(original + ' upraveno');

    // Cleanly discard the edit so this test does not leave the account changed.
    await navigation.getByRole('link', { name: 'Zabezpečení', exact: true }).click();
    await guard.getByRole('button', { name: 'Zahodit', exact: true }).click();
    await expect(guard).toBeHidden();
    await navigation.getByRole('link', { name: 'Účet a vzhled', exact: true }).click();
    await expect(name).toHaveValue(original);
  });
});
