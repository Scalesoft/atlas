import { expect, test, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { startEditing, LIVE_EDITOR } from './editing';

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.beforeEach(() => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !process.env.ATLAS_E2E_PASSWORD,
    'Requires the initialized acceptance wiki and its test credentials.');
});

async function login(page: Page) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).fill(process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}

async function createPage(context: BrowserContext, paragraphCount = 1) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const spaces = await (await context.request.get('/api/spaces')).json();
  expect(spaces.length).toBeGreaterThan(0);
  const response = await context.request.post('/api/pages', {
    headers: { 'X-CSRF-TOKEN': csrf.token },
    data: {
      spaceId: spaces[0].id,
      parentId: null,
      title: `Audit ovládání editoru ${Date.now()}`,
      markdown: Array.from({ length: paragraphCount }, (_, index) => `Odstavec ${index + 1}`).join('\n\n'),
      contentJson: JSON.stringify({
        format: 'atlas-rich',
        version: 1,
        document: {
          type: 'doc',
          content: Array.from({ length: paragraphCount }, (_, index) => ({
            type: 'paragraph', content: [{ type: 'text', text: `Odstavec ${index + 1}` }],
          })),
        },
      }),
    },
  });
  expect(response.status()).toBe(200);
  return (await response.json()).id as string;
}

async function expectPaintedPopover(popover: Locator) {
  await expect(popover).toBeVisible();
  const paint = await popover.evaluate(element => {
    const style = getComputedStyle(element);
    const bounds = element.getBoundingClientRect();
    const target = document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + 8);
    return {
      backgroundColor: style.backgroundColor,
      borderTopWidth: style.borderTopWidth,
      receivesPointer: target === element || element.contains(target),
    };
  });
  expect(paint.backgroundColor).not.toBe('rgba(0, 0, 0, 0)');
  expect(paint.borderTopWidth).not.toBe('0px');
  expect(paint.receivesPointer).toBe(true);
}

// The editor renders no toolbar of its own (toolbar-presentation="hidden", 2026-09-08): its insert and tool actions
// are items of the page menu "Další akce stránky" while editing, and the slash menu inserts at the caret.
const pageMenu = (page: Page) => page.getByRole('menu', { name: 'Další akce stránky', exact: true });

test('slash and page menus inherit editor styling and accept pointer input', async ({ page, context }) => {
  await login(page);
  const id = await createPage(context);
  await page.goto(`/#/pages/${id}`);
  await startEditing(page);
  const editor = page.locator(LIVE_EDITOR);
  await expect(editor).toBeVisible();

  await editor.press('Control+End');
  await editor.press('Enter');
  await page.keyboard.insertText('/');
  const slash = page.getByTestId('slash-menu');
  await expectPaintedPopover(slash);
  const slashFont = await slash.evaluate(element => getComputedStyle(element).fontFamily);
  await slash.getByRole('option', { name: /^Nadpis 2/ }).click();
  await expect(editor.locator('h2')).toBeVisible();

  await page.keyboard.insertText('Stejný symbol odkazu');
  await editor.press('Control+a');
  const editorLink = page.getByRole('group', { name: 'Formátování textu', exact: true })
    .getByRole('button', { name: 'Odkaz (Ctrl+K)', exact: true });
  await expect(editorLink).toBeVisible();
  const [pageLinkPaths, editorLinkPaths] = await Promise.all([
    page.getByRole('button', { name: 'Kopírovat odkaz na stránku', exact: true }).locator('svg path').evaluateAll(paths => paths.map(path => path.getAttribute('d'))),
    editorLink.locator('svg path').evaluateAll(paths => paths.map(path => path.getAttribute('d'))),
  ]);
  expect(pageLinkPaths.length).toBeGreaterThan(0);
  expect(pageLinkPaths).toEqual(editorLinkPaths);

  const insert = page.getByRole('button', { name: 'Další akce stránky', exact: true });
  await insert.click();
  await expect(insert).toHaveAttribute('aria-expanded', 'true');
  const insertMenu = pageMenu(page);
  await expect(insertMenu.getByRole('menuitem', { name: 'Příloha', exact: true })).toBeVisible();
  await expect(insertMenu.getByRole('menuitem', { name: 'Hledat a nahradit', exact: true })).toBeVisible();
  await expectPaintedPopover(insertMenu);
  const [wikiFont, insertFont] = await Promise.all([
    page.locator('.atlas').evaluate(element => getComputedStyle(element).fontFamily),
    insertMenu.evaluate(element => getComputedStyle(element).fontFamily),
  ]);
  expect(insertFont).toBe(wikiFont);
  expect(slashFont).toBe(wikiFont);
});

