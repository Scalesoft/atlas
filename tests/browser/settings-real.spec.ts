import { expect, test, type Page, type Request } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// The real settings (plan 2026-09-27, phase 1) against a mocked API: run against a Vite dev server, no wiki needed.
//   pnpm --dir src/Atlas.Web exec vite --port 5348
//   $env:ATLAS_URL='http://127.0.0.1:5348'; npx --prefix tests/browser playwright test settings-real.spec.ts
// Screenshots go to SETTINGS_SHOTS (default artifacts/settings-real).
const shots = resolve(process.env.SETTINGS_SHOTS ?? '../../artifacts/settings-real');
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

const now = '2026-09-27T09:30:00Z';
const spaces = [
  { id: 'provoz', name: 'Provoz', description: 'Servery, síť a pohotovost', icon: 'book-open', color: 'blue', role: 'Admin' },
  { id: 'vyvoj', name: 'Vývoj', description: 'Aplikace a nasazení', icon: 'code-xml', color: 'green', role: 'Admin' },
];
type Json = Record<string, unknown>;
type Source = { kind: string; name: string; level: string; groupId: string | null; grantId: string | null; startAt: string | null; expiresAt: string | null; version: string | null; effective: boolean };
const source = (kind: string, level: string, extra: Partial<Source> = {}): Source => ({ kind, name: '', level, groupId: null, grantId: null, startAt: null, expiresAt: null, version: null, effective: true, ...extra });

