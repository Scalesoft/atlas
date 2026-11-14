// UX review cycle 3, area writer (findings WR3-n). Journeys of a
// non-programmer and of a keyboard-only developer writing pages: `ATLAS_URL=http://localhost:<vite> ATLAS_UX_DEV_LOGIN=1 npx playwright test ux-writer`.
// Pages go to the space „UX writer“ with titles „UX-writer …“.
import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { LIVE_EDITOR } from "./editing";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });
test.describe.configure({ timeout: 120_000 });

async function signIn(page: Page) {
  await page.goto('/');
  const developer = page.getByRole('button', { name: 'Přihlásit jako vývojář' });
  test.skip(!(await developer.waitFor({ timeout: 10_000 }).then(() => true, () => false)), 'Needs a Development instance with the developer sign-in.');
  await developer.click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ }).first()).toBeVisible();
}

async function send(context: BrowserContext, method: 'post', path: string, data?: unknown) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  const response = await context.request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
  expect(response.status(), await response.text()).toBeLessThan(300);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}

async function writerSpace(page: Page): Promise<string> {
  const context = page.context();
  const spaces = await (await context.request.get('/api/spaces')).json() as { id: string; name: string }[];
  return (spaces.find(item => item.name === 'UX writer') ?? await send(context, 'post', '/api/spaces', { name: 'UX writer', icon: 'pen-line', color: 'blue' })).id;
}

// WR3-1 (cycle 5, ED5-1): every page the journeys leave in „UX writer“ goes to the trash with its subtree, however it was created.
test.afterAll(async ({ browser }) => {
  const page = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  await signIn(page);
  const request = page.context().request;
  const spaces = await (await request.get('/api/spaces')).json() as { id: string; name: string }[];
  const space = spaces.find(item => item.name === 'UX writer');
  if (space) {
    const tree = await (await request.get(`/api/spaces/${space.id}/tree`)).json() as { version: number; items: { id: string; parentId: string | null; version: number }[] };
    let version = tree.version;
    for (const root of tree.items.filter(item => item.parentId === null)) {
      await send(page.context(), 'post', `/api/pages/${root.id}/trash`, { includeSubtree: true, expectedTreeVersion: version, expectedVersion: root.version });
      version = (await (await request.get(`/api/spaces/${space.id}/tree`)).json()).version;
    }
  }
  await page.close();
});

test('WR3-1: letters typed before the editor has the caret never reach the app shortcuts', async ({ page }) => {
  await signIn(page);
  const spaceId = await writerSpace(page);
  const { id } = await send(page.context(), 'post', '/api/pages', { spaceId, parentId: null, title: `UX-writer shortcuts ${Date.now()}`, markdown: '',
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } }) });
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator('[data-edit-lock]')).toBeVisible();
  await page.locator('.document-body .ProseMirror:not(.editor-hydrating *)').first().focus();
  // The editor needs a moment to sync; a fast typist's first letters (N opens the new-page form) land before it has the caret.
  await page.keyboard.type('nggk', { delay: 0 });
  await expect(page.locator(`${LIVE_EDITOR}:focus`)).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`/pages/${id}$`));
});

test('WR3-2: choosing a template with the mouse moves the caret to the page name', async ({ page }) => {
  await signIn(page);
  const spaceId = await writerSpace(page);
  await page.goto(`/#/spaces/${spaceId}/new-page`);
  await page.getByRole('radio', { name: /^Kontakty/ }).click();
  await expect(page.locator('input[name="page-title"]')).toBeFocused();
});
