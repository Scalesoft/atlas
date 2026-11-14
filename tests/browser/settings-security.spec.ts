import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// Settings phase 2: the Security Center on the V4/V5 API against a mocked API. The owner's acceptance walkthrough: add a VPN
// network, change the default mode (with a fresh sign-in), why someone has access, find a password release in the audit and
// approve a request. Run against a Vite dev server, no wiki needed:
//   pnpm --dir src/Atlas.Web exec vite --port 5361
//   $env:ATLAS_URL='http://127.0.0.1:5361'; npx --prefix tests/browser playwright test settings-security.spec.ts
// Screenshots go to SETTINGS_SHOTS (default artifacts/settings-security), prefixed sec-.
const shots = resolve(process.env.SETTINGS_SHOTS ?? '../../artifacts/settings-security');
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
const spaces = [{ id: 'provoz', name: 'Provoz', description: 'Servery, síť a pohotovost', icon: 'book-open', color: 'blue', role: 'Admin' }];

type Json = Record<string, any>;
type Call = { method: string; path: string; body: any; proof: string | null };
type Handler = (context: { method: string; url: URL; body: any; match: RegExpMatchArray; model: Model; proof: string | null }) => unknown;

function protection(model: Model, level: 'instance' | 'vault' | 'secret', id: string | null) {
  const chain: Json[] = [{ level: 'instance', targetId: null, name: null, mode: model.instanceMode }];
  if (level !== 'instance') chain.push({ level: 'vault', targetId: 'v-1', name: 'Servery a síť', mode: model.vaultMode });
  if (level === 'secret') chain.push({ level: 'secret', targetId: id, name: 'Hlavní router', mode: null });
  const explicit = level === 'instance' ? model.instanceMode : level === 'vault' ? model.vaultMode : null;
  const nearest = [...chain].reverse().find((step) => step.mode);
  return {
    level, targetId: id, explicit, effective: nearest!.mode, source: nearest!.level, chain, canManage: true, version: explicit ? `p${model.version}` : null,
    impact: level === 'instance' ? { vaults: 1, secrets: 14 } : level === 'vault' ? { vaults: 0, secrets: 13 } : { vaults: 0, secrets: 1 },
    preview: { reauthRequired: nearest!.mode !== 'never', networkException: false, freshAuth: 'supported', blocked: false }, configurationVersion: String(model.version),
  };
}

function createModel() {
  return {
    calls: [] as Call[],
    version: 7,
    instanceMode: 'always',
    vaultMode: null as string | null,
    networks: [
      { id: 'n-office', name: 'Kancelář Praha', kind: 'lan', range: '10.5.0.0/16', isActive: true, warnings: [] as string[] },
    ] as Json[],
    proxies: [{ id: 'p-nginx', name: 'Reverse proxy nginx', kind: null, range: '10.5.0.5/32', isActive: true, warnings: [] }] as Json[],
    requests: [
      { id: 'r-1', spaceId: 'provoz', vaultId: 'v-1', vaultName: 'Servery a síť', secretId: 's-router', secretTitle: 'Hlavní router', level: 'read', reason: 'Výměna firmware v pátek večer.', state: 'pending',
        requesterId: 'u-tomas', requesterName: 'Tomáš Král', createdAt: '2026-09-27T07:40:00Z', decidedBy: null, decidedByName: null, decidedAt: null, note: null, grantId: null, grantExpiresAt: null, canManage: true },
    ] as Json[],
  };
}
type Model = ReturnType<typeof createModel>;

