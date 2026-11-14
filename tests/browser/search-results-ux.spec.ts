import { expect, test, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

const ARTIFACTS = '../../artifacts/search-results-ux';
test.beforeAll(() => mkdirSync(ARTIFACTS, { recursive: true }));
const space = { id: 'engineering', name: 'Engineering handbook', icon: 'book', color: 'blue', role: 'Admin' };
const topics = [
  'Incident response', 'Service ownership', 'Release readiness', 'Database recovery', 'API compatibility',
  'Observability guide', 'Security review', 'Capacity planning', 'On-call handover', 'Dependency updates',
];

const results = Array.from({ length: 60 }, (_, index) => ({
  id: `result-${index + 1}`,
  spaceId: space.id,
  spaceName: space.name,
  title: `${topics[index % topics.length]} · playbook ${String(index + 1).padStart(2, '0')}`,
  snippet: `Practical search guidance for ${topics[index % topics.length].toLowerCase()}, including owners, review dates, and the next verified action.`,
  changedAt: new Date(Date.UTC(2026, 8, 14 - (index % 12), 8 + (index % 8))).toISOString(),
  isDraft: index % 11 === 0,
  matchedAlias: index % 9 === 0 ? `Runbook ${index + 1}` : null,
}));

async function installFixture(page: Page, mode: 'light' | 'dark' = 'light', answer = false) {
  const searchRequests: URL[] = [];
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    let json: unknown;
    if (path === '/api/setup/status') json = { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: false };
    else if (path === '/api/auth/me') json = { id: 'reader', username: 'reader', displayName: 'Alex Reader', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode } };
    else if (path === '/api/spaces') json = [space];
    else if (path === '/api/ai/capabilities') json = { answer, assistant: false, related: false, semanticSearch: false, summary: false, editorSuggestions: false };
    else if (path === '/api/me/ai-preferences') json = { searchAnswer: 'auto' };
    else if (path === '/api/auth/csrf') json = { token: 'local-test-csrf' };
    else if (path === '/api/ai/conversations') json = { conversationId: 'search-answer', messageId: 'answer', state: 'no_sources', sources: [] };
    else if (path === `/api/spaces/${space.id}/tree`) json = { spaceId: space.id, version: 1, items: [] };
    else if (path === '/api/search') {
      searchRequests.push(new URL(url));
      const offset = Number(url.searchParams.get('offset') ?? 0);
      json = offset === 0
        ? { items: results.slice(0, 50), spaces: [], hasMore: true, nextOffset: 50 }
        : { items: results.slice(offset, offset + 10), spaces: [], hasMore: false, nextOffset: null };
    }
    else if (path === '/api/integrations/youtrack/search') json = [];
    else if (path.startsWith('/api/me/library/')) json = { items: [], hasMore: false, nextOffset: null };
    else if (path === '/api/tags') json = { items: [] };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json });
  });
  return searchRequests;
}

async function openPopulatedSearch(page: Page) {
  await page.goto('/');
  const launch = page.getByRole('button', { name: /^Hledat ve wiki/ });
  await expect(launch).toBeVisible();
  await launch.click();
  const dialog = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  const field = dialog.locator('.omnisearch-input input');
  await field.fill('playbook');
  const options = dialog.getByRole('listbox', { name: 'Výsledky hledání', exact: true }).getByRole('option');
  await expect(options).toHaveCount(50);
  await expect(dialog.locator('.search-source-status')).toContainText('Wiki · hotovo');
  return { dialog, field, options };
}

async function fullyVisibleResultCount(dialog: Locator) {
  return dialog.evaluate(element => {
    const content = element.querySelector('.search-content')!.getBoundingClientRect();
    const controls = element.querySelector('.search-query-controls')!.getBoundingClientRect();
    const footer = element.querySelector('.search-footer')!.getBoundingClientRect();
    const top = Math.max(content.top, controls.bottom);
    const bottom = Math.min(content.bottom, footer.top);
    return [...element.querySelectorAll<HTMLElement>('#search-results [role="option"]')].filter(option => {
      const box = option.getBoundingClientRect();
      const style = getComputedStyle(option);
      return box.top >= top && box.bottom <= bottom && style.visibility !== 'hidden' && Number(style.opacity) > 0.99;
    }).length;
  });
}

