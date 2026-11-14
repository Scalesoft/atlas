import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// Synthetic data only: the library and article tests answer every /api call themselves and run against any build;
// the gallery tests need the Vite dev server. The gallery mounts the production panel, not a replica.
const shots = resolve('../../artifacts/responsive-workspace');
mkdirSync(shots, { recursive: true });

test('library uses a balanced wide frame and does not overflow a notebook or phone', async ({ page }) => {
  const spaces = Array.from({ length: 12 }, (_, i) => ({
    id: `space-${i}`, name: ['Produktová dokumentace', 'Runbooky a provoz', 'Lidé a onboarding'][i % 3] + ` ${i + 1}`,
    description: 'Dokumentace, rozhodnutí a návody pro každodenní práci týmu.', icon: 'book', color: 'blue', role: 'Reader',
    pinPolicy: 'Default', pinned: i < 3, pageCount: 24 + i, lastChangedAt: '2026-10-09T10:00:00Z',
  }));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const json = path === '/api/setup/status' ? { version: '2.6.0-dev+ui-fixture', databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true }
      : path === '/api/auth/me' ? { id: 'layout-user', username: 'layout', displayName: 'Ukázkový účet', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'teal', mode: 'light' } }
      : path === '/api/spaces' ? spaces
      : path.startsWith('/api/me/library/') ? { items: [], hasMore: false, nextOffset: null }
      : undefined;
    if (json === undefined) { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json });
  });
  await page.goto('/#/library/spaces');
  await expect(page.locator('.library-overview')).toBeVisible();
  for (const width of [1366, 1920, 3440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const frame = await page.locator('.library-overview').boundingBox();
    const content = await page.locator('.workspace-content').boundingBox();
    expect(frame!.width).toBeLessThanOrEqual(1600);
    expect(Math.abs(frame!.x - content!.x - (content!.width - frame!.width) / 2)).toBeLessThan(2);
    if (width === 3440) {
      expect(frame!.width).toBeGreaterThan(1400);
      const grid = await page.locator('.space-directory-grid').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
      expect(grid).toBeGreaterThanOrEqual(4);
    }
    await expect.poll(() => page.locator('.library-overview').evaluate(el => el.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length)).toBe(0);
    await page.screenshot({ path: resolve(shots, `library-${width}.png`) });
  }
});

test.describe('gallery', () => {
  // main.ts mounts the gallery only under import.meta.env.DEV: a published build ignores ?gallery=1 and shows the wiki.
  // /@vite/client is a script only on the Vite dev server; a published server answers it with its HTML fallback.
  test.beforeEach(async ({ request }) => {
    const client = await request.get('/@vite/client');
    test.skip(!client.headers()['content-type']?.includes('javascript'), 'The gallery exists only on the Vite dev server (/?gallery=1).');
  });

  for (const reducedMotion of ['no-preference', 'reduce'] as const) {
    test(`panel adapts to its container, exits smoothly and keeps focus (${reducedMotion})`, async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.emulateMedia({ reducedMotion });
      await page.goto('/?gallery=1#/gallery/refresh');
      const example = page.locator('[data-refresh-example="ArticlePageDockExample"]');
      const trigger = example.getByRole('button', { name: 'Otevřít panel ukázky' });
      await trigger.click();
      const panel = example.locator('.workspace-panel');
      await expect(panel).toHaveAttribute('data-mode', 'beside');
      await expect.poll(async () => (await example.locator('.dock-article').boundingBox())!.width).toBeGreaterThanOrEqual(599);
      await example.getByLabel('Šířka pracovní plochy').selectOption('800');
      await expect(panel).toHaveAttribute('data-mode', 'overlay');
      await expect.poll(async () => (await example.locator('.dock-article').boundingBox())!.width).toBeGreaterThan(750);
      await panel.getByRole('button', { name: 'Zavřít panel', exact: true }).click();
      await expect(panel).toHaveCount(0);
      await expect(trigger).toBeFocused();
      await example.getByLabel('Šířka pracovní plochy').selectOption('1200');
      await trigger.click();
      await expect(panel).toHaveAttribute('data-mode', 'beside');
      // Keep the close/reopen within the transition without Playwright's stability wait.
      await example.getByRole('button', { name: 'Zavřít panel ukázky' }).evaluate(el => (el as HTMLButtonElement).click());
      await trigger.evaluate(el => (el as HTMLButtonElement).click());
      await expect(panel).toBeVisible();
      await expect.poll(() => panel.evaluate(el => el.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length)).toBe(0);
      await expect(panel.locator('.workspace-panel-surface')).not.toHaveAttribute('inert');
      // 340 px: the example opens at the dock's default width, where its four tabs (Obsah, Asistent, Historie, Přístup) fit.
      await expect(panel).toHaveCSS('width', '340px');
      await page.screenshot({ path: resolve(shots, `panel-${reducedMotion}.png`) });
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(panel).toHaveAttribute('data-mode', 'overlay');
      await expect(panel).toHaveCSS('width', '390px');
      await panel.getByRole('button', { name: 'Zavřít panel', exact: true }).press('Escape');
      await expect(panel).toHaveCount(0);
    });
  }

  test('AI has visible motion at button size and reduces to an opacity fade', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.goto('/?gallery=1#/gallery/ai');
    const size = page.locator('[data-glyph-size="s"] [data-ai-glyph]');
    await expect(size).toHaveAttribute('data-state', 'thinking');
    const changes = await size.evaluate(el => {
      const animations = el.getAnimations({ subtree: true });
      const star = el.querySelector('.ai-glyph-mark > :first-child')!;
      const sample = (time: number) => {
        animations.forEach(a => { a.pause(); a.currentTime = time; });
        const box = star.getBoundingClientRect();
        return { width: box.width, rotate: getComputedStyle(star).rotate };
      };
      const start = sample(0), middle = sample(800);
      animations.forEach(a => a.play());
      return { count: animations.length, start, middle };
    });
    expect(changes.count).toBeGreaterThan(1);
    expect(changes.start.rotate).not.toBe(changes.middle.rotate);
    expect(Math.abs(changes.start.width - changes.middle.width)).toBeGreaterThan(1);
    await page.getByLabel('Pohyb ve skutečné velikosti').selectOption('writing');
    await expect(size).toHaveAttribute('data-state', 'writing');
    await page.getByLabel('Pohyb ve skutečné velikosti').scrollIntoViewIfNeeded();
    await page.screenshot({ path: resolve(shots, 'ai-motion.png') });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect.poll(() => size.locator('.ai-glyph-mark').evaluate(el => getComputedStyle(el).animationName)).toBe('atlas-ai-fade-breathe');
    expect(await size.locator('.ai-glyph-mark > *').evaluateAll(elements => elements.every(el => getComputedStyle(el).animationName === 'none'))).toBe(true);
  });
});

