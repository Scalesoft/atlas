import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { createAiAdminMock } from '../../src/Atlas.Web/src/components/settings/general/ai/aiAdminMock';

// Simulated data only. Every /api request is fulfilled here; no wiki, model, or provider is contacted.
const shots = fileURLToPath(new URL('../../artifacts/ai-setup-walkthrough/', import.meta.url));
mkdirSync(shots, { recursive: true });
writeFileSync(resolve(shots, 'README.md'), '# AI setup walkthrough\n\nScreenshots from a mocked Playwright journey against the local Vite frontend. All accounts, pages, models, keys, index counts, and API replies are simulated. No live AI provider or wiki data is used.\n');

test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' }, viewport: { width: 1440, height: 1000 } });

async function screenshot(page: Page, name: string) {
  await page.evaluate(() => {
    scrollTo(0, 0);
    for (const element of document.querySelectorAll('.workspace')) element.scrollTop = 0;
  });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(shots, name + '.png'), fullPage: true, animations: 'disabled' });
}

test('a fresh administrator saves each AI setup step through group access', async ({ page }) => {
  const mock = createAiAdminMock({ step: 2000 });
  const emptyAccess = mock.handle('PUT', '/admin/ai/access', { expectedVersion: 'a1', groups: [] });
  expect(emptyAccess.status).toBe(200);
  // Mirror the remaining writing tasks from AiTaskCatalog; the base mock predates them.
  mock.state.features.items.push(
    ...['editor.intent', 'editor.transform', 'editor.outline', 'publish.check', 'publish.change-summary', 'page.tag-suggest']
      .map(task => ({ task, group: 'write', enabled: false, profileKind: 'quick', cloudWorkingText: false })),
  );

  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const path = url.pathname;
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    if (path === '/api/auth/me') return route.fulfill({ json: { id: 'u-demo-admin', username: 'demo.admin', displayName: 'Ukázkový správce', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } } });
    if (path === '/api/setup/status') return route.fulfill({ json: { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true } });
    if (path === '/api/auth/methods') return route.fulfill({ json: { builtinEnabled: true, providers: [] } });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { token: 'mock-csrf' } });
    if (path === '/api/admin/setup') return route.fulfill({ json: { pending: false } });
    if (path === '/api/admin/modules') return route.fulfill({ json: { items: [{ id: 'ai', title: 'AI', description: 'Simulované nastavení AI.', settingsUi: 'ai', order: 21, showInSetup: false }] } });
    if (path === '/api/spaces') return route.fulfill({ json: [] });
    if (path === '/api/admin/groups' || path.startsWith('/api/admin/ai')) {
      const result = mock.handle(method, path + url.search, body);
      return result.body === undefined ? route.fulfill({ status: result.status }) : route.fulfill({ status: result.status, json: result.body });
    }
    if (path === '/api/me/library/recent' || path.startsWith('/api/me/library/')) return route.fulfill({ json: { items: [], hasMore: false } });
    return route.fulfill({ status: 404, json: { title: 'Mock endpoint unavailable', detail: method + ' ' + path } });
  });

  await page.goto('/#/settings/general/modules/ai');
  await expect(page.locator('[data-ai-setup-step="power"]')).toBeVisible();
  await expect(page.locator('[data-ai-setup-step]')).toHaveCount(1);
  await screenshot(page, '01-unconfigured');

  await page.locator('[data-ai-setup-step="power"] [data-ai-switch]').click();
  await page.locator('[data-ai-setup-step="power"] [data-save-bar-save]').click();
  await expect(page.locator('[data-ai-setup-step="embeddings"]')).toBeVisible();
  expect(mock.state.sw.enabled).toBe(true);
  await screenshot(page, '02-master-enabled');

  await page.locator('[data-ai-setup-step="embeddings"]').getByRole('radio', { name: /Firemní LM Studio/ }).click();
  await page.locator('[data-ai-setup-step="embeddings"] [data-ai-model]').fill('demo-embedding-model');
  await screenshot(page, '03-embedding-provider');
  await page.locator('[data-ai-setup-step="embeddings"] [data-save-bar-save]').click();
  await expect(page.locator('[data-ai-setup-step="index"]')).toBeVisible();
  expect(mock.state.profiles.embeddings.enabled).toBe(true);
  await screenshot(page, '04-embedding-saved');

  await page.locator('[data-ai-setup-step="index"] [data-ai-build-action]').click();
  await expect(page.locator('[data-ai-index-flow="build"]')).toBeVisible();
  await screenshot(page, '05-index-plan');
  await page.locator('[data-ai-setup-step="index"] [data-flow-next]').click();
  await expect(page.locator('[data-ai-setup-step="index"] [data-step="build"]')).toBeVisible();
  await expect(page.locator('[data-ai-setup-step="index"] [data-flow-next]')).toBeEnabled();
  await screenshot(page, '06-index-built');
  await page.locator('[data-ai-setup-step="index"] [data-flow-next]').click();
  await page.locator('[data-ai-setup-step="index"] [data-ai-verify]').click();
  await expect(page.locator('[data-ai-setup-step="index"] [data-ai-verification]')).toContainText('Index jde aktivovat');
  await screenshot(page, '07-index-verified');
  await page.locator('[data-ai-setup-step="index"] [data-flow-next]').click();
  await expect(page.locator('[data-ai-setup-step="index"] [data-ai-activate-impact]')).toBeVisible();
  await screenshot(page, '08-index-activation');
  await page.locator('[data-ai-setup-step="index"] [data-flow-next]').click();
  await expect(page.locator('[data-ai-setup-step="chat"]')).toBeVisible();
  expect(mock.state.active?.state).toBe('Active');
  await screenshot(page, '09-index-active');

  await page.locator('[data-ai-setup-step="chat"]').getByRole('radio', { name: /Anthropic/ }).click();
  await page.locator('[data-ai-setup-step="chat"] [data-ai-model]').fill('claude-example-model');
  await page.locator('[data-ai-setup-step="chat"] [data-ai-key-input]').fill('sk-ant-example-not-a-real-key');
  await screenshot(page, '10-chat-provider');
  await page.locator('[data-ai-setup-step="chat"] [data-save-bar-save]').click();
  await expect(page.locator('[data-ai-setup-step="access"]')).toBeVisible();
  expect(mock.state.profiles.chat.enabled).toBe(true);
  expect(JSON.stringify(mock.state.profiles.chat)).not.toContain('sk-ant-example');
  await screenshot(page, '11-chat-saved-access-empty');

  await page.locator('[data-ai-setup-step="access"] [data-ai-add-group]').click();
  await page.locator('[data-ai-setup-step="access"] [data-value="g-ops"]').click();
  await expect(page.locator('[data-ai-setup-step="access"] [data-ai-access-group="g-ops"]')).toBeVisible();
  await screenshot(page, '12-group-selected');
  await page.locator('[data-ai-setup-step="access"] [data-save-bar-save]').click();
  await expect(page.locator('[data-ai-setup-complete]')).toBeVisible();
  await screenshot(page, '13-setup-complete');

  await page.locator('[data-ai-setup-complete] a[href*="/features"]').click();
  await expect(page.locator('[data-ai-group="write"] [data-ai-feature]')).toHaveCount(7);
  await screenshot(page, '14-features');

  // Optional writing assistance borrows the saved Chat profile. Only enabled writing tasks
  // then reveal consent for unpublished text sent to the simulated cloud provider.
  await page.goto('/#/settings/general/modules/ai/quick');
  await expect(page.locator('[data-ai-profile="quick"]')).toBeVisible();
  await page.locator('[data-value="chat"] [role="radio"]').click();
  await page.locator('[data-save-bar-save]').click();
  await expect(page.locator('[data-ai-quick-chat]')).toBeVisible();
  expect(mock.state.profiles.quick.useChatProfile).toBe(true);
  await screenshot(page, '15-quick-uses-chat');

  await page.goto('/#/settings/general/modules/ai/features');
  await expect(page.locator('[data-ai-group="write"] [data-ai-feature]')).toHaveCount(7);
  await page.locator('[data-ai-feature="editor.transform"] [data-ai-feature-switch]').click();
  await page.locator('[data-ai-feature="editor.intent"] [data-ai-feature-switch]').click();
  await expect(page.locator('[data-ai-cloud-permissions="write"] [data-ai-cloud-consent]')).toHaveCount(2);
  await screenshot(page, '16-writing-cloud-consent');
  await page.locator('[data-ai-cloud-consent="editor.transform"] [role="checkbox"]').click();
  await page.locator('[data-save-bar-save]').click();
  await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
  expect(mock.state.features.items.find((item: { task: string }) => item.task === 'editor.transform').cloudWorkingText).toBe(true);
  expect(mock.state.features.items.find((item: { task: string }) => item.task === 'editor.intent').cloudWorkingText).toBe(false);
  await screenshot(page, '17-writing-consent-saved');
  await expect(page.getByRole('region', { name: 'Uložení změn' })).toBeHidden();
  await page.locator('[data-ai-group="write"]').screenshot({ path: resolve(shots, '18-writing-section.png'), animations: 'disabled' });
  expect(mock.calls.some(call => call.path === '/admin/ai/access' && call.method === 'PUT' && JSON.stringify(call.body).includes('g-ops'))).toBe(true);
  await page.goto('/#/settings/general/modules/ai/usage');
  await expect(page.locator('[data-ai-provider-row]').first()).toBeVisible();
  await screenshot(page, '19-calls-and-costs');

  // A provider outage stays discoverable for an administrator. Retrying is explicit and
  // uses the saved profile, while automatic metadata refresh never spends model tokens.
  let recovered = false;
  let probes = 0;
  await page.route('**/api/admin/ai', async route => {
    const body = mock.handle('GET', '/admin/ai').body as { profiles: { kind: string; state: string; lastErrorCode: string | null; since: string }[] };
    const chat = body.profiles.find(item => item.kind === 'chat')!;
    chat.state = recovered ? 'Available' : 'Unavailable';
    chat.lastErrorCode = recovered ? null : 'timeout';
    chat.since = '2026-10-06T00:00:00Z';
    await route.fulfill({ json: body });
  });
  await page.route('**/api/admin/ai/profiles/chat/test', async route => {
    probes++; recovered = true;
    await route.fulfill({ json: { ok: true, code: null, latencyMs: 80, model: 'claude-example-model', dimensions: null } });
  });
  await page.goto('/#/settings/general/modules/ai/overview');
  await page.reload();
  await expect(page.locator('[data-ai-health-profile="chat"]')).toBeVisible();
  await expect(page.locator('.settings-nav [data-path="general/modules/ai"] .settings-nav-count')).toBeVisible();
  expect(probes).toBe(0);
  await screenshot(page, '20-admin-model-outage');
  await page.locator('[data-ai-health-retry="chat"]').click();
  await expect(page.locator('[data-ai-health-profile="chat"]')).toHaveCount(0);
  await expect(page.locator('[data-ai-health-status]')).toContainText('funguje');
  expect(probes).toBe(1);
  await screenshot(page, '21-admin-model-recovered');

});