const release = (id: string, at: string, extra: Json) => ({
  id, occurredAt: at, actorId: 'u-jana', actorName: 'Jana Nováková', userId: null, userName: null, groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: null,
  isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: 'v-1', vaultName: 'Servery a síť', secretId: 's-router', secretTitle: 'Hlavní router', field: 'password',
  mode: 'always', modeSource: 'vault', matchedNetwork: 'Kancelář Praha', reauthUsed: true, clientIp: '10.5.3.24', sessionId: 'sess-a3f9c1', requestId: '0HN6T2B1Q7K4M:00000012', ...extra,
});
const audit = [
  release('41', '2026-09-27T08:12:04Z', { action: 'secret.client_report', result: 'copied', operation: 'copy' }),
  release('40', '2026-09-27T08:12:03Z', { action: 'secret.released', result: 'released', operation: 'copy' }),
  release('39', '2026-09-27T08:11:58Z', { action: 'reauth.consumed', field: null, mode: null, matchedNetwork: null, clientIp: null }),
  { id: '38', occurredAt: '2026-09-27T07:40:00Z', actorId: 'u-tomas', actorName: 'Tomáš Král', action: 'access_request.created', userId: 'u-tomas', userName: 'Tomáš Král', groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: 'read', isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: 'v-1', vaultName: 'Servery a síť', secretId: 's-router', secretTitle: 'Hlavní router', note: 'Výměna firmware v pátek večer.' },
  { id: '37', occurredAt: '2026-09-26T15:00:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'trusted_network.created', userId: null, userName: null, groupId: null, groupName: null, spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null, trustedAddressName: 'Kancelář Praha', trustedAddressRange: '10.5.0.0/16', configurationVersion: '6' },
  { id: '36', occurredAt: '2026-09-26T14:58:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'protection.set', userId: null, userName: null, groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: 'v-1', vaultName: 'Servery a síť', protectionMode: 'always', configurationVersion: '5' },
  { id: '35', occurredAt: '2026-09-25T09:00:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'resource_grant.added', userId: 'u-jana', userName: 'Jana Nováková', groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: 'read', isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: 'v-1', vaultName: 'Servery a síť' },
];
const source = (kind: string, level: string, extra: Json = {}) => ({ kind, name: '', level, groupId: null, grantId: null, startAt: null, expiresAt: null, version: null, effective: true, ...extra });
const access = {
  spaceId: 'provoz', spaceName: 'Provoz', vaultId: 'v-1', vaultName: 'Servery a síť', secretId: null, secretTitle: null, canManage: true,
  myAccess: { userId: 'u-martin', displayName: 'Marek Musil', username: 'marek.musil', level: 'manage', sources: [source('wikiAdmin', 'manage')] },
  people: [
    { userId: 'u-martin', displayName: 'Marek Musil', username: 'marek.musil', level: 'manage', sources: [source('wikiAdmin', 'manage')] },
    { userId: 'u-jana', displayName: 'Jana Nováková', username: 'jana.novakova', level: 'read', sources: [source('vault', 'read', { grantId: 'g-1', version: 'v-jana', expiresAt: '2026-12-31T23:00:00Z' }), source('vault', 'discover', { name: 'Provoz tým', groupId: 'g-ops', grantId: 'g-2', version: 'v-ops' })] },
  ],
  hasMore: false,
  groups: [{ groupId: 'g-ops', name: 'Provoz tým', memberCount: 4, level: 'discover', sources: [source('vault', 'discover', { name: 'Provoz tým', groupId: 'g-ops', grantId: 'g-2', version: 'v-ops' })] }],
  lastChange: null,
};

const routes: [string, RegExp, Handler][] = [
  ['GET', /^\/api\/setup\/status$/, () => ({ databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true })],
  ['GET', /^\/api\/auth\/methods$/, () => ({ builtinEnabled: true, providers: [{ id: 'hub', displayName: 'JetBrains Hub', loginUrl: '/api/auth/oidc/hub/login' }] })],
  ['GET', /^\/api\/auth\/csrf$/, () => ({ token: 'csrf-test' })],
  ['GET', /^\/api\/admin\/setup$/, () => ({ pending: false })],
  ['GET', /^\/api\/admin\/backups\/?$/, () => ({ settings: { enabled: true, intervalMinutes: 1440, mode: 'full', destination: 'smb://nas01/zalohy', username: '', domain: '', hasPassword: false, retentionCount: 14 }, runs: [], nextRunAt: null, lastSuccessAt: '2026-09-27T02:03:10Z', warning: null })],
  ['GET', /^\/api\/spaces$/, () => spaces],
  ['GET', /^\/api\/spaces\/([^/]+)\/tree$/, ({ match }) => ({ spaceId: match[1], version: 1, items: [] })],
  ['GET', /^\/api\/me\/library\/(\w+)$/, () => ({ items: [], hasMore: false, nextOffset: null })],
  ['GET', /^\/api\/analytics\/.*$/, () => ({ items: [], revisions: 0, pages: 0, days: [], authors: [] })],
  ['GET', /^\/api\/vaults$/, () => [{ id: 'v-1', spaceId: 'provoz', spaceName: 'Provoz', name: 'Servery a síť', locationId: 'loc-1', locationName: 'FS01', fileName: 'servery.kdbx', status: 'unlocked', lastFailure: null, lastRefreshAt: '2026-09-27T08:00:00Z', snapshotSha256: 'abcdef0123456789', entryCount: 14, createdAt: '2026-09-01T08:00:00Z', level: 'manage' }]],
  ['GET', /^\/api\/vault-locations$/, () => [{ id: 'loc-1', name: 'FS01', kind: 'smb', root: 'smb://fs01/it/keepass', username: 'svc', domain: '', hasPassword: true, vaultCount: 1 }]],
  ['GET', /^\/api\/vaults\/v-1\/access$/, () => access],
  // The person's card (access layer, docs/access-model.md §3): Jana reaches the vault directly and through Provoz tým.
  ['GET', /^\/api\/access\/kinds$/, () => []],
  ['GET', /^\/api\/admin\/users$/, () => [{ id: 'u-jana', username: 'jana.novakova', displayName: 'Jana Nováková', isActive: true, isWikiAdmin: false, version: 'u1', hasBuiltinCredential: true, source: 'builtin', credentialState: 'active' }]],
  ['GET', /^\/api\/access\/principals\/user\/u-jana$/, () => ({
    principal: { kind: 'user', id: 'u-jana', name: 'Jana Nováková', detail: 'jana.novakova', isActive: true, groups: [{ id: 'g-ops', name: 'Provoz tým' }] },
    scope: 'wiki',
    canManage: true,
    entries: [{ kind: 'vault', resourceId: 'v-1', title: 'Servery a síť', spaceId: 'provoz', spaceName: 'Provoz', level: 'read', note: '14 hesel', sources: [
      { via: 'direct', label: '', level: 'read', version: 'g1', startAt: null, expiresAt: null, effective: true },
      { via: 'group', label: 'Provoz tým', groupId: 'g-ops', level: 'discover', effective: true },
    ] }],
  })],
  ['GET', /^\/api\/vaults\/v-1\/secrets$/, () => [{ secretId: 's-router', entryUuid: 'e1', title: 'Hlavní router', groupPath: 'Síť', availableFields: ['userName', 'password', 'url'], firstSeenAt: '2026-09-01T08:00:00Z', removedAt: null, level: 'manage' }]],
  ['GET', /^\/api\/vaults\/v-1\/audit$/, () => ({ items: audit.filter((item) => item.vaultId === 'v-1'), hasMore: false, nextBeforeId: null })],
  ['GET', /^\/api\/admin\/access-audit$/, () => ({ items: audit, hasMore: false, nextBeforeId: null, coverageStartedAt: '2026-09-01T00:00:00Z' })],
  // V4: security centre
  ['GET', /^\/api\/security\/me$/, () => ({ globalSecurityAdmin: true, managesAllVaults: true, vaultManagerSpaces: [], vaults: [], freshAuth: 'supported', freshAuthMethod: 'password' })],
  ['GET', /^\/api\/security\/protection$/, ({ model }) => protection(model, 'instance', null)],
  ['PUT', /^\/api\/security\/protection$/, ({ model, body, proof }) => {
    if (proof !== 'proof-1') return { status: 403, json: { detail: 'Tato akce vyžaduje nové přihlášení.', code: 'reauth_required' } };
    if (body.expectedVersion !== `p${model.version}`) return { status: 409, json: { detail: 'Změněno.' } };
    model.instanceMode = body.mode;
    model.version++;
    return protection(model, 'instance', null);
  }],
  ['GET', /^\/api\/security\/protection\/overrides$/, ({ model }) => (model.vaultMode ? [{ spaceId: 'provoz', spaceName: 'Provoz', vaultId: 'v-1', vaultName: 'Servery a síť', secretId: null, secretTitle: null, mode: model.vaultMode }] : [])],
  ['GET', /^\/api\/vaults\/v-1\/protection$/, ({ model }) => protection(model, 'vault', 'v-1')],
  ['GET', /^\/api\/secrets\/s-router\/protection$/, ({ model }) => protection(model, 'secret', 's-router')],
  ['GET', /^\/api\/security\/networks$/, ({ model }) => ({ version: String(model.version), items: model.networks })],
  ['POST', /^\/api\/security\/networks\/test$/, ({ model, body }) => {
    const inside = body.address?.startsWith('10.5.');
    return { address: body.address, addressError: null, isProxy: body.address === '10.5.0.5', network: inside ? { id: 'n-office', name: 'Kancelář Praha', kind: 'lan', range: '10.5.0.0/16' } : null, inactiveMatches: [], draft: null };
  }],
  ['POST', /^\/api\/security\/networks$/, ({ model, body, proof }) => {
    if (proof !== 'proof-1') return { status: 403, json: { detail: 'Tato akce vyžaduje nové přihlášení.', code: 'reauth_required' } };
    if (body.expectedVersion !== String(model.version)) return { status: 409, json: { detail: 'Změněno.' } };
    const created = { id: 'n-vpn', name: body.name, kind: body.kind, range: body.range, isActive: true, warnings: [] };
    model.networks.push(created);
    model.version++;
    return { id: created.id, warnings: [], version: String(model.version) };
  }],
  ['GET', /^\/api\/security\/proxies$/, ({ model }) => ({ version: String(model.version), hopLimit: 1, maxHopLimit: 3, items: model.proxies, environment: { addresses: ['10.5.0.5'], differs: false, recovery: false, importedAt: '2026-09-20T10:00:00Z' } })],
  ['GET', /^\/api\/security\/connection$/, ({ model }) => ({ address: '10.5.3.24', source: 'forwarded', reason: 'forwarded', peer: '10.5.0.5', peerIsProxy: true, hops: 1, forwardedFor: ['10.5.3.24'],
    network: { id: 'n-office', name: 'Kancelář Praha', kind: 'lan', range: '10.5.0.0/16' }, networkException: model.instanceMode === 'outsideTrustedNetworks', instanceMode: model.instanceMode, reauthRequired: model.instanceMode === 'always', version: String(model.version) })],
  ['POST', /^\/api\/security\/reauth$/, () => ({ id: 'proof-1', method: 'password', providerId: null, providerName: null, expiresAt: '2099-01-01T00:00:00Z', redirectUrl: null })],
  ['POST', /^\/api\/security\/reauth\/proof-1\/password$/, ({ body }) =>
    body.password === 'spravne-heslo'
      ? { id: 'proof-1', operation: 'security', targetKind: 'instance', targetId: null, method: 'password', state: 'verified', failure: null, expiresAt: '2099-01-01T00:00:00Z', proofExpiresAt: '2099-01-01T00:00:00Z' }
      : { status: 403, json: { detail: 'Heslo nesouhlasí.', code: 'reauth_password' } }],
  ['GET', /^\/api\/security\/reauth\/self-test$/, () => [{ providerId: 'hub', displayName: 'JetBrains Hub', verdict: 'unknown', authTimePresent: null, authTimeFresh: null, checkedAt: null, selfTest: null }]],
  // V5: requests
  ['GET', /^\/api\/access-requests$/, ({ model }) => ({ items: model.requests.filter((item) => item.state === 'pending'), hasMore: false })],
  ['GET', /^\/api\/access-requests\/mine$/, ({ model }) => ({ items: model.requests, hasMore: false })],
  ['POST', /^\/api\/access-requests\/([^/]+)\/approve$/, ({ model, match, body, proof }) => {
    if (proof !== 'proof-1') return { status: 403, json: { detail: 'Tato akce vyžaduje nové přihlášení.', code: 'reauth_required' } };
    const request = model.requests.find((item) => item.id === match[1])!;
    Object.assign(request, { state: 'approved', decidedBy: 'u-martin', decidedByName: 'Marek Musil', decidedAt: '2026-09-27T09:00:00Z', grantId: 'g-new', grantExpiresAt: body.expiresAt });
    return request;
  }],
];

async function mockApi(page: Page, combo: Combo, model: Model) {
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    const proof = (await request.allHeaders())['x-atlas-reauth'] ?? null;
    model.calls.push({ method, path: `${url.pathname}${url.search}`, body, proof });
    if (url.pathname === '/api/auth/me')
      return route.fulfill({ json: { id: 'u-martin', username: 'marek.musil', displayName: 'Marek Musil', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: combo.theme } } });
    for (const [verb, pattern, handler] of routes) {
      const match = url.pathname.match(pattern);
      if (verb !== method || !match) continue;
      const result = handler({ method, url, body, match, model, proof }) as unknown;
      if (result === undefined) return route.fulfill({ status: 204 });
      if (result && typeof result === 'object' && 'status' in (result as Json) && 'json' in (result as Json)) {
        const { status, json } = result as { status: number; json: unknown };
        return route.fulfill({ status, json });
      }
      return route.fulfill({ json: result });
    }
    return route.fulfill({ status: 404, json: { title: `Mock: ${method} ${url.pathname}`, detail: `Mock: ${method} ${url.pathname}` } });
  });
  // No SignalR in the mock: the hub negotiation fails and the pages stay usable without live events.
  await page.route('**/hubs/**', (route) => route.fulfill({ status: 404, body: '' }));
}