test('article and dock share available width with a widened rail in both themes', async ({ page }) => {
  const contentJson = JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
    { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Přehled architektury' }] },
    ...Array.from({ length: 12 }, () => ({ type: 'paragraph', content: [{ type: 'text', text: 'Dokumentace pomáhá týmu sdílet rozhodnutí. Text má zůstat pohodlně čitelný na notebooku i na širokém monitoru, také při otevřeném panelu stránky.' }] })),
  ] } });
  const article = { id: 'page', spaceId: 'space', parentId: null, title: 'Dokumentace a spolupráce', version: 1, isPublished: true, publishedVersion: 1, contentJson, activeFrom: '2026-10-09T10:00:00Z' };
  let mode = 'light';
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const value = path === '/api/setup/status' ? { version: '2.6.0-dev+ui-fixture', databaseReady: true, requiresSetup: false }
      : path === '/api/auth/me' ? { id: 'reader', username: 'reader', displayName: 'Ukázkový účet', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode } }
      : path === '/api/spaces' ? [{ id: 'space', name: 'Vývoj', icon: 'book', color: 'blue', role: 'Reader', pinned: true }]
      : path === '/api/spaces/space/tree' ? { spaceId: 'space', version: 1, items: [{ ...article, sortOrder: 1024, canEdit: false }] }
      : path === '/api/pages/page' ? article
      : path === '/api/pages/short' ? { ...article, id: 'short', title: 'Krátká stránka', contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Jeden odstavec.' }] }] } }) }
      : /\/personal-state$|\/visit$/.test(path) ? { isFavorite: false }
      : path.endsWith('/tags') ? { pageId: 'page', tags: [] }
      : path.startsWith('/api/me/library/') ? { items: [], hasMore: false, nextOffset: null }
      : undefined;
    await route.fulfill(value === undefined ? { status: 404, json: {} } : { json: value });
  });
  await page.addInitScript(() => {
    localStorage.setItem('atlas:reading:reader:sidebar-width', '480');
    localStorage.setItem('atlas:reading:reader:dock', 'open');
  });
  for (mode of ['light', 'dark']) {
    await page.setViewportSize({ width: 1920, height: 1000 });
    await page.goto('/#/pages/page');
    if (mode === 'dark') await page.reload();
    await expect(page.locator('.document-body .ProseMirror')).toContainText('Dokumentace pomáhá');
    const dock = page.locator('.workspace-panel');
    await expect(dock).toHaveAttribute('data-mode', 'beside');
    const rail = page.locator('#wiki-navigation');
    // The breadcrumbs follow the rail in one move, each way. The way back to the rail used to be inserted and removed
    // with the state and pushed them 40 px the other way in the first frame (2.6.1).
    const watchTrail = () => page.evaluate(() => {
      const trail = document.querySelector('.breadcrumb-trail')!, seen: number[] = [];
      (window as unknown as { trailSeen: number[] }).trailSeen = seen;
      const tick = () => { seen.push(trail.getBoundingClientRect().x); if (seen.length < 90) requestAnimationFrame(tick); };
      tick();
    });
    const trailSeen = async () => {
      await expect.poll(() => page.evaluate(() => (window as unknown as { trailSeen: number[] }).trailSeen.length)).toBe(90);
      return page.evaluate(() => (window as unknown as { trailSeen: number[] }).trailSeen);
    };
    const collapse = page.getByRole('button', { name: 'Sbalit boční navigaci', exact: true });
    await watchTrail();
    await collapse.click();
    await expect(rail).toHaveAttribute('inert');
    const expand = page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true });
    await expect(expand).toBeFocused();
    const leaving = await trailSeen();
    expect(leaving[0]! - leaving.at(-1)!).toBeGreaterThan(300);
    leaving.forEach((x, index) => { if (index) expect(x, `frame ${index} while the rail leaves`).toBeLessThanOrEqual(leaving[index - 1]! + 0.5); });
    await watchTrail();
    await expand.click();
    await expect(rail).not.toHaveAttribute('inert');
    // The rail is visible, and so focusable, the moment it returns: its own toggle takes the focus over.
    await expect(collapse).toBeFocused();
    const returning = await trailSeen();
    expect(returning.at(-1)! - returning[0]!).toBeGreaterThan(300);
    returning.forEach((x, index) => { if (index) expect(x, `frame ${index} while the rail returns`).toBeGreaterThanOrEqual(returning[index - 1]! - 0.5); });
    for (const width of [1920, 1366, 1100, 390]) {
      if (width === 390) await rail.locator('.search-launch').focus();
      await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
      await expect.poll(() => page.evaluate(() => document.getAnimations().filter(a => a.playState === 'running' && a.effect?.getTiming().iterations !== Infinity).length)).toBe(0);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      const text = page.locator('.document-body .ProseMirror');
      const title = page.locator('.document-title');
      const body = await text.boundingBox(), heading = await title.boundingBox();
      expect(body!.width).toBeGreaterThan(width === 390 ? 280 : 430);
      expect(Math.abs(body!.x - heading!.x)).toBeLessThan(3);
      if (width === 1366) await expect(dock).toHaveCount(0);
      await page.screenshot({ path: resolve(shots, `article-${mode}-${width}.png`) });
      if (width === 390) {
        await expect(rail).toHaveAttribute('inert');
        await expect(page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true })).toBeFocused();
        await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
        await expect(rail).not.toHaveAttribute('inert');
        await expect(page.locator('main.workspace')).toHaveAttribute('inert');
        await rail.getByRole('button', { name: 'Zavřít menu', exact: true }).press('Escape');
        await expect(rail).toHaveAttribute('inert');
        await expect(page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true })).toBeFocused();
        // The page's bar of a phone sits at the bottom of the screen also under a page shorter than the screen, below its
        // last line (2.6.1: it followed the content and hung in the middle of the screen).
        await page.goto('/#/pages/short');
        await expect(page.locator('.document-body .ProseMirror')).toContainText('Jeden odstavec.');
        const actions = page.getByRole('navigation', { name: 'Akce stránky', exact: true });
        await expect.poll(async () => { const box = await actions.boundingBox(); return box ? Math.round(box.y + box.height) : 0; }).toBe(844);
        expect((await page.locator('.document-body').boundingBox())!.y + (await page.locator('.document-body').boundingBox())!.height).toBeLessThan((await actions.boundingBox())!.y);
        expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBe(844);
      }
      if (width === 1100) {
        const textWidth = body!.width;
        await page.getByRole('button', { name: 'Boční panel stránky', exact: true }).click();
        await expect(dock).toHaveAttribute('data-mode', 'overlay');
        await expect.poll(() => dock.evaluate(el => el.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length)).toBe(0);
        const box = (await dock.boundingBox())!, bar = (await page.locator('.workspace-bar').boundingBox())!;
        expect(Math.abs(box.x + box.width - width)).toBeLessThan(2);
        expect(Math.abs(box.y - bar.y - bar.height)).toBeLessThan(2);
        expect(Math.abs((await text.boundingBox())!.width - textWidth)).toBeLessThan(2);
        await page.screenshot({ path: resolve(shots, `article-${mode}-overlay.png`) });
        await dock.getByRole('button', { name: 'Zavřít panel', exact: true }).click();
        await expect(dock).toHaveCount(0);
      }
    }
  }
});
