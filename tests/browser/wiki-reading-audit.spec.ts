import { test, expect, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { startEditing } from './editing';

// Read-only document audit. Opening pages still updates the user's recent visits.
// Use a dedicated test account and a pre-existing, searchable, heading-rich page.
test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });
const username = process.env.ATLAS_E2E_USER;
const password = process.env.ATLAS_E2E_PASSWORD;
const pageId = process.env.ATLAS_E2E_READING_PAGE_ID;
const title = process.env.ATLAS_E2E_READING_TITLE;
const section = process.env.ATLAS_E2E_READING_SECTION;
test.skip(!username || !password || !pageId || !title || !section,
  'Set ATLAS_E2E_USER, ATLAS_E2E_PASSWORD and ATLAS_E2E_READING_PAGE_ID/TITLE/SECTION.');
let state: Awaited<ReturnType<BrowserContext['storageState']>>;

test.beforeAll(async ({ browser }) => {
  expect(pageId, 'The audit fixture requires a page UUID.').toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
  const context = await browser.newContext();
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
    state = await context.storageState(); // Session cookies stay in memory.
  } finally {
    await context.close();
  }
});

test.beforeEach(async ({ context, page }) => {
  await context.addCookies(state.cookies);
  await page.goto(`/#/pages/${pageId}`);
  await expect(page.locator('.document-header').getByRole('heading', { name: title!, exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole('document', { name: 'Obsah stránky', exact: true }))
    .toHaveClass(/\breadonly\b/);
  await expect(page.getByRole('document', { name: 'Obsah stránky', exact: true })
    .getByRole('heading', { name: section!, exact: true })).toBeAttached();
  await page.evaluate(() => document.fonts.ready);
  // „Na této stránce“: the page dock's list where the dock has room (from about 1440 px), else a row above the text.
  await expect(page.getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible();
});

const article = (page: Page) => page.getByRole('document', { name: 'Obsah stránky', exact: true }).locator('.ProseMirror');
const dock = (page: Page) => page.getByRole('complementary', { name: 'Boční panel stránky', exact: true });

/**
 * A wide enough workspace brings the page dock back by itself; elsewhere its top-bar toggle opens it. That return can
 * land between reading the toggle and clicking it, which would close the dock again, so the check repeats until it holds.
 */
async function openDock(page: Page) {
  const toggle = page.getByRole('button', { name: 'Boční panel stránky', exact: true });
  await expect(async () => {
    if (await toggle.getAttribute('aria-pressed') !== 'true') await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true', { timeout: 1000 });
    await expect(dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible({ timeout: 1000 });
  }).toPass();
  await settle(page);
}

/**
 * Waits until nothing animates. Polls the play state: a transition in the collapsed sidebar can end as „finished“
 * while its `finished` promise never settles, which hung a Promise.all over every animation.
 */
async function settle(page: Page) {
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation => animation.playState === 'running').length)).toBe(0);
}

async function geometry(page: Page) {
  await settle(page);
  return page.evaluate(() => {
    const article = document.querySelector('.document-body [role="document"] .ProseMirror')!;
    const rect = article.getBoundingClientRect();
    const dockRect = document.querySelector('.workspace-panel')?.getBoundingClientRect();
    const first = article.firstElementChild!.getBoundingClientRect();
    return {
      viewport: innerWidth, viewportHeight: innerHeight,
      documentWidth: document.documentElement.scrollWidth,
      articleWidth: Math.round(rect.width),
      articleLeft: Math.round(rect.left), articleRight: Math.round(rect.right),
      firstBlockTop: Math.round(first.top),
      dockLeft: dockRect && dockRect.width > 0 ? Math.round(dockRect.left) : null,
      dockRight: dockRect && dockRect.width > 0 ? Math.round(dockRect.right) : null,
      topbarHeight: Math.round(document.querySelector('.workspace-bar')!.getBoundingClientRect().height),
    };
  });
}

test('direct link survives reload and browser Back without entering edit mode', async ({ page }) => {
  await page.reload();
  await expect(page.locator('.document-header').getByRole('heading', { name: title!, exact: true, level: 1 })).toBeVisible();
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Knihovna', exact: true, level: 1 })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(new RegExp(`#/pages/${pageId}$`));
  await expect(page.locator('.document-header').getByRole('heading', { name: title!, exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole('document', { name: 'Obsah stránky', exact: true }))
    .toHaveClass(/\breadonly\b/);
});

async function expectHeadingBelowToolbar(page: Page, heading: Locator) {
  let previousTop: number | undefined;
  let stableSamples = 0;
  await expect.poll(async () => {
    const top = (await heading.boundingBox())!.y;
    stableSamples = previousTop !== undefined && Math.abs(top - previousTop) < .5 ? stableSamples + 1 : 0;
    previousTop = top;
    return stableSamples;
  }, { message: 'Wait for the heading navigation scroll to settle.', intervals: [50, 50, 100], timeout: 3000 }).toBeGreaterThanOrEqual(3);
  const target = await heading.boundingBox();
  const toolbar = await page.locator('.workspace-bar').boundingBox();
  expect(target!.y, 'The requested heading must remain visible below the application toolbar.')
    .toBeGreaterThanOrEqual(toolbar!.y + toolbar!.height);
}

test('docked outline navigation leaves the requested heading below the toolbar', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  await openDock(page);
  const heading = page.getByRole('document', { name: 'Obsah stránky', exact: true })
    .getByRole('heading', { name: section!, exact: true });
  await dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true })
    .getByRole('button', { name: section!, exact: true }).click();
  await expect(heading).toBeInViewport();
  await expectHeadingBelowToolbar(page, heading);
});