/** A small stateful model of the vault API: grants, revokes and new vaults change what later reads return. */
function createModel() {
  const locations: Json[] = [
    { id: 'loc-fs01', name: 'Souborový server FS01', kind: 'smb', root: 'smb://fs01/it/keepass', username: 'svc-atlas', domain: 'FIRMA', hasPassword: true, vaultCount: 2 },
    { id: 'loc-local', name: 'Disk serveru', kind: 'local', root: 'D:\\Atlas\\trezory', username: null, domain: null, hasPassword: false, vaultCount: 0 },
  ];
  const vault = (id: string, spaceId: string, name: string, fileName: string, extra: Json = {}) => ({
    id, spaceId, spaceName: spaces.find((space) => space.id === spaceId)!.name, name, locationId: 'loc-fs01', locationName: 'Souborový server FS01', fileName,
    status: 'unlocked', lastFailure: null, lastRefreshAt: '2026-09-27T08:12:00Z', snapshotSha256: '5e1f0c9a7b3d44e2a1c0f9e8d7c6b5a4', entryCount: 3, createdAt: '2026-09-02T10:00:00Z', level: 'manage', ...extra,
  });
  const vaults: Json[] = [
    vault('v-servers', 'provoz', 'Servery a síť', 'servery.kdbx'),
    vault('v-legacy', 'provoz', 'Staré přístupy', 'stare.kdbx', { status: 'needsPassword', lastFailure: 'Uložené hlavní heslo přestalo fungovat.', entryCount: 12 }),
    vault('v-deploy', 'vyvoj', 'Nasazení', 'nasazeni.kdbx', { entryCount: 7 }),
  ];
  const secrets: Json[] = [
    { secretId: 's-root', entryUuid: 'e1', title: 'root@fw01', groupPath: 'Síť/Firewally', availableFields: ['userName', 'password', 'url'], firstSeenAt: '2026-09-02T10:00:00Z', removedAt: null, level: 'manage' },
    { secretId: 's-vpn', entryUuid: 'e2', title: 'VPN – sdílený klíč', groupPath: 'Síť', availableFields: ['password', 'notes'], firstSeenAt: '2026-09-02T10:00:00Z', removedAt: null, level: 'manage' },
    { secretId: 's-ups', entryUuid: 'e3', title: 'UPS správa', groupPath: 'Servery', availableFields: ['userName', 'password'], firstSeenAt: '2026-09-02T10:00:00Z', removedAt: null, level: 'manage' },
  ];
  const directory = [
    { id: 'u-martin', kind: 'user', name: 'Marek Musil', detail: 'marek.musil' },
    { id: 'u-jana', kind: 'user', name: 'Jana Nováková', detail: 'jana.novakova' },
    { id: 'u-petr', kind: 'user', name: 'Petr Svoboda', detail: 'petr.svoboda' },
    { id: 'u-eva', kind: 'user', name: 'Eva Malá', detail: 'eva.mala' },
    { id: 'g-ops', kind: 'group', name: 'Provoz tým', detail: '3 členové' },
    { id: 'g-dev', kind: 'group', name: 'Vývojáři', detail: '5 členů' },
  ];
  // Grants on vaults and secrets: target id → subject id → grant.
  let serial = 1;
  const grants = new Map<string, Map<string, { kind: 'user' | 'group'; level: string; version: string; expiresAt: string | null }>>();
  const put = (target: string, subject: string, kind: 'user' | 'group', level: string, expiresAt: string | null = null) => {
    const map = grants.get(target) ?? new Map();
    map.set(subject, { kind, level, version: `ver-${serial++}`, expiresAt });
    grants.set(target, map);
  };
  put('v-servers', 'u-jana', 'user', 'read');
  put('v-servers', 'g-ops', 'group', 'discover', '2026-12-31T23:00:00Z');
  const lastChange = { occurredAt: '2026-09-26T14:02:00Z', action: 'resource_grant.added', actorId: 'u-martin', actorName: 'Marek Musil' };

  function state(targetId: string): Json {
    const secret = secrets.find((item) => item.secretId === targetId);
    const vaultId = secret ? 'v-servers' : targetId;
    const target = vaults.find((item) => item.id === vaultId)!;
    const kind = secret ? 'secret' : 'vault';
    const own = grants.get(targetId) ?? new Map();
    const fromVault = secret ? (grants.get(vaultId) ?? new Map()) : new Map();
    const groups = [
      { groupId: 'g-admins', name: 'Správci provozu', memberCount: 2, level: 'manage', sources: [source('spaceAdmin', 'manage', { groupId: 'g-admins', name: 'Správci provozu' })] },
      ...directory.filter((item) => item.kind === 'group' && (own.has(item.id) || fromVault.has(item.id))).map((item) => {
        const list = [...(fromVault.has(item.id) ? [source('vault', fromVault.get(item.id)!.level, { groupId: item.id, name: item.name, version: fromVault.get(item.id)!.version, expiresAt: fromVault.get(item.id)!.expiresAt })] : []), ...(own.has(item.id) ? [source(kind, own.get(item.id)!.level, { groupId: item.id, name: item.name, version: own.get(item.id)!.version, expiresAt: own.get(item.id)!.expiresAt })] : [])];
        return { groupId: item.id, name: item.name, memberCount: 3, level: list[list.length - 1]!.level, sources: list };
      }),
    ];
    const people = [
      { userId: 'u-martin', displayName: 'Marek Musil', username: 'marek.musil', level: 'manage', sources: [source('wikiAdmin', 'manage'), source('spaceAdmin', 'manage')] },
      ...directory.filter((item) => item.kind === 'user' && item.id !== 'u-martin').flatMap((item) => {
        const list: Source[] = [];
        if (fromVault.has(item.id)) list.push(source('vault', fromVault.get(item.id)!.level, { version: fromVault.get(item.id)!.version }));
        if (own.has(item.id)) list.push(source(kind, own.get(item.id)!.level, { version: own.get(item.id)!.version, expiresAt: own.get(item.id)!.expiresAt }));
        if (item.id === 'u-petr' && (own.has('g-ops') || fromVault.has('g-ops'))) list.push(source(own.has('g-ops') ? kind : 'vault', (own.get('g-ops') ?? fromVault.get('g-ops'))!.level, { groupId: 'g-ops', name: 'Provoz tým' }));
        return list.length ? [{ userId: item.id, displayName: item.name, username: item.detail, level: list[0]!.level, sources: list }] : [];
      }),
    ];
    return {
      spaceId: target.spaceId, spaceName: target.spaceName, vaultId, vaultName: target.name, secretId: secret?.secretId ?? null, secretTitle: secret?.title ?? null,
      canManage: true, myAccess: people[0], people, hasMore: false, groups, lastChange,
    };
  }
  /** GET /api/access/resources/{vault|secret}/{id}: the same grants in the access contract (docs/access-model.md §3). */
  function resource(targetId: string): Json {
    const secret = secrets.find((item) => item.secretId === targetId);
    const kind = secret ? 'secret' : 'vault';
    const own = grants.get(targetId) ?? new Map();
    const fromVault = secret ? (grants.get('v-servers') ?? new Map()) : new Map();
    const direct = (grant: { level: string; version: string; expiresAt: string | null }) => ({ via: 'direct', label: '', level: grant.level, version: grant.version, startAt: null, expiresAt: grant.expiresAt, effective: true });
    const entries = directory.filter((item) => item.id !== 'u-martin' && (own.has(item.id) || fromVault.has(item.id) || (item.id === 'u-petr' && (own.has('g-ops') || fromVault.has('g-ops'))))).map((item) => {
      const sources: Json[] = [];
      if (own.has(item.id)) sources.push(direct(own.get(item.id)!));
      if (fromVault.has(item.id)) sources.push({ via: 'derived', label: 'Z trezoru Servery a síť', level: fromVault.get(item.id)!.level, effective: true });
      if (item.id === 'u-petr') sources.push({ via: 'group', label: 'Provoz tým', groupId: 'g-ops', level: (own.get('g-ops') ?? fromVault.get('g-ops'))!.level, effective: true });
      return { subject: { kind: item.kind, id: item.id, name: item.name, detail: item.kind === 'user' ? item.detail : null, memberCount: item.kind === 'group' ? 3 : null }, level: sources[0]!.level, sources };
    });
    return {
      resource: { kind, id: targetId, title: secret?.title ?? 'Servery a síť', spaceId: 'provoz', spaceName: 'Provoz', parentTitle: secret ? 'Servery a síť' : null },
      canManage: true,
      automatic: [
        { label: 'Správci prostoru', detail: 'Správci provozu · mění se v přístupech prostoru Provoz', level: 'manage', count: 2 },
        { label: 'Správci wiki', detail: 'Marek Musil · automaticky ve všech prostorech', level: 'manage', count: 1 },
      ],
      entries,
      hasMore: false,
    };
  }
  return { locations, vaults, secrets, directory, grants, put, state, resource, calls: [] as { method: string; path: string; body: unknown }[] };
}
type Model = ReturnType<typeof createModel>;