async function open(page: Page, combo: Combo, hash: string, model = createModel()) {
  await page.setViewportSize(viewport(combo.device));
  await mockApi(page, combo, model);
  await page.goto(`/${hash}`);
  await expect(page.locator('.settings-page').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  return model;
}
async function go(page: Page, hash: string) {
  await page.evaluate((target) => { location.hash = target; }, hash);
  await expect(page.locator('.settings-page').first()).toBeVisible();
}
function shooter(page: Page, combo: Combo) {
  return async (name: string) => {
    await page.evaluate(() => { scrollTo(0, 0); for (const element of document.querySelectorAll('.workspace')) element.scrollTop = 0; });
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${shots}/sec-${name}-${combo.theme}-${combo.device}.png`, fullPage: true });
  };
}
/** The fresh sign-in of the open step: a wrong password first, then the right one. */
async function reauth(page: Page, scope = page.locator('.confirm-reauth').last()) {
  await scope.locator('input[type="password"]').fill('spatne');
  await scope.locator('[data-confirm-reauth]').click();
  await expect(scope.locator('[data-reauth-error]')).toContainText('Heslo nesouhlasí');
  await scope.locator('input[type="password"]').fill('spravne-heslo');
  await scope.locator('[data-confirm-reauth]').click();
}
const noHorizontalScroll = async (page: Page, combo: Combo) =>
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport(combo.device).width);

for (const combo of combos) {
  test.describe(`${combo.theme} ${combo.device}`, () => {
    test('overview, add a VPN network and change the default mode with a fresh sign-in', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/settings/security/overview');
      await expect(page.locator('[data-stat="protection"]')).toContainText('Vždy');
      await expect(page.locator('[data-stat="networks"]')).toContainText('1 aktivní síť');
      await expect(page.locator('[data-issue="requests"]')).toContainText('1 žádost o přístup čeká');
      // The fresh sign-in self-test sits in the collapsed advanced section.
      await page.getByRole('button', { name: /Ověření přihlášení/ }).click();
      await expect(page.locator('[data-fresh-auth-test]')).toContainText('JetBrains Hub');
      await shot('10-overview');

      // Add a VPN network.
      await go(page, '#/settings/security/networks');
      await expect(page.locator('[data-connection]')).toContainText('10.5.3.24');
      await expect(page.locator('[data-connection]')).toContainText('Přes důvěryhodnou proxy');
      await expect(page.locator('[data-network="n-office"]')).toContainText('10.5.0.0/16');
      await shot('20-networks');
      // A progressive flow: the kind reveals the range, a valid range reveals the impact and the confirmation,
      // which is the fresh sign-in itself.
      await page.locator('[data-add-network]').click();
      await page.getByRole('radio', { name: /^VPN/ }).click();
      await page.locator('[data-network-name]').fill('VPN pro externisty');
      await page.locator('[data-network-cidr]').fill('10.4.1.7/22');
      await expect(page.locator('[data-network-normalized]')).toContainText('10.4.0.0/22');
      await shot('21-network-wizard-range');
      await expect(page.getByText('Vaše adresa 10.5.3.24 do rozsahu nepatří.')).toBeVisible();
      expect(model.calls.some((call) => call.method === 'POST' && call.path === '/api/security/networks')).toBe(false);
      await reauth(page);
      await expect(page.locator('[data-network="n-vpn"]')).toContainText('10.4.0.0/22');
      const created = model.calls.find((call) => call.method === 'POST' && call.path === '/api/security/networks')!;
      expect(created.body).toMatchObject({ name: 'VPN pro externisty', kind: 'vpn', range: '10.4.0.0/22', isActive: true, expectedVersion: '7' });
      expect(created.proof).toBe('proof-1');
      expect(model.calls.find((call) => call.path === '/api/security/reauth')?.body).toEqual({ operation: 'security' });
      // The address tester in the advanced section.
      await page.getByRole('button', { name: /Proxy a hopy/ }).click();
      await page.locator('[data-network-tester]').fill('10.5.8.1');
      await page.locator('[data-network-test]').click();
      await expect(page.locator('[data-tester-result]')).toContainText('Kancelář Praha');
      await shot('22-networks-advanced');

      // Change the default mode: a card, the impact from the API, then the fresh sign-in.
      await go(page, '#/settings/security/protection');
      await expect(page.locator('[data-instance-modes] [data-value="always"]')).toHaveAttribute('data-selected', 'true');
      await shot('30-protection');
      await page.locator('[data-instance-modes] [data-value="outsideTrustedNetworks"] [role="radio"]').click();
      await expect(page).toHaveURL(/security\/protection\/change\?rezim=outsideTrustedNetworks$/);
      // The chosen mode reveals the impact from the API and the confirmation with the fresh sign-in.
      await expect(page.locator('.impact-preview[role="group"][aria-label="Co se změní"]')).toContainText('1 trezor bez vlastního nastavení převezme režim Mimo důvěryhodné sítě');
      await shot('31-protection-impact');
      await expect(page.locator('.confirm-reauth input[type="password"]')).toBeVisible();
      await shot('32-protection-reauth');
      await reauth(page);
      await expect(page.locator('[data-result-banner]')).toContainText('Mimo důvěryhodné sítě');
      const changed = model.calls.find((call) => call.method === 'PUT' && call.path === '/api/security/protection')!;
      expect(changed.body).toEqual({ mode: 'outsideTrustedNetworks', expectedVersion: 'p8' });
      expect(changed.proof).toBe('proof-1');
      await expect(page.locator('[data-instance-modes] [data-value="outsideTrustedNetworks"]')).toHaveAttribute('data-selected', 'true');
      await noHorizontalScroll(page, combo);
    });

    test('why someone has access, a password release in the audit and approving a request', async ({ page }) => {
      const shot = shooter(page, combo);
      // Přehled práv became the person's card: every access with where it comes from.
      const model = await open(page, combo, '#/settings/people/accounts/u-jana');
      const vault = page.locator('[data-access-entry="vault:v-1"]');
      await expect(vault).toContainText('Přímý přístup a 1 další cesta');
      await expect(vault.locator('.level-badge')).toContainText('Čte');
      await page.locator('[data-access-via-filter]').click();
      await page.locator('.filter-menu-panel [data-value="group"]').click();
      await expect(vault).toBeVisible();
      await shot('40-access-why');

      // Find a password release in the audit: Co = Vydání hesel, then its details.
      await go(page, '#/settings/security/audit');
      await page.locator('[data-filter="what"]').click();
      await page.getByRole('menuitemradio', { name: /Vydání hesel/ }).click();
      await expect(page.locator('[data-filter="what"]')).toContainText('Vydání hesel');
      await expect(page).toHaveURL(/security\/audit\?co=release$/);
      const copied = page.locator('.activity-item').filter({ hasText: 'zkopíroval(a)' }).filter({ hasText: 'Hlavní router' });
      await expect(copied).toHaveCount(1);
      await copied.locator('[data-activity-details]').click();
      await expect(copied).toContainText('10.5.3.24');
      await expect(copied).toContainText('Vždy · nastavení trezoru');
      await expect(copied).toContainText('0HN6T2B1Q7K4M:00000012');
      const report = page.locator('.activity-item').filter({ hasText: 'nahlásil(a) zkopírování' });
      await report.locator('[data-activity-details]').click();
      await expect(report).toContainText('není důkaz přečtení');
      await shot('50-audit-release');

      // Approve a request in the space settings: 30 dní by default, then the fresh sign-in for the password.
      await go(page, '#/spaces/provoz/settings/requests');
      const request = page.locator('[data-request="r-1"]');
      await expect(request).toContainText('Výměna firmware v pátek večer.');
      await shot('60-requests');
      await request.locator('[data-request-approve]').click();
      await expect(request.locator('[data-value="30"]')).toHaveAttribute('data-selected', 'true');
      await shot('61-request-approve');
      await reauth(page, request.locator('.confirm-reauth'));
      await expect(page.locator('[data-result-banner]')).toContainText('Tomáš Král má úroveň Čte');
      await expect(page.locator('[data-request="r-1"]')).toHaveCount(0);
      const approved = model.calls.find((call) => call.method === 'POST' && call.path === '/api/access-requests/r-1/approve')!;
      expect(approved.proof).toBe('proof-1');
      const days = (new Date(approved.body.expiresAt).getTime() - Date.now()) / 86_400_000;
      expect(days).toBeGreaterThan(29.9);
      expect(days).toBeLessThan(30.1);
      expect(model.calls.filter((call) => call.path === '/api/security/reauth').map((call) => call.body)).toContainEqual({ operation: 'security', secretId: 's-router' });

      // The vault's own protection and audit, and the requester's view in the profile.
      await go(page, '#/spaces/provoz/settings/vaults/v-1/protection');
      await expect(page.locator('.effective-value')).toContainText('Zděděno z úrovně instance');
      await shot('70-vault-protection');
      await go(page, '#/spaces/provoz/settings/vaults/v-1/audit');
      await expect(page.locator('.activity-item').filter({ hasText: 'zkopíroval(a)' })).toHaveCount(1);
      await page.evaluate(() => { location.hash = '#/profile/requests'; });
      await expect(page.locator('[data-my-request="r-1"]')).toContainText('Schválil(a) Marek Musil');
      await shot('80-profile-requests');
      await noHorizontalScroll(page, combo);
    });
  });
}