test('keyboard outline navigation brings the requested section into view', async ({ page }) => {
  // Without the dock the outline is the „Na této stránce“ row above the text, unfolded in place.
  await page.setViewportSize({ width: 1024, height: 900 });
  const heading = page.getByRole('document', { name: 'Obsah stránky', exact: true })
    .getByRole('heading', { name: section!, exact: true });
  const panel = page.getByRole('navigation', { name: 'Na této stránce', exact: true });
  const trigger = panel.getByRole('button', { name: /^Na této stránce/ });
  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');
  const entry = panel.getByRole('button', { name: section!, exact: true });
  await expect(entry).toBeVisible();
  const tabStops = await panel.getByRole('button').count();
  for (let index = 0; index < tabStops && !await entry.evaluate(el => el === document.activeElement); index++) {
    await page.keyboard.press('Tab');
  }
  await expect(entry).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(heading).toBeInViewport();
  await expectHeadingBelowToolbar(page, heading);
});

test('search finds the fixture and opens it using the keyboard', async ({ page }) => {
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click();
  await page.keyboard.press('ControlOrMeta+k');
  const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  const input = search.getByRole('combobox', { name: /^Hledat (publikované|dostupné|ve)/ });
  await expect(input).toBeFocused();
  await search.getByRole('button', { name: 'Filtry', exact: true }).click();
  await search.getByRole('radio', { name: (process.env.ATLAS_E2E_READING_SCOPE ?? 'published') === 'drafts' ? 'Dostupné koncepty' : 'Publikované stránky', exact: true }).click();
  await input.fill(title!);
  const results = search.getByRole('listbox', { name: 'Výsledky hledání', exact: true });
  const result = results.getByRole('option').and(page.locator(`a[href*="#/pages/${pageId}"]`));
  await expect(result).toHaveCount(1);
  await input.focus();
  const resultCount = await results.getByRole('option').count();
  for (let index = 0; index < resultCount && await result.getAttribute('aria-selected') !== 'true'; index++) {
    await input.press('ArrowDown');
  }
  await expect(result).toHaveAttribute('aria-selected', 'true');
  await input.press('Enter');
  await expect(search).toBeHidden();
  await expect(page).toHaveURL(new RegExp(`#/pages/${pageId}(?:[?&].*)?$`));
  await expect(page.locator('.document-header').getByRole('heading', { name: title!, exact: true, level: 1 })).toBeVisible();
});

test('mobile menu closes with Escape and returns focus to its trigger', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const trigger = page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true });
  await trigger.click();
  const navigation = page.getByRole('complementary', { name: 'Hlavní navigace', exact: true });
  await expect(navigation).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(navigation).toBeHidden();
  await expect(trigger).toBeFocused();
  await expect(page.locator('.document-header').getByRole('heading', { name: title!, exact: true, level: 1 })).toBeInViewport();
});

test('sidebar drag persists through reload and double click restores its width', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  const separator = page.getByRole('separator', { name: 'Šířka boční navigace', exact: true });
  const handle = await separator.boundingBox();
  await page.mouse.move(handle!.x + handle!.width / 2, 300);
  await page.mouse.down();
  await page.mouse.move(handle!.x + handle!.width / 2 + 100, 300, { steps: 8 });
  await page.mouse.up();
  await expect(separator).toHaveAttribute('aria-valuenow', '372');
  await expect.poll(async () => (await page.getByRole('complementary', { name: 'Hlavní navigace', exact: true }).boundingBox())!.width).toBe(372);
  await page.reload();
  await expect(separator).toHaveAttribute('aria-valuenow', '372');
  await separator.dblclick();
  await expect(separator).toHaveAttribute('aria-valuenow', '272');
});

