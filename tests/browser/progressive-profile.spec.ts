import { expect, test } from '@playwright/test';

test('profile reveals relevant choices while preserving the draft on a narrow screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/?gallery=1#/gallery/ai');
  const profile = page.getByRole('region', { name: 'Předvolby AI', exact: true }).nth(1);
  const types = profile.locator('[data-ai-suggestion-types]');
  const firstType = types.getByRole('checkbox').first();
  await firstType.click();
  await expect(firstType).toHaveAttribute('aria-checked', 'false');
  await profile.locator('[data-ai-suggestions-mode]').getByRole('radio', { name: 'Vypnuto', exact: true }).click();
  await expect(types).not.toBeVisible();
  await profile.locator('[data-ai-suggestions-mode]').getByRole('radio', { name: 'Zapnuto', exact: true }).click();
  await expect(types).toBeVisible();
  await expect(firstType).toHaveAttribute('aria-checked', 'false');
  await profile.locator('[data-ai-search-answer]').getByRole('radio', { name: 'Na vyžádání', exact: true }).click();
  await expect(profile.locator('[data-ai-answer-help]')).toContainText('až po vašem kliknutí');
  await profile.locator('[data-ai-search-answer]').getByRole('radio', { name: 'Nikdy', exact: true }).click();
  await expect(profile.locator('[data-ai-answer-help]')).toContainText('pouze nalezené stránky');
  await expect(profile.locator('[data-ai-delete-conversations]')).not.toBeVisible();
  await profile.getByRole('button', { name: /^Správa historie konverzací/ }).click();
  await expect(profile.locator('[data-ai-delete-conversations]')).toBeVisible();
  expect(await profile.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: '../../artifacts/ui-fresh-audit/profile-disclosure-mobile.png' });
});
