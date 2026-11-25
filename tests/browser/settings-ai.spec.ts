import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { createAiAdminMock, type AiAdminMock, type AiAdminMockOptions } from '../../src/Atlas.Web/src/components/settings/general/ai/aiAdminMock';

// Administrace → AI (W6) against a mocked API: Přehled, Embeddingy, Rychlé úlohy, Funkce and the Index with its StepFlow,
// light and dark, desktop and narrow. The A1 endpoints are answered like the merged server; the overview (A3), the connection
// test (A2) and the index (V2) are the MOCKED contracts of aiAdminMock.ts. Run against a Vite dev server, no wiki needed:
//   pnpm --dir src/Atlas.Web exec vite --port 5349
//   $env:ATLAS_URL='http://127.0.0.1:5349'; npx --prefix tests/browser playwright test settings-ai.spec.ts
// Screenshots go to AI_SHOTS (default artifacts/w6-admin-ai), prefixed by the scene.
const shots = resolve(process.env.AI_SHOTS ?? '../../artifacts/w6-admin-ai');
mkdirSync(shots, { recursive: true });

test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });

type Combo = { theme: 'light' | 'dark'; device: 'desktop' | 'mobile' };
const combos: Combo[] = [
  { theme: 'light', device: 'desktop' },
  { theme: 'dark', device: 'desktop' },
  { theme: 'light', device: 'mobile' },
  { theme: 'dark', device: 'mobile' },
];
const viewport = (device: Combo['device']) => (device === 'desktop' ? { width: 1440, height: 1000 } : { width: 390, height: 844 });
const modules = [
  { id: 'oidc', title: 'Firemní přihlášení', description: 'Připojte JetBrains Hub nebo jiného poskytovatele OpenID Connect.', settingsUi: 'oidc', order: 10, showInSetup: true },
  { id: 'youtrack', title: 'YouTrack', description: 'Odkazy na úkoly a jejich stav přímo ve stránkách.', settingsUi: 'youtrack', order: 20, showInSetup: true },
  { id: 'ai', title: 'AI', description: 'Významové hledání, související stránky a návrhy nad obsahem wiki. Volitelné; bez nastavení se wiki chová jako dosud.', settingsUi: 'ai', order: 21, showInSetup: false },
  { id: 'wiki-js-import', title: 'Import Wiki.js', description: 'Přeneste stránky a přílohy z exportu Wiki.js.', settingsUi: 'wiki-js-import', order: 30, showInSetup: true },
];