test('dense results arrive quickly and pagination preserves the selected rows', async ({ page }) => {
  const requests = await installFixture(page);
  const { dialog, field, options } = await openPopulatedSearch(page);

  await expect.poll(() => fullyVisibleResultCount(dialog)).toBeGreaterThanOrEqual(8);
  expect(requests.length).toBeGreaterThanOrEqual(2);
  expect(requests.every(url => url.searchParams.get('limit') === '50'), 'Every preview, ranked, and paginated search uses the explicit supported page size.').toBe(true);
  await expect(dialog.locator('.search-summary-count')).toContainText('50');
  await expect(options.first().locator('.search-result-meta')).toHaveCSS('display', 'flex');
  const rowHeights = await options.evaluateAll(nodes => nodes.slice(0, 8).map(node => node.getBoundingClientRect().height));
  expect(rowHeights[0], 'The draft and alias-rich row stays compact.').toBeLessThanOrEqual(100);
  expect(Math.max(...rowHeights.slice(1)), 'Ordinary result rows stay within the dense layout.').toBeLessThanOrEqual(80);
  await dialog.locator('.search-content').evaluate(element => { element.scrollTop = 0; });
  await page.screenshot({ path: `${ARTIFACTS}/after-light-desktop.png` });

  const firstRow = options.first();
  await firstRow.evaluate(element => element.setAttribute('data-original-row', 'yes'));
  await field.focus();
  await field.press('PageDown');
  await expect(field).toBeFocused();
  await expect(options.nth(8)).toHaveAttribute('aria-selected', 'true');
  await expect(options.nth(8)).toBeInViewport();

  const selected = options.nth(8);
  await selected.evaluate(element => element.setAttribute('data-selected-row', 'yes'));
  const more = dialog.getByRole('button', { name: 'Načíst další výsledky', exact: true });
  await expect(more).toBeInViewport();
  // Arrival now uses the shared Web Animations primitive, not a CSS keyframe name.
  // Record its real effect so this check does not race the short finished animation.
  await page.evaluate(() => {
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (...args: Parameters<Element['animate']>) {
      const animation = animate.apply(this, args);
      if (this.matches('.search-result')) this.setAttribute('data-test-arrival', JSON.stringify(animation.effect?.getTiming()));
      return animation;
    };
  });
  await more.click();
  await expect(options).toHaveCount(60);
  await expect(dialog.locator('.search-summary-count')).toContainText('60');
  await expect(firstRow).toHaveAttribute('data-original-row', 'yes');
  await expect(selected).toHaveAttribute('data-selected-row', 'yes');
  await expect(selected).toHaveAttribute('aria-selected', 'true');
  expect(requests.at(-1)?.searchParams.get('offset')).toBe('50');

  await expect(options.nth(50)).toHaveAttribute('data-test-arrival');
  const animation = JSON.parse((await options.nth(50).getAttribute('data-test-arrival'))!);
  expect(animation.delay).toBe(0);
  expect(animation.duration).toBeLessThanOrEqual(200);
});

test('typing then Down selects the first result and commands stay navigable from the field', async ({ page }) => {
  await installFixture(page);
  const { field, options, dialog } = await openPopulatedSearch(page);
  await expect(dialog.getByRole('complementary', { name: 'Odpověď AI' })).toHaveCount(0);
  await field.press('ArrowDown');
  await expect(options.first()).toHaveAttribute('aria-selected', 'true');
  await expect(field).toBeFocused();
  await field.press('ArrowDown');
  await expect(options.nth(1)).toHaveAttribute('aria-selected', 'true');
  await field.press('ArrowUp');
  await expect(options.first()).toHaveAttribute('aria-selected', 'true');
  await field.fill('>');
  await field.press('ArrowDown');
  const commands = dialog.getByRole('listbox', { name: 'Příkazy', exact: true }).getByRole('option');
  await expect(commands.first()).toHaveAttribute('aria-selected', 'true');
  await field.press('ArrowDown');
  await expect(commands.nth(1)).toHaveAttribute('aria-selected', 'true');
  await field.press('Enter');
  await expect(dialog).not.toBeVisible();
  await expect(page).toHaveURL(/#\/library\/favorites/);
});

test('AI answers beside results without moving the keyboard selection', async ({ page }) => {
  await installFixture(page, 'light', true);
  const { dialog, field, options } = await openPopulatedSearch(page);
  await field.press('ArrowDown');
  const answer = dialog.getByRole('complementary', { name: 'Odpověď AI' });
  await expect(answer).toBeVisible();
  const resultsBox = await dialog.locator('.search-content').boundingBox();
  const answerBox = await answer.boundingBox();
  expect(answerBox!.x).toBeGreaterThanOrEqual(resultsBox!.x + resultsBox!.width - 1);
  await expect(answer.locator('[data-search-answer]')).toBeVisible();
  await expect(options.first()).toHaveAttribute('aria-selected', 'true');
  await expect(field).toBeFocused();
  await field.press('F6');
  await expect.poll(() => answer.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Shift+F6');
  await expect(field).toBeFocused();
});

test('dark, reduced-motion, and mobile layouts keep search usable', async ({ page }) => {
  await installFixture(page, 'dark');
  const { dialog, field, options } = await openPopulatedSearch(page);
  await expect(page.locator('html')).toHaveAttribute('data-mode', 'dark');
  await page.screenshot({ path: `${ARTIFACTS}/after-dark-desktop.png` });

  await page.emulateMedia({ reducedMotion: 'reduce' });
  await expect(options.first()).toHaveCSS('animation-name', 'none');
  await expect(options.first()).toHaveCSS('transition-duration', '0s');

  await page.setViewportSize({ width: 390, height: 520 });
  await dialog.locator('.search-content').evaluate(element => { element.scrollTop = 0; });
  const more = dialog.getByRole('button', { name: 'Načíst další výsledky', exact: true });
  await expect(more).toBeInViewport();
  await expect(dialog.locator('.search-footer')).toBeInViewport();
  expect(await dialog.locator('.search-query-controls, .search-footer').evaluateAll(elements => elements.every(element => {
    const box = element.getBoundingClientRect();
    return box.left >= 0 && box.right <= innerWidth && box.top >= 0 && box.bottom <= innerHeight
      && element.scrollWidth <= element.clientWidth + 1 && element.scrollHeight <= element.clientHeight + 1;
  })), 'The sticky field and footer remain fully visible without clipped text.').toBe(true);
  await field.focus();
  await field.press('PageDown');
  await expect(field).toBeFocused();
  await expect(options.nth(8)).toHaveAttribute('aria-selected', 'true');
  await expect(options.nth(8)).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  const fieldBox = await field.boundingBox();
  expect(fieldBox!.y).toBeGreaterThanOrEqual(0);
  expect(fieldBox!.y + fieldBox!.height).toBeLessThanOrEqual(520);
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: `${ARTIFACTS}/after-mobile.png` });
});