test('a wider sidebar preserves outline separation and keyboard resizing on desktop', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  const separator = page.getByRole('separator', { name: 'Šířka boční navigace', exact: true });
  await separator.focus(); await separator.press('End');
  await expect(separator).toHaveAttribute('aria-valuenow', '480');
  await expect(page.locator('.atlas')).toHaveAttribute('data-reading-space', 'compact');
  // The page dock steps aside for the wider rail; the outline returns as the row above the text.
  await expect(dock(page)).toBeHidden();
  const outline = page.getByRole('navigation', { name: 'Na této stránce', exact: true });
  await expect(outline).toBeVisible();
  const measured = await geometry(page);
  const row = await outline.boundingBox();
  const heading = await page.locator('.document-header h1').boundingBox();
  expect(row!.y + row!.height).toBeLessThanOrEqual(measured.firstBlockTop);
  expect(Math.abs(row!.x - measured.articleLeft)).toBeLessThanOrEqual(1);
  expect(Math.abs(heading!.x - measured.articleLeft)).toBeLessThanOrEqual(1);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(separator).toBeHidden();
  expect((await geometry(page)).articleWidth).toBeGreaterThanOrEqual(340);
  await page.setViewportSize({ width: 1440, height: 960 });
  await expect(separator).toHaveAttribute('aria-valuenow', '480');
});

test('Knihovna lists all spaces first, then the recently opened pages, and refreshes them', async ({ page }) => {
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click();
  await expect(page).toHaveURL(/#\/library\/spaces$/);
  await expect(page.getByRole('list', { name: 'Seznam prostorů', exact: true }).getByRole('listitem').first()).toBeVisible();
  const responsePromise = page.waitForResponse(response => response.url().includes('/api/me/library/recent?') && response.request().method() === 'GET');
  await page.getByRole('navigation', { name: 'Části knihovny', exact: true }).getByRole('link', { name: 'Nedávné', exact: true }).click();
  const response = await responsePromise;
  expect(response.ok()).toBe(true);
  const data = await response.json();
  expect(data.items.length).toBeGreaterThan(0);
  const recent = page.getByRole('list', { name: 'Nedávno otevřené', exact: true });
  await expect(recent.getByRole('link')).toHaveCount(data.items.length);
  await expect(recent.getByRole('link').first()).toContainText(data.items[0].title);
  await expect(recent.locator('time').first()).toHaveAttribute('datetime', data.items[0].visitedAt);
  const refresh = page.waitForResponse(response => response.url().includes('/api/me/library/recent?'));
  await page.locator('.library-refresh').click();
  expect((await refresh).ok()).toBe(true);
  await recent.getByRole('link').first().click();
  await expect(page.locator('.document-header h1')).toBeVisible();
});

test('Knihovna fits mobile and a desktop with expanded navigation', async ({ page }) => {
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click();
  await page.getByRole('navigation', { name: 'Části knihovny', exact: true }).getByRole('link', { name: 'Nedávné', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Nedávno otevřené', exact: true })).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.getByRole('separator', { name: 'Šířka boční navigace', exact: true }).press('End');
  for (const width of [1440, 1024, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 960 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    const list = await page.getByRole('list', { name: 'Nedávno otevřené', exact: true }).boundingBox();
    expect(list!.x).toBeGreaterThanOrEqual(0);
    expect(list!.x + list!.width).toBeLessThanOrEqual(width);
  }
});

test('command palette executes a filtered command and restores the document through Back', async ({ page }) => {
  await page.keyboard.press('ControlOrMeta+Shift+p');
  const palette = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  const input = palette.getByRole('combobox', { name: 'Hledat příkaz', exact: true });
  await expect(input).toBeFocused();
  await input.fill('> neexistujici prikaz');
  await expect(palette.getByRole('status')).toContainText('Žádný příkaz');
  await input.press('Enter');
  await expect(palette).toBeVisible();
  await input.fill('> oblibene');
  await expect(palette.getByRole('option')).toHaveCount(1);
  await input.press('Enter');
  await expect(palette).toBeHidden();
  await expect(page).toHaveURL(/#\/library\/favorites$/);
  await page.goBack();
  await expect(page.locator('.document-header h1')).toHaveText(title!);
  await expect(page.getByRole('document', { name: 'Obsah stránky', exact: true })).toHaveClass(/\breadonly\b/);
});

test('wide reading and collapsed navigation provide more space and survive reload', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  const comfortable = await geometry(page);
  // A layout preference must leave the same editor DOM and document intact.
  const original = await article(page).elementHandle();
  await page.keyboard.press('ControlOrMeta+k');
  const palette = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  await palette.getByRole('button', { name: 'Příkazy', exact: false }).click();
  const input = palette.getByRole('combobox', { name: 'Hledat příkaz', exact: true });
  await input.fill('> siroky'); await input.press('Enter');
  await expect(page.locator('.atlas')).toHaveAttribute('data-reading-width', 'wide');
  await page.getByRole('button', { name: 'Sbalit boční navigaci', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'Hlavní navigace', exact: true })).toBeHidden();
  await expect.poll(async () => (await geometry(page)).articleWidth).toBeGreaterThan(comfortable.articleWidth);
  expect(await original!.evaluate(element => element.isConnected)).toBe(true);
  await original!.dispose();
  await page.reload();
  await expect(article(page)).toBeVisible();
  await expect(page.locator('.atlas')).toHaveAttribute('data-reading-width', 'wide');
  await expect(page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true })).toBeVisible();
  await expect(dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible();
  await expect.poll(async () => (await geometry(page)).dockLeft).not.toBeNull();
  const measured = await geometry(page);
  expect(measured.documentWidth).toBeLessThanOrEqual(1440);
  expect(measured.articleRight).toBeLessThanOrEqual(measured.dockLeft!);
});

test('mobile page menu keeps secondary actions reachable and palette fits the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
  const menu = page.getByRole('menu', { name: 'Další akce stránky', exact: true });
  await expect(menu.getByRole('menuitem', { name: /do oblíbených|z oblíbených/ })).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: 'Nová stránka', exact: true })).toBeVisible();
  // A phone's top bar has no search button of its own; search is the first item of this menu.
  await menu.getByRole('menuitem', { name: 'Hledat ve wiki', exact: true }).click();
  const palette = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  await palette.getByRole('button', { name: 'Příkazy', exact: false }).click();
  const bounds = await palette.boundingBox();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(844);
  await palette.getByRole('combobox', { name: 'Hledat příkaz', exact: true }).fill('> rezim cteni');
  await page.keyboard.press('Escape');
  await expect(palette).toBeHidden();
  await expect(page.getByRole('button', { name: 'Další akce stránky', exact: true })).toBeFocused();
});