async function open(page: Page, combo: Combo, hash: string, options: AiAdminMockOptions): Promise<AiAdminMock> {
  const mock = createAiAdminMock(options);
  await page.setViewportSize(viewport(combo.device));
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    const path = url.pathname;
    if (path === '/api/auth/me') return route.fulfill({ json: { id: 'u-martin', username: 'marek.musil', displayName: 'Marek Musil', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: combo.theme } } });
    if (path === '/api/setup/status') return route.fulfill({ json: { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true } });
    if (path === '/api/auth/methods') return route.fulfill({ json: { builtinEnabled: true, providers: [] } });
    if (path === '/api/auth/csrf') return route.fulfill({ json: { token: 'csrf-test' } });
    if (path === '/api/admin/setup') return route.fulfill({ json: { pending: false } });
    if (path === '/api/admin/modules') return route.fulfill({ json: { items: modules } });
    if (path === '/api/spaces') return route.fulfill({ json: [] });
    if (path.startsWith('/api/admin/ai')) {
      const result = mock.handle(method, `${path}${url.search}`, body);
      return result.body === undefined ? route.fulfill({ status: result.status }) : route.fulfill({ status: result.status, json: result.body });
    }
    if (path === '/api/me/library/recent' || path.startsWith('/api/me/library/')) return route.fulfill({ json: { items: [], hasMore: false } });
    return route.fulfill({ status: 404, json: { title: `Mock: ${method} ${path}`, detail: `Mock: ${method} ${path}` } });
  });
  await page.goto(`/${hash}`);
  await expect(page.locator('.settings-page').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  return mock;
}
async function go(page: Page, hash: string) {
  await page.evaluate((target) => { location.hash = target; }, hash);
  await page.waitForTimeout(150);
}
function shooter(page: Page, combo: Combo) {
  return async (name: string) => {
    await page.evaluate(() => { scrollTo(0, 0); for (const element of document.querySelectorAll('.workspace')) element.scrollTop = 0; });
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${shots}/${name}-${combo.theme}-${combo.device}.png`, fullPage: true });
  };
}
const base = '#/settings/general/modules/ai';

for (const combo of combos) {
  test.describe(`${combo.theme} ${combo.device}`, () => {
    test('module card and Přehled with a failing embeddings profile', async ({ page }) => {
      const shot = shooter(page, combo);
      await open(page, combo, '#/settings/general/modules', { enabled: true, configured: true, embeddingsDown: true, activeIndex: true });
      await expect(page.locator('[data-module="ai"]')).toHaveCount(0);
      if (combo.device === 'mobile') await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
      await expect(page.locator('.settings-nav [data-path="general/modules/ai"]').filter({ visible: true })).toBeVisible();
      if (combo.device === 'mobile') await page.keyboard.press('Escape');
      await shot('01-module-card');

      await go(page, `${base}/overview`);
      await expect(page.locator('[data-ai-tile="embeddings"]')).toContainText('Nedostupné od');
      await expect(page.locator('[data-ai-tile-error]')).toContainText('Server modelu neodpověděl včas.');
      await expect(page.locator('[data-ai-tile="index"]')).toContainText('1 480');
      await shot('02-overview');

      await page.locator('[data-ai-switch]').click();
      await expect(page.locator('[data-ai-switch-impact]')).toContainText('Vypnutí skryje všechny AI funkce; wiki funguje jako bez AI.');
      await shot('03-overview-switch-off');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('dialog', { name: 'Vypnout AI ve wiki?' })).toBeVisible();
      await shot('04-overview-confirm-off');
      await page.locator('dialog[open] [data-confirm-action]').click();
      await expect(page.locator('[data-ai-switch-state]')).toHaveText('Vypnuto');
    });

    test('Embeddingy: progressive disclosure, write-only key, test and the locked model', async ({ page }) => {
      const shot = shooter(page, combo);
      const mock = await open(page, combo, `${base}/embeddings`, { enabled: true });
      await expect(page.locator('[data-ai-url]')).toHaveCount(0);
      await shot('10-embeddings-empty');
      await page.getByRole('radio', { name: /OpenAI/ }).click();
      await expect(page.locator('[data-ai-default-url]')).toContainText('https://api.openai.com/v1');
      await page.locator('[data-ai-model]').fill('text-embedding-3-large');
      await page.locator('[data-ai-key-input]').fill('sk-test-not-a-real-key');
      await page.locator('[data-ai-profile] .settings-section-header.is-toggle').click();
      await shot('11-embeddings-openai');
      await page.locator('[data-ai-enabled]').click();
      await page.locator('[data-save-bar-save]').click();
      await expect(page.locator('[data-ai-key-saved]')).toContainText('Uložený klíč ✓');
      expect(JSON.stringify(mock.state.profiles.embeddings)).not.toContain('sk-test');
      await expect(page.locator('[data-ai-test]')).toBeEnabled();
      await page.locator('[data-ai-test]').click();
      await expect(page.locator('[data-ai-test-result]')).toContainText('Spojení funguje');
      await shot('12-embeddings-saved-tested');
      // Switching the provider keeps the model, resets the address and says so.
      await page.getByRole('radio', { name: /Firemní LM Studio/ }).click();
      await expect(page.locator('[data-ai-url]')).toHaveValue('http://127.0.0.1:1234/v1');
      await expect(page.locator('[data-provider-dropped]')).toContainText('Adresa API');
      await shot('13-embeddings-provider-change');

    });

    test('Embeddingy with an active index lock the model and lead to the swap', async ({ page }) => {
      const shot = shooter(page, combo);
      await open(page, combo, `${base}/embeddings`, { enabled: true, configured: true, activeIndex: true });
      await expect(page.locator('[data-ai-model-locked]')).toBeVisible();
      await page.locator('[data-ai-profile] .settings-section-header.is-toggle').click();
      await shot('14-embeddings-locked');
      await page.locator('[data-ai-swap]').click();
      await expect(page.locator('[data-ai-index-flow="replace"]')).toBeVisible();
      await expect(page.locator('[data-step="profile"]')).toBeVisible();
      await shot('15-index-swap-profile');
    });

    test('Rychlé úlohy and Funkce', async ({ page }) => {
      const shot = shooter(page, combo);
      await open(page, combo, `${base}/quick`, { enabled: true, configured: true });
      await expect(page.locator('[data-ai-profile="quick"]')).toBeVisible();
      await page.locator('[data-ai-profile] .settings-section-header.is-toggle').click();
      await shot('20-quick');
      await go(page, `${base}/features`);
      await expect(page.locator('[data-ai-group]')).toHaveCount(4);
      await expect(page.locator('[data-ai-feature="search.answer"], [data-ai-feature="chat.answer"]')).toHaveCount(2);
      await shot('21-features');
      await page.locator('[data-ai-feature="page.related"] [data-ai-feature-switch]').click();
      await shot('22-features-dirty');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
    });

    test('Chat card, Použít profil Chat and the daily limit in Funkce (W12)', async ({ page }) => {
      const shot = shooter(page, combo);
      const mock = await open(page, combo, `${base}/chat`, { enabled: true });
      await expect(page.locator('[data-ai-profile="chat"]')).toBeVisible();
      await expect(page.locator('[data-ai-url]')).toHaveCount(0);
      await shot('30-chat-empty');
      await page.getByRole('radio', { name: /Anthropic/ }).click();
      await page.locator('[data-ai-model]').fill('claude-sonnet-5-5');
      await page.locator('[data-ai-key-input]').fill('sk-ant-not-a-real-key');
      await shot('31-chat-anthropic');
      await page.locator('[data-ai-enabled]').click();
      await page.locator('[data-save-bar-save]').click();
      await expect(page.locator('[data-ai-key-saved]')).toContainText('Uložený klíč ✓');
      expect(JSON.stringify(mock.state.profiles.chat)).not.toContain('sk-ant');
      // Rychlé úlohy borrow it.
      await go(page, `${base}/quick`);
      await page.locator('[data-value="chat"] [role="radio"]').click();
      await expect(page.locator('[data-ai-quick-chat]')).toBeVisible();
      // Choosing the source changes the draft; save it before navigation so the
      // real dirty guard does not keep this scenario on Rychlé úlohy.
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      expect(mock.state.profiles.quick.useChatProfile).toBe(true);
      await shot('32-quick-uses-chat');
      // Funkce: the daily limit of the answer tasks.
      await go(page, `${base}/features`);
      await page.locator('[data-ai-limit-section] .settings-section-header.is-toggle').click();
      await expect(page.locator('[data-ai-limit-input]')).toHaveCount(2);
      await page.locator('[data-ai-limit="search.answer"] [data-ai-limit-input]').fill('0');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.locator('[data-ai-limit="search.answer"]')).toContainText('celé číslo od 1 do 10 000');
      await shot('33-features-limit-error');
      await page.locator('[data-ai-limit="search.answer"] [data-ai-limit-input]').fill('25');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      await shot('34-features-limit-saved');
    });

    test('Index: first build, then a model swap through the StepFlow', async ({ page }) => {
      const shot = shooter(page, combo);
      const mock = await open(page, combo, `${base}/index`, { enabled: true, configured: true, step: 2000 });
      await expect(page.locator('[data-ai-build-action]')).toBeVisible();
      await shot('30-index-empty');
      await page.locator('[data-ai-build-action]').click();
      await expect(page.locator('[data-ai-index-flow="build"]')).toBeVisible();
      await shot('31-flow-build-profile');
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-step="build"]')).toBeVisible();
      await shot('32-flow-build-running');
      await expect(page.locator('[data-flow-next]')).toBeEnabled({ timeout: 12_000 });
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-step="verify"]')).toBeVisible();
      await expect(page.locator('[data-flow-next]')).toBeDisabled();
      await page.locator('[data-ai-verify]').click();
      await expect(page.locator('[data-ai-verification]')).toContainText('Index jde aktivovat');
      await shot('33-flow-verify');
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-ai-activate-impact]')).toContainText('Starý index se smaže po 24 hodinách');
      await shot('34-flow-activate');
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-result-banner]')).toContainText('Nový index je aktivní');
      expect(mock.state.active?.state).toBe('Active');
      await shot('35-index-active');

      // Resume: leave a running build and come back. Held still: a finished build resumes at Ověření instead.
      mock.pace(0);
      await page.locator('[data-ai-replace-action]').click();
      await page.locator('[data-ai-model]').fill('text-embedding-3-large');
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-step="build"]')).toBeVisible();
      await page.locator('[data-ai-flow-leave]').click();
      await expect(page.locator('[data-ai-resume]')).toBeVisible();
      await shot('36-index-building');
      await page.locator('[data-ai-resume]').click();
      await expect(page.locator('[data-step="build"]')).toBeVisible();
      await page.locator('[data-flow-cancel]').click();
      await expect(page.getByRole('dialog', { name: 'Zrušit stavbu indexu?' })).toBeVisible();
      await shot('37-flow-cancel-confirm');
      await page.locator('dialog[open] [data-confirm-action]').click();
      await expect(page.locator('[data-result-banner]')).toContainText('Stavba byla zrušena');
    });
  });
}
