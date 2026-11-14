import { expect, test, type Page } from '@playwright/test';

// The three places of settings (design 2026-10-10: Profil, Nastavení prostoru, Správa wiki) and O prostoru, the tab of a
// space for everyone who can open it, against a mocked API: run against a Vite dev server, no wiki needed.
//   pnpm --dir src/Atlas.Web exec vite --port 5348
//   $env:ATLAS_URL='http://127.0.0.1:5348'; npx --prefix tests/browser playwright test settings-places.spec.ts
test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });

type Role = 'Reader' | 'Admin';
const template = { id: 'sablona-porada', spaceId: 'provoz', parentId: null, title: 'Zápis z porady', version: 1, isPublished: true, isTemplate: true };

/** The account is no wiki administrator: what it may open in the two spaces is decided by its `role` in both. */
async function mockWiki(page: Page, role: Role) {
  const spaces = [
    { id: 'provoz', name: 'Provoz', description: 'Servery, síť a pohotovost.', icon: 'book-open', color: 'blue' },
    { id: 'vyvoj', name: 'Vývoj', description: 'Aplikace a nasazení.', icon: 'code-xml', color: 'green' },
  ].map((space) => ({ ...space, role, pinPolicy: 'None', pinned: true, pageCount: 2, lastChangedAt: '2026-10-09T08:30:00Z' }));
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const json = (value: unknown) => route.fulfill({ status: 200, json: value });
    let match: RegExpMatchArray | null;
    if (path === '/api/setup/status') return json({ version: '2.6.0-dev+ui-fixture', databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true });
    if (path === '/api/auth/me') return json({ id: 'u-eva', username: 'eva.mala', displayName: 'Eva Malá', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } });
    if (path === '/api/auth/csrf') return json({ token: 'csrf-test' });
    if (path === '/api/spaces') return json(spaces);
    if (path.startsWith('/api/me/library/')) return json({ items: [], hasMore: false, nextOffset: null });
    if (path === '/api/ai/capabilities') return json({ semanticSearch: false, related: false, editorLinks: false, createSuggestions: false });
    if ((match = path.match(/^\/api\/spaces\/([^/]+)\/tree$/))) {
      const spaceId = match[1]!;
      return json({ spaceId, version: 1, items: [
        { id: `${spaceId}-uvod`, spaceId, parentId: null, title: 'Úvod', version: 1, isPublished: true, sortOrder: 1024, canEdit: role === 'Admin' },
        { id: `${spaceId}-koncept`, spaceId, parentId: null, title: 'Rozpracovaný postup', version: 1, isPublished: false, sortOrder: 2048, canEdit: role === 'Admin' },
      ] });
    }
    if ((match = path.match(/^\/api\/spaces\/([^/]+)\/settings$/))) return json({ ...spaces.find((space) => space.id === match![1]), version: '1' });
    if ((match = path.match(/^\/api\/spaces\/([^/]+)\/page-templates$/))) return json([{ ...template, spaceId: match[1] }]);
    if (path === '/api/vaults' || path === '/api/vault-locations') return json([]);
    if (path === '/api/access-requests') return json({ items: [], hasMore: false });
    await route.fulfill({ status: 404, json: {} });
  });
}
const noHorizontalScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
/** Arrives at an address the way the browser does (a typed address, back and forward): nothing was pressed. */
const visit = (page: Page, hash: string) => page.evaluate((target) => { location.hash = target; }, hash);