async function mockApi(page: Page, combo: Combo, model: Model) {
  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    model.calls.push({ method, path: `${path}${url.search}`, body });
    const json = (value: unknown, status = 200) => route.fulfill({ status, json: value });
    const problem = (status: number, detail: string) => route.fulfill({ status, json: { title: detail, detail } });
    let match: RegExpMatchArray | null;
    if (path === '/api/setup/status') return json({ databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true });
    if (path === '/api/auth/me') return json({ id: 'u-martin', username: 'marek.musil', displayName: 'Marek Musil', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: combo.theme } });
    if (path === '/api/auth/methods') return json({ builtinEnabled: true, providers: [] });
    if (path === '/api/auth/csrf') return json({ token: 'csrf-test' });
    if (path === '/api/admin/setup') return json({ pending: false });
    if (path === '/api/admin/backups') return json({ warning: null });
    if (path === '/api/spaces') return json(spaces);
    if (path.startsWith('/api/me/library/')) return json({ items: [], hasMore: false, nextOffset: null });
    if ((match = path.match(/^\/api\/spaces\/([^/]+)\/tree$/))) return json({ spaceId: match[1], version: 1, items: [] });
    // V4/V5: security/me, a password re-authentication, protection, networks and requests.
    if (path === '/api/security/me') return json({ globalSecurityAdmin: true, managesAllVaults: true, vaultManagerSpaces: [], vaults: [], freshAuth: 'supported', freshAuthMethod: 'password' });
    if (path === '/api/security/reauth') return json({ id: 'proof-1', method: 'password', providerId: null, providerName: null, expiresAt: '2099-01-01T00:00:00Z', redirectUrl: null });
    if (path === '/api/security/reauth/proof-1/password') return json({ id: 'proof-1', operation: 'security', targetKind: 'vault', targetId: null, method: 'password', state: 'verified', failure: null, expiresAt: '2099-01-01T00:00:00Z', proofExpiresAt: '2099-01-01T00:00:00Z' });
    if (path === '/api/security/reauth/self-test') return json([]);
    if (path === '/api/access-requests') return json({ items: [], hasMore: false });
    if (path === '/api/security/protection/overrides') return json([]);
    if (path === '/api/security/networks') return json({ version: '1', items: [] });
    if ((match = path.match(/^\/api\/(security|vaults\/[^/]+|secrets\/[^/]+)\/protection$/))) {
      const level = match[1] === 'security' ? 'instance' : match[1]!.startsWith('vaults') ? 'vault' : 'secret';
      const chain = [{ level: 'instance', targetId: null, name: null, mode: 'always' }, ...(level === 'instance' ? [] : [{ level: 'vault', targetId: 'v-servers', name: 'Servery a síť', mode: null }])];
      return json({ level, targetId: null, explicit: level === 'instance' ? 'always' : null, effective: 'always', source: 'instance', chain, canManage: true, version: level === 'instance' ? 'p1' : null, impact: { vaults: 2, secrets: 12 }, preview: { reauthRequired: true, networkException: false, freshAuth: 'supported', blocked: false }, configurationVersion: '1' });
    }
    if ((match = path.match(/^\/api\/vaults\/([^/]+)\/audit$/))) {
      const items = [
        { id: '12', occurredAt: '2026-09-26T14:02:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'resource_grant.added', userId: 'u-jana', userName: 'Jana Nováková', groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: 'read', isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: match[1] },
        { id: '11', occurredAt: '2026-09-25T09:40:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'resource_grant.added', userId: null, userName: null, groupId: 'g-ops', groupName: 'Provoz tým', spaceId: 'provoz', spaceName: 'Provoz', role: 'discover', isActive: null, isWikiAdmin: null, canIssueTokens: null, vaultId: match[1] },
      ];
      return json({ items, hasMore: false, nextBeforeId: null });
    }
    if (path === '/api/admin/access-audit') {
      const vaultId = url.searchParams.get('vaultId');
      const items = [
        { id: '12', occurredAt: '2026-09-26T14:02:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'resource_grant.added', userId: 'u-jana', userName: 'Jana Nováková', groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: 'read', isActive: null, isWikiAdmin: null, canIssueTokens: null },
        { id: '11', occurredAt: '2026-09-25T09:40:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'resource_grant.added', userId: null, userName: null, groupId: 'g-ops', groupName: 'Provoz tým', spaceId: 'provoz', spaceName: 'Provoz', role: 'discover', isActive: null, isWikiAdmin: null, canIssueTokens: null },
        ...(vaultId ? [] : [{ id: '10', occurredAt: '2026-09-24T16:10:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'group.created', userId: null, userName: null, groupId: 'g-ops', groupName: 'Provoz tým', spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null }]),
      ];
      return json({ items, hasMore: false, nextBeforeId: null, coverageStartedAt: '2026-09-01T00:00:00Z' });
    }
    if (path === '/api/vault-locations' && method === 'GET') return json(model.locations);
    if (path === '/api/vault-locations/test') return json({ message: 'Složka je dostupná a zapisovatelná.' });
    if (path === '/api/vault-locations' && method === 'POST') {
      const location = { id: `loc-${model.locations.length + 1}`, name: body.name, kind: body.kind, root: body.root, username: body.username || null, domain: body.domain || null, hasPassword: !!body.password, vaultCount: 0 };
      model.locations.push(location);
      return json(location);
    }
    if ((match = path.match(/^\/api\/vault-locations\/([^/]+)\/files$/))) {
      const q = (url.searchParams.get('q') ?? '').toLocaleLowerCase('cs');
      const files = [
        { name: 'archiv-2025.kdbx', sizeBytes: 18_432, lastWriteAt: '2026-08-30T12:00:00Z', registered: false, usedBy: null },
        { name: 'servery.kdbx', sizeBytes: 9_216, lastWriteAt: '2026-09-27T08:12:00Z', registered: true, usedBy: 'Servery a síť' },
        { name: 'stare.kdbx', sizeBytes: 4_096, lastWriteAt: '2026-09-01T08:12:00Z', registered: true, usedBy: 'Staré přístupy' },
      ].filter((file) => !q || file.name.includes(q));
      return json({ files, truncated: false });
    }
    if (path === '/api/vaults/attach') {
      const space = spaces.find((item) => item.id === body.spaceId)!;
      const attached = { id: `v-att-${model.vaults.length}`, spaceId: space.id, spaceName: space.name, name: body.name, locationId: body.locationId, locationName: 'Souborový server FS01', fileName: body.fileName, status: 'unlocked', lastFailure: null, lastRefreshAt: now, snapshotSha256: '11aa', entryCount: 21, createdAt: now, level: 'manage' };
      model.vaults.push(attached);
      return json(attached);
    }
    if (path === '/api/vaults' && method === 'GET') return json(model.vaults);
    if (path === '/api/vaults/new') {
      const space = spaces.find((item) => item.id === body.spaceId)!;
      const location = model.locations.find((item) => item.id === body.locationId)!;
      const created = { id: `v-new-${model.vaults.length}`, spaceId: space.id, spaceName: space.name, name: body.name, locationId: location.id, locationName: location.name, fileName: body.fileName, status: 'unlocked', lastFailure: null, lastRefreshAt: now, snapshotSha256: '00aa11bb22cc33dd44ee55ff66778899', entryCount: 0, createdAt: now, level: 'manage' };
      model.vaults.push(created);
      return json({ vault: created, generatedPassword: body.generatePassword ? 'Qm7-vT2k-9Lp4-xR8s-Hw3n' : null });
    }
    if ((match = path.match(/^\/api\/vaults\/([^/]+)\/(verify|secrets|password|archive)$/))) {
      const target = model.vaults.find((item) => item.id === match![1])!;
      if (match[2] === 'secrets') return json(match[1] === 'v-servers' ? model.secrets : []);
      if (match[2] === 'verify') return json({ vault: target, entryCount: target.entryCount, snapshotSha256: target.snapshotSha256, missing: [] });
      if (match[2] === 'password') { Object.assign(target, { status: 'unlocked', lastFailure: null }); return json(target); }
      model.vaults.splice(model.vaults.indexOf(target), 1);
      return route.fulfill({ status: 204 });
    }
    if ((match = path.match(/^\/api\/access\/resources\/(vault|secret)\/([^/]+)$/))) return json(model.resource(match[2]!));
    if (path === '/api/access/candidates') {
      const q = (url.searchParams.get('q') ?? '').toLocaleLowerCase('cs');
      return json(model.directory.filter((item) => item.kind === url.searchParams.get('kind') && q && item.name.toLocaleLowerCase('cs').includes(q)));
    }
    if ((match = path.match(/^\/api\/access\/resources\/(vault|secret)\/([^/]+)\/grants(?:\/(user|group)\/([^/]+))?$/))) {
      const [, , id, , revoked] = match;
      const map = model.grants.get(id!) ?? new Map();
      const subject = (body?.subjectId ?? revoked) as string;
      const expected = (body ? body.expectedVersion : url.searchParams.get('version')) ?? null;
      const current = map.get(subject);
      if ((current?.version ?? null) !== expected) return problem(409, 'Přístup se mezitím změnil.');
      if (method === 'DELETE') map.delete(subject);
      else model.put(id!, subject, body.subjectKind, body.level, body.expiresAt ?? null);
      return route.fulfill({ status: 204 });
    }
    if ((match = path.match(/^\/api\/(vaults|secrets)\/([^/]+)\/(access|grant-candidates|grants)$/))) {
      const [, , id, part] = match;
      if (part === 'access') return json(model.state(id!));
      if (part === 'grant-candidates') {
        const q = (url.searchParams.get('q') ?? '').toLocaleLowerCase('cs');
        return json(model.directory.filter((item) => !q || item.name.toLocaleLowerCase('cs').includes(q)));
      }
      const map = model.grants.get(id!) ?? new Map();
      const subject = (body?.userId ?? body?.groupId ?? url.searchParams.get('userId') ?? url.searchParams.get('groupId')) as string;
      const expected = (body ? body.expectedVersion : url.searchParams.get('expectedVersion')) ?? null;
      const current = map.get(subject);
      if ((current?.version ?? null) !== expected) return problem(409, 'Přístup se mezitím změnil.');
      if (method === 'DELETE') map.delete(subject);
      else model.put(id!, subject, body.userId ? 'user' : 'group', body.level, body.expiresAt ?? null);
      return route.fulfill({ status: 204 });
    }
    if ((match = path.match(/^\/api\/spaces\/([^/]+)\/settings$/))) {
      const space = spaces.find((item) => item.id === match![1])!;
      return json({ id: space.id, name: space.name, icon: space.icon, color: space.color, description: space.description, version: '1' });
    }
    return problem(404, `Mock: ${method} ${path}`);
  });
}

async function open(page: Page, combo: Combo, hash: string, model = createModel()) {
  await page.setViewportSize(viewport(combo.device));
  await mockApi(page, combo, model);
  await page.goto(`/${hash}`);
  await expect(page.locator('[data-settings-host]')).toBeVisible();
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
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${shots}/${name}-${combo.theme}-${combo.device}.png`, fullPage: true });
  };
}
/** The explicit final action of a progressive flow (earlier sections reveal themselves as they become valid). */
const confirm = (page: Page) => page.locator('[data-flow-next]').click();
/** Grant changes are security changes: the open ReauthDialog confirms them with the account password. */
async function reauth(page: Page) {
  const dialog = page.locator('[data-reauth-dialog]');
  await dialog.locator('input[type="password"]').fill('heslo');
  await dialog.locator('[data-confirm-reauth]').click();
}
const settingsPage = (page: Page) => page.locator('.settings-page').first();

for (const combo of combos) {
  test.describe(`${combo.theme} ${combo.device}`, () => {
    test('main screens', async ({ page }) => {
      const shot = shooter(page, combo);
      // The old address redirects to the overview of global settings.
      await open(page, combo, '#/settings');
      await expect(page).toHaveURL(/#\/settings\/security\/overview$/);
      // 2.5: the rail and the breadcrumb name the surface (scope Wiki); the h1 stays for assistive technology only.
      await expect(page.getByRole('heading', { level: 1, name: 'Správa wiki' })).toBeAttached();
      await expect(page.getByRole('navigation', { name: 'Drobečková navigace' }).locator('[aria-current="page"]')).toHaveText('Správa wiki');
      await expect(page.locator('[data-issue="vault-v-legacy"]')).toContainText('Staré přístupy');
      await expect(page.locator('[data-stat="protection"]')).toBeVisible();
      await expect(page.locator('[data-soon]')).toHaveCount(0);
      await shot('01-global-overview');
      for (const [hash, name, heading] of [
        ['#/settings/security/vaults', '02-all-vaults', 'Trezory napříč prostory'],
        ['#/settings/general/files', '03-locations', 'Umístění souborů'],
        ['#/settings/general/files/new', '04-location-wizard', 'Umístění souborů'],
        ['#/spaces/provoz/settings/vaults', '10-space-vaults', 'Trezory a hesla'],
        ['#/spaces/provoz/settings/vaults/v-servers/status', '11-vault-status', 'Servery a síť'],
        ['#/spaces/provoz/settings/vaults/v-legacy/status', '12-vault-needs-password', 'Staré přístupy'],
        ['#/spaces/provoz/settings/vaults/v-servers/access', '13-vault-access', 'Servery a síť'],
        ['#/spaces/provoz/settings/vaults/v-servers/secrets', '14-vault-secrets', 'Servery a síť'],
        ['#/spaces/provoz/settings/vaults/v-servers/audit', '15-vault-audit', 'Servery a síť'],
        ['#/spaces/provoz/settings/vaults/v-servers/protection', '16-vault-protection', 'Servery a síť'],
      ] as const) {
        await go(page, hash);
        await expect(settingsPage(page).getByRole('heading', { level: 2, name: heading })).toBeVisible();
        if (name === '13-vault-access') await expect(page.locator('[data-access-entry="automatic:1"]')).toContainText('Marek Musil');
        if (name === '14-vault-secrets') await expect(page.locator('[data-secret="s-root"]')).toContainText('root@fw01');
        if (name === '15-vault-audit') await expect(page.getByText('přidělil(a) přístup k trezoru').first()).toBeVisible();
        await shot(name);
      }
      // A secret's own access opens in place, on the same components as the vault.
      await go(page, '#/spaces/provoz/settings/vaults/v-servers/secrets');
      await page.locator('[data-secret-access="s-vpn"]').click();
      await expect(page.locator('[data-secret="s-vpn"] [data-access-entry="user:u-jana"]')).toContainText('Z trezoru');
      await shot('17-secret-access');
      if (combo.device === 'mobile') {
        await page.getByRole('button', { name: /Sekce/ }).click();
        await expect(page.getByRole('dialog', { name: 'Sekce' })).toBeVisible();
        await shot('18-mobile-sections');
        await page.getByRole('dialog').getByRole('link', { name: /Obecné/ }).click();
        await expect(page).toHaveURL(/#\/spaces\/provoz\/settings\/general$/);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport(combo.device).width);
    });

    test('Přidat trezor wizard end to end', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/spaces/provoz/settings/vaults');
      await page.locator('[data-add-vault]').first().click();
      await expect(page.getByRole('heading', { name: 'Co chcete udělat?' })).toBeVisible();
      await page.getByRole('radio', { name: /Založit nový trezor/ }).click();
      await shot('20-wizard-1-mode');
      // A progressive flow: each valid answer reveals the next section. Inside a space the space step is skipped;
      // the wiki administrator may add a location.
      await expect(page.getByRole('heading', { name: 'Kam trezor uložit?' })).toBeVisible();
      await expect(page.getByRole('radio', { name: /Nové umístění/ })).toBeVisible();
      await page.getByRole('radio', { name: /Souborový server FS01/ }).click();
      await shot('21-wizard-2-location');
      await page.locator('[data-vault-name]').fill('Provozní síť');
      await expect(page.locator('[data-vault-file]')).toHaveValue('provozni-sit.kdbx');
      await shot('22-wizard-3-file');
      await expect(page.getByRole('radio', { name: /Vygenerovat/ })).toHaveAttribute('aria-checked', 'true');
      await expect(page.getByRole('heading', { name: 'Zkontrolujte a potvrďte' })).toBeVisible();
      await expect(page.locator('[data-summary="file"]')).toContainText('smb://fs01/it/keepass/provozni-sit.kdbx');
      // Nothing is created before the explicit confirmation.
      expect(model.calls.some((call) => call.path === '/api/vaults/new')).toBe(false);
      await shot('23-wizard-4-summary');
      await confirm(page);
      await expect(page.getByText('Zobrazí se jen jednou.', { exact: false })).toBeVisible();
      await page.getByRole('button', { name: 'Zobrazit', exact: true }).click();
      await expect(page.locator('[data-one-time-value]')).toHaveText('Qm7-vT2k-9Lp4-xR8s-Hw3n');
      await shot('24-wizard-5-one-time-secret');
      await page.getByRole('checkbox', { name: 'Heslo mám bezpečně uložené' }).check();
      await page.locator('[data-one-time-done]').click();
      await expect(page).toHaveURL(/#\/spaces\/provoz\/settings\/vaults$/);
      await expect(page.locator('[data-result-banner]')).toContainText('Provozní síť');
      await expect(page.locator('[data-vault="v-new-3"]')).toContainText('Provozní síť');
      const created = model.calls.find((call) => call.path === '/api/vaults/new');
      expect(created?.body).toMatchObject({ spaceId: 'provoz', locationId: 'loc-fs01', name: 'Provozní síť', fileName: 'provozni-sit.kdbx', generatePassword: true, password: null });
      await shot('25-wizard-done');

      // Attaching picks the file from the folder listing; files already used by a vault are disabled.
      await page.locator('[data-add-vault]').first().click();
      await page.getByRole('radio', { name: /Připojit existující/ }).click();
      await page.getByRole('radio', { name: /Souborový server FS01/ }).click();
      await expect(page.getByRole('radio', { name: /servery.kdbx/ })).toBeDisabled();
      await expect(page.locator('[data-vault-files]')).toContainText('Už je připojený jako trezor „Servery a síť“');
      await page.getByRole('radio', { name: /archiv-2025.kdbx/ }).click();
      await expect(page.locator('[data-vault-name]')).toHaveValue('Archiv 2025');
      await shot('26-wizard-attach-file');
      await page.locator('[data-vault-attach-password]').fill('heslo-souboru');
      await confirm(page);
      await expect(page.getByText('Připojeno: 21 záznamů načteno.')).toBeVisible();
      expect(model.calls.find((call) => call.path === '/api/vaults/attach')?.body).toMatchObject({ spaceId: 'provoz', locationId: 'loc-fs01', name: 'Archiv 2025', fileName: 'archiv-2025.kdbx', password: 'heslo-souboru' });
      expect(model.calls.some((call) => call.path === '/api/vault-locations/loc-fs01/files?spaceId=provoz')).toBe(true);
    });

    test('grant level change, revoke and a conflict', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/spaces/provoz/settings/vaults/v-servers/access');
      const jana = page.locator('[data-access-entry="user:u-jana"]');
      await expect(jana.locator('.level-badge')).toContainText('Čte');
      await jana.hover();
      await jana.locator('[data-change-access]').click();
      await jana.locator('[data-access-level-picker]').getByRole('radio', { name: /Spravuje/ }).click();
      await shot('30-access-level-change');
      await jana.locator('[data-save-access]').click();
      await reauth(page);
      await expect(page.locator('[data-result-banner]')).toContainText('Přístup pro Jana Nováková je uložený: Spravuje');
      await expect(jana.locator('.level-badge')).toContainText('Spravuje');
      const change = model.calls.find((call) => call.method === 'PUT' && call.path === '/api/access/resources/vault/v-servers/grants');
      expect(change?.body).toMatchObject({ subjectKind: 'user', subjectId: 'u-jana', level: 'manage', expectedVersion: 'ver-1' });

      const group = page.locator('[data-access-entry="group:g-ops"]');
      await group.hover();
      await group.locator('[data-revoke-access]').click();
      await expect(page.getByRole('dialog', { name: 'Odebrat přístup' })).toContainText('Provoz tým');
      await shot('31-access-revoke-confirm');
      await reauth(page);
      await expect(page.locator('[data-result-banner]')).toContainText('Přístup pro Provoz tým je odebraný');
      await expect(group).toHaveCount(0);
      expect(model.calls.some((call) => call.method === 'DELETE' && call.path === '/api/access/resources/vault/v-servers/grants/group/g-ops?version=ver-2')).toBe(true);

      // Someone else changes Jana's grant meanwhile: the save is refused, the tab reloads and says so.
      model.put('v-servers', 'u-jana', 'user', 'discover');
      await jana.hover();
      await jana.locator('[data-change-access]').click();
      await jana.locator('[data-access-level-picker]').getByRole('radio', { name: /Čte/ }).click();
      await jana.locator('[data-save-access]').click();
      await reauth(page);
      await expect(page.locator('[data-access-conflict]')).toContainText('mezitím změnil někdo jiný');
      await expect(jana.locator('.level-badge')).toContainText('Vidí');
      await shot('32-access-conflict');

      // Adding a grant is GrantFlow in place, with people searched on the server.
      await page.locator('[data-add-access]').click();
      await page.getByRole('radio', { name: /Uživatel/ }).click();
      await page.getByRole('searchbox', { name: 'Uživatelé: hledat' }).fill('Eva');
      await page.getByRole('radio', { name: /Eva Malá/ }).click();
      await page.locator('[data-access-level-picker]').getByRole('radio', { name: /Čte/ }).click();
      await shot('33-access-grant-summary');
      // The flow's summary confirms in place with the fresh sign-in (a vault grant always needs one).
      await page.locator('[data-resource-grant-flow] .confirm-reauth input[type="password"]').fill('heslo');
      await page.locator('[data-resource-grant-flow] [data-confirm-reauth]').click();
      await expect(page.locator('[data-result-banner]')).toContainText('Eva Malá má přístup Čte');
      await expect(page.locator('[data-access-entry="user:u-eva"] .level-badge')).toContainText('Čte');

      // Archiving asks with a destructive confirmation and returns to the vault cards.
      await page.locator('[data-tab="status"]').click();
      await page.locator('[data-vault-archive]').click();
      await expect(page.getByRole('dialog', { name: 'Archivovat trezor' })).toContainText('servery.kdbx');
      await shot('34-vault-archive-confirm');
      await page.getByRole('dialog', { name: 'Archivovat trezor', exact: true }).locator('[data-confirm-action]').click();
      await expect(page).toHaveURL(/#\/spaces\/provoz\/settings\/vaults$/);
      await expect(page.locator('[data-result-banner]')).toContainText('je archivovaný');
      await expect(page.locator('[data-vault="v-servers"]')).toHaveCount(0);
    });
  });
}
