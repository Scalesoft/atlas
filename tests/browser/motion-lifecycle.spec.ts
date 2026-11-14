import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const shots = resolve('../../artifacts/motion-lifecycle');
mkdirSync(shots, { recursive: true });

for (const reducedMotion of ['no-preference', 'reduce'] as const) {
  test(`disclosure reverses without a height or opacity jump and preserves drafts (${reducedMotion})`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion });
    await page.setViewportSize({ width: reducedMotion === 'reduce' ? 390 : 1366, height: 1000 });
    await page.goto('/?gallery=1#/gallery/controls');
    const demo = page.locator('[data-motion-lifecycle-example]');
    const trigger = demo.locator('button[aria-controls="gallery-reveal-lifecycle"]');
    const reveal = demo.locator('#gallery-reveal-lifecycle');
    await trigger.click();
    await expect(reveal).toHaveClass(/is-settled/);
    await demo.getByLabel('Poznámka k ukázce').fill('Rozepsaná poznámka');
    await trigger.click();
    await expect(reveal).toHaveAttribute('hidden', 'until-found');
    await trigger.click();
    const before = await reveal.evaluate(el => {
      const active = el.getAnimations()[0]!;
      active.pause();
      active.currentTime = Number(active.effect!.getTiming().duration) * .4;
      return { height: el.getBoundingClientRect().height, opacity: getComputedStyle(el).opacity };
    });
    await trigger.dispatchEvent('click');
    const frame = await reveal.evaluate(el => el.getAnimations()[0]!.effect!.getKeyframes()[0]!);
    expect(Number(frame.opacity)).toBeCloseTo(Number(before.opacity), 2);
    if (reducedMotion === 'no-preference') expect(parseFloat(String(frame.height))).toBeCloseTo(before.height, 1);
    else expect(frame.height).toBeUndefined();
    await trigger.dispatchEvent('click');
    await demo.getByRole('button', { name: 'Přidat obsah', exact: true }).click();
    await demo.getByRole('button', { name: 'Další možnosti', exact: true }).click();
    await expect(reveal).toHaveClass(/is-settled/);
    await expect(demo.getByLabel('Poznámka k ukázce')).toHaveValue('Rozepsaná poznámka');
    expect(await reveal.evaluate(el => el.hasAttribute('inert'))).toBe(false);
    await expect.poll(() => reveal.evaluate(el => el.getBoundingClientRect().height >= el.querySelector('.inline-reveal-inner')!.getBoundingClientRect().height - 1)).toBe(true);
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await demo.screenshot({ path: resolve(shots, `reveal-${reducedMotion}.png`) });
  });
}

test('the library directory glides the view chip, shows a pending pin as a spinner and fades its list out for a search that matches nothing', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/?gallery=1#/gallery/refresh');
  const example = page.locator('[data-refresh-example="LibraryDirectoryExample"]');
  const directory = example.locator('.space-directory');
  await directory.scrollIntoViewIfNeeded();
  const sample = (selector: string, read: string, ms: number) => page.evaluate(([selector, read, ms]) => new Promise<{ seen: number[]; gone: boolean }>(resolve => {
    const seen: number[] = [];
    const started = performance.now();
    const tick = () => {
      const element = document.querySelector(selector);
      if (element) seen.push(read === 'opacity' ? Number(getComputedStyle(element).opacity) : new DOMMatrixReadOnly(getComputedStyle(element, '::before').transform).m41);
      if (performance.now() - started < Number(ms)) requestAnimationFrame(tick); else resolve({ seen, gone: !element });
    };
    requestAnimationFrame(tick);
  }), [selector, read, ms] as const);

  // Karty -> Seznam: the one chip behind the choices travels instead of vanishing and appearing.
  const group = '[data-refresh-example="LibraryDirectoryExample"] .space-directory-view .option-pills-group';
  const travelled = sample(group, 'x', 700);
  await directory.getByRole('radio', { name: 'Seznam' }).click();
  const chip = await travelled;
  const positions = [...new Set(chip.seen.map(x => Math.round(x)))];
  expect(positions.length).toBeGreaterThan(3);
  expect(Math.max(...positions)).toBeGreaterThan(Math.min(...positions) + 40);

  // A pin whose request is running shows the spinner, an open arc: a fill (the pin is already pinned) would make it a solid blob.
  const pin = directory.locator('button.space-pin[aria-pressed="false"]').first();
  await pin.click();
  const pinned = directory.locator('button.space-pin[aria-busy="true"]');
  await expect(pinned).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => pinned.locator('svg path').first().evaluate(path => getComputedStyle(path).fill)).toMatch(/^(rgba\(0, 0, 0, 0\)|transparent)$/);
  await expect(directory.locator('button.space-pin[aria-busy="true"]')).toHaveCount(0);

  // A search with no match: the list leaves by fading (it is never removed at full opacity) and the empty state comes in.
  const stage = '[data-refresh-example="LibraryDirectoryExample"] .space-directory-stage';
  const fade = sample(stage, 'opacity', 600);
  await directory.getByRole('searchbox').or(directory.locator('input[data-space-search], [data-space-search] input')).first().fill('zzzz');
  const faded = await fade;
  expect(faded.gone).toBe(true);
  expect(faded.seen.some(opacity => opacity > 0.05 && opacity < 0.9)).toBe(true);
  expect(faded.seen.at(-1)!).toBeLessThan(0.5);
  await expect(directory.locator('[data-space-no-match]')).toBeVisible();
});

