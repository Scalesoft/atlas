import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { expectSaved, startEditing, PAGE_TEXT, LIVE_EDITOR } from './editing';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

async function openLongDraft(page: Page, context: BrowserContext) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).fill(process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.locator('.sidebar')).toBeAttached();
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const spaces = await (await context.request.get('/api/spaces')).json();
  const paragraphs = Array.from({ length: 80 }, (_, i) => `Paragraph ${i + 1}: This long document exercises editing stability across idle connection heartbeats and pauses between typing.`);
  const response = await context.request.post('/api/pages', {
    headers: { 'X-CSRF-TOKEN': csrf.token }, data: {
      spaceId: spaces[0].id, title: `Scroll stability ${Date.now()}`, parentId: null,
      contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: {
        type: 'doc', content: paragraphs.map(text => ({ type: 'paragraph', content: [{ type: 'text', text }] })),
      } }), markdown: paragraphs.join('\n\n'),
    },
  });
  expect(response.status()).toBe(200);
  const document = await response.json();
  await page.goto(`/#/pages/${document.id}`);
  await startEditing(page);
  const editor = page.locator(LIVE_EDITOR);
  await editor.locator('p').nth(40).click();
  await page.keyboard.press('Home');
  return editor;
}

test('idle collaboration preserves scroll, focus, selection and editor controls across server heartbeats', async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !process.env.ATLAS_E2E_PASSWORD, 'Requires the initialized development acceptance wiki.');
  const editor = await openLongDraft(page, context);
  await page.keyboard.press('Shift+ArrowRight');
  await page.mouse.move(270, 20);
  await expect(page.locator('.bubble-menu')).toBeVisible();
  const selectedText = await page.evaluate(() => document.getSelection()!.toString());
  expect(selectedText).toHaveLength(1);
  await page.screenshot({ path: '../../artifacts/ux-deep/scroll-selected-before.png' });
  await editor.evaluate(element => {
    const initialY = window.scrollY;
    const state = { maxMovement: 0, disabled: 0, replaced: false, lostFocus: false };
    Object.assign(window, { __scrollStability: state });
    document.addEventListener('scroll', () => { state.maxMovement = Math.max(state.maxMovement, Math.abs(window.scrollY - initialY)); }, true);
    new MutationObserver(records => {
      state.disabled += records.filter(record => record.oldValue === 'false').length;
      state.replaced ||= !element.isConnected;
      state.lostFocus ||= document.activeElement !== element;
    }).observe(element, { attributes: true, attributeFilter: ['contenteditable'], attributeOldValue: true });
    element.setAttribute('data-stability-instance', 'original');
  });
  // The default ASP.NET Core server sends a keepalive at 15s. Waiting past that
  // catches the former 6s client timeout and more than one reconnect cycle.
  await page.waitForTimeout(17_000);
  const stability = await page.evaluate(() => (window as any).__scrollStability);
  expect(stability).toEqual({ maxMovement: 0, disabled: 0, replaced: false, lostFocus: false });
  await expect(editor).toBeFocused();
  await expect(editor).toHaveAttribute('data-stability-instance', 'original');
  expect(await page.evaluate(() => document.getSelection()!.toString())).toBe(selectedText);
  await expect(page.locator('.bubble-menu')).toBeVisible();
  await page.screenshot({ path: '../../artifacts/ux-deep/scroll-selected-after.png' });
  // Typing after a realistic pause must still reach the original selection.
  await page.keyboard.type('stable');
  await expect(editor.locator('p').nth(40)).toContainText('stable');
  await page.waitForTimeout(17_000);
  await expect(editor).toBeFocused();
  await page.keyboard.type(' continuation');
  await expect(editor.locator('p').nth(40)).toContainText('stable continuation');
  expect(await page.evaluate(() => (window as any).__scrollStability)).toEqual(stability);
  await page.keyboard.press('Control+z');
  await expect(editor.locator('p').nth(40)).not.toContainText(' continuation');
});

for (const width of [1280, 390]) test(`a forced offline transition preserves geometry and draft at ${width}px`, async ({ page, context }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !process.env.ATLAS_E2E_PASSWORD, 'Requires the initialized development acceptance wiki.');
  await page.setViewportSize({ width, height: 900 });
  await openLongDraft(page, context);
  const editor = page.locator(PAGE_TEXT);
  await editor.evaluate(element => element.setAttribute('data-reconnect-instance', 'original'));
  await page.keyboard.type('Before reconnect ');
  await expectSaved(page);
  await page.evaluate((selector) => {
    const anchor = document.querySelectorAll(selector)[40];
    const toolbar = document.querySelector('.md-plugin-toolbar')!;
    const footer = document.querySelector('.paper-footer')!;
    const measure = () => ({ y: scrollY, textTop: anchor.getBoundingClientRect().top,
      height: document.documentElement.scrollHeight, header: document.querySelector('.document-header')!.getBoundingClientRect().height, toolbar: toolbar.getBoundingClientRect().height,
      footer: footer.getBoundingClientRect().height });
    Object.assign(window, { __offlineGeometry: measure(), __measureOfflineGeometry: measure });
  }, `${PAGE_TEXT} p`);
  const geometry = await page.evaluate(() => (window as any).__offlineGeometry);
  if (width === 1280) {
    const peer = await context.newPage();
    await peer.goto(page.url());
    await startEditing(peer);
    await expect(page.getByTestId('presence-stack')).toBeVisible();
    expect(await page.evaluate(() => (window as any).__measureOfflineGeometry())).toEqual(geometry);
    await peer.getByRole('button', { name: 'Atlas', exact: true }).click();
    await expect(peer.locator(PAGE_TEXT)).toHaveCount(0);
    await expect(page.getByTestId('presence-stack')).toHaveCount(0);
    await peer.close();
    expect(await page.evaluate(() => (window as any).__measureOfflineGeometry())).toEqual(geometry);
  }
  await context.setOffline(true);
  try {
    await expect(editor).toHaveAttribute('contenteditable', 'false');
    expect(await page.evaluate(() => (window as any).__measureOfflineGeometry())).toEqual(geometry);
    await page.screenshot({ path: `../../artifacts/ux-deep/scroll-offline-${width}.png` });
    await expect(editor.locator('p').nth(40)).toContainText('Before reconnect');
  } finally {
    await context.setOffline(false);
  }
  await expect(editor).toHaveAttribute('contenteditable', 'true', { timeout: 30_000 });
  await expect(editor).toHaveAttribute('data-reconnect-instance', 'original');
  expect(await page.evaluate(() => (window as any).__measureOfflineGeometry())).toEqual(geometry);
  await expect(editor.locator('p').nth(40)).toContainText('Before reconnect');
  await editor.locator('p').nth(40).click();
  await page.keyboard.press('End');
  await page.keyboard.type(' After reconnect');
  await expect(editor.locator('p').nth(40)).toContainText('After reconnect');
  await expectSaved(page);
});
