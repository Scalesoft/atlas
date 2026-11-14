import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const shots = resolve('../../artifacts/search-ui-audit');
mkdirSync(shots, { recursive: true });

test('search reveals filters and answers without an empty column or hidden active scope', async ({ page }) => {
  let modelCalls = 0;
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/ai/conversations') modelCalls++;
    const json = path === '/api/setup/status' ? { version: '2.6.0-dev+ui-fixture', databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true }
      : path === '/api/auth/me' ? { id: 'search-user', username: 'search', displayName: 'Ukázkový účet', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'teal', mode: 'light' } }
      : path === '/api/spaces' ? [{ id: 'space-1', name: 'Dokumentace a provoz', icon: 'book', color: 'blue', role: 'Reader', pinPolicy: 'None', pinned: false }]
      : path === '/api/ai/capabilities' ? { answer: true, assistant: true }
      : path === '/api/me/ai-preferences' ? { searchAnswer: 'ask', assistantScope: 'context' }
      : path.startsWith('/api/me/library/') || path === '/api/search' || path === '/api/tags' ? { items: [], hasMore: false, nextOffset: null }
      : undefined;
    await route.fulfill(json === undefined ? { status: 404, json: {} } : { json });
  });
  await page.goto('/#/library/spaces');
  await page.locator('.search-launch').click();
  const dialog = page.locator('.search-dialog');
  const input = dialog.locator('.omnisearch-input input');
  const filters = dialog.locator('#search-filters');
  for (const width of [1366, 390, 1920]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
    // The answer pane leaves as a wipe of a finished pane: what is inside keeps its width while the pane's track shrinks (it used to
    // re-wrap into a ribbon: 482 px down to nothing, the card growing taller as it narrowed).
    const dialogWidth = () => dialog.evaluate(el => el.getBoundingClientRect().width);
    await expect.poll(async () => { const before = await dialogWidth(); await page.waitForTimeout(120); return before === await dialogWidth(); }).toBe(true); // the dialog has finished following the viewport
    const closing = await dialog.evaluate(async el => {
      const widths: number[] = [];
      const field = el.querySelector<HTMLInputElement>('.omnisearch-input input')!;
      const sample = () => { const inner = el.querySelector('.search-answer-pane > *'); if (inner) widths.push(Math.round(inner.getBoundingClientRect().width)); };
      sample();
      const until = performance.now() + 450;
      await new Promise<void>(done => { const tick = () => { sample(); if (performance.now() < until) requestAnimationFrame(tick); else done(); }; requestAnimationFrame(tick); field.value = ''; field.dispatchEvent(new Event('input', { bubbles: true })); });
      return widths;
    });
    expect(new Set(closing).size, `the pane's content keeps one width while the pane closes at ${width}px: ${closing.join(' ')}`).toBeLessThanOrEqual(1);
    await input.fill('');
    await expect(dialog.locator('.search-answer-pane')).toHaveCount(0);
    await expect.poll(() => dialog.locator('.search-columns').evaluate(el => {
      const content = el.querySelector('.search-content')!.getBoundingClientRect();
      return Math.abs(content.width - el.getBoundingClientRect().width);
    })).toBeLessThan(2);
    await expect(filters).toHaveAttribute('aria-hidden', 'true');
    await dialog.getByRole('button', { name: 'Filtry', exact: true }).click();
    await filters.getByRole('radio', { name: 'Posledních 7 dní', exact: true }).click();
    await dialog.locator('.search-filter-toggle').click();
    await expect(filters).toHaveAttribute('aria-hidden', 'true');
    await expect(dialog.getByRole('button', { name: 'Zrušit filtr poslední změny' })).toBeVisible();
    await input.fill('VPN');
    await expect(dialog.locator('.search-answer-pane')).toBeVisible();
    await expect(dialog.locator('.search-source-status')).toContainText('hotovo');
    await expect.poll(() => dialog.locator('.search-columns').evaluate(el => el.getAnimations().filter(animation => animation.playState === 'running').length)).toBe(0);
    await expect(filters).toHaveAttribute('hidden', 'until-found');
    await expect(dialog.locator('.search-answer-pane')).toHaveCSS('opacity', '1');
    await expect.poll(() => dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: resolve(shots, `search-disclosure-${width}.png`) });
    await dialog.getByRole('button', { name: 'Zrušit filtr poslední změny' }).click();
  }
  await input.fill('');
  await dialog.locator('[data-search-ask-mode]').click();
  await expect(dialog.locator('.search-answer-pane')).toBeVisible();
  expect(modelCalls).toBe(0);
});