test('Insert tools open at the document text column', async ({ page, context }) => {
  await login(page);
  const id = await createPage(context);
  await page.goto(`/#/pages/${id}`);
  await startEditing(page);
  const editor = page.locator(LIVE_EDITOR);
  const slash = page.getByTestId('slash-menu');
  await expect(editor).toBeVisible();

  // Fullscreen is a focus mode without the page menu: the slash menu is how one inserts there.
  for (const fullscreen of [false, true]) {
    if (fullscreen) await page.getByRole('button', { name: 'Celá obrazovka', exact: true }).click();
    await editor.press('Control+End');
    await editor.press('Enter');
    await page.keyboard.insertText('/');
    await expect(slash).toBeVisible();
    // The caret stands at the start of an empty line, so its x is the text column's.
    const column = await page.evaluate(() => getSelection()!.getRangeAt(0).getBoundingClientRect().x);
    const editorBounds = (await editor.boundingBox())!;
    expect(column).toBeGreaterThanOrEqual(editorBounds.x);
    expect(Math.abs((await slash.boundingBox())!.x - column), fullscreen ? 'fullscreen' : 'page').toBeLessThanOrEqual(1);
    await page.keyboard.press('Escape');
    await expect(slash).toBeHidden();
  }
});

test('page menu stays inside a phone screen with a long editor action label', async ({ page, context }) => {
  await login(page);
  const id = await createPage(context);
  await page.goto(`/#/pages/${id}`);
  await startEditing(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
  const menu = pageMenu(page);
  const action = await menu.getByRole('menuitem', { name: 'Hledat a nahradit', exact: true }).elementHandle();
  await action!.evaluate(element => {
    element.lastChild!.textContent = 'Velmi dlouhý název doplňkové akce, který se musí zalomit';
  });
  const [menuBounds, actionBounds] = await Promise.all([menu.boundingBox(), action!.boundingBox()]);
  expect(menuBounds!.x).toBeGreaterThanOrEqual(0);
  expect(menuBounds!.x + menuBounds!.width).toBeLessThanOrEqual(390);
  expect(actionBounds!.x + actionBounds!.width).toBeLessThanOrEqual(menuBounds!.x + menuBounds!.width);
  expect(await action!.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
});

test('slash menu follows a visible caret during page scroll and remains clickable', async ({ page, context }) => {
  await login(page);
  const id = await createPage(context, 40);
  await page.goto(`/#/pages/${id}`);
  await startEditing(page);
  const editor = page.locator(LIVE_EDITOR);
  await expect(editor).toBeVisible();
  // ProseMirror puts its own selection (here the end) back 20 ms after the editor gains focus (prosemirror-view
  // handlers.focus): press() focuses and types at once, so the caret it moves sooner would return to the end.
  await editor.focus();
  await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 50)));
  await editor.press('Control+Home');
  await editor.locator('p').first().evaluate(element => element.scrollIntoView({ block: 'center' }));
  await page.keyboard.insertText('/');
  const slash = page.getByTestId('slash-menu');
  await expect(slash).toBeVisible();
  const before = (await slash.boundingBox())!;

  await page.mouse.wheel(0, 20);
  await expect.poll(async () => (await slash.boundingBox())?.y).toBeLessThan(before.y);
  const caretParagraph = editor.locator('p').first();
  const caretBounds = (await caretParagraph.boundingBox())!;
  expect(caretBounds.y).toBeGreaterThanOrEqual(0);
  expect(caretBounds.y).toBeLessThan(page.viewportSize()!.height);
  await slash.getByRole('option', { name: /^Nadpis 2/ }).hover();
  await slash.getByRole('option', { name: /^Nadpis 2/ }).click();
  await expect(editor.locator('h2')).toContainText('Odstavec 1');
});
