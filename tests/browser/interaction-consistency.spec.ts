import { expect, test } from '@playwright/test';

// Production primitives in the gallery; no backend or persisted state.
test('hover keeps card hit areas steady and disabled choices unchanged', async ({ page }) => {
  await page.goto('/?gallery=1#/gallery/controls');
  const example = page.locator('[data-interaction-example]');
  await example.scrollIntoViewIfNeeded();
  await expect.poll(() => example.evaluate(el => el.getAnimations({ subtree: true }).filter(a => a.playState === 'running').length)).toBe(0);

  for (const selector of ['.choice-tile[data-value="available"]', '.space-card']) {
    const card = example.locator(selector);
    await card.scrollIntoViewIfNeeded();
    const before = await card.boundingBox();
    await page.mouse.move(before!.x + before!.width / 2, before!.y + before!.height - 0.5);
    await expect.poll(() => card.evaluate(el => getComputedStyle(el).transform)).toBe('none');
    const after = await card.boundingBox();
    expect(after!.y).toBeCloseTo(before!.y, 1);
    expect(await card.evaluate(el => el.matches(':hover'))).toBe(true);
  }

  for (const selector of ['.choice-tile[data-value="disabled"]', '.searchable-item[data-disabled]', '.level-step[data-disabled]']) {
    const target = example.locator(selector).last();
    await page.mouse.move(0, 0);
    await target.scrollIntoViewIfNeeded();
    const before = await target.evaluate(el => ({ border: getComputedStyle(el).borderColor, background: getComputedStyle(el).backgroundColor }));
    await target.hover();
    await page.mouse.down();
    await expect.poll(() => target.evaluate(el => ({ border: getComputedStyle(el).borderColor, background: getComputedStyle(el).backgroundColor, transform: getComputedStyle(el).transform }))).toEqual({ ...before, transform: 'none' });
    await page.mouse.up();
  }
  expect(await example.locator('.searchable-item[data-disabled]').evaluate(el => Number(getComputedStyle(el).opacity))).toBeLessThan(0.7);
  await page.screenshot({ path: '../../artifacts/responsive-workspace/interaction-states.png' });
});

test('selection preserves pill widths, keyboard skips disabled, and pending pins reject repeats', async ({ page }) => {
  await page.goto('/?gallery=1#/gallery/controls');
  const example = page.locator('[data-interaction-example]');
  const group = example.getByRole('radiogroup', { name: 'Stálá šířka voleb' });
  await expect(group.getByRole('radio')).toHaveCount(3);
  const widths = () => group.getByRole('radio').evaluateAll(els => els.map(el => el.getBoundingClientRect().width));
  const before = await widths();
  await group.getByRole('radio', { name: 'Týden' }).press('ArrowRight', { delay: 50 });
  await expect(group.getByRole('radio', { name: 'Měsíc' })).toBeChecked();
  expect(await widths()).toEqual(before);
  await group.getByRole('radio', { name: 'Měsíc' }).press('ArrowRight', { delay: 50 });
  await expect(group.getByRole('radio', { name: 'Týden' })).toBeChecked();
  await example.getByRole('button', { name: 'Simulovat čekání připnutí' }).click();
  const pin = example.locator('[data-pin="interaction-example"]');
  await pin.press('Enter');
  await pin.press('Space');
  await expect(example.getByLabel('Počet připnutí')).toHaveText('0');
  await example.getByRole('button', { name: 'Ukončit čekání připnutí' }).click();
  await pin.press('Enter');
  await expect(example.getByLabel('Počet připnutí')).toHaveText('1');
});

test('touch and reduced motion keep selected targets stable without sticky hover movement', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, reducedMotion: 'reduce' });
  const page = await context.newPage();
  await page.goto((process.env.ATLAS_URL ?? 'http://127.0.0.1:5219') + '/?gallery=1#/gallery/controls');
  const example = page.locator('[data-interaction-example]');
  await example.getByRole('radio', { name: 'Měsíc', exact: true }).tap();
  await expect(example.getByRole('radio', { name: 'Měsíc', exact: true })).toBeChecked();
  const card = example.locator('.choice-tile[data-value="available"]');
  await card.tap();
  await expect.poll(() => card.evaluate(el => getComputedStyle(el).transform)).toBe('none');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await context.close();
});

test('the copy field button stays as wide and as placed when it confirms and is concentric with its field', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto('/?gallery=1#/gallery/modules');
  const field = page.locator('.copy-field').first();
  await field.scrollIntoViewIfNeeded();
  const button = field.getByRole('button', { name: 'Kopírovat adresu' });
  const before = await button.boundingBox();
  // The button sits 2 px inside the field (1 px border + 1 px padding): its radius is the field's less 2 px.
  const radii = await field.evaluate(el => ({ box: parseFloat(getComputedStyle(el.querySelector('.copy-field-value')!).borderTopRightRadius), button: parseFloat(getComputedStyle(el.querySelector('button')!).borderTopRightRadius) }));
  expect(radii.button).toBe(radii.box - 2);
  await button.click();
  const confirmed = field.getByRole('button', { name: 'Zkopírováno' });
  await expect(confirmed).toBeVisible();
  const after = await confirmed.boundingBox();
  expect(after!.width).toBeCloseTo(before!.width, 1);
  expect(after!.x).toBeCloseTo(before!.x, 1);
});
