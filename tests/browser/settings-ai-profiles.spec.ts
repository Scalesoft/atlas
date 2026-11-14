import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createAiAdminMock } from '../../src/Atlas.Web/src/components/settings/general/ai/aiAdminMock';
import type { AiProfileView, SaveAiProfileRequest } from '../../src/Atlas.Web/src/components/settings/general/ai/aiAdmin';
import type { SavedAiProfile } from '../../src/Atlas.Web/src/components/settings/general/ai/aiSavedProfiles';

// Every API request is fulfilled locally. Profiles and credentials are synthetic; this test never calls AI or reads a wiki.
const shots = fileURLToPath(new URL('../../artifacts/ai-saved-profiles/', import.meta.url));
test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' }, viewport: { width: 1440, height: 1000 } });

async function screenshot(page: Page, name: string) {
  await page.evaluate(() => {
    scrollTo(0, 0);
    for (const element of document.querySelectorAll('.workspace')) element.scrollTop = 0;
  });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(shots, name + '.png'), fullPage: name !== '03-cloud-confirmation', animations: 'disabled' });
}

test('saved AI profiles prepare offline and change the current model only after explicit confirmation', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  writeFileSync(resolve(shots, 'README.md'), '# Saved AI profiles\n\nScreenshots from the isolated settings-ai-profiles Playwright journey against Vite. All accounts, connections, models and API responses are simulated. No real wiki content, credentials or AI requests.\n');
  const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true });
  Object.assign(mock.state.profiles.chat, { provider: 'gateway', baseUrl: 'https://gateway.example.test/v1', model: 'gateway-routine', version: 'active-1' });
  const initial = structuredClone(mock.state.profiles.chat) as AiProfileView;
  const library: SavedAiProfile[] = [
    { id: '11111111-1111-4111-8111-111111111111', name: 'Gateway · běžná práce', version: 'saved-1', profile: { ...initial, version: 'saved-1' }, isActive: true },
    { id: '22222222-2222-4222-8222-222222222222', name: 'Cloud · náročné úlohy', version: 'saved-2', profile: {
      ...initial, provider: 'anthropic', location: 'cloud', baseUrl: 'https://api.anthropic.com', model: 'claude-demo',
      hasApiKey: true, version: 'saved-2',
    }, isActive: false },
  ];
  const savedBase = '/api/admin/ai/profiles/chat/saved';
  const requests: { method: string; path: string; body: unknown }[] = [];
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  let theme = 'light';
  let activeVersion = initial.version;
  let serial = 2;
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const path = url.pathname;
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    requests.push({ method, path, body });
    if (path === '/api/auth/me') return route.fulfill({ json: { id: 'u-demo-admin', username: 'demo.admin', displayName: 'Ukázkový správce', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: theme } } });
    if (path === '/api/setup/status') return route.fulfill({ json: { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true } });
    if (path === '/api/auth/methods') return route.fulfill({ json: { builtinEnabled: true, providers: [] } });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { token: 'mock-csrf' } });
    if (path === '/api/admin/setup') return route.fulfill({ json: { pending: false } });
    if (path === '/api/admin/modules') return route.fulfill({ json: { items: [{ id: 'ai', title: 'AI', description: 'Simulované nastavení AI.', settingsUi: 'ai', order: 21, showInSetup: false }] } });
    if (path === '/api/spaces') return route.fulfill({ json: [] });

    // Only the additive saved-profile routes are overridden; the existing admin endpoints use the shared mock.
    if (path === savedBase && method === 'GET') return route.fulfill({ json: { items: library, activeVersion } });
    if (path === savedBase && method === 'POST') {
      const input = body as { name: string; profile: SaveAiProfileRequest; expectedVersion: string | null };
      if (input.expectedVersion !== null) return route.fulfill({ status: 409, json: { detail: 'Uložený profil se změnil.' } });
      const { apiKey, clearApiKey, expectedVersion, ...configuration } = input.profile;
      const version = 'saved-' + ++serial;
      const saved: SavedAiProfile = {
        id: '33333333-3333-4333-8333-333333333333', name: input.name, version, isActive: false,
        profile: { ...initial, ...configuration, configured: true, hasApiKey: !!apiKey && !clearApiKey, modelLockedByIndex: false, version },
      };
      library.push(saved);
      return route.fulfill({ json: saved });
    }
    const selected = library.find(item => path === savedBase + '/' + item.id + '/activate');
    if (selected && method === 'POST') {
      if (body.expectedVersion !== activeVersion || body.expectedSavedVersion !== selected.version)
        return route.fulfill({ status: 409, json: { detail: 'Uložený nebo aktivní profil se změnil.' } });
      activeVersion = 'active-' + ++serial;
      mock.state.profiles.chat = { ...selected.profile, version: activeVersion };
      for (const item of library) item.isActive = item.id === selected.id;
      return route.fulfill({ json: mock.state.profiles.chat });
    }
    if (path === savedBase + '/models' && method === 'POST') return route.fulfill({ json: { ok: true, code: null, models: [{ id: 'local-backup-model', name: 'Local backup model' }] } });
    if (path.startsWith(savedBase)) return route.fulfill({ status: 404, json: { detail: 'Saved-profile mock route unavailable.' } });
    if (path === '/api/admin/groups' || path.startsWith('/api/admin/ai')) {
      const result = mock.handle(method, path + url.search, body);
      return result.body === undefined ? route.fulfill({ status: result.status }) : route.fulfill({ status: result.status, json: result.body });
    }
    if (path.startsWith('/api/me/library/')) return route.fulfill({ json: { items: [], hasMore: false } });
    return route.fulfill({ status: 404, json: { detail: method + ' ' + path } });
  });

  await page.goto('/#/settings/general/modules/ai/chat');
  const current = page.locator('[data-ai-current-fields]');
  await expect(current.locator('[data-ai-model]')).toHaveValue('gateway-routine');
  await expect(page.locator('[data-ai-saved-toggle]')).toBeEnabled();
  expect(requests.filter(item => item.path.startsWith(savedBase))).toHaveLength(0);
  await page.locator('[data-ai-saved-toggle]').click();
  await expect(page.locator('[data-ai-saved-profile]')).toHaveCount(2);
  await expect(page.locator('[data-ai-saved-profile]').first()).toContainText('Používá se');
  await screenshot(page, '01-prepared-library');

  await page.locator('[data-ai-saved-new]').click();
  const editor = page.locator('[data-ai-saved-editor]');
  await editor.locator('[data-ai-saved-name]').fill('LM Studio · záloha');
  await editor.getByRole('radio', { name: /Firemní LM Studio/ }).click();
  await editor.locator('[data-ai-url]').fill('http://lmstudio.example.test:1234/v1');
  await editor.locator('[data-ai-model]').fill('local-backup-model');
  await screenshot(page, '02-backup-editor');
  await editor.locator('[data-save-bar-save]').click();
  await expect(editor).toBeHidden();
  const backup = page.locator('[data-ai-saved-profile="33333333-3333-4333-8333-333333333333"]');
  await expect(backup).toContainText('LM Studio · záloha');
  await expect(backup).not.toContainText('Používá se');
  await expect(current.locator('[data-ai-model]')).toHaveValue('gateway-routine');
  expect(requests.filter(item => item.method === 'POST' && item.path === savedBase)).toHaveLength(1);
  expect(requests.filter(item => item.path.endsWith('/activate') || item.path.endsWith('/test'))).toHaveLength(0);

  expect(requests.filter(item => item.path.endsWith('/models')).map(item => item.path)).toEqual([savedBase + '/models']);
  await page.locator('[data-ai-saved-profile="22222222-2222-4222-8222-222222222222"] [data-ai-saved-use]').click();
  const confirm = page.getByRole('dialog', { name: 'Použít připravený profil?' });
  await expect(confirm).toContainText('Cloudový model zpracovává odeslaný text mimo firmu.');
  await expect(confirm).toContainText('Souhlasy uživatelů pro cloud');
  await expect(current.locator('[data-ai-model]')).toHaveValue('gateway-routine');
  expect(requests.filter(item => item.path.endsWith('/activate'))).toHaveLength(0);
  await screenshot(page, '03-cloud-confirmation');
  await confirm.locator('[data-confirm-action]').click();
  await expect(confirm).toBeHidden();
  await expect(current.locator('[data-ai-model]')).toHaveValue('claude-demo');
  await expect(page.locator('[data-ai-saved-profile="22222222-2222-4222-8222-222222222222"]')).toContainText('Používá se');
  expect(requests.filter(item => item.path.endsWith('/activate'))).toEqual([{
    method: 'POST', path: savedBase + '/22222222-2222-4222-8222-222222222222/activate',
    body: { expectedVersion: 'active-1', expectedSavedVersion: 'saved-2' },
  }]);
  expect(requests.filter(item => item.path.endsWith('/test') || item.path.includes('/index/generations'))).toHaveLength(0);
  await screenshot(page, '04-cloud-active');

  theme = 'dark';
  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-mode', 'dark');
  await page.locator('[data-ai-saved-toggle]').click();
  await expect(page.locator('[data-ai-saved-profile]')).toHaveCount(3);
  await expect(current.locator('[data-ai-model]')).toHaveValue('claude-demo');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  expect(overflow).toBe(false);
  await screenshot(page, '05-dark-mobile-library');
  const mobileCloud = page.locator('[data-ai-saved-profile="22222222-2222-4222-8222-222222222222"]');
  const badge = await mobileCloud.getByText('Používá se', { exact: true }).boundingBox();
  const actions = await mobileCloud.locator('.settings-list-row-actions').boundingBox();
  expect(badge).not.toBeNull();
  expect(actions).not.toBeNull();
  const separated = badge!.x + badge!.width <= actions!.x || actions!.x + actions!.width <= badge!.x
    || badge!.y + badge!.height <= actions!.y || actions!.y + actions!.height <= badge!.y;
  expect(separated, 'The active-profile badge must not overlap the mobile action buttons').toBe(true);
  expect(pageErrors).toEqual([]);
});
