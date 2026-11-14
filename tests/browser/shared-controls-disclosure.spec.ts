import { expect, test, type Locator } from '@playwright/test';

test('compact tag suggestions dismiss without losing a query or selecting a hidden match', async ({ page }) => {
  await page.goto('/?gallery=1#/gallery/controls');
  const input = page.getByRole('searchbox', { name: 'Kompaktní výběr lidí: hledat' });
  const choices = page.getByRole('group', { name: 'Kompaktní výběr lidí: návrhy' });
  await input.fill('Jana');
  await expect(choices).toBeVisible();
  await page.getByRole('searchbox', { name: 'Hledat účet', exact: true }).click();
  await expect(choices).toHaveCount(0);
  await expect(input).toHaveValue('Jana');
  await input.click();
  await expect(choices).toBeVisible();
  await choices.getByRole('button', { name: /Jana Nováková/ }).press('Escape');
  await expect(input).toBeFocused();
  await expect(choices).toHaveCount(0);
  await input.press('Enter');
  await expect(input).toHaveValue('Jana');
  await expect(page.getByRole('button', { name: 'Odebrat Jana Nováková' })).toHaveCount(0);
  await input.fill('Petr');
  await choices.getByRole('button', { name: /Petr Král/ }).click();
  await expect(page.getByRole('button', { name: 'Odebrat Petr Král' })).toBeVisible();

  // Reverse a slow closing transition before it finishes: the new suggestions must not inherit inert.
  await page.getByRole('switch', { name: 'Zpomalit 3×' }).check();
  await input.fill('Jana');
  await input.press('Escape');
  await input.fill('Jan');
  await expect(choices).toBeVisible();
  await expect(choices).not.toHaveAttribute('inert');
  await choices.getByRole('button', { name: /Jana Nováková/ }).click();
  await expect(page.getByRole('button', { name: 'Odebrat Jana Nováková' })).toBeVisible();
});

test('appearance choices fit their container and only reveal the icon browser on demand', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 600 });
  await page.goto('/?gallery=1#/gallery/controls');
  const example = page.locator('[data-appearance-example]');
  await expect(example.getByRole('searchbox', { name: 'Hledat ikonu' })).toHaveCount(0);
  await example.getByRole('button', { name: 'Zúžit výběr vzhledu na 320 px' }).click();
  const picker = example.locator('.appearance-picker');
  expect(await picker.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  const icons = picker.locator('[data-appearance-icons] [role="radio"]');
  expect((await icons.first().boundingBox())!.width).toBeGreaterThanOrEqual(38);
  await picker.getByRole('button', { name: 'Další barvy a tóny (60)' }).click();
  const palette = picker.locator('[data-appearance-colors]');
  await expect(palette).toBeVisible();
  expect((await palette.getByRole('radio').first().boundingBox())!.width).toBeGreaterThanOrEqual(38);
  // A reveal clips (overflow: clip) while it opens and hides anything that spills; measure once it has settled to visible.
  const settled = (inside: Locator) => picker.locator('[data-inline-reveal].is-settled[data-state="open"]', { has: inside });
  await expect(settled(page.locator('[data-appearance-colors]'))).toHaveCount(1);
  expect(await picker.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);

  // „Další ikony (N)“ loads the icon pack and opens the browser on its search, with the 87 recommended icons first.
  await picker.getByRole('button', { name: /^Další ikony \(/ }).click();
  const search = picker.getByRole('searchbox', { name: 'Hledat ikonu' });
  await expect(search).toBeFocused();
  const results = picker.getByRole('listbox', { name: 'Ikony' });
  const options = results.getByRole('option');
  await expect(options).toHaveCount(87);
  await expect(settled(page.getByRole('searchbox', { name: 'Hledat ikonu' }))).toHaveCount(1);
  expect(await picker.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  // Diacritics are ignored and the icon named exactly so comes first.
  await search.fill('zamek');
  await expect(options.first()).toHaveAttribute('data-value', 'lock');

  // Every icon: Down from the search enters at the chosen one, Down again moves one row lower in the same column.
  await search.fill('');
  await picker.locator('[data-appearance-scopes]').getByRole('radio', { name: 'Vše', exact: true }).click();
  await search.press('ArrowDown');
  const focused = results.locator('[role="option"]:focus');
  await expect(focused).toHaveAttribute('data-value', 'server');
  await page.keyboard.press('ArrowDown');
  await expect(focused).not.toHaveAttribute('data-value', 'server');
  const from = (await results.locator('[data-value="server"]').boundingBox())!, to = (await focused.boundingBox())!;
  expect(Math.abs(to.x - from.x)).toBeLessThan(1);
  expect(to.y).toBeGreaterThan(from.y);
  expect(to.y - from.y).toBeLessThan(2 * from.height);
  // Moving chose nothing; Enter does, and Escape returns to the search.
  await expect(results.locator('[data-value="server"]')).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Enter');
  await expect(focused).toHaveAttribute('aria-selected', 'true');
  await expect(results.locator('[data-value="server"]')).toHaveAttribute('aria-selected', 'false');
  await page.keyboard.press('Escape');
  await expect(search).toBeFocused();

  await picker.getByRole('button', { name: 'Méně ikon' }).click();
  await expect(search).toHaveCount(0);
  await expect(icons).toHaveCount(28);

  // Available and disabled hover must keep the same hit target in a cramped panel.
  const icon = icons.first();
  await expect.poll(() => icon.evaluate(el => el.getAnimations().filter(a => a.playState === 'running').length)).toBe(0);
  await icon.hover();
  await expect.poll(() => icon.evaluate(el => getComputedStyle(el).transform)).toBe('none');
  await example.getByRole('button', { name: 'Zakázat výběr vzhledu' }).click();
  await page.mouse.move(0, 0);
  await expect.poll(() => icon.evaluate(el => el.getAnimations().filter(a => a.playState === 'running').length)).toBe(0);
  const before = await icon.evaluate(el => getComputedStyle(el).backgroundColor);
  await icon.hover();
  await page.mouse.down();
  await expect.poll(() => icon.evaluate(el => ({ background: getComputedStyle(el).backgroundColor, transform: getComputedStyle(el).transform }))).toEqual({ background: before, transform: 'none' });
  await page.mouse.up();
  await expect(example.getByRole('button', { name: 'Vymazat: Nedostupný filtr' })).toBeDisabled();
  await expect(example.getByRole('button', { name: 'Vymazat: Filtr pouze pro čtení' })).toHaveCount(0);
  await page.screenshot({ path: '../../artifacts/ui-fresh-audit/appearance-narrow.png' });
});