test('resizing an open mobile drawer restores desktop interaction even with collapsed navigation', async ({ page }) => {
  await page.getByRole('button', { name: 'Sbalit boční navigaci', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
  await page.setViewportSize({ width: 1280, height: 960 });
  await expect(page.locator('main.workspace')).not.toHaveAttribute('inert');
  await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'Hlavní navigace', exact: true })).toBeVisible();
});

test('copy command from the mobile drawer makes the manual clipboard fallback accessible', async ({ page, context }) => {
  await context.grantPermissions([]);
  await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => Promise.reject(new DOMException('Clipboard unavailable', 'NotAllowedError')) }, configurable: true }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
  await page.getByRole('button', { name: /^Hledat ve wiki/ }).click();
  const palette = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  // Named after the scope, then 'Hledat příkaz' once '>' switches it to commands.
  const field = palette.getByRole('combobox', { name: /^Hledat (ve wiki|příkaz)/ });
  await field.fill('> kopirovat');
  await field.press('Enter');
  const fallback = page.getByRole('textbox', { name: 'Odkaz na stránku', exact: true });
  await expect(fallback).toBeFocused();
  await expect(fallback).toHaveValue(new RegExp(`#/pages/${pageId}$`));
  const fallbackBox = await page.locator('[data-slot="popover-content"]').filter({ has: fallback }).boundingBox();
  expect(fallbackBox!.x).toBeGreaterThanOrEqual(0);
  expect(fallbackBox!.x + fallbackBox!.width).toBeLessThanOrEqual(390);
  await expect(page.locator('main.workspace')).not.toHaveAttribute('inert');
});

