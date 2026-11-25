import { test, expect, type Locator, type Page } from '@playwright/test';

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });

async function enterSecret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, secret) => {
    const input = element as HTMLInputElement;
    input.value = secret;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }, value);
}

async function signIn(page: Page, username: string, password: string) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username);
  await enterSecret(page.getByLabel('Heslo', { exact: true }), password);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}

/** A switch inside its settings row: visible, labelled and not pushed out of the row. */
async function expectSwitchRow(control: Locator) {
  await expect(control).toBeVisible();
  const inside = await control.evaluate(element => {
    const row = element.closest('.settings-row') ?? element.parentElement!;
    const bounds = row.getBoundingClientRect(), own = element.getBoundingClientRect();
    return own.left >= bounds.left && own.right <= bounds.right;
  });
  expect(inside).toBe(true);
}

test.beforeEach(async ({ page }) => {
  const password = process.env.ATLAS_E2E_PASSWORD;
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !password, 'Requires the initialized disposable development wiki and its private test credential.');
  await signIn(page, process.env.ATLAS_E2E_USER ?? 'browser-admin', password!);
});

test('settings rows keep labels, switches and actions visible from phone to desktop', async ({ page }) => {
  await // The entry carries a backup warning in its name while no backup is configured.
  await page.getByRole('button', { name: /^Správa wiki/ }).click();
  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Lidé(\s|$)/ }).click();
  const username = process.env.ATLAS_E2E_USER ?? 'browser-admin';
  const ownRow = page.locator('[data-account]').filter({ hasText: username }).first();
  await page.setViewportSize({ width: 390, height: 844 });
  // Other accounts may list first, so the own row can sit below the fold: the action must fit the phone's width
  // (not be pushed out of its row) and come into view by an ordinary vertical scroll.
  const openAccount = ownRow.locator('[data-open-account]');
  await expect(openAccount).toBeVisible();
  const across = await openAccount.evaluate(element => { const bounds = element.getBoundingClientRect(); return { left: bounds.left, right: bounds.right }; });
  expect(across.left).toBeGreaterThanOrEqual(0);
  expect(across.right).toBeLessThanOrEqual(390);
  await openAccount.scrollIntoViewIfNeeded();
  await expect(openAccount).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.setViewportSize({ width: 1280, height: 900 });

  await ownRow.locator('[data-open-account]').click();
  // The person's card opens on Přístupy; the switches are on its Účet tab.
  await page.locator('.settings-tabs [data-tab="account"]').click();
  for (const label of ['Účet je aktivní', 'Správce wiki', 'Povolit osobní API tokeny'])
    await expectSwitchRow(page.getByRole('switch', { name: label, exact: true }));

  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: 'Moduly a integrace', exact: true }).click();
  await page.locator('[data-module="oidc"]').click();
  // The section header and the empty state both offer the action.
  await page.getByRole('button', { name: 'Přidat poskytovatele', exact: true }).first().click();
  // A progressive flow: its confirmation appears only once the fields are filled, the first field at once.
  await expect(page.locator('[data-provider-name]')).toBeVisible();
  await page.locator('[data-flow-cancel]').click();
  const reset = page.locator('[data-save-bar-cancel]');
  if (await reset.isVisible()) await reset.click();

  await page.getByRole('link', { name: 'Všechny moduly' }).click();
  await page.locator('[data-module="youtrack"]').click();
  await expectSwitchRow(page.getByRole('switch', { name: 'Používat spojení s YouTrack', exact: true }));
});

test('narrow profile navigation keeps every destination visible', async ({ page }) => {
  await page.getByRole('button', { name: /Profil a vzhled/ }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
  const navigation = page.locator('#atlas-settings-rail').getByRole('navigation', { name: 'Nastavení profilu', exact: true });
  const fit = await navigation.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const links = [...element.querySelectorAll('a')];
    return {
      names: links.map(link => link.textContent!.trim()),
      allInside: links.every(link => { const own = link.getBoundingClientRect(); return own.left >= bounds.left && own.right <= bounds.right; }),
    };
  });
  // "Osobní API" is listed only for an account allowed to issue tokens.
  expect(fit.names).toEqual(expect.arrayContaining(['Účet a vzhled', 'Zabezpečení', 'Moje žádosti', 'Historie návštěv', 'Aktivita']));
  expect(fit.allInside).toBe(true);
  await navigation.getByRole('link', { name: 'Zabezpečení', exact: true }).click();
  await expect(page).toHaveURL(/#\/profile\/security$/);
});
