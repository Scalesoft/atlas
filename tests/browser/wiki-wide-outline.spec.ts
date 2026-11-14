import { expect, test, type BrowserContext, type Page } from '@playwright/test';

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
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await page.goto('/');
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username!);
    const passwordField = page.getByLabel('Heslo', { exact: true });
    await expect(passwordField).toBeVisible();
    await passwordField.evaluate((element, value) => {
      (element as HTMLInputElement).value = value;
      element.dispatchEvent(new Event('input', { bubbles: true }));
    }, password!);
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    state = await context.storageState();
  } finally {
    await context.close();
  }
});

test.beforeEach(async ({ context, page }) => {
  await context.addCookies(state.cookies);
  await page.goto(`/#/pages/${pageId}`);
  await expect(page.locator('.document-header')
    .getByRole('heading', { name: title!, exact: true, level: 1 })).toBeVisible();
  const article = page.getByRole('document', { name: 'Obsah stránky', exact: true });
  await expect(article).toHaveClass(/\breadonly\b/);
  await expect(article.getByRole('heading', { name: section!, exact: true })).toBeAttached();
  await page.evaluate(() => document.fonts.ready);
  await expect(page.locator('.atlas')).toHaveAttribute('data-reading-width', 'comfortable');
});

/**
 * Waits until nothing animates. Polls the play state: a transition in a collapsed sidebar can end as „finished“
 * while its `finished` promise never settles, which hung a Promise.all over every animation.
 */
async function settle(page: Page) {
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation => animation.playState === 'running').length)).toBe(0);
}

const article = (page: Page) => page.getByRole('document', { name: 'Obsah stránky', exact: true }).locator('.ProseMirror');
const dock = (page: Page) => page.getByRole('complementary', { name: 'Boční panel stránky', exact: true });
const dockToggle = (page: Page) => page.getByRole('button', { name: 'Boční panel stránky', exact: true });

async function geometry(page: Page) {
  await settle(page);
  return page.evaluate(() => {
    const article = document.querySelector<HTMLElement>('.document-body [role="document"] .ProseMirror')!;
    const heading = document.querySelector<HTMLElement>('.document-header h1')!;
    const panel = document.querySelector<HTMLElement>('.workspace-panel')!;
    const articleBox = article.getBoundingClientRect();
    const headingBox = heading.getBoundingClientRect();
    const panelBox = panel.getBoundingClientRect();
    return {
      articleWidth: Math.round(articleBox.width),
      articleLeft: Math.round(articleBox.left),
      articleRight: Math.round(articleBox.right),
      headingLeft: Math.round(headingBox.left),
      panelLeft: Math.round(panelBox.left),
    };
  });
}

async function setSidebarWidth(page: Page, width: 272 | 480) {
  const separator = page.getByRole('separator', { name: 'Šířka boční navigace', exact: true });
  await separator.dblclick();
  if (width === 480) {
    await separator.focus();
    await separator.press('End');
  }
  await expect(separator).toHaveAttribute('aria-valuenow', String(width));
}

/**
 * The page dock (Obsah · … ) carries the outline. A wide workspace brings it back by itself, and that return can land
 * between reading the toggle and clicking it, so both helpers repeat until the state holds.
 */
async function openDock(page: Page) {
  await expect(async () => {
    if (await dockToggle(page).getAttribute('aria-pressed') !== 'true') await dockToggle(page).click();
    await expect(dockToggle(page)).toHaveAttribute('aria-pressed', 'true', { timeout: 1000 });
    await expect(dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true })).toBeVisible({ timeout: 1000 });
  }).toPass();
  await settle(page);
}

async function closeDock(page: Page) {
  await expect(async () => {
    if (await dockToggle(page).getAttribute('aria-pressed') === 'true') await dockToggle(page).click();
    await expect(dock(page)).toBeHidden({ timeout: 1000 });
  }).toPass();
  await settle(page);
}

// Only the dock follows the reader: without it the outline is the „Na této stránce“ row, which scrolls with the text.
for (const width of [1024, 1920]) {
  test(`scrolling keeps the page dock's outline below the top bar at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await openDock(page);
    await page.mouse.move(width / 3, 600);
    await page.mouse.wheel(0, 900);
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(500);
    await settle(page);

    const bar = (await page.locator('.workspace-bar').boundingBox())!;
    const toolbarBottom = bar.y + bar.height;
    const outline = dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true });
    expect((await outline.boundingBox())!.y, 'The outline must not enter the sticky page toolbar').toBeGreaterThanOrEqual(toolbarBottom + 8);
    const entry = outline.getByRole('button', { name: section!, exact: true });
    expect(await entry.evaluate(button => {
      const rect = button.getBoundingClientRect();
      return button.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2));
    }), 'The outline entry must remain clickable').toBe(true);
  });
}

test('spacious screens place the page dock in the margin without narrowing the article', async ({ page }) => {
  for (const viewportWidth of [1920, 2560]) {
    for (const sidebarWidth of [272, 480] as const) {
      await page.setViewportSize({ width: viewportWidth, height: 960 });
      await setSidebarWidth(page, sidebarWidth);
      await closeDock(page);
      // The open dock's width counts against the reading space, so the screen is judged without it.
      await expect(page.locator('.atlas')).toHaveAttribute('data-reading-space', 'spacious');
      const floatingWidth = await article(page).evaluate(element => Math.round(element.getBoundingClientRect().width));

      await openDock(page);
      const docked = await geometry(page);

      expect(docked.articleWidth,
        `${viewportWidth}px viewport / ${sidebarWidth}px sidebar: the dock must preserve article width`)
        .toBe(floatingWidth);
      expect(Math.abs(docked.headingLeft - docked.articleLeft),
        `${viewportWidth}px viewport / ${sidebarWidth}px sidebar: title and article must stay aligned`)
        .toBeLessThanOrEqual(1);
      expect(docked.articleRight,
        `${viewportWidth}px viewport / ${sidebarWidth}px sidebar: the dock must remain outside the article`)
        .toBeLessThanOrEqual(docked.panelLeft);
    }
  }
});

test('the spacious page dock reads as margin navigation instead of a floating popover', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 960 });
  await setSidebarWidth(page, 480);
  await openDock(page);

  const panel = page.locator('.workspace-panel');
  await expect(panel).toHaveAttribute('data-mode', 'beside');
  const appearance = await panel.evaluate(host => {
    const style = getComputedStyle(host.querySelector('.workspace-panel-surface')!);
    return {
      shadow: getComputedStyle(host).boxShadow,
      borderLeftWidth: parseFloat(style.borderLeftWidth),
      otherBorders: [style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth],
    };
  });

  expect(appearance.shadow).toBe('none');
  expect(appearance.borderLeftWidth).toBeGreaterThanOrEqual(1);
  expect(appearance.otherBorders).toEqual(['0px', '0px', '0px']);
  await expect(dock(page).getByRole('tab', { name: 'Obsah', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(dock(page).getByText('Na této stránce', { exact: true })).toBeVisible();
  // The whole outline is listed, not cut to the height of a popover.
  expect((await dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true }).boundingBox())!.height).toBeGreaterThan(260);
});
