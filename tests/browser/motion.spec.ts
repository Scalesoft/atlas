import { test, expect } from '@playwright/test';
import { startEditing, expectSaved, LIVE_EDITOR } from './editing';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

for (const reducedMotion of ['no-preference', 'reduce'] as const) {
  test(`shell motion preserves keyboard interaction and editor state (${reducedMotion})`, async ({ page, context }) => {
    test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the initialized acceptance wiki.');
    await page.emulateMedia({ reducedMotion });
    await page.goto('/');
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin');
    await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
      (element as HTMLInputElement).value = value!;
      element.dispatchEvent(new Event('input', { bubbles: true }));
    }, process.env.ATLAS_E2E_PASSWORD);
    const login = page.getByRole('button', { name: 'Přihlásit se', exact: true });
    await expect(login).toHaveCSS('transition-duration', '0.12s'); // fast is 120 ms; reduced motion caps at min(d, 160 ms), plan O3
    await login.click();
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
    const csrf = await (await context.request.get('/api/auth/csrf')).json();
    const spaces = await (await context.request.get('/api/spaces')).json();
    const created = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: {
      spaceId: spaces[0].id, parentId: null, title: 'Jemný pohyb rozhraní', markdown: 'Původní obsah',
      contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Původní obsah' }] }] } }),
    } });
    expect(created.status()).toBe(200);
    await page.goto(`/#/pages/${(await created.json()).id}`);
    await startEditing(page);
    const editor = page.locator(LIVE_EDITOR);
    await expect(editor).toBeVisible();
    await editor.evaluate(element => element.setAttribute('data-motion-instance', 'retained'));
    await editor.press('Control+End');
    await editor.press('Enter');
    await editor.pressSequentially('Rozepsaná poznámka.');
    // Saved means collaboration has joined; wait for that so the set of keyboard targets does not change mid-assertion.
    await expectSaved(page);

    for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      const trigger = page.getByRole('button', { name: 'Další akce stránky', exact: true });
      const menu = page.getByRole('menu', { name: 'Další akce stránky', exact: true });
      for (let attempt = 0; attempt < 2; attempt++) {
        await trigger.press('ArrowDown');
        await expect(menu).toBeVisible();
        await expect(menu).toHaveCSS('animation-name', reducedMotion === 'reduce' ? 'atlas-backdrop-in' : /^atlas-menu-in/); // rises from its side, fades under reduced motion (motion.css)
        await expect(menu.locator('[role=menuitem]:not([data-disabled]):visible').first()).toBeFocused();
        const bounds = (await menu.boundingBox())!;
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(viewport.width);
        expect(bounds.y + bounds.height).toBeLessThanOrEqual(viewport.height);
        expect(await menu.evaluate(element => {
          // The first shown row: on a wide page the actions the bar already shows stay in the menu, hidden.
          const button = [...element.querySelectorAll<HTMLElement>('[role=menuitem]:not([data-disabled])')].find(row => row.offsetParent)!;
          const bounds = button.getBoundingClientRect();
          return element.contains(document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2));
        }), 'The animated menu stays above the document and receives pointer input.').toBe(true);
        if (attempt === 0) await page.screenshot({ path: `../../artifacts/ux-review/motion-menu-${reducedMotion}-${viewport.width}.png` });
        await page.keyboard.press('Escape');
        await expect(menu).not.toBeVisible();
        await expect(trigger).toBeFocused();
      }
      const newPage = page.getByRole('button', { name: 'Nová stránka', exact: true });
      for (let attempt = 0; attempt < 2; attempt++) {
        await newPage.click();
        const dialog = page.getByRole('dialog', { name: 'Nová stránka', exact: true });
        await expect(dialog).toHaveCSS('animation-name', 'atlas-reveal');
        await expect(dialog.getByRole('textbox', { name: 'Název', exact: true })).toBeFocused();
        await page.keyboard.press('Escape');
        await expect(dialog).not.toBeVisible();
        await expect(newPage).toBeFocused();
      }
      if (viewport.width < 680) {
        await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
        await expect(page.locator('.sidebar')).toHaveCSS('transition-duration', reducedMotion === 'reduce' ? '0s' : '0.16s');
      }
      await page.locator('.profile-launch').click();
      const profile = page.getByRole('region', { name: 'Profil', exact: true });
      // The profile opens on Účet a vzhled; its Vzhled half rises in like every page.
      await expect(profile.locator('[data-profile-section="appearance"]')).toHaveCSS('animation-name', 'st-rise-in');
      await page.locator('[data-profile-back]').click();
      await page.keyboard.press('Control+k');
      const search = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
      await expect(search.getByRole('combobox', { name: /^Hledat ve wiki – / })).toBeFocused();
      if (reducedMotion === 'reduce') await expect(search).toHaveCSS('animation-name', 'none');
      await page.keyboard.press('Escape');
      await expect(search).not.toBeVisible();
      await expect(editor).toHaveAttribute('data-motion-instance', 'retained');
      await expect(editor).toContainText('Rozepsaná poznámka.');
      await expect(editor).toHaveCSS('animation-name', 'none');
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
    }
    await editor.press('Control+z');
    await expect(editor).not.toContainText('Rozepsaná poznámka.');
    await expect(editor).toContainText('Původní obsah');
    await expectSaved(page);
    await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
    await page.getByRole('button', { name: 'Správa wiki', exact: true }).click();
    // The sections are in the left panel (a drawer on a phone); the drawer was opened above.
    await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Skupiny(\s|$)/ }).click();
    // A settings page enters with the settings rise; under reduced motion its distance is zero (a fade).
    const groups = page.getByRole('region', { name: 'Skupiny', exact: true });
    await expect(groups).toHaveCSS('animation-name', 'st-rise-in');
    await groups.getByRole('link', { name: 'Nová skupina', exact: true }).click();
    const groupName = page.getByRole('textbox', { name: 'Název skupiny', exact: true });
    await groupName.fill('Rozepsaný formulář');
    await expect(groupName).toHaveCSS('transition-duration', '0.12s'); // fast is 120 ms; reduced motion caps at min(d, 160 ms), plan O3
    await expect(groupName).toHaveValue('Rozepsaný formulář');
  });
}
