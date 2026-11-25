import { test, expect } from '@playwright/test';
import { PAGE_TEXT } from "./editing";

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

test('omnisearch filters drafts, restores history, and opens real links on desktop and mobile', { tag: '@smoke' }, async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the isolated initialized acceptance wiki.');
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
    (element as HTMLInputElement).value = value!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  const unexpected: string[] = [];
  page.on('pageerror', error => unexpected.push(error.message));
  page.on('response', response => { if (response.status() >= 400) unexpected.push(`${response.status()} ${new URL(response.url()).pathname}`); });
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const headers = { 'X-CSRF-TOKEN': csrf.token };
  const marker = `filtr${Date.now()}`;
  // A dedicated scope keeps this scenario deterministic with semantic search enabled, too.
  const spaceResponse = await context.request.post('/api/spaces', { headers, data: { name: `Přejímka filtrů ${Date.now()}`, icon: 'book-open', color: 'blue' } });
  expect(spaceResponse.status()).toBe(200);
  const space = await spaceResponse.json();
  async function create(title: string) {
    const rich = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Pravidla přístupů a týmové dokumentace.' }] }] } };
    const response = await context.request.post('/api/pages', { headers, data: { spaceId: space.id, title, parentId: null, contentJson: JSON.stringify(rich), markdown: 'Pravidla přístupů a týmové dokumentace.' } });
    expect(response.status()).toBe(200); return response.json();
  }
  const published = await create(`Přístupová příručka ${marker}`);
  expect((await context.request.post(`/api/pages/${published.id}/publish`, { headers, data: { expectedVersion: 1 } })).status()).toBe(200);
  const draft = await create(`Připravovaná pravidla ${marker}`);
  for (let index = 1; index <= 8; index++) {
    const article = await create(`Postup týmu ${index} ${marker}`);
    expect((await context.request.post(`/api/pages/${article.id}/publish`, { headers, data: { expectedVersion: 1 } })).status()).toBe(200);
  }
  await page.goto(`/#/pages/${published.id}`);
  // The API fixture created its space after login; refresh the shell's space directory.
  await page.reload();
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('region', { name: 'Nedávné a oblíbené stránky' })).toBeVisible();
  const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  // The field is named after the active content scope; the default scope "Vše" covers
  // published pages and accessible drafts alike.
  const searchField = search.getByRole('combobox', { name: /^Hledat ve wiki – / });
  await expect(searchField).toHaveAccessibleName('Hledat ve wiki – Vše');
  await search.getByRole('button', { name: /^Prostor: / }).click();
  await page.locator(`.filter-menu-panel [data-value="${space.id}"]`).click();
  // Quote the fixture marker: unquoted fuzzy matching can also find a previous
  // run whose timestamp differs by only a few digits.
  await searchField.fill(`"${marker}"`);
  await expect(search.getByRole('option').filter({ hasText: published.title })).toBeVisible();
  await expect(search.getByRole('option').filter({ hasText: draft.title })).toBeVisible();
  // Results arrive per source (wiki first, then YouTrack enrichment); the source status line can
  // shift the list, so let every source settle before measuring or clicking results.
  const sources = search.getByRole('status').filter({ hasText: /^Wiki ·/ });
  await expect(sources).not.toContainText('hledáme');
  await expect(search.getByRole('listbox', { name: 'Výsledky hledání', exact: true })).toHaveAttribute('aria-busy', 'false');
  await page.setViewportSize({ width: 390, height: 844 });
  await search.locator('.search-content').evaluate(element => { element.scrollTop = element.scrollHeight; });
  const controls = await search.locator('.search-query-controls').boundingBox();
  expect(controls!.y).toBeGreaterThanOrEqual(0);
  expect(controls!.y + controls!.height).toBeLessThan(844);
  await expect(searchField).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await search.locator('.search-content').evaluate(element => { element.scrollTop = 0; });
  const resultLink = search.getByRole('option').filter({ hasText: published.title });
  const resultHref = (await resultLink.getAttribute('href'))!;
  expect(new URL(resultHref).search).toBe('');
  // A result is a real link: the app leaves a modified (Ctrl) click to the browser, which opens
  // the href in a new tab. Driving that background tab through Playwright is unreliable in the
  // container (the tab is sometimes never reported, or captured at about:blank with a navigation
  // that never "finishes"), so assert the click contract here and open the href in a page.
  const ctrlClick = await resultLink.evaluate(element => new Promise<{ prevented: boolean }>(resolve => {
    window.addEventListener('click', event => { resolve({ prevented: event.defaultPrevented }); event.preventDefault(); }, { once: true });
    element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true, button: 0 }));
  }));
  expect(ctrlClick.prevented, 'Ctrl+click on a result must keep the browser default (open in a new tab).').toBe(false);
  await expect(search).toBeVisible();
  const opened = await context.newPage();
  await opened.goto(resultHref);
  await expect(opened.locator(PAGE_TEXT)).toBeVisible();
  await expect(opened.getByRole('dialog', { name: 'Hledat ve wiki', exact: true })).not.toBeVisible();
  await opened.close();
  const keyboardResults = search.getByRole('listbox', { name: 'Výsledky hledání', exact: true }).getByRole('option');
  // The published guide, eight published articles and the draft.
  await expect(keyboardResults).toHaveCount(10);
  // A pointer that does not move selects nothing (layout moving under a resting pointer must not override the
  // keyboard), so the pointer comes from elsewhere: the earlier steps may leave it resting over this very row.
  await page.mouse.move(0, 0);
  await keyboardResults.nth(0).hover();
  await searchField.focus();
  await searchField.press('ArrowDown');
  await expect(searchField).toBeFocused();
  await expect(keyboardResults.nth(1)).toHaveAttribute('aria-selected', 'true');
  await searchField.press('ArrowUp');
  await expect(keyboardResults.nth(0)).toHaveAttribute('aria-selected', 'true');
  // Tab follows the palette from top to bottom: the four filter menus (Prostor, Upraveno, Obsah, Řazení) and the
  // whole-page switch of the field row, then the mode tabs and the result list.
  await search.getByRole('button', { name: 'Vymazat dotaz', exact: true }).focus();
  for (const name of [/^Prostor: /, /^Upraveno: /, /^Obsah: /, /^Řazení: /]) {
    await page.keyboard.press('Tab');
    await expect(search.getByRole('button', { name })).toBeFocused();
  }
  await page.keyboard.press('Tab');
  await expect(search.getByRole('button', { name: 'Hledání na celou stránku', exact: true })).toBeFocused();
  const modes = search.getByRole('group', { name: 'Režim hledání', exact: true }).getByRole('button');
  for (let index = 0, count = await modes.count(); index < count; index++) await page.keyboard.press('Tab');
  await expect(modes.last()).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(keyboardResults.nth(0)).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(keyboardResults.nth(1)).toBeFocused();
  const selectedTitle = await keyboardResults.nth(1).locator('strong').first().textContent();
  const selectedHash = new URL((await keyboardResults.nth(1).getAttribute('href'))!, page.url()).hash;
  const searchContent = search.locator('.search-content');
  const savedScroll = await searchContent.evaluate(element => {
    element.scrollTop = element.scrollHeight;
    return element.scrollTop;
  });
  expect(savedScroll).toBeGreaterThan(0);
  await page.keyboard.press('Enter');
  await expect(search).not.toBeVisible();
  await expect.poll(() => new URL(page.url()).hash).toBe(selectedHash);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await page.goBack();
  await expect(search).toBeVisible();
  await expect(search.getByRole('option').filter({ hasText: selectedTitle! })).toHaveAttribute('aria-selected', 'true');
  await expect.poll(() => searchContent.evaluate(element => element.scrollTop)).toBe(savedScroll);
  const pick = async (menu: RegExp, option: string) => {
    await search.getByRole('button', { name: menu }).click();
    await page.getByRole('menuitemradio', { name: option, exact: true }).click();
  };
  await pick(/^Obsah: /, 'Dostupné koncepty');
  await pick(/^Upraveno: /, 'Posledních 7 dní');
  await pick(/^Řazení: /, 'Poslední uložená změna');
  await expect(search.getByRole('option').filter({ hasText: draft.title })).toBeVisible();
  await expect(search.getByRole('option').filter({ hasText: published.title })).toHaveCount(0);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: '../../artifacts/search-filters-1440.png', fullPage: true });
  await search.getByRole('option').filter({ hasText: draft.title }).click();
  await expect(search).not.toBeVisible();
  await page.goBack();
  await expect(search).toBeVisible();
  await expect(search.getByRole('button', { name: 'Obsah: Dostupné koncepty' })).toBeVisible();
  await expect(search.getByRole('button', { name: 'Upraveno: Posledních 7 dní' })).toBeVisible();
  await expect(search.getByRole('option').filter({ hasText: draft.title })).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await search.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: '../../artifacts/search-filters-390.png', fullPage: true });
  await expect(searchField).toHaveAccessibleName('Hledat ve wiki – Dostupné koncepty');
  await searchField.focus();
  // The first Esc empties the typed text and keeps the search open; the next one closes it (docs/interaction-rules.md P3).
  await page.keyboard.press('Escape');
  await expect(searchField).toHaveValue('');
  await expect(search).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(search).not.toBeVisible();
  await page.keyboard.press('Control+k');
  await expect(searchField).toHaveValue('');
  await expect(searchField).toHaveAccessibleName('Hledat ve wiki – Vše');
  // The filters are reset on every opening.
  await expect(search.getByRole('button', { name: 'Upraveno: Kdykoli' })).toBeVisible();
  expect(unexpected).toEqual([]);
});
