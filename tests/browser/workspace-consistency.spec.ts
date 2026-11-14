import { test, expect } from '@playwright/test';
import { startEditing, LIVE_EDITOR } from './editing';

// Login uses a private test credential; record only deliberate screenshots after login.
test.use({ trace: 'off', screenshot: 'off', video: 'off' });

test('document workspace and routed profile remain consistent on desktop and mobile', async ({ page, context }, info) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the isolated initialized acceptance wiki.');
  test.setTimeout(120_000);
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
    (element as HTMLInputElement).value = value!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();

  const unexpected: string[] = [];
  page.on('pageerror', error => unexpected.push(error.message));
  // 3.6 saves by itself: a checkpoint that comes before the server stored the live snapshot is answered 409 collaboration_pending and
  // tried again shortly (savePage.ts putWithCollaborationRetry), so the page's own PUT 409 and its console line are expected.
  const checkpointRetry = (response: { status(): number; request(): { method(): string }; url(): string }) =>
    response.status() === 409 && response.request().method() === 'PUT' && /^\/api\/pages\/[^/]+$/.test(new URL(response.url()).pathname);
  page.on('console', message => { if ((message.type() === 'error' && !message.text().includes('status of 409 (Conflict)')) || message.text().includes('[Vue warn]')) unexpected.push(message.text()); });
  page.on('response', response => { if (response.status() >= 400 && !checkpointRetry(response)) unexpected.push(`${response.status()} ${new URL(response.url()).pathname}`); });
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const spaces = await (await context.request.get('/api/spaces')).json();
  const rich = { format: 'atlas-rich', version: 1, plugins: [{ id: 'heading-anchors', version: 1 }], document: { type: 'doc', content: [
    { type: 'heading', attrs: { level: 2, headingId: 'pristupy' }, content: [{ type: 'text', text: 'Jak funguje přihlášení' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'Jedno místo pro týmovou dokumentaci. Správce prostoru určuje, kdo může stránky číst a upravovat. Publikovaná verze je dostupná čtenářům, zatímco rozepsané změny zůstávají v konceptu.' }] },
    { type: 'heading', attrs: { level: 2, headingId: 'postup' }, content: [{ type: 'text', text: 'Praktický postup' }] },
    { type: 'bulletList', content: ['Přihlaste se svým firemním účtem.', 'Vyberte prostor svého týmu.', 'Uložte koncept a publikujte dokončenou stránku.'].map(text => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) },
  ] } };
  const created = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: {
    spaceId: spaces[0].id, parentId: null, title: 'Identita a přístupy · týmová příručka', contentJson: JSON.stringify(rich), markdown: '## Jak funguje přihlášení\n\nJedno místo pro týmovou dokumentaci.\n\n## Praktický postup',
  } });
  expect(created.status()).toBe(200);
  const createdPage = await created.json();
  const editorStarted = performance.now();
  await page.goto(`/#/pages/${createdPage.id}`);
  await startEditing(page);
  const editor = page.locator(LIVE_EDITOR);
  await expect(editor).toBeVisible();
  const editorReadyMs = performance.now() - editorStarted;
  // The deliberately shorter Markdown projection must never replace rich content on first join.
  await expect(editor).toContainText('Správce prostoru určuje, kdo může stránky číst a upravovat.');
  await expect(editor.locator('li')).toHaveCount(3);
  await expect(editor.locator('h2').first()).toHaveAttribute('data-heading-id', 'pristupy');
  await editor.evaluate(element => element.setAttribute('data-workspace-instance', 'retained'));
  await editor.press('Control+End'); await editor.press('Enter'); await editor.pressSequentially('Rozepsaná poznámka pro tým.');

  const profileReadyMs: number[] = [];
  const contactEmail = `ux-${Date.now()}@example.test`;
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    if (viewport.width < 680) await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
    const profileStarted = performance.now();
    await page.locator('.profile-launch').click();
    const profile = page.getByRole('region', { name: 'Profil', exact: true });
    await expect(profile.getByLabel('Zobrazované jméno')).toHaveValue('Test správce');
    profileReadyMs.push(performance.now() - profileStarted);
    if (viewport.width > 680) {
      await profile.getByLabel('Kontaktní e-mail').fill(contactEmail);
      await profile.getByRole('button', { name: 'Uložit údaje', exact: true }).click();
      // The SaveBar confirms with Uloženo for a moment before it leaves.
      await expect(profile.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      expect((await (await context.request.get('/api/profile/account')).json()).email).toBe(contactEmail);
    } else await expect(profile.getByLabel('Kontaktní e-mail')).toHaveValue(contactEmail);
    expect(new URL(page.url()).hash).toBe('#/profile/account');
    await expect(page.getByRole('dialog', { name: 'Profil', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
    await page.screenshot({ path: `../../artifacts/profile-account-${viewport.width}.png`, fullPage: true, animations: 'disabled' });
    // Profile sections sit in the application rail on a wide screen (2.5); a phone offers them in the "Sekce" sheet.
    const openSection = async (name: string) => {
      if (viewport.width > 680) return page.locator('#atlas-settings-rail').getByRole('link', { name, exact: true }).click();
      await profile.getByRole('button', { name: /^Sekce/ }).click();
      const sheet = page.getByRole('dialog', { name: 'Sekce', exact: true });
      await sheet.getByRole('link', { name, exact: true }).click();
      await expect(sheet).toBeHidden();
    };
    await openSection('Zabezpečení');
    await expect(profile.getByRole('heading', { name: 'Přihlášení', exact: true })).toBeVisible();
    // Účet a vzhled is one page (2.5); its appearance part follows the account details.
    await openSection('Účet a vzhled');
    await profile.getByRole('radio', { name: /^Propojené/ }).click();
    await profile.getByRole('radio', { name: /^Modrá/ }).click();
    await profile.getByRole('radio', { name: /^Světlý/ }).click();
    await page.screenshot({ path: `../../artifacts/profile-appearance-${viewport.width}.png`, fullPage: true, animations: 'disabled' });
    // The SaveBar is offered only when the look differs from the saved one.
    const saveAppearance = profile.getByRole('button', { name: 'Uložit vzhled', exact: true });
    if (await saveAppearance.isVisible()) {
      await saveAppearance.click();
      await expect(page.getByText('Vzhled uložen.', { exact: true })).toBeVisible();
    }
    await page.locator('[data-profile-back]').click();
    await expect(editor).toHaveAttribute('data-workspace-instance', 'retained');
    await expect(editor).toContainText('Rozepsaná poznámka pro tým.');
    const saveButton = await page.locator('[data-edit-lock], [data-page-mobile-lock]').filter({ visible: true }).first().boundingBox();
    expect(saveButton!.height, 'Document actions use the shared shell button size.').toBeGreaterThanOrEqual(36);
    // Both action lists are ARIA menus named after their trigger; the space trigger includes the space name.
    // A phone with an open document shows only the title crumb (shell.css, data-document-open), so the
    // space menu is checked on desktop only.
    const menus = viewport.width > 680 ? ['Další akce stránky', `Akce prostoru: ${spaces[0].name}`] : ['Další akce stránky'];
    if (viewport.width <= 680) await expect(page.getByRole('button', { name: /^Akce prostoru/ })).toBeHidden();
    for (const label of menus) {
      const trigger = page.getByRole('button', { name: label, exact: true });
      await trigger.press('ArrowDown');
      const menu = page.getByRole('menu', { name: label, exact: true });
      await expect(menu).toBeVisible();
      const bounds = await menu.boundingBox();
      expect(bounds!.x).toBeGreaterThanOrEqual(0);
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width);
      expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height);
      await expect(menu.getByRole('menuitem', { disabled: false }).first()).toBeFocused();
      await page.screenshot({ path: `../../artifacts/workspace-${label === 'Další akce stránky' ? 'page' : 'space'}-actions-${viewport.width}.png`, fullPage: true, animations: 'disabled' });
      await page.keyboard.press('Escape');
      await expect(menu).not.toBeVisible();
      await expect(trigger).toBeFocused();
    }
    const workspace = await page.locator('.workspace').boundingBox();
    const surface = await page.locator('.document-body').boundingBox();
    // The page dock sits inside the workspace beside the document on a wide screen (2.5); the document takes the rest.
    const dock = page.getByRole('complementary', { name: 'Boční panel stránky', exact: true });
    const dockWidth = await dock.isVisible() ? (await dock.boundingBox())!.width : 0;
    expect(surface!.width).toBeGreaterThanOrEqual(workspace!.width - dockWidth - 4);
    expect(surface!.height).toBeGreaterThan(viewport.height * .45);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
    await page.screenshot({ path: `../../artifacts/document-workspace-${viewport.width}.png`, fullPage: true, animations: 'disabled' });
  }
  await editor.press('Control+z');
  await expect(editor).toHaveAttribute('data-workspace-instance', 'retained');
  await expect(editor).not.toContainText('Rozepsaná poznámka pro tým.');
  await expect(editor.locator('li')).toHaveCount(3);
  await expect(editor.locator('h2').first()).toHaveAttribute('data-heading-id', 'pristupy');
  await page.keyboard.press('Control+k');
  const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  const searchInput = search.getByRole('combobox', { name: /^Hledat ve wiki – / });
  const results = search.getByRole('listbox', { name: 'Výsledky hledání' });
  const searchTimings: number[] = [];
  for (let index = 0; index < 20; index++) {
    const query = index % 2 === 0 ? 'pristupy' : 'pristupi';
    const response = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/search' && url.searchParams.get('q') === query;
    });
    const started = performance.now();
    await searchInput.fill(query);
    expect((await response).status()).toBe(200);
    await expect(results).toHaveAttribute('aria-busy', 'false');
    await expect(results.getByRole('option').first()).toBeVisible();
    searchTimings.push(performance.now() - started);
  }
  // The first Esc empties the typed text and keeps the search open; the next one closes it (docs/interaction-rules.md P3).
  await searchInput.press('Escape');
  await expect(searchInput).toHaveValue('');
  await expect(search).toBeVisible();
  await searchInput.press('Escape');
  await expect(search).not.toBeVisible();
  await expect(editor).toHaveAttribute('data-workspace-instance', 'retained');
  searchTimings.sort((a, b) => a - b);
  await info.attach('omnisearch-response-times.json', { body: JSON.stringify({ samples: searchTimings.length, p50: searchTimings[9], p95: searchTimings[18], scope: 'Local mobile browser; 20 alternating exact/fuzzy inputs, debounce + HTTP + visible results; initialized acceptance corpus, not the 10k benchmark corpus' }), contentType: 'application/json' });
  expect(searchTimings[18], 'Omnisearch reaches usable results within the local 700 ms target.').toBeLessThan(700);
  const timings: number[] = [];
  for (let index = 0; index < 15; index++) {
    const started = performance.now();
    const response = await context.request.get('/api/profile/account');
    expect(response.status()).toBe(200);
    timings.push(performance.now() - started);
  }
  timings.sort((a, b) => a - b);
  await info.attach('profile-http-response-times.json', { body: JSON.stringify({ samples: timings.length, p50: timings[7], p95: timings[14], editorReadyMs, profileReadyMs, scope: 'Local authenticated HTTP requests, warm account, sequential; UI timings include Playwright interaction and assertions' }), contentType: 'application/json' });
  expect(timings[14], 'A simple profile read must remain responsive in the local acceptance environment.').toBeLessThan(1000);
  expect(unexpected).toEqual([]);
});
