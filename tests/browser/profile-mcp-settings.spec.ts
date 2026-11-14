import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Profile MCP guidance against local responses only. No token is issued or shown in these scenes.
const shots = fileURLToPath(new URL('../../artifacts/ai-setup-walkthrough/', import.meta.url));
mkdirSync(shots, { recursive: true });
test.use({ trace: 'off', screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });

type TokenCatalog = {
  canIssueTokens: boolean;
  permissionVersion: string | null;
  level: string;
  mcpEnabled: boolean;
  mcpUrl: string | null;
  tokens: never[];
  apiBaseUrl: string;
  openApiUrl: string;
};
const catalog = (mcpEnabled: boolean, canIssueTokens: boolean): TokenCatalog => ({
  canIssueTokens,
  permissionVersion: canIssueTokens ? 'perm1' : null,
  level: canIssueTokens ? 'read' : 'none',
  mcpEnabled,
  mcpUrl: mcpEnabled ? 'https://wiki.example.test/mcp' : null,
  tokens: [],
  apiBaseUrl: '/api/v1',
  openApiUrl: '/api/v1/openapi.json',
});
async function open(page: Page, route: '#/profile/api' | '#/profile/account', data: TokenCatalog) {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.route('**/api/**', async (requestRoute: Route) => {
    const path = new URL(requestRoute.request().url()).pathname;
    if (path === '/api/auth/me') return requestRoute.fulfill({ json: { id: 'u-reader', username: 'reader', displayName: 'Reader', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } } });
    if (path === '/api/setup/status') return requestRoute.fulfill({ json: { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true } });
    if (path === '/api/auth/methods') return requestRoute.fulfill({ json: { builtinEnabled: true, providers: [] } });
    if (path === '/api/auth/csrf') return requestRoute.fulfill({ json: { token: 'csrf-test' } });
    if (path === '/api/profile/api-tokens') return requestRoute.fulfill({ json: data });
    if (path === '/api/profile/account') return requestRoute.fulfill({ json: { displayName: 'Reader', email: null, version: 'v1', source: 'builtin', canEditDisplayName: true, canEditEmail: true, currentSessionAuthenticatedAt: null, currentSessionAuthenticationMethod: null, lastSignInAt: null, lastSignInMethod: null } });
    if (path === '/api/spaces') return requestRoute.fulfill({ json: [] });
    if (path.startsWith('/api/me/library/')) return requestRoute.fulfill({ json: { items: [], hasMore: false } });
    return requestRoute.fulfill({ status: 404, json: { title: `Mock: ${path}` } });
  });
  await page.goto(`/${route}`);
  await expect(page.locator('.settings-page').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
}
async function shot(page: Page, name: string) {
  await page.screenshot({ path: `${shots}/${name}.png`, fullPage: true });
}

test('MCP enabled and permitted: shows connection guidance and token creation', async ({ page }) => {
  await open(page, '#/profile/api', catalog(true, true));
  await expect(page.locator('[data-mcp-profile-guide]')).toContainText('Authorization: Bearer');
  await expect(page.locator('[data-mcp-profile-guide]')).toContainText('HTTP');
  await expect(page.locator('[data-mcp-profile-url]')).toContainText('https://wiki.example.test/mcp');
  await expect(page.locator('[data-token-new]')).toBeVisible();
  await expect(page.locator('[data-one-time-value]')).toHaveCount(0);
  await shot(page, '22-mcp-allowed');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await shot(page, '22-mcp-allowed-mobile');
});

test('MCP enabled but issuance denied: explains administrator permission without creation', async ({ page }) => {
  await open(page, '#/profile/api', catalog(true, false));
  await expect(page.locator('[data-mcp-profile-guide]')).toContainText('Správce wiki');
  await expect(page.locator('[data-mcp-profile-url]')).toContainText('https://wiki.example.test/mcp');
  await expect(page.locator('[data-token-new]')).toHaveCount(0);
  await expect(page.locator('[data-one-time-value]')).toHaveCount(0);
  await shot(page, '23-mcp-denied');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await shot(page, '23-mcp-denied-mobile');
});

test('MCP off with no issuance or owned tokens: hides the profile entry', async ({ page }) => {
  await open(page, '#/profile/account', catalog(false, false));
  await expect(page.locator('[data-path="/profile/api"]')).toHaveCount(0);
  await expect(page.locator('[data-profile-section="account"]')).toBeVisible();
  await shot(page, '24-profile-without-tokens');
});