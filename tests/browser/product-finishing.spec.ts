import { test, expect, type Locator, type Page } from '@playwright/test';
import { PAGE_TEXT } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
async function secret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, text) => { (element as HTMLInputElement).value = text; element.dispatchEvent(new Event('input', { bubbles: true })); }, value);
}
async function login(page: Page, username: string, password: string) {
  await page.goto('/'); await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username);
  await secret(page.getByLabel('Heslo', { exact: true }), password);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}
test('space identity, article navigation, API reference and access history work together', async ({ page, context, browser }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Only an initialized isolated acceptance wiki with admin and reader.');
  test.setTimeout(60_000);
  await login(page, 'browser-admin', process.env.ATLAS_E2E_PASSWORD!);
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const headers = { 'X-CSRF-TOKEN': csrf.token };
  const originalName = `Produktová přejímka ${Date.now()}`;
  const created = await context.request.post('/api/spaces', { headers, data: { name: originalName, icon: 'book-open', color: 'blue' } });
  expect(created.status()).toBe(200); const space = await created.json();
  await page.goto(`/#/spaces/${space.id}`); await page.reload();
  await page.getByRole('button', { name: `Akce prostoru: ${originalName}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Nastavení prostoru', exact: true }).click();
  // Nastavení prostoru is a page of its own (#/spaces/<id>/settings/general), no longer a dialog.
  await expect(page).toHaveURL(new RegExp(`#/spaces/${space.id}/settings/general$`));
  const settings = page.locator('[data-settings-host]');
  await expect(settings.getByLabel('Název prostoru', { exact: true })).toHaveValue(originalName);
  const name = `${originalName} · tým`;
  await settings.getByLabel('Název prostoru', { exact: true }).fill(name);
  // AppearancePicker: icons are tiles of one radio group ("compass" is labelled "Průvodce", one of the common
  // icons); ten quick colours come first and the 12 hues × 5 tones palette opens under Další barvy a tóny
  // (refresh 2.5). A colour is stored as "<hue>-<tone>".
  await settings.getByRole('radiogroup', { name: 'Ikona prostoru', exact: true }).getByRole('radio', { name: 'Průvodce', exact: true }).click();
  await settings.getByRole('button', { name: 'Další barvy a tóny (60)', exact: true }).click();
  await settings.getByRole('radiogroup', { name: 'Všechny barvy', exact: true }).getByRole('radio', { name: 'Zelená · sytá', exact: true }).click();
  const description = 'Dokumentace pro tým. Jedno místo pro rozhodnutí a praktické postupy.';
  await settings.getByLabel('Popis prostoru', { exact: true }).fill(description);
  await expect(settings.locator('[data-appearance-preview]')).toContainText(name);
  const saved = page.waitForResponse(response => response.url().endsWith(`/api/spaces/${space.id}/settings`) && response.request().method() === 'PUT');
  await settings.getByRole('button', { name: 'Uložit nastavení', exact: true }).click();
  expect((await saved).status()).toBe(200);
  await expect(settings.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
  // Settings hold the rail (2.5); its way back restores the wiki navigation, where the space row carries the new name
  // and opens the space overview.
  await page.getByRole('button', { name: 'Zpět do prostoru', exact: true }).click();
  await page.locator('aside').getByRole('link', { name }).click();
  await expect(page.locator('.space-description')).toHaveText(description);
  const current = await (await context.request.get(`/api/spaces/${space.id}/settings`)).json();
  expect(current).toMatchObject({ name, icon: 'compass', color: 'green-3', description });

  const users = await (await context.request.get('/api/admin/users')).json();
  const reader = users.find((user: { username: string }) => user.username === 'browser-reader');
  expect(!!reader, 'First-run creates a separate ordinary reader account.').toBe(true);
  const grant = await context.request.put(`/api/spaces/${space.id}/access`, { headers, data: { userId: reader.id, role: 'Reader', expectedVersion: null } });
  expect(grant.status()).toBe(204);
  const rich = { format: 'atlas-rich', version: 1, plugins: [{ id: 'heading-anchors', version: 1 }], document: { type: 'doc', content: [
    { type: 'heading', attrs: { level: 1, headingId: 'overview' }, content: [{ type: 'text', text: 'Přehled řešení' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'Praktické informace pro celý tým.' }] },
    { type: 'heading', attrs: { level: 2, headingId: 'installation' }, content: [{ type: 'text', text: 'Instalace' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'Tady začíná postup instalace.' }] },
    { type: 'codeBlock', attrs: { language: 'bash' }, content: [{ type: 'text', text: 'export ATLAS_URL="https://wiki.example.test"' }] },
  ] } };
  const pageResponse = await context.request.post('/api/pages', { headers, data: { spaceId: space.id, parentId: null, title: 'Článek s obsahem', contentJson: JSON.stringify(rich), markdown: '# Přehled řešení\n\nPraktické informace pro celý tým.\n\n## Instalace\n\nTady začíná postup instalace.\n\n```bash\nexport ATLAS_URL="https://wiki.example.test"\n```' } });
  expect(pageResponse.status()).toBe(200); const document = await pageResponse.json();
  expect((await context.request.post(`/api/pages/${document.id}/publish`, { headers, data: { expectedVersion: 1 } })).status()).toBe(200);
  // The dock opens by itself only when the workspace beside the rail is at least 1000 px wide (App.vue, pageDockHasRoom).
  // This account has the Ostrovy layout from first-run.spec.ts, which leaves 946 px of a 1280 px window, so "wide" is 1440 here.
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/#/pages/${document.id}`);
  // A writer's page is its editor from the first frame (3.6, no read-only mode); nothing below writes to it.
  const editor = page.locator(PAGE_TEXT); await expect(editor).toHaveAttribute('contenteditable', 'true');
  // DocumentEditor is a lazy chunk that starts loading code highlighting when it is evaluated; the
  // first editor of a session re-keys once when highlighting arrives (DocumentEditor.vue,
  // codeBlockGeneration). A highlighted code block proves the final instance is mounted.
  await expect(editor.locator('pre [class^="hljs-"]').first()).toBeVisible();
  await editor.evaluate(element => { (element as HTMLElement & { retained?: boolean }).retained = true; });
  // The outline is the Obsah tab of the page dock, which a wide screen opens by itself (2.5, board 03).
  const toc = page.getByRole('complementary', { name: 'Boční panel stránky', exact: true }).getByRole('navigation', { name: 'Na této stránce', exact: true });
  await expect(toc).toContainText('Přehled řešení');
  await page.screenshot({ path: '../../artifacts/atlas-finishing-desktop-contents.png', fullPage: true, animations: 'disabled' });
  await page.setViewportSize({ width: 360, height: 900 });
  expect(await page.evaluate(() => window.document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
  await page.screenshot({ path: '../../artifacts/atlas-finishing-mobile-contents.png', fullPage: true, animations: 'disabled' });
  // A phone has no dock: the outline is a row above the text there (board 13). The open sidebar and its
  // backdrop must still cover the document and its overlays.
  await expect(toc).toBeHidden();
  await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
  // The drawer has its own Zavřít menu button beside the backdrop's (2.5).
  await expect(page.locator('#wiki-navigation').getByRole('button', { name: 'Zavřít menu', exact: true })).toBeVisible();
  const documentShowsThroughMenu = await editor.locator('h1').evaluate(heading => {
    const bounds = heading.getBoundingClientRect();
    return [bounds.left + 2, bounds.right - 2].map(x => heading.contains(document.elementFromPoint(x, bounds.top + bounds.height / 2)));
  });
  expect(documentShowsThroughMenu, 'The mobile sidebar and its backdrop must cover the document.').toEqual([false, false]);
  await page.screenshot({ path: '../../artifacts/atlas-finishing-mobile-menu.png', fullPage: true, animations: 'disabled' });
  await page.locator('.sidebar-backdrop').click({ position: { x: 350, y: 20 } });
  await page.setViewportSize({ width: 1440, height: 900 });
  // Back on a wide screen the dock returns with its outline; choosing a heading scrolls to it and focuses it.
  await expect(toc).toBeVisible();
  await toc.getByRole('button', { name: 'Instalace', exact: true }).click();
  // In the live editor a heading is text: the caret goes to its start (pageOutline goToHeading), the editor keeps the focus.
  await expect(editor).toBeFocused();
  expect(await editor.evaluate(() => getSelection()?.anchorNode?.parentElement?.closest('h2')?.textContent)).toBe('Instalace');
  expect(await editor.evaluate(element => (element as HTMLElement & { retained?: boolean }).retained)).toBe(true);
  await expect(editor).toHaveAttribute('contenteditable', 'true');
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.getByRole('button', { name: 'Kopírovat odkaz na stránku', exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(new URL(`/#/pages/${document.id}`, page.url()).href);
  const unchanged = await (await context.request.get(`/api/pages/${document.id}`)).json(); expect(unchanged.version).toBe(1);

  await page.getByRole('button', { name: /Profil a vzhled/ }).click();
  const profile = page.locator('.profile-page');
  await page.locator('.settings-sidebar-rail [data-path="/profile/api"]').click();
  // Dokumentace API reveals the personal OpenAPI document in Scalar in place, loaded on demand.
  const docs = profile.getByRole('button', { name: 'Dokumentace API', exact: true });
  await expect(docs).toHaveAttribute('aria-expanded', 'false');
  await docs.click();
  await expect(docs).toHaveAttribute('aria-expanded', 'true');
  const reference = profile.getByRole('region', { name: 'Dokumentace API', exact: true });
  await expect(reference.locator('[data-api-docs-reference] iframe.md-openapi-frame').contentFrame().locator('body')).toContainText('Atlas personal API', { timeout: 30_000 });
  // The server still hosts its own Scalar page beside the OpenAPI JSON.
  const scalar = await context.request.get('/scalar/');
  expect(scalar.status()).toBe(200);
  expect(scalar.headers()['content-type']).toContain('text/html');
  await page.screenshot({ path: '../../artifacts/atlas-finishing-api-reference.png', fullPage: true, animations: 'disabled' });
  await page.locator('[data-profile-back]').click();
  await page.getByRole('button', { name: /^Správa wiki/ }).click();
  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: 'Audit', exact: true }).click();
  const audit = page.getByRole('region', { name: 'Audit', exact: true });
  // Kdo co kdy: "kdo — co udělal — s čím" names the space; whom it concerns and the role are the context line.
  const event = audit.locator('.activity-item').filter({ hasText: `udělil(a) roli v prostoru ${name}` }).filter({ hasText: 'účet E2E Čtenář · úroveň Čte' });
  await expect(event).toHaveCount(1);
  await page.screenshot({ path: '../../artifacts/atlas-finishing-access-audit.png', fullPage: true, animations: 'disabled' });

  const readerContext = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  try {
    const readerPage = await readerContext.newPage(); await login(readerPage, 'browser-reader', process.env.ATLAS_E2E_READER_PASSWORD ?? `${process.env.ATLAS_E2E_PASSWORD!}-reader`);
    await readerPage.goto(`/#/spaces/${space.id}`);
    await expect(readerPage.locator('.space-description')).toHaveText(description);
    await expect(readerPage.getByRole('button', { name: 'Nastavení prostoru', exact: true })).toHaveCount(0);
    // The reader has a role in this space, only a too low one: 403 (rule S3, docs/architecture.md); a space it cannot see stays 404.
    expect((await readerContext.request.get(`/api/spaces/${space.id}/settings`)).status()).toBe(403);
    expect((await readerContext.request.get('/api/admin/access-audit')).status()).toBe(403);
    await readerPage.goto(`/#/pages/${document.id}`);
    // A reader gets the page outline too: in the dock on a wide screen, as a row above the text otherwise (2.5).
    await expect(readerPage.getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible();
  } finally { await readerContext.close(); }
});