test('a reader reads about a space and finds nothing of its administration', async ({ page }) => {
  await mockWiki(page, 'Reader');
  await page.goto('/#/spaces/provoz');
  // Stránky and O prostoru for everyone; Ke schválení belongs to wiki administrators only.
  const tabs = page.getByRole('tablist', { name: 'Zobrazení prostoru' }).getByRole('tab');
  await expect(tabs).toHaveText([/^Stránky/, 'O prostoru']);
  await tabs.getByText('O prostoru').click();
  await expect(page).toHaveURL(/#\/spaces\/provoz\/about$/);
  await expect(page.getByRole('tab', { name: 'O prostoru' })).toHaveAttribute('aria-selected', 'true');
  // The head of the space says the description too: the claims are about the tab's own region.
  const about = page.locator('[data-space-about]');
  await expect(about.locator('.space-about-description')).toHaveText('Servery, síť a pohotovost.');
  await expect(about.getByText('Váš přístup', { exact: true })).toBeVisible();
  await expect(about.locator('[data-summary="role"]')).toContainText('Čte');
  await expect(about.getByRole('button', { name: 'Otevřít šablonu Zápis z porady' })).toBeVisible();
  // Nothing about administering the space, and no way into its settings.
  await expect(page.locator('[data-space-about-admin]')).toHaveCount(0);
  await expect(page.getByText('Správa prostoru', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Nastavení prostoru' })).toHaveCount(0);
  await page.getByRole('tab', { name: /^Stránky/ }).click();
  await expect(page).toHaveURL(/#\/spaces\/provoz$/);
  await expect(about).toHaveCount(0);
  // The address of the settings is not a way in: it lands on Knihovna, not on a blank surface.
  await visit(page, '#/spaces/provoz/settings/general');
  await expect(page).toHaveURL(/#\/$/);
  await expect(page.locator('#atlas-settings-rail')).toHaveCount(0);
});

test('an administrator goes from O prostoru into the settings, moves to another space and back', async ({ page }) => {
  await mockWiki(page, 'Admin');
  await page.goto('/#/spaces/provoz');
  await page.getByRole('tab', { name: 'O prostoru' }).click();
  await expect(page).toHaveURL(/#\/spaces\/provoz\/about$/);
  // The block "Správa prostoru" leads into the five sections of the settings.
  const links = page.locator('[data-space-about-admin] a[data-space-about-link]');
  await expect(links).toHaveCount(5);
  await expect(links).toHaveText(['Obecné', 'Přístupy', 'Sdílení odkazem', 'Šablony stránek', 'Trezory']);
  for (const [index, section] of ['general', 'access', 'sharing', 'templates', 'vaults'].entries()) {
    await expect(links.nth(index)).toHaveAttribute('href', `#/spaces/provoz/settings/${section}`);
  }
  await page.getByRole('button', { name: 'Nastavení prostoru', exact: true }).click();
  await expect(page).toHaveURL(/#\/spaces\/provoz\/settings\/general$/);
  // One rail with the name of the place, the way back and the sections; nothing in it switches between places.
  const rail = page.locator('#wiki-navigation');
  await expect(rail.locator('.settings-sidebar-title')).toHaveText('Nastavení prostoru');
  await expect(page.locator('.settings-scope-tabs')).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Drobečková navigace' }).locator('[aria-current="page"]')).toHaveText('Provoz · Nastavení prostoru');
  await page.locator('#atlas-settings-rail').getByRole('link', { name: /^Šablony stránek/ }).click();
  await expect(page).toHaveURL(/#\/spaces\/provoz\/settings\/templates$/);
  // The switcher lists the spaces the account manages and opens the same section of the one it picks.
  const switcher = rail.locator('[data-space-switcher]');
  await expect(switcher).toHaveAttribute('title', 'Provoz');
  await switcher.click();
  await page.getByRole('radio', { name: 'Vývoj' }).click();
  await expect(page).toHaveURL(/#\/spaces\/vyvoj\/settings\/templates$/);
  await expect(switcher).toHaveAttribute('title', 'Vývoj');
  await expect(page.getByRole('navigation', { name: 'Drobečková navigace' }).locator('[aria-current="page"]')).toHaveText('Vývoj · Nastavení prostoru');
  await page.getByRole('button', { name: 'Zpět do prostoru', exact: true }).click();
  await expect(page).toHaveURL(/#\/spaces\/vyvoj$/);
  await expect(page.locator('#atlas-settings-rail')).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 1, name: 'Vývoj' })).toBeVisible();
});

test('on a phone the sections are in the sheet and the switcher in the drawer moves to the other space', async ({ page }) => {
  await mockWiki(page, 'Admin');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/#/spaces/provoz/settings/templates');
  await expect(page.locator('.settings-shell')).toBeVisible();
  // "Sekce" is sections only: the space is moved from the drawer, so the sheet holds no switcher.
  await page.getByRole('button', { name: /^Sekce/ }).click();
  const sheet = page.getByRole('dialog', { name: 'Sekce', exact: true });
  await expect(sheet).toBeVisible();
  for (const section of ['Obecné', 'Přístupy', 'Sdílení odkazem', 'Šablony stránek', 'Trezory']) {
    await expect(sheet.getByRole('link', { name: new RegExp(`^${section}`) })).toBeVisible();
  }
  await expect(sheet.locator('[data-space-switcher]')).toHaveCount(0);
  await sheet.getByRole('button', { name: 'Zavřít sekce' }).press('Escape');
  await expect(sheet).toBeHidden();
  expect(await noHorizontalScroll(page)).toBe(true);
  await page.getByRole('button', { name: 'Zobrazit boční navigaci' }).click();
  const switcher = page.locator('#wiki-navigation [data-space-switcher]');
  await expect(switcher).toBeVisible();
  expect(await noHorizontalScroll(page)).toBe(true);
  await switcher.click();
  await page.getByRole('radio', { name: 'Vývoj' }).click();
  await expect(page).toHaveURL(/#\/spaces\/vyvoj\/settings\/templates$/);
  await expect(switcher).toHaveAttribute('title', 'Vývoj');
  expect(await noHorizontalScroll(page)).toBe(true);
});

// Kept apart from the journey above so a failure here does not hide what the drawer's switcher does.
test('the drawer of space settings takes the focus to its way back when it opens', async ({ page }) => {
  await mockWiki(page, 'Admin');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/#/spaces/provoz/settings/templates');
  await expect(page.locator('.settings-shell')).toBeVisible();
  await page.getByRole('button', { name: 'Zobrazit boční navigaci' }).click();
  await expect(page.getByRole('button', { name: 'Zpět do prostoru', exact: true })).toBeFocused();
});
