import { test, expect } from '@playwright/test';
import { PAGE_TEXT } from "./editing";

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

test('page and space actions share aligned icons, readable styling and usable dialogs', async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !process.env.ATLAS_E2E_PASSWORD, 'Requires the initialized development wiki.');
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
    (element as HTMLInputElement).value = value!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const spaces = await (await context.request.get('/api/spaces')).json();
  const created = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: {
    spaceId: spaces[0].id, parentId: null, title: 'Vizuální kontrola nabídek', markdown: 'Článek pro kontrolu nabídek.',
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Článek pro kontrolu nabídek.' }] }] } }),
  } });
  expect(created.status()).toBe(200);
  // A second writable space, so the organization dialog offers a transfer with its checkboxes.
  const target = await context.request.post('/api/spaces', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: { name: `Cíl přesunu ${Date.now()}`, icon: 'book', color: 'green' } });
  expect(target.status()).toBe(200);
  const transferTarget: string = (await target.json()).id;
  const pageUrl = `/#/pages/${(await created.json()).id}`;
  await page.goto(pageUrl);
  // The session loaded its spaces before the API created the transfer target.
  await page.reload();
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  for (const viewport of [{ width: 1440, height: 960 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    // Both action lists are the shared ARIA menu named after its trigger (the space one carries the space name).
    // A phone with an open document shows only the title crumb, so the space menu is checked on desktop only.
    const menus: [string, string][] = [['Další akce stránky', 'page']];
    if (viewport.width > 680) menus.push([`Akce prostoru: ${spaces[0].name}`, 'space']);
    else await expect(page.getByRole('button', { name: /^Akce prostoru/ })).toBeHidden();
    for (const [label, slug] of menus) {
      const trigger = page.getByRole('button', { name: label, exact: true });
      await trigger.press('ArrowDown');
      const menu = page.getByRole('menu', { name: label, exact: true });
      await expect(menu).toBeVisible();
      await menu.screenshot({ path: `../../artifacts/ux-deep/shell-${slug}-actions-${viewport.width}.png` });
      const rows = menu.getByRole('menuitem');
      const [text, error] = await page.locator('.atlas').evaluate(el => {
        const probe = el.appendChild(document.createElement('span'));
        probe.style.color = 'var(--error)';
        const colors = [getComputedStyle(el).color, getComputedStyle(probe).color];
        probe.remove();
        return colors;
      });
      for (const row of await rows.all()) {
        await expect(row.locator('svg')).toHaveCount(1);
        await expect(row).toHaveCSS('column-gap', '12px');
        await expect(row).toHaveCSS('align-items', 'center');
        // The keyboard-highlighted row takes the accent; a destructive row (Přesunout do koše) reads in the error colour;
        // every other row reads in the shell's text colour.
        if (await row.getAttribute('data-highlighted') === null) await expect(row).toHaveCSS('color', await row.getAttribute('data-destructive') === null ? text : error);
      }
      const iconPositions = await rows.evaluateAll(elements => elements.map(el => el.querySelector('svg')!.getBoundingClientRect().x));
      expect(Math.max(...iconPositions) - Math.min(...iconPositions)).toBeLessThan(1);
      await page.keyboard.press('Escape');
      await expect(trigger).toBeFocused();
    }
    // Page creation is a routed ProgressiveFlow; the blank template is ready, so the title field shows at once.
    // A phone with an open document moves Nová stránka into the page actions menu.
    // The page header's Nový menu offers Stránka and Tabule; the sidebar offers Nová stránka in each space row's … menu.
    if (viewport.width > 680) {
      await page.locator('header').getByRole('button', { name: 'Nový obsah v prostoru', exact: true }).click();
      await page.getByRole('menuitem', { name: 'Stránka', exact: true }).click();
    } else {
      await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
      await page.getByRole('menuitem', { name: 'Nová stránka', exact: true }).click();
    }
    const newPage = page.getByRole('region', { name: 'Nová stránka', exact: true });
    await expect(newPage).toBeVisible();
    await expect(newPage.getByRole('button', { name: 'Další', exact: true })).toHaveCount(0);
    await expect(newPage.getByRole('textbox', { name: 'Název stránky', exact: true })).toBeEditable();
    await newPage.screenshot({ path: `../../artifacts/ux-deep/shell-new-page-${viewport.width}.png` });
    await newPage.getByRole('button', { name: 'Zrušit', exact: true }).click();
    await expect(newPage).toHaveCount(0);
    await page.goto(pageUrl);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Přesunout…', exact: true }).click();
    const organization = page.getByRole('dialog');
    await expect(organization).toBeVisible();
    await organization.screenshot({ path: `../../artifacts/ux-deep/shell-organization-${viewport.width}.png` });
    // Obecné and Umístění are tabs of one dialog; each shows its own sections.
    let sectionIndex = 0;
    for (const tab of ['Obecné', 'Umístění']) {
      await organization.getByRole('navigation', { name: 'Nastavení stránky', exact: true }).getByRole('button', { name: tab, exact: true }).click();
      if (tab === 'Umístění') {
        // Checkboxes appear once a target space of the transfer is chosen.
        await organization.getByText('Přesun do jiného prostoru', { exact: true }).click();
        // The target is a searchable radiogroup of spaces; each tile carries its space id.
        await organization.getByRole('radiogroup', { name: 'Cílový prostor', exact: true }).locator(`[data-value="${transferTarget}"]`).click();
        await expect(organization.locator('.organization-check').first()).toBeVisible();
      }
      for (const section of await organization.locator('.organization-section:visible').all()) {
        await section.screenshot({ path: `../../artifacts/ux-deep/shell-organization-section-${sectionIndex++}-${viewport.width}.png` });
      }
    }
    await expect(organization.locator('.organization-check').first()).toHaveCSS('flex-direction', 'row');
    await page.keyboard.press('Escape');
    await expect(organization).toHaveCount(0);
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Historie', exact: true }).click();
    const history = page.getByRole('dialog', { name: 'Historie stránky', exact: true });
    await expect(history).toBeVisible();
    await expect(history.locator('.history-preview')).toHaveAttribute('aria-busy', 'false');
    await history.screenshot({ path: `../../artifacts/ux-deep/shell-history-${viewport.width}.png` });
    await history.getByRole('button', { name: 'Zavřít', exact: true }).click();
    await expect(history).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
    if (viewport.width < 700) await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
    // Nový prostor is the + beside the sidebar's Prostory heading.
    await page.getByRole('complementary', { name: 'Hlavní navigace', exact: true }).getByRole('button', { name: 'Nový prostor', exact: true }).click();
    // The fields of Nastavení prostoru → Obecné, then Přístup and Vytvořit prostor at the end of the form.
    const spaceForm = page.getByRole('form', { name: 'Nový prostor', exact: true });
    await spaceForm.getByLabel('Název prostoru').fill('Rozvržení nového prostoru');
    // Scroll once the access step has unfolded, and centre the button: scrolled to the bottom edge while the step was
    // still unfolding, it ended below the viewport, and at the edge the fractional layout left a third of a pixel outside.
    await spaceForm.evaluate((form) => Promise.all(form.getAnimations({ subtree: true })
      .filter((animation) => Number.isFinite(Number(animation.effect?.getComputedTiming().endTime)))
      .map((animation) => animation.finished.catch(() => undefined))));
    const createSpace = spaceForm.getByRole('button', { name: 'Vytvořit prostor', exact: true });
    await createSpace.evaluate((button) => button.scrollIntoView({ block: 'center' }));
    await expect(createSpace).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width);
    await spaceForm.screenshot({ path: `../../artifacts/ux-deep/shell-new-space-${viewport.width}.png` });
    await spaceForm.getByRole('button', { name: 'Zrušit', exact: true }).click();
    await page.getByRole('dialog', { name: 'Neuložené změny', exact: true }).getByRole('button', { name: 'Zahodit', exact: true }).click();
    await expect(spaceForm).toHaveCount(0);
    // Zahodit returns to Knihovna; the next viewport starts from the article again.
    await page.goto(pageUrl);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
  }
});
