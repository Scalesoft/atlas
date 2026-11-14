import { test, expect, type Page } from '@playwright/test';
import { PAGE_TEXT } from "./editing";
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Two things this file measures on a real wiki instance, both about the document
// surface rather than about any single control:
//
//   1. „Na této stránce“ — the page outline (src/Atlas.Web/src/components/PageOutline.vue, refresh 2.5). It replaced
//      the editor's floating outline rail: without the page dock it is one quiet row above the article that unfolds in
//      place (variant „disclosure“), and the page dock („Boční panel stránky“, tab Obsah) lists it beside the article
//      (variant „list“), where the active heading follows the reading position.
//
//   2. „Prostor pro obsah“ — how much of the viewport actually carries article text, written as relative invariants
//      (no magic pixel budgets): „Široký dokument“ uses a wider screen, the dock beside the article costs gutter and
//      not reading width, the dock never lies on the text unless it is the overlay drawer, and a phone keeps most of
//      its height for the article.
//
// Like ui-unification.spec.ts this file uses whatever account ATLAS_E2E_USER/ATLAS_E2E_PASSWORD supply (the plain
// `test`/`test` development account works) and logs in exactly once, shared via storageState, to stay under the
// ten-logins-per-minute builtin auth guard (README.md „Pořadí průchodů“).
const ATLAS_USER = process.env.ATLAS_E2E_USER;
const ATLAS_PASSWORD = process.env.ATLAS_E2E_PASSWORD;
const STORAGE_STATE = path.join(__dirname, '..', '..', 'artifacts', 'browser-tests', 'outline-reading-space-state.json');

// test.use with trace/screenshot/video must stay top-level (Playwright forces a new
// worker otherwise), so this file skips itself outright rather than nesting the skip
// inside a describe block.
test.skip(!ATLAS_USER || !ATLAS_PASSWORD, 'Requires ATLAS_E2E_USER and ATLAS_E2E_PASSWORD.');
// The local dev instance serves the ASP.NET Core dev certificate: the OS trusts it but
// Node's own TLS validation (used by context.request) does not — hence ignoreHTTPSErrors,
// matching ui-unification.spec.ts.
test.use({ trace: 'off', screenshot: 'off', video: 'off', storageState: STORAGE_STATE, ignoreHTTPSErrors: true, contextOptions: { reducedMotion: 'reduce' } });

/** Headings the fixture document carries; enough of them to fill several screens. */
const SECTIONS = [
  'Přehled a rozsah', 'Architektura', 'Datový model', 'Oprávnění', 'Publikování',
  'Vyhledávání', 'Import obsahu', 'Přílohy', 'Historie revizí', 'Provoz a záloha',
  'Známá omezení', 'Další kroky',
];
const FILLER =
  'Tento odstavec existuje jen proto, aby dokument měl skutečnou výšku a osnova ' +
  'měla co sledovat při rolování. Text se neposuzuje, posuzuje se geometrie sazby.';

interface Geometry {
  viewport: { width: number; height: number };
  /** Rendered width of the ProseMirror article box — the reading column. */
  textColumn: number;
  textLeft: number;
  textRight: number;
  /** Left edge of the workspace's content box, and the text's inset inside it (--article-gutter). */
  contentLeft: number;
  gutter: number;
  /** Viewport y of the first block inside the article: everything above it is chrome. */
  firstLineTop: number | null;
  /** The page dock, when it is open: where it starts and whether it sits beside the article or over it. */
  dock: { left: number; mode: string | null } | null;
}

const outlineRow = (page: Page) => page.locator('.document-header').getByRole('navigation', { name: 'Na této stránce', exact: true });
const dock = (page: Page) => page.getByRole('complementary', { name: 'Boční panel stránky', exact: true });
const dockToggle = (page: Page) => page.getByRole('button', { name: 'Boční panel stránky', exact: true });
const dockOutline = (page: Page) => dock(page).getByRole('navigation', { name: 'Na této stránce', exact: true });

/**
 * Waits until nothing animates. Polls the play state: a transition in a collapsed sidebar can end as „finished“
 * while its `finished` promise never settles (wiki-wide-outline.spec.ts).
 */
async function settle(page: Page) {
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation => animation.playState === 'running').length)).toBe(0);
}

