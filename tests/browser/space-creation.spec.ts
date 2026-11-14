import { test, expect, type Locator, type Page } from '@playwright/test';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

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

test('new space assigns the selected group atomically and preserves owner administration', async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the isolated initialized acceptance wiki.');
  await signIn(page);

  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const groupName = `Skupina pro založení ${Date.now()}`;
  const groupResponse = await context.request.post('/api/admin/groups', {
    headers: { 'X-CSRF-TOKEN': csrf.token }, data: { name: groupName },
  });
  expect(groupResponse.status()).toBe(200);
  const group = await groupResponse.json();
  const owner = await (await context.request.get('/api/auth/me')).json();
  let creationRequests = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/spaces') creationRequests++;
  });
  // Nový prostor is the + beside the sidebar's Prostory heading and opens the full-screen flow at
  // #/new-space. Zrušit over a named draft asks first; Zahodit returns to Knihovna with focus on the +.
  const navigation = page.getByRole('complementary', { name: 'Hlavní navigace', exact: true });
  const trigger = navigation.getByRole('button', { name: 'Nový prostor', exact: true });
  const form = page.getByRole('form', { name: 'Nový prostor', exact: true });
  const openForm = async () => { await trigger.click(); await expect(page).toHaveURL(/#\/new-space$/); };
  await openForm();
  await form.getByLabel('Název prostoru').fill('Tento koncept nevytvářet');
  await form.getByRole('button', { name: 'Zrušit', exact: true }).click();
  const leave = page.getByRole('dialog', { name: 'Neuložené změny', exact: true });
  await leave.getByRole('button', { name: 'Zahodit', exact: true }).click();
  await expect(form).toHaveCount(0);
  await expect(page).toHaveURL(/#\/$/);
  await expect(trigger).toBeFocused();
  expect(creationRequests).toBe(0);

  await openForm();
  const name = `Přístup při založení ${Date.now()}`;
  // Naming the space reveals the optional access step; without groups only the owner and wiki admins get in.
  await form.getByLabel('Název prostoru').fill(name);
  await expect(form).toContainText('Zatím přístupný pouze vám a správcům wiki.');
  await form.getByRole('button', { name: 'Přidat skupinu', exact: true }).click();
  const picker = form.locator('[data-group-picker]');
  await picker.getByRole('searchbox').fill(groupName);
  await picker.getByRole('radio', { name: groupName }).click();
  await form.getByRole('radiogroup', { name: `Role skupiny ${groupName}`, exact: true }).getByRole('radio', { name: 'Upravuje', exact: true }).click();
  const responsePromise = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/spaces');
  await form.getByRole('button', { name: 'Vytvořit prostor', exact: true }).click();
  const response = await responsePromise;
  expect(response.status()).toBe(200);
  expect(response.request().postDataJSON().groups).toEqual([{ groupId: group.id, role: 'Writer' }]);
  const space = await response.json();
  await expect(form).toHaveCount(0);
  // The new space opens and is pinned for its creator: the sidebar lists it among the pinned spaces.
  await expect(navigation.getByRole('link', { name, exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(navigation.getByRole('button', { name: `Další akce prostoru ${name}`, exact: true })).toBeVisible();
  await expect(navigation.getByRole('button', { name: `Připnout prostor ${name}`, exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Vytvořit první stránku', exact: true })).toBeVisible();
  expect(creationRequests).toBe(1);
  const accessResponse = await context.request.get(`/api/spaces/${space.id}/access`);
  expect(accessResponse.status()).toBe(200);
  const access = await accessResponse.json();
  expect(access.grants).toHaveLength(2);
  expect(access.grants).toEqual(expect.arrayContaining([
    expect.objectContaining({ userId: owner.id, groupId: null, role: 'Admin' }),
    expect.objectContaining({ groupId: group.id, userId: null, role: 'Writer' }),
  ]));
});

/** An icon of the icon pack is drawn once the pack has arrived: until then its svg carries the name but no shapes. */
async function expectDrawn(scope: Locator, icon: string) {
  const drawing = scope.locator(`svg[data-icon="${icon}"]`).first();
  await expect(drawing).toBeVisible();
  await expect(drawing.locator('> *').first()).toBeAttached();
}

test('a space and a page take an icon of the icon pack and keep it after a reload', async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the isolated initialized acceptance wiki.');
  await signIn(page);
  const navigation = page.getByRole('complementary', { name: 'Hlavní navigace', exact: true });
  const form = page.getByRole('form', { name: 'Nový prostor', exact: true });
  await navigation.getByRole('button', { name: 'Nový prostor', exact: true }).click();
  await expect(page).toHaveURL(/#\/new-space$/);
  const name = `Ikona z balíku ${Date.now()}`;
  await form.getByLabel('Název prostoru').fill(name);
  // The pack is behind „Další ikony (N)“; its icons are found by English words too.
  await form.getByRole('button', { name: /^Další ikony \(/ }).click();
  await form.getByRole('searchbox', { name: 'Hledat ikonu' }).fill('rocket');
  await form.getByRole('listbox', { name: 'Ikony' }).locator('[data-value="rocket"]').click();
  const creation = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/spaces');
  await form.getByRole('button', { name: 'Vytvořit prostor', exact: true }).click();
  const created = await creation;
  expect(created.status()).toBe(200);
  expect(created.request().postDataJSON().icon).toBe('rocket');
  const space = await created.json();

  const sidebarSpace = navigation.getByRole('link', { name, exact: true });
  const card = page.locator(`.space-card[data-space="${space.id}"]`);
  await expectDrawn(sidebarSpace, 'rocket');
  await page.goto('/#/library/spaces');
  await expectDrawn(card, 'rocket');

  // A page of the space, created through the API; its icon is picked in the page-icon panel.
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const text = 'Stránka pro ověření ikony z balíku.';
  const pageResponse = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: {
    spaceId: space.id, parentId: null, title: `Stránka s ikonou ${Date.now()}`, markdown: text,
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } }),
  } });
  expect(pageResponse.ok()).toBe(true);
  const wikiPage = await pageResponse.json();
  await page.goto(`/#/pages/${wikiPage.id}`);
  await page.reload();
  await page.getByRole('button', { name: 'Přidat ikonu', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Ikona stránky', exact: true });
  await panel.getByRole('button', { name: /^Další ikony \(/ }).click();
  await panel.getByRole('searchbox', { name: 'Hledat ikonu' }).fill('dog');
  await panel.getByRole('listbox', { name: 'Ikony' }).locator('[data-value="dog"]').click();
  const saving = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith(`/api/pages/${wikiPage.id}/decoration`));
  await panel.getByRole('button', { name: 'Použít', exact: true }).click();
  expect((await saving).ok()).toBe(true);

  // The title: an h1 only while locked; a writer's page (3.6) opens with its field, which holds the icon the same way.
  const heading = page.locator('.document-header .document-title');
  const treeRow = page.locator(`.tree-page[href$="/pages/${wikiPage.id}"]`);
  await expectDrawn(heading, 'dog');
  await expectDrawn(treeRow, 'dog');

  await page.reload();
  await expectDrawn(heading, 'dog');
  await expectDrawn(treeRow, 'dog');
  await expectDrawn(sidebarSpace, 'rocket');
  await page.goto('/#/library/spaces');
  await expectDrawn(card, 'rocket');
});