test('a menu stays above its dialog on a narrow screen and Escape unwinds focus one layer at a time', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/?gallery=1#/gallery/controls');
  const demo = page.locator('[data-motion-lifecycle-example]');
  const launch = demo.getByRole('button', { name: 'Dialog s nabídkou akcí' });
  await launch.click();
  const dialog = page.getByRole('dialog', { name: 'Nabídka uvnitř dialogu' });
  const trigger = dialog.getByRole('button', { name: 'Akce uvnitř dialogu' });
  await trigger.press('ArrowUp');
  const menu = dialog.getByRole('menu', { name: 'Akce uvnitř dialogu' });
  await expect(menu.getByRole('menuitem', { name: 'Poslední akce' })).toBeFocused();
  const box = await menu.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: resolve(shots, 'dialog-menu-mobile.png') });
  await page.keyboard.press('Escape');
  await expect(menu).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).not.toBeVisible();
  await expect(launch).toBeFocused();
});

test('a dialog taller than the window keeps its body out of the bands around its sticky header and footer', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 260 });
  await page.goto('/?gallery=1#/gallery/layouts');
  await page.getByRole('button', { name: 'Otevřít dialog' }).click();
  const dialog = page.getByRole('dialog', { name: 'Ukázkový dialog' });
  await expect(dialog).toBeVisible();
  // Scrolled to its middle, the body passes under both; the padding above the title and below the buttons belongs to them.
  const owners = await dialog.evaluate(el => {
    el.scrollTop = (el.scrollHeight - el.clientHeight) / 2;
    const box = el.getBoundingClientRect();
    const owner = (y: number) => document.elementFromPoint(box.left + box.width / 2, y)?.closest('.app-dialog-header, .app-dialog-footer')?.className ?? 'body';
    return { scrolled: el.scrollTop, above: owner(box.top + 12), below: owner(box.bottom - 12) };
  });
  expect(owners.scrolled).toBeGreaterThan(20);
  expect(owners.above).toContain('app-dialog-header');
  expect(owners.below).toContain('app-dialog-footer');
});

test('native dialog backdrop fades out and reverses from its visible opacity', async ({ page }) => {
  await page.goto('/?gallery=1#/gallery/controls');
  const launch = page.getByRole('button', { name: 'Dialog s nabídkou akcí' });
  await launch.click();
  const dialog = page.locator('[data-motion-lifecycle-example] dialog');
  await expect(dialog).toHaveAttribute('data-animated-backdrop', '');
  await expect.poll(() => dialog.evaluate(el => getComputedStyle(el, '::backdrop').opacity)).toBe('1');
  await dialog.getByRole('button', { name: 'Zavřít', exact: true }).click();
  const faded = await dialog.evaluate(el => {
    const active = el.getAnimations({ subtree: true }).find(a => (a.effect as KeyframeEffect).pseudoElement === '::backdrop')!;
    active.pause(); active.currentTime = Number(active.effect!.getTiming().duration) * .4;
    return Number(getComputedStyle(el, '::backdrop').opacity);
  });
  expect(faded).toBeGreaterThan(0);
  expect(faded).toBeLessThan(1);
  // A parent may reopen while the native dialog is still in its exiting top layer.
  await launch.dispatchEvent('click');
  const from = await dialog.evaluate(el => {
    const active = el.getAnimations({ subtree: true }).find(a => (a.effect as KeyframeEffect).pseudoElement === '::backdrop')!;
    return Number((active.effect as KeyframeEffect).getKeyframes()[0]!.opacity);
  });
  expect(from).toBeCloseTo(faded, 2);
  await expect(dialog).not.toHaveAttribute('inert', '');
  await expect.poll(() => dialog.evaluate(el => getComputedStyle(el, '::backdrop').opacity)).toBe('1');
  await dialog.getByRole('button', { name: 'Zavřít', exact: true }).click();
  await expect(dialog).not.toBeVisible();
});

test('the setup wizard changes its step without the card resizing in a single frame, and the new heading gets the focus', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/?gallery=1#/gallery/brand');
  const wizard = page.locator('.setup-wizard');
  await wizard.scrollIntoViewIfNeeded();
  await expect(wizard.locator('.sw-counter')).toHaveText('KROK 1 ZE 2');
  await page.evaluate(() => {
    (window as unknown as { cardHeights: Promise<number[]> }).cardHeights = new Promise(resolve => {
      const seen: number[] = [];
      const started = performance.now();
      const tick = (now: number) => {
        seen.push(document.querySelector('.setup-wizard')!.getBoundingClientRect().height);
        if (now - started < 900) requestAnimationFrame(tick); else resolve(seen);
      };
      requestAnimationFrame(tick);
    });
  });
  await page.getByRole('button', { name: 'Správce a token' }).click();
  const heights = await page.evaluate(() => (window as unknown as { cardHeights: Promise<number[]> }).cardHeights);
  const difference = Math.abs(heights.at(-1)! - heights[0]!);
  expect(difference).toBeGreaterThan(20);
  const biggestStep = Math.max(...heights.slice(1).map((height, index) => Math.abs(height - heights[index]!)));
  // The card eases to its new height; without the stage's height animation the whole difference came in one frame.
  expect(biggestStep).toBeLessThan(difference * 0.9);
  await expect(wizard.locator('.sw-counter')).toHaveText('KROK 2 ZE 2');
  await expect(wizard.getByRole('heading', { level: 1 })).toBeFocused();
});