async function readGeometry(page: Page): Promise<Geometry> {
  await settle(page);
  return page.evaluate(() => {
    const article = document.querySelector('.document-body [role="document"] .ProseMirror');
    const articleBox = article?.getBoundingClientRect();
    const firstBlock = article?.querySelector('p, h1, h2, h3, li');
    const panel = document.querySelector<HTMLElement>('.workspace-panel');
    const panelBox = panel?.getBoundingClientRect();
    return {
      viewport: { width: window.innerWidth, height: window.innerHeight },
      textColumn: articleBox ? Math.round(articleBox.width) : 0,
      textLeft: articleBox ? Math.round(articleBox.left) : 0,
      textRight: articleBox ? Math.round(articleBox.right) : 0,
      contentLeft: Math.round(document.querySelector('.workspace-content')?.getBoundingClientRect().left ?? 0),
      gutter: parseFloat(getComputedStyle(document.querySelector('.document-body') ?? document.body).getPropertyValue('--article-gutter')) || 0,
      firstLineTop: firstBlock ? Math.round(firstBlock.getBoundingClientRect().top) : null,
      dock: panelBox && panelBox.width > 0 ? { left: Math.round(panelBox.left), mode: panel!.dataset.mode ?? null } : null,
    };
  });
}

/**
 * The dock comes back by itself on a wide workspace, and that return can land between reading the toggle and
 * clicking it, so both helpers repeat until the state holds (wiki-wide-outline.spec.ts).
 */