test('the palette unfolds from the search field and looks the same in the frame its unfolding ends as in the frame before', async ({ page }) => {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const json = path === '/api/setup/status' ? { version: '2.6.1-dev+ui-fixture', databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true }
      : path === '/api/auth/me' ? { id: 'search-user', username: 'search', displayName: 'Ukázkový účet', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'teal', mode: 'light' } }
      : path === '/api/spaces' ? [{ id: 'space-1', name: 'Dokumentace a provoz', icon: 'book', color: 'blue', role: 'Reader', pinPolicy: 'None', pinned: false }]
      : path.startsWith('/api/me/library/') || path === '/api/search' || path === '/api/tags' ? { items: [], hasMore: false, nextOffset: null }
      : undefined;
    await route.fulfill(json === undefined ? { status: 404, json: {} } : { json });
  });
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto('/#/library/spaces');
  const launch = page.locator('.search-launch');
  await expect(launch).toBeVisible();
  const dialog = page.locator('.search-dialog');
  // Near the palette (a laptop) the field grows into it; further than the field is wide (a wide monitor) the palette opens in place.
  for (const { width, height, travels } of [{ width: 1280, height: 720, travels: true }, { width: 1920, height: 1080, travels: false }]) {
  await page.setViewportSize({ width, height });
  // Open and hold the unfolding (and the backdrop): first on its first frame, then on its last.
  const first = await launch.evaluate(async el => {
    const field = el.getBoundingClientRect();
    (el as HTMLElement).click();
    await new Promise<void>(done => requestAnimationFrame(() => done()));
    const panel = document.querySelector('.search-dialog')!;
    const animations = panel.getAnimations({ subtree: true }).filter(item => (item.effect as KeyframeEffect).target === panel);
    for (const animation of animations) { animation.pause(); animation.currentTime = 0; }
    // The window the reader sees: the panel's box where it is carried to, cut by the clip.
    const box = panel.getBoundingClientRect();
    const transform = getComputedStyle(panel).transform;
    const [top, right, bottom, left] = /inset\(([^)]*?)(?: round|\))/.exec(getComputedStyle(panel).clipPath)![1]!.trim().split(/\s+/).map(Number.parseFloat) as [number, number, number, number];
    for (const animation of animations) animation.currentTime = Number(animation.effect!.getTiming().duration) - 0.01;
    const home = panel.getBoundingClientRect();
    return {
      transform,
      window: [box.left + left, box.top + top, box.width - left - right, box.height - top - bottom].map(Math.round),
      field: [field.left, field.top, field.width, field.height].map(Math.round),
      home: [home.left, home.top, home.width, home.height].map(Math.round),
    };
  });
  if (travels) expect(first.window, `${width}: the first frame of the unfolding is the search field itself`).toEqual(first.field);
  else {
    expect(first.transform, `${width}: the palette is not carried across the screen`).toBe('none');
    expect(first.window[0], `${width}: it opens from its own nearest edge`).toBe(first.home[0]);
    expect(first.window[1]).toBe(first.home[1]);
  }
  await expect(dialog).toBeVisible();
  const box = (await dialog.boundingBox())!;
  // Where the palette's shadow falls: the strip right under it.
  const strip = { x: Math.round(box.x + 100), y: Math.round(box.y + box.height + 1), width: Math.round(box.width - 200), height: 28 };
  const last = (await page.screenshot({ clip: strip })).toString('base64');
  await dialog.evaluate(el => { for (const animation of el.getAnimations({ subtree: true }).filter(item => (item.effect as KeyframeEffect).target === el)) animation.finish(); });
  await expect.poll(() => dialog.evaluate(el => getComputedStyle(el).clipPath)).toBe('none');
  const settled = (await page.screenshot({ clip: strip })).toString('base64');
  const difference = await page.evaluate(async ([first, second]) => {
    const pixels = async (source: string) => {
      const image = new Image();
      image.src = `data:image/png;base64,${source}`;
      await image.decode();
      const canvas = document.createElement('canvas');
      canvas.width = image.width; canvas.height = image.height;
      const context = canvas.getContext('2d')!;
      context.drawImage(image, 0, 0);
      return context.getImageData(0, 0, canvas.width, canvas.height).data;
    };
    const [a, b] = [await pixels(first!), await pixels(second!)];
    let sum = 0;
    for (let index = 0; index < a.length; index += 1) sum += Math.abs(a[index]! - b[index]!);
    return sum / a.length;
  }, [last, settled]);
  // The shadow used to be clipped away until the animation was removed: 15 levels of luminance in that one frame.
  expect(difference, `${width}: mean difference per channel between the last frame of the unfolding and the settled palette`).toBeLessThan(0.5);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  }
});

test('publication can reverse its closing motion and keeps the draft editable', async ({ page }) => {
  await page.goto('/?gallery=1#/gallery/ai');
  const example = page.locator('.settings-section').filter({ has: page.getByRole('heading', { name: 'PublishControl', exact: true }) });
  const arrow = example.getByRole('button', { name: 'Publikovat s popisem změny…', exact: true });
  await arrow.click();
  const panel = page.getByRole('dialog', { name: 'Před publikací', exact: true });
  await panel.getByRole('textbox', { name: 'Popis změny (nepovinné)' }).fill('Rozepsaný popis');
  await arrow.evaluate(el => (el as HTMLButtonElement).click());
  await arrow.evaluate(el => (el as HTMLButtonElement).click());
  await expect(arrow).toHaveAttribute('aria-expanded', 'true');
  await expect(panel).not.toHaveAttribute('inert');
  await expect(panel.getByRole('textbox')).toHaveValue('Rozepsaný popis');
  await expect(panel.getByRole('textbox')).toBeFocused();
  await panel.getByRole('textbox').press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(arrow).toBeFocused();
});