// Below about 1280 px the dock cannot leave the page 600 px and floats over its edge instead (WorkspaceSidePanel).
for (const width of [1280, 1440, 1920]) {
  test(`docked outline reserves its own column at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 960 });
    await openDock(page);
    await expect(page.locator('.workspace-panel')).toHaveAttribute('data-mode', 'beside');
    const box = await article(page).boundingBox(), outline = await dock(page).boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(outline!.x);
    const heading = await page.locator('.document-header h1').boundingBox();
    expect(Math.abs(heading!.x - box!.x)).toBeLessThanOrEqual(1);
  });
}

test('wide document keeps a separate outline column and aligned title', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.keyboard.press('ControlOrMeta+Shift+p');
  const input = page.getByRole('combobox', { name: 'Hledat příkaz', exact: true });
  await input.fill('> siroky'); await input.press('Enter');
  await page.setViewportSize({ width: 390, height: 844 });
  expect((await geometry(page)).articleWidth).toBeGreaterThanOrEqual(340);
  for (const width of [1280, 1440, 1920]) {
    await page.setViewportSize({ width, height: 960 });
    await openDock(page);
    const box = await article(page).boundingBox();
    const panel = await dock(page).boundingBox();
    const heading = await page.locator('.document-header h1').boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(panel!.x);
    expect(Math.abs(box!.x - heading!.x)).toBeLessThanOrEqual(1);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  const mobileHeading = await page.locator('.document-header h1').boundingBox();
  expect(mobileHeading!.width).toBeGreaterThanOrEqual(300);
  expect((await geometry(page)).firstBlockTop).toBeLessThanOrEqual(281);
});

test('opening the editor keeps mobile save controls inside the viewport without changing the page', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await startEditing(page);
  await expect(page.locator('.document-title-input')).toHaveValue(title!);
  for (const button of [page.locator('[data-page-mobile-lock]')]) {
    await expect(button).toBeVisible();
    const box = await button.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});

test('a desktop dock does not displace the article after switching to mobile', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  await openDock(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(dock(page)).toBeHidden();
  const measured = await geometry(page);
  expect(measured.firstBlockTop).toBeLessThanOrEqual(281);
  expect(measured.articleWidth).toBeGreaterThanOrEqual(300);
  await page.setViewportSize({ width: 1440, height: 960 });
  await expect(dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible();
});

for (const width of [390, 1024, 1440, 1920]) {
  test(`reading surface stays in bounds at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 960 });
    await expect(page.getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible();
    // From 1440 px the reader's open dock is back beside the article.
    if (width >= 1440) await expect.poll(async () => (await geometry(page)).dockLeft).not.toBeNull();
    const measured = await geometry(page);
    await info.attach('reading-geometry.json', {
      body: JSON.stringify(measured, null, 2), contentType: 'application/json',
    });
    expect(measured.articleWidth).toBeGreaterThan(0);
    expect(measured.articleLeft).toBeGreaterThanOrEqual(0);
    expect(measured.articleRight).toBeLessThanOrEqual(width);
    expect(measured.documentWidth, 'The page must not scroll horizontally.').toBeLessThanOrEqual(width);
    if (measured.dockLeft !== null) {
      expect(measured.dockLeft >= measured.articleRight || measured.dockRight! <= measured.articleLeft,
        `The page dock overlaps the article: ${JSON.stringify(measured)}`).toBe(true);
    }
  });
}

// Proposed UX acceptance criteria, intentionally separate from regression checks.
// Enable with ATLAS_E2E_READING_PROPOSALS=1; use --grep @proposal to select only these.
test.describe('proposed reading improvements @proposal', () => {
  test.skip(process.env.ATLAS_E2E_READING_PROPOSALS !== '1', 'Opt in to proposed UX criteria.');
  test('mobile article starts within the first third of the screen', async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const measured = await geometry(page);
    await info.attach('mobile-budget.json', {
      body: JSON.stringify(measured, null, 2), contentType: 'application/json',
    });
    expect(measured.firstBlockTop).toBeGreaterThan(0);
    expect(measured.firstBlockTop).toBeLessThanOrEqual(281);
  });

  test('the page dock preserves article width on a desktop', async ({ page }, info) => {
    await page.setViewportSize({ width: 1440, height: 960 });
    await openDock(page);
    const docked = await geometry(page);
    await page.getByRole('button', { name: 'Boční panel stránky', exact: true }).click();
    await expect(dock(page)).toBeHidden();
    const floating = await geometry(page);
    await info.attach('docking-budget.json', {
      body: JSON.stringify({ floating, docked }, null, 2), contentType: 'application/json',
    });
    expect(docked.articleWidth).toBeGreaterThanOrEqual(floating.articleWidth);
  });
});