async function openDock(page: Page) {
  await expect(async () => {
    if (await dockToggle(page).getAttribute('aria-pressed') !== 'true') await dockToggle(page).click();
    await expect(dockToggle(page)).toHaveAttribute('aria-pressed', 'true', { timeout: 1000 });
    await expect(dockOutline(page)).toBeVisible({ timeout: 1000 });
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

/** „Široký dokument“ / „Pohodlná šířka“ from the page's „…“ menu; it is remembered per account in this browser. */
async function setReadingWidth(page: Page, width: 'comfortable' | 'wide') {
  if (await page.locator('.atlas').getAttribute('data-reading-width') === width) return;
  await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
  await page.getByRole('menuitem', { name: width === 'wide' ? 'Široký dokument' : 'Pohodlná šířka', exact: true }).click();
  await expect(page.locator('.atlas')).toHaveAttribute('data-reading-width', width);
}

test.describe('Osnova dokumentu a prostor pro obsah', () => {
  test.beforeAll(async ({ browser }) => {
    // Playwright applies this file's test.use() options (storageState included) to any
    // browser.newContext() made inside a test/hook, so the very first run — before
    // STORAGE_STATE exists — must explicitly opt back out of it here.
    const context = await browser.newContext({ ignoreHTTPSErrors: true, storageState: undefined });
    const page = await context.newPage();
    await page.goto('/');
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(ATLAS_USER!);
    await page.getByLabel('Heslo', { exact: true }).fill(ATLAS_PASSWORD!);
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    await context.storageState({ path: STORAGE_STATE });
    await context.close();
  });

  const createdPageIds: string[] = [];
  /** The heading-rich fixture, created once and reused by every test in the file. */
  let fixtureId = '';

  /**
   * A published page with SECTIONS.length h2 headings and three filler paragraphs each.
   * Built through the API rather than the editor: the outline reads the rendered
   * ProseMirror document, so the fixture only has to arrive in the right shape.
   */
  async function createHeadingRichDocument(page: Page) {
    const csrf = await (await page.context().request.get('/api/auth/csrf')).json();
    const headers = { 'X-CSRF-TOKEN': csrf.token };
    const spaces = await (await page.context().request.get('/api/spaces')).json();
    expect(spaces.length, 'the instance needs at least one space to host the fixture').toBeGreaterThan(0);

    const content: unknown[] = [];
    const markdown: string[] = [];
    for (const section of SECTIONS) {
      content.push({ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: section }] });
      markdown.push(`## ${section}`);
      for (let paragraph = 0; paragraph < 3; paragraph += 1) {
        content.push({ type: 'paragraph', content: [{ type: 'text', text: FILLER }] });
        markdown.push(FILLER);
      }
    }

    const title = `Osnova a sazba ${Date.now()}`;
    const created = await page.context().request.post('/api/pages', {
      headers,
      data: {
        spaceId: spaces[0].id, parentId: null, title,
        markdown: markdown.join('\n\n'),
        contentJson: JSON.stringify({
          format: 'atlas-rich', version: 1,
          document: { type: 'doc', content },
        }),
      },
    });
    expect(created.status()).toBe(200);
    const document = await created.json();
    createdPageIds.push(document.id);
    const published = await page.context().request.post(`/api/pages/${document.id}/publish`, {
      headers, data: { expectedVersion: document.version },
    });
    expect(published.status()).toBe(200);
    return document.id as string;
  }

  /** Opens the fixture at a 1280 px viewport (the dock stays closed there) and waits for the article and its outline. */
  async function openFixture(page: Page) {
    if (!fixtureId) fixtureId = await createHeadingRichDocument(page);
    await page.goto(`/#/pages/${fixtureId}`);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
    await expect(page.locator('.ProseMirror h2').first()).toBeVisible();
    await setReadingWidth(page, 'comfortable');
    await closeDock(page);
    // The outline collects headings one frame after the document renders; wait for the row to agree with the
    // document instead of sleeping.
    await expect(outlineRow(page).getByRole('button', { name: /Na této stránce/ })).toContainText(`${SECTIONS.length} sekcí`);
  }

  test.afterAll(async ({ browser }) => {
    // There is no hard-delete endpoint for a page (by design — see
    // PageOrganizationEndpoints); move each fixture to trash instead, the same action a
    // user takes. TrashAsync is optimistic-concurrency-checked against both the page and
    // its space's tree version, so both are re-fetched immediately before the call.
    if (!createdPageIds.length) return;
    const context = await browser.newContext({ ignoreHTTPSErrors: true, storageState: STORAGE_STATE });
    try {
      const page = await context.newPage();
      const csrf = await (await page.context().request.get('/api/auth/csrf')).json();
      const headers = { 'X-CSRF-TOKEN': csrf.token };
      for (const id of createdPageIds) {
        const pageResponse = await page.context().request.get(`/api/pages/${id}`);
        if (!pageResponse.ok()) continue; // already gone
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

  // ---------------------------------------------------------------------------
  // 1. The outline.
  // ---------------------------------------------------------------------------

  test('the outline lists every heading and a click scrolls to that section', async ({ page }) => {
    await openFixture(page);

    // Without the dock the outline is one row above the article; it unfolds in place with every heading as text.
    const toggle = outlineRow(page).getByRole('button', { name: /Na této stránce/ });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const entries = outlineRow(page).getByRole('listitem').getByRole('button');
    await expect(entries).toHaveCount(SECTIONS.length);
    await expect(entries).toHaveText(SECTIONS);

    // Clicking an entry well below the fold brings that heading into the viewport and folds the row again.
    const targetTitle = SECTIONS[SECTIONS.length - 3]!;
    await entries.filter({ hasText: targetTitle }).click();
    const heading = page.locator('.ProseMirror h2', { hasText: targetTitle });
    await expect(heading).toBeInViewport();
    expect(await page.evaluate(() => Math.round(window.scrollY)),
      'clicking an entry below the fold has to scroll the document').toBeGreaterThan(0);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  test('the outline follows the reading position while the document scrolls', async ({ page }) => {
    await openFixture(page);
    await openDock(page);
    const current = dockOutline(page).locator('[aria-current="location"]');

    await page.evaluate(() => window.scrollTo(0, 0));
    await expect(current).toHaveText(SECTIONS[0]!);

    // Scroll deep into the document, then compare the outline against the heading the document itself says is
    // current. activeHeadingIndex() (src/editor/pageOutline.ts) marks the last heading whose top has passed the reading
    // line 32 px under the sticky top bar; deriving the expectation from the live DOM keeps this test independent of
    // that exact offset instead of guessing where the scroll happens to land.
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight * 0.45));
    await settle(page);
    const expectedTitle = await page.evaluate(() => {
      const line = document.querySelector('.workspace-bar')!.getBoundingClientRect().bottom + 32;
      const headings = [...document.querySelectorAll('.ProseMirror h2')];
      let index = 0;
      for (let i = 0; i < headings.length; i += 1) {
        if (headings[i]!.getBoundingClientRect().top <= line) index = i;
        else break;
      }
      return headings[index]!.textContent!.trim();
    });
    expect(expectedTitle, 'the scroll has to move past the first section').not.toBe(SECTIONS[0]);
    await expect(current).toHaveText(expectedTitle);
  });

  test('opening the page dock moves the outline into it and the row steps aside', async ({ page }) => {
    await openFixture(page);
    await expect(outlineRow(page)).toBeVisible();

    await openDock(page);
    await expect(dock(page).getByRole('tab', { name: 'Obsah', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(dockOutline(page).getByRole('button')).toHaveText(SECTIONS);
    // One outline at a time: the row above the article gives way to the dock's list...
    await expect(outlineRow(page)).toHaveCount(0);

    // ...and comes back when the dock closes.
    await closeDock(page);
    await expect(outlineRow(page)).toBeVisible();
  });

  // ---------------------------------------------------------------------------
  // 2. Prostor pro obsah — invariants the reading surface holds.
  // ---------------------------------------------------------------------------

  test('„Široký dokument“ uses a wider screen', async ({ page }) => {
    await openFixture(page);
    await setReadingWidth(page, 'wide');
    try {
      const byWidth = new Map<number, Geometry>();
      for (const width of [1280, 1440, 1920, 2560]) {
        await page.setViewportSize({ width, height: 960 });
        await closeDock(page);
        await expect.poll(async () => (await readGeometry(page)).viewport.width).toBe(width);
        byWidth.set(width, await readGeometry(page));
      }
      const summary = [...byWidth].map(([width, geometry]) =>
        `${width}px → ${geometry.textColumn}px (${Math.round((geometry.textColumn / width) * 100)}%)`).join(', ');

      // Measured 2026-09-09 (before „Široký dokument“): 740px at every width — 29% of a 2560 viewport. The comfortable
      // width keeps that readable measure on purpose (AGENTS.md: prose keeps a readable measure); the wide one follows
      // the room up to --atlas-page-max.
      expect(byWidth.get(2560)!.textColumn,
        `the wide reading column must use some of a wider screen (measured: ${summary})`)
        .toBeGreaterThan(byWidth.get(1440)!.textColumn);
    } finally {
      await setReadingWidth(page, 'comfortable');
    }
  });

  test('the dock beside the article costs gutter, not reading width', async ({ page }) => {
    await openFixture(page);
    await page.setViewportSize({ width: 1440, height: 960 });
    await closeDock(page);
    const floating = await readGeometry(page);

    await openDock(page);
    const docked = await readGeometry(page);

    // Measured 2026-09-09 with the old docked outline at 1440x960: 740px floating → 636px docked while 444px of free
    // margin sat unused beside it. The dock has to spend that free margin first: the article may narrow only once
    // nothing but the text's two insets is left around it.
    expect(docked.dock?.mode, 'at 1440 px the dock has room beside the article').toBe('beside');
    expect(docked.textRight, 'the dock starts after the article').toBeLessThanOrEqual(docked.dock!.left);
    if (docked.textColumn < floating.textColumn) {
      const margin = (docked.textLeft - docked.contentLeft) + (docked.dock!.left - docked.textRight);
      expect(margin,
        `the article narrowed (closed ${floating.textColumn}px → open ${docked.textColumn}px) while ${margin}px of margin remained beside it`)
        .toBeLessThanOrEqual(2 * docked.gutter + 2);
    }
  });

  test('an open dock stays open on the next document', async ({ page }) => {
    // Create the second document up front, so nothing but the navigation itself happens
    // between opening the dock and checking whether the choice survived.
    await openFixture(page);
    const second = await createHeadingRichDocument(page);
    await openDock(page);

    await page.goto(`/#/pages/${second}`);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
    await expect(page.locator('.ProseMirror h2').first()).toBeVisible();

    // Measured 2026-09-09 with the old outline: pinning was lost on every navigation.
    await expect(dockToggle(page), 'the reader opened the dock; opening another page should not undo that')
      .toHaveAttribute('aria-pressed', 'true');
    await expect(dockOutline(page).getByRole('button')).toHaveCount(SECTIONS.length);
  });

  test('the dock never lies on the text unless it is the overlay drawer', async ({ page }) => {
    await openFixture(page);

    const overlaps: string[] = [];
    for (const width of [900, 1000, 1024, 1100, 1280, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      await openDock(page);
      await expect.poll(async () => (await readGeometry(page)).viewport.width).toBe(width);
      const geometry = await readGeometry(page);
      // Without room for 600 px of article the dock floats over the article's edge by design (AGENTS.md, the page dock).
      if (geometry.dock?.mode !== 'beside') continue;
      if (geometry.dock.left < geometry.textRight) {
        overlaps.push(`${width}px: dock at x=${geometry.dock.left}, text ends at x=${geometry.textRight} (${geometry.textRight - geometry.dock.left}px over)`);
      }
    }

    // Measured 2026-09-09 with the old rail: at 1024px it started at x=968 while the article ran to x=984.
    expect(overlaps, 'the dock beside the article must stay clear of the text column').toEqual([]);
  });

  test('a phone spends its height on content rather than chrome', async ({ page }) => {
    await openFixture(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await expect.poll(async () => (await readGeometry(page)).viewport.width).toBe(390);
    await page.evaluate(() => window.scrollTo(0, 0));
    const geometry = await readGeometry(page);

    // Measured 2026-09-09 at 390x844: the first line of the article started at y=376 — 45% of the viewport — because
    // the top bar wrapped its actions onto three rows above a tall document header. A third of the screen is the budget.
    expect(geometry.firstLineTop,
      `chrome above the article on a 844px-tall phone (measured: ${geometry.firstLineTop}px)`)
      .toBeLessThanOrEqual(Math.round(geometry.viewport.height / 3));
  });
});
