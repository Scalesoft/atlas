import { test, expect, type Page } from '@playwright/test';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

// A 1 × 1 PNG whose every chunk has its CRC right: one the browser cannot decode would leave the preview without its <img>.
const pixel = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgyGn4DwADSAHsZePq6AAAAABJRU5ErkJggg==', 'base64');

async function signIn(page: Page) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
    (element as HTMLInputElement).value = value!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}

// The one journey of a space's image: the server picks a built-in
// drawing from the name, a person chooses another, uploads their own and returns to the automatic one.
test('a space gets a drawing by its name, and its image can be chosen, uploaded and made automatic again', async ({ page }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || process.env.ATLAS_E2E_ALLOW_UPLOADS !== '1',
    'Requires the isolated initialized acceptance wiki and explicit ATLAS_E2E_ALLOW_UPLOADS=1.');
  await signIn(page);

  // A drawing is a static file of the build under a hashed name, never part of a script.
  const backupDrawing = /it-backup-.*\.svg/;
  const navigation = page.getByRole('complementary', { name: 'Hlavní navigace', exact: true });
  await navigation.getByRole('button', { name: 'Nový prostor', exact: true }).click();
  const form = page.getByRole('form', { name: 'Nový prostor', exact: true });
  const name = `Zálohy databází ${Date.now()}`;
  await form.getByLabel('Název prostoru').fill(name);
  // The preview card shows the pick while the name is typed, before anything is saved.
  await expect(form.locator('[data-space-cover] img.space-cover-image')).toHaveAttribute('src', backupDrawing);
  const creation = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/spaces');
  await form.getByRole('button', { name: 'Vytvořit prostor', exact: true }).click();
  const response = await creation;
  expect(response.status()).toBe(200);
  const space = await response.json();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();

  // The card in Knihovna has the drawing at once, without a reload.
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click();
  await expect(page.locator(`.space-directory [data-space="${space.id}"] img.space-cover-image`)).toHaveAttribute('src', backupDrawing);

  await page.goto(`/#/spaces/${space.id}/settings/general`);
  const block = page.locator('[data-space-cover]');
  const line = block.locator('[data-space-cover-line]');
  const preview = block.locator('.space-cover-preview img.space-cover-image');
  const status = block.getByRole('status');
  await expect(line).toContainText('Vybráno automaticky: Zálohy a obnova');
  await block.getByRole('button', { name: 'Vybrat jiný', exact: true }).click();
  const gallery = block.getByRole('radiogroup', { name: 'Obrázek prostoru', exact: true });
  await gallery.getByRole('radio', { name: 'Databáze a data', exact: true }).click();
  await expect(status).toHaveText('Obrázek je uložený.');
  await expect(line).toHaveText('Vybráno z katalogu: Databáze a data');
  await expect(preview).toHaveAttribute('src', /it-database-.*\.svg/);

  await block.locator('[data-space-cover-input]').setInputFiles({ name: 'vlastni.png', mimeType: 'image/png', buffer: pixel });
  await expect(preview).toHaveAttribute('src', new RegExp(`/api/spaces/${space.id}/cover\\?v=`));
  await expect(line).toHaveText('Vlastní obrázek.');

  // Automaticky releases the upload and the server picks by the name again.
  await gallery.getByRole('radio', { name: 'Automaticky', exact: true }).click();
  await expect(status).toHaveText('Obrázek se zase vybírá automaticky.');
  await expect(preview).toHaveAttribute('src', backupDrawing);
  await expect(line).toContainText('Vybráno automaticky: Zálohy a obnova');
});

// Saving any other image makes the server delete an uploaded one, so over an upload a choice by keyboard is only shown
// until Enter or Space is pressed on that very option. End moves focus to an option that is not the shown one: a key
// there must not save the shown one.
test('a choice by keyboard keeps an uploaded image until a key confirms the option it is on', async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || process.env.ATLAS_E2E_ALLOW_UPLOADS !== '1',
    'Requires the isolated initialized acceptance wiki and explicit ATLAS_E2E_ALLOW_UPLOADS=1.');
  await signIn(page);
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const created = await context.request.post('/api/spaces', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: { name: `Obrázek klávesnicí ${Date.now()}`, icon: 'book-open', color: 'blue' } });
  expect(created.status()).toBe(200);
  const space = await created.json();
  const choices: string[] = [];
  page.on('request', request => {
    if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/spaces/${space.id}/cover/choice`) choices.push(request.postData() ?? '');
  });
  await page.goto(`/#/spaces/${space.id}/settings/general`);
  // The session loaded its spaces before the API created this one.
  await page.reload();

  const block = page.locator('[data-space-cover]');
  const line = block.locator('[data-space-cover-line]');
  const preview = block.locator('.space-cover-preview img.space-cover-image');
  const status = block.getByRole('status');
  const upload = new RegExp(`/api/spaces/${space.id}/cover\\?v=`);
  const kept = 'Vlastní obrázek zůstává. Jiný obrázek potvrdíte klávesou Enter.';
  await block.locator('[data-space-cover-input]').setInputFiles({ name: 'vlastni.png', mimeType: 'image/png', buffer: pixel });
  await expect(line).toHaveText('Vlastní obrázek.');
  await expect(preview).toHaveAttribute('src', upload);

  await block.getByRole('button', { name: 'Vybrat jiný', exact: true }).click();
  const gallery = block.getByRole('radiogroup', { name: 'Obrázek prostoru', exact: true });
  const first = gallery.getByRole('radio').first();
  const last = gallery.getByRole('radio').last();
  const lastKey = await last.getAttribute('data-value');
  const lastLine = `Vybráno z katalogu: ${await last.getAttribute('title')}`;
  // Space on the first option only shows it.
  await first.focus();
  await page.keyboard.press('Space');
  await expect(first).toHaveAttribute('aria-checked', 'true');
  await expect(status).toHaveText(kept);
  // End moves focus without selecting. Space there shows that option; it does not save the first one.
  await page.keyboard.press('End');
  await expect(last).toBeFocused();
  await expect(last).toHaveAttribute('aria-checked', 'false');
  await page.keyboard.press('Space');
  await expect(last).toHaveAttribute('aria-checked', 'true');
  await expect(line).toHaveText(lastLine);
  await expect(status).toHaveText(kept);
  await page.keyboard.press('Escape');
  await expect(line).toHaveText('Vlastní obrázek.');
  await expect(preview).toHaveAttribute('src', upload);
  expect(choices).toEqual([]);

  // Space, then Enter on the same option: now it replaces the upload.
  await page.keyboard.press('Space');
  await expect(status).toHaveText(kept);
  await page.keyboard.press('Enter');
  await expect(status).toHaveText('Obrázek je uložený.');
  await expect(line).toHaveText(lastLine);
  expect(choices.map(body => JSON.parse(body))).toEqual([{ source: 'Catalog', catalogKey: lastKey }]);
});
