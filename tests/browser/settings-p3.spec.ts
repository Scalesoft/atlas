import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// Phase 3 of the real settings (plan 2026-09-27): the rebuilt people, modules, backups, access history,
// space and profile pages against a mocked API. Run against a Vite dev server, no wiki needed:
//   pnpm --dir src/Atlas.Web exec vite --port 5348
//   $env:ATLAS_URL='http://127.0.0.1:5348'; npx --prefix tests/browser playwright test settings-p3.spec.ts --fully-parallel
// Screenshots go to SETTINGS_SHOTS (default artifacts/settings-real), prefixed p3-.
const shots = resolve(process.env.SETTINGS_SHOTS ?? '../../artifacts/settings-real');
mkdirSync(shots, { recursive: true });

test.use({ screenshot: 'off', video: 'off', contextOptions: { reducedMotion: 'reduce' } });

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
type Call = { method: string; path: string; body: unknown };
type Handler = (context: { method: string; url: URL; body: any; match: RegExpMatchArray; model: Model }) => unknown;

/** A small stateful model of the APIs the pages touch; handlers mutate it like the server would. */
function createModel() {
  let serial = 10;
  const version = () => `v${++serial}`;
  const model = {
    calls: [] as Call[],
    version,
    spaceSettings: { id: 'provoz', name: 'Provoz', icon: 'book-open', color: 'blue', description: 'Servery, síť a pohotovost', version: 'v1' } as Json,
    sharing: { enabled: true, version: 'p1' } as Json,
    links: [
      { id: 'l1', pageId: 'p1', pageTitle: 'Pohotovostní příručka', name: 'Pro dodavatele', revisionVersion: 7, createdByName: 'Marek Musil', createdAt: '2026-09-20T10:00:00Z', expiresAt: '2026-10-20T10:00:00Z', revokedAt: null, lastOpenedAt: '2026-09-26T08:00:00Z', status: 'active' },
      { id: 'l2', pageId: 'p2', pageTitle: 'Síťové schéma', name: 'Audit', revisionVersion: 3, createdByName: 'Jana Nováková', createdAt: '2026-08-01T10:00:00Z', expiresAt: '2026-08-15T10:00:00Z', revokedAt: null, lastOpenedAt: null, status: 'expired' },
    ] as Json[],
    access: {
      users: [
        { id: 'u-martin', username: 'marek.musil', displayName: 'Marek Musil', isActive: true },
        { id: 'u-jana', username: 'jana.novakova', displayName: 'Jana Nováková', isActive: true },
        { id: 'u-petr', username: 'petr.svoboda', displayName: 'Petr Svoboda', isActive: true },
        { id: 'u-eva', username: 'eva.mala', displayName: 'Eva Malá', isActive: true },
      ],
      groups: [{ id: 'g-ops', name: 'Provoz tým' }, { id: 'g-dev', name: 'Vývojáři' }],
      grants: [
        { id: 'gr1', userId: 'u-jana', groupId: null, name: 'Jana Nováková', role: 'Writer', version: 'a1', startAt: null, expiresAt: null },
        { id: 'gr2', userId: null, groupId: 'g-ops', name: 'Provoz tým', role: 'Reader', version: 'a2', startAt: null, expiresAt: '2026-12-31T23:00:00Z' },
        { id: 'gr3', userId: 'u-martin', groupId: null, name: 'Marek Musil', role: 'Admin', version: 'a3', startAt: null, expiresAt: null },
      ] as Json[],
    },
    account: { displayName: 'Marek Musil', email: 'marek@example.test', version: 'acc1', source: 'builtin', canEditDisplayName: true, canEditEmail: true, currentSessionAuthenticatedAt: '2026-09-27T07:58:00Z', currentSessionAuthenticationMethod: 'builtin', lastSignInAt: '2026-09-26T16:10:00Z', lastSignInMethod: 'builtin' } as Json,
    appearance: { layout: 'connected', accent: 'blue', mode: 'light' } as Json,
    users: [
      { id: 'u-martin', username: 'marek.musil', displayName: 'Marek Musil', isActive: true, isWikiAdmin: true, version: 'uv1', hasBuiltinCredential: true, source: 'builtin', credentialState: 'active' },
      { id: 'u-jana', username: 'jana.novakova', displayName: 'Jana Nováková', isActive: true, isWikiAdmin: false, version: 'uv2', hasBuiltinCredential: true, source: 'builtin', credentialState: 'active' },
      { id: 'u-petr', username: 'petr.svoboda', displayName: 'Petr Svoboda', isActive: true, isWikiAdmin: false, version: 'uv3', hasBuiltinCredential: false, source: 'external', credentialState: 'none' },
      { id: 'u-eva', username: 'eva.mala', displayName: 'Eva Malá', isActive: true, isWikiAdmin: false, version: 'uv4', hasBuiltinCredential: true, source: 'builtin', credentialState: 'pending' },
      { id: 'u-karel', username: 'karel.stary', displayName: 'Karel Starý', isActive: false, isWikiAdmin: false, version: 'uv5', hasBuiltinCredential: true, source: 'builtin', credentialState: 'active' },
    ] as Json[],
    groups: [{ id: 'g-ops', name: 'Provoz tým', version: 'gv1', source: 'local' }, { id: 'g-dev', name: 'Vývojáři', version: 'gv2', source: 'local' }] as Json[],
    members: { 'g-ops': [{ id: 'u-jana', username: 'jana.novakova', displayName: 'Jana Nováková', isActive: true, version: 'm1' }], 'g-dev': [] } as Record<string, Json[]>,
    apiAccess: { canIssueTokens: false, version: 'api1', level: 'none' } as Json,
    oidc: { version: 'o1', publicOrigin: 'https://wiki.example.test', restartRequired: false, builtinEnabled: true, providers: [
      { id: 'hub', displayName: 'JetBrains Hub', issuer: 'https://hub.example.test/hub', clientId: 'atlas', hasSecret: true, scopes: ['openid', 'profile', 'email'], allowSignup: true, callbackUrl: 'https://wiki.example.test/signin-oidc/hub', adminMappings: [{ claimType: 'groups', claimValue: 'wiki-admins' }] },
    ] } as Json,
    backups: {
      settings: { enabled: true, intervalMinutes: 1440, mode: 'full', destination: 'smb://nas01/zalohy/atlas', username: 'svc-atlas', domain: 'FIRMA', hasPassword: true, retentionCount: 14 },
      runs: [
        { id: 'r3', startedAt: '2026-09-27T02:00:00Z', finishedAt: '2026-09-27T02:03:10Z', status: 'succeeded', trigger: 'scheduled', mode: 'full', destination: 'smb://nas01/zalohy/atlas', fileName: 'atlas-2026-09-27.zip', size: 48_234_112, message: '' },
        { id: 'r2', startedAt: '2026-09-26T02:00:00Z', finishedAt: '2026-09-26T02:00:05Z', status: 'skipped', trigger: 'scheduled', mode: 'full', destination: 'smb://nas01/zalohy/atlas', fileName: null, size: 0, message: 'Od poslední úspěšné zálohy se obsah nezměnil.' },
        { id: 'r1', startedAt: '2026-09-25T02:00:00Z', finishedAt: '2026-09-25T02:00:40Z', status: 'failed', trigger: 'scheduled', mode: 'full', destination: 'smb://nas01/zalohy/atlas', fileName: null, size: 0, message: 'Sdílená složka neodpověděla.' },
      ],
      nextRunAt: '2026-09-28T02:00:00Z', lastSuccessAt: '2026-09-27T02:03:10Z', warning: null,
    } as Json,
  };
  return model;
}
type Model = ReturnType<typeof createModel>;

const routes: [string, RegExp, Handler][] = [
  ['GET', /^\/api\/setup\/status$/, () => ({ databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true })],
  ['GET', /^\/api\/auth\/methods$/, () => ({ builtinEnabled: true, providers: [{ id: 'hub', displayName: 'JetBrains Hub', loginUrl: '/api/auth/oidc/hub/login' }] })],
  ['GET', /^\/api\/auth\/csrf$/, () => ({ token: 'csrf-test' })],
  ['GET', /^\/api\/admin\/setup$/, () => ({ pending: false })],
  ['GET', /^\/api\/admin\/backups\/?$/, ({ model }) => model.backups],
  ['PUT', /^\/api\/admin\/backups\/?$/, ({ model, body }) => {
    const settings = { ...(model.backups.settings as Json), ...body };
    delete settings.expectedVersion; delete settings.password;
    model.backups = { ...model.backups, settings };
    return model.backups;
  }],
  ['POST', /^\/api\/admin\/backups\/run$/, ({ model }) => {
    const run = { id: 'r4', startedAt: now, finishedAt: now, status: 'succeeded', trigger: 'manual', mode: 'full', destination: 'smb://nas01/zalohy/atlas', fileName: 'atlas-2026-09-27-manual.zip', size: 48_300_000, message: '' };
    model.backups = { ...model.backups, runs: [run, ...(model.backups.runs as Json[])], lastSuccessAt: now };
    return run;
  }],
  ['POST', /^\/api\/admin\/backups\/test$/, () => ({ message: 'Cíl zálohy je dostupný a zapisovatelný.' })],
  // The git backup beside the archives (Zálohy → Git): set up and in sync.
  ['GET', /^\/api\/admin\/backups\/git$/, () => ({
    settings: { enabled: true, remoteUrl: 'ssh://git@git.example.test:2222/Infrastructure/atlas-backup.git', branch: 'main', publicKey: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGl0IGlzIGEgdGVzdCBrZXk atlas@wiki.example.test', hostKeys: ['ssh-ed25519 SHA256:q1w2e3r4t5y6u7i8o9p0'] },
    running: false, progress: null, pendingPages: 0, lastSyncAt: '2026-09-27T08:15:00Z', lastCommit: '4f2c9e1ab37d5c60e8f1a2b3c4d5e6f708192a3b', lastError: null, lastErrorAt: null, nextAttemptAt: null, warning: null,
  })],
  // People and access history
  ['GET', /^\/api\/admin\/users$/, ({ model }) => model.users],
  ['POST', /^\/api\/admin\/users$/, ({ model, body }) => {
    const user = { id: 'u-new', username: body.username, displayName: body.displayName, isActive: true, isWikiAdmin: body.isWikiAdmin, version: 'uvn', hasBuiltinCredential: true, source: 'builtin', credentialState: 'pending' };
    model.users.push(user);
    return { token: 'ACT-9Qm7-vT2k-9Lp4-xR8s-Hw3n-5dF1-a8Zq-3pLm', expiresAt: '2026-10-04T09:30:00Z', user };
  }],
  ['PUT', /^\/api\/admin\/users\/([^/]+)$/, ({ model, body, match }) => {
    const user = model.users.find((item) => item.id === match[1])!;
    if (body.expectedVersion !== user.version) return { status: 409, json: { detail: 'Účet se mezitím změnil.' } };
    Object.assign(user, { displayName: body.displayName, isActive: body.isActive, isWikiAdmin: body.isWikiAdmin, version: model.version() });
    return user;
  }],
  ['GET', /^\/api\/admin\/users\/([^/]+)\/ai-policy$/, () => ({ enabled: null, searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null, version: null, resolved: { enabled: false, limits: [] } })],
  ['GET', /^\/api\/admin\/users\/([^/]+)\/api-access$/, ({ model }) => model.apiAccess],
  ['PUT', /^\/api\/admin\/users\/([^/]+)\/api-access$/, ({ model, body }) => (model.apiAccess = { canIssueTokens: body.level !== 'none', level: body.level, version: model.version() })],
  ['GET', /^\/api\/admin\/api-tokens\/summary$/, () => ({ levelsReview: false })],
  ['GET', /^\/api\/admin\/groups$/, ({ model }) => model.groups],
  ['POST', /^\/api\/admin\/groups$/, ({ model, body }) => {
    const group = { id: 'g-new', name: body.name, version: 'gvn', source: 'local' };
    model.groups.push(group); model.members['g-new'] = [];
    return group;
  }],
  ['GET', /^\/api\/admin\/groups\/([^/]+)\/members$/, ({ model, match }) => model.members[match[1]!] ?? []],
  ['PUT', /^\/api\/admin\/groups\/([^/]+)\/members\/([^/]+)$/, ({ model, body, match }) => {
    const list = model.members[match[1]!] ?? [];
    const user = model.users.find((item) => item.id === match[2]);
    model.members[match[1]!] = body.isMember ? [...list, { id: user!.id, username: user!.username, displayName: user!.displayName, isActive: true, version: model.version() }] : list.filter((item) => item.id !== match[2]);
    return undefined;
  }],
  ['GET', /^\/api\/admin\/access-audit$/, () => ({ items: [
    { id: '30', occurredAt: '2026-09-27T08:20:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'grant.added', userId: 'u-jana', userName: 'Jana Nováková', groupId: null, groupName: null, spaceId: 'provoz', spaceName: 'Provoz', role: 'Writer', isActive: null, isWikiAdmin: null, canIssueTokens: null },
    { id: '29', occurredAt: '2026-09-26T15:05:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'membership.added', userId: 'u-jana', userName: 'Jana Nováková', groupId: 'g-ops', groupName: 'Provoz tým', spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null },
    { id: '28', occurredAt: '2026-09-26T10:40:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'account.created', userId: 'u-eva', userName: 'Eva Malá', groupId: null, groupName: null, spaceId: null, spaceName: null, role: null, isActive: true, isWikiAdmin: false, canIssueTokens: null },
    { id: '27', occurredAt: '2026-09-24T09:12:00Z', actorId: 'u-martin', actorName: 'Marek Musil', action: 'api_permission.enabled', userId: 'u-petr', userName: 'Petr Svoboda', groupId: null, groupName: null, spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: true },
  ], hasMore: false, nextBeforeId: null, coverageStartedAt: '2026-09-01T00:00:00Z' })],
  // Modules
  ['GET', /^\/api\/admin\/modules\/?$/, () => ({ items: [
    { id: 'oidc', title: 'Firemní přihlášení', description: 'Připojte JetBrains Hub nebo jiného poskytovatele OpenID Connect.', settingsUi: 'oidc', order: 10, showInSetup: true },
    { id: 'youtrack', title: 'YouTrack', description: 'Odkazy na úkoly a jejich stav přímo ve stránkách.', settingsUi: 'youtrack', order: 20, showInSetup: true },
    { id: 'wiki-js-import', title: 'Import Wiki.js', description: 'Přeneste stránky a přílohy z exportu Wiki.js.', settingsUi: 'wiki-js-import', order: 30, showInSetup: true },
  ] })],
  ['GET', /^\/api\/admin\/modules\/oidc$/, ({ model }) => model.oidc],
  ['PUT', /^\/api\/admin\/modules\/oidc$/, ({ model, body }) => {
    model.oidc = { ...model.oidc, publicOrigin: body.publicOrigin, version: model.version(), restartRequired: true, providers: body.providers.map((provider: Json) => ({ ...provider, hasSecret: true, callbackUrl: `${body.publicOrigin}/signin-oidc/${provider.id}` })) };
    return model.oidc;
  }],
  // Saving the providers is a security change: the fresh sign-in is a password (SEC-A-1).
  ['POST', /^\/api\/security\/reauth$/, () => ({ id: 'proof-1', method: 'password', providerId: null, providerName: null, expiresAt: '2099-01-01T00:00:00Z', redirectUrl: null })],
  ['POST', /^\/api\/security\/reauth\/proof-1\/password$/, () => ({ id: 'proof-1', operation: 'security', targetKind: 'vault', targetId: null, method: 'password', state: 'verified', failure: null, expiresAt: '2099-01-01T00:00:00Z', proofExpiresAt: '2099-01-01T00:00:00Z' })],
  ['POST', /^\/api\/admin\/modules\/oidc\/test$/, () => ({ ok: true, message: 'Poskytovatel odpovídá, discovery dokument je platný.', checkedAt: now })],
  ['GET', /^\/api\/admin\/integrations\/youtrack\/?$/, () => ({ moduleEnabled: true, enabled: true, baseUrl: 'https://youtrack.example.test', hasToken: true, version: 'y1', status: 'configured', spaces: [{ id: 'provoz', name: 'Provoz' }, { id: 'vyvoj', name: 'Vývoj' }], spaceProjects: [{ spaceId: 'provoz', projectIds: ['OPS'] }] })],
  ['GET', /^\/api\/spaces$/, () => spaces],
  ['GET', /^\/api\/me\/library\/(\w+)$/, () => ({ items: [
    { pageId: 'p1', spaceId: 'provoz', spaceName: 'Provoz', title: 'Pohotovostní příručka', at: '2026-09-27T08:40:00Z', isPublished: true },
    { pageId: 'p2', spaceId: 'provoz', spaceName: 'Provoz', title: 'Síťové schéma', at: '2026-09-26T13:10:00Z', isPublished: true },
  ], hasMore: false, nextOffset: null })],
  ['GET', /^\/api\/spaces\/([^/]+)\/tree$/, ({ match }) => ({ spaceId: match[1], version: 1, items: [] })],
  // Space settings
  ['GET', /^\/api\/spaces\/provoz\/settings$/, ({ model }) => model.spaceSettings],
  ['PUT', /^\/api\/spaces\/provoz\/settings$/, ({ model, body }) => {
    if (body.expectedVersion !== model.spaceSettings.version) return { status: 409, json: { detail: 'Nastavení se mezitím změnilo.' } };
    model.spaceSettings = { ...model.spaceSettings, ...body, version: model.version() };
    delete model.spaceSettings.expectedVersion;
    return model.spaceSettings;
  }],
  ['GET', /^\/api\/spaces\/provoz\/external-sharing$/, ({ model }) => model.sharing],
  ['PUT', /^\/api\/spaces\/provoz\/external-sharing$/, ({ model, body }) => {
    model.sharing = { enabled: body.enabled, version: model.version() };
    if (!body.enabled) model.links = model.links.map((link) => ({ ...link, status: link.status === 'active' ? 'revoked' : link.status, revokedAt: link.status === 'active' ? now : link.revokedAt }));
    return model.sharing;
  }],
  ['GET', /^\/api\/spaces\/provoz\/share-links$/, ({ model }) => ({ items: model.links, hasMore: false })],
  ['DELETE', /^\/api\/pages\/([^/]+)\/share-links\/([^/]+)$/, ({ model, match }) => {
    model.links = model.links.map((link) => (link.id === match[2] ? { ...link, status: 'revoked', revokedAt: now } : link));
    return undefined;
  }],
  ['GET', /^\/api\/spaces\/provoz\/access$/, ({ model }) => model.access],
  ['PUT', /^\/api\/spaces\/provoz\/access$/, ({ model, body }) => {
    const subject = body.userId ?? body.groupId;
    const current = model.access.grants.find((grant) => (grant.userId ?? grant.groupId) === subject);
    if ((current?.version ?? null) !== body.expectedVersion) return { status: 409, json: { detail: 'Přístup se mezitím změnil.' } };
    if (body.role === null) model.access.grants = model.access.grants.filter((grant) => grant !== current);
    else if (current) Object.assign(current, { role: body.role, startAt: body.startAt ?? null, expiresAt: body.expiresAt ?? null, version: model.version() });
    else {
      const user = model.access.users.find((item) => item.id === body.userId);
      const group = model.access.groups.find((item) => item.id === body.groupId);
      model.access.grants.push({ id: `gr${model.version()}`, userId: body.userId ?? null, groupId: body.groupId ?? null, name: user?.displayName ?? group?.name ?? '', role: body.role, version: model.version(), startAt: body.startAt ?? null, expiresAt: body.expiresAt ?? null });
    }
    return undefined;
  }],
  // The access layer (docs/access-model.md §3) over the same space grants.
  ['GET', /^\/api\/access\/kinds$/, () => []],
  ['GET', /^\/api\/access\/resources\/space\/provoz$/, ({ model }) => {
    const level = (role: string) => ({ Reader: 'read', Writer: 'write', Admin: 'manage' })[role as 'Reader'];
    return {
      resource: { kind: 'space', id: 'provoz', title: 'Provoz' },
      canManage: true,
      automatic: [{ label: 'Správci wiki', detail: 'automaticky ve všech prostorech', level: 'manage', count: 1 }],
      entries: model.access.grants.map((grant) => ({
        subject: grant.groupId ? { kind: 'group', id: grant.groupId, name: grant.name, memberCount: 3 } : { kind: 'user', id: grant.userId, name: grant.name, detail: model.access.users.find((user) => user.id === grant.userId)?.username ?? null },
        level: level(grant.role),
        sources: [{ via: 'direct', label: '', level: level(grant.role), version: grant.version, startAt: grant.startAt ?? null, expiresAt: grant.expiresAt ?? null, effective: true }],
      })),
      hasMore: false,
      exceptions: [],
    };
  }],
  ['GET', /^\/api\/access\/candidates$/, ({ model, url }) => {
    const q = (url.searchParams.get('q') ?? '').toLocaleLowerCase('cs');
    const list = url.searchParams.get('kind') === 'group'
      ? model.access.groups.map((group) => ({ kind: 'group', id: group.id, name: group.name }))
      : model.access.users.filter((user) => user.isActive).map((user) => ({ kind: 'user', id: user.id, name: user.displayName, detail: user.username }));
    return list.filter((item) => q && item.name.toLocaleLowerCase('cs').includes(q));
  }],
  ['PUT', /^\/api\/access\/resources\/space\/provoz\/grants$/, ({ model, body }) => {
    const role = ({ read: 'Reader', write: 'Writer', manage: 'Admin' })[body.level as 'read'];
    const current = model.access.grants.find((grant) => (grant.userId ?? grant.groupId) === body.subjectId);
    if ((current?.version ?? null) !== body.expectedVersion) return { status: 409, json: { detail: 'Přístup se mezitím změnil.' } };
    if (current) Object.assign(current, { role, startAt: body.startAt ?? null, expiresAt: body.expiresAt ?? null, version: model.version() });
    else {
      const user = model.access.users.find((item) => item.id === body.subjectId);
      const group = model.access.groups.find((item) => item.id === body.subjectId);
      model.access.grants.push({ id: `gr${model.version()}`, userId: user ? body.subjectId : null, groupId: group ? body.subjectId : null, name: user?.displayName ?? group?.name ?? '', role, version: model.version(), startAt: body.startAt ?? null, expiresAt: body.expiresAt ?? null });
    }
    return undefined;
  }],
  ['DELETE', /^\/api\/access\/resources\/space\/provoz\/grants\/(user|group)\/([^/]+)$/, ({ model, match }) => {
    model.access.grants = model.access.grants.filter((grant) => (grant.userId ?? grant.groupId) !== match[2]);
    return undefined;
  }],
  ['GET', /^\/api\/access\/principals\/(user|group)\/([^/]+)$/, ({ model, match }) => ({
    principal: { kind: match[1], id: match[2], name: model.users.find((user) => user.id === match[2])?.displayName ?? model.groups.find((group) => group.id === match[2])?.name ?? match[2], detail: null, groups: [{ id: 'g-ops', name: 'Provoz tým' }] },
    scope: 'wiki',
    canManage: true,
    entries: [{ kind: 'space', resourceId: 'provoz', title: 'Provoz', spaceId: 'provoz', spaceName: 'Provoz', level: 'write', sources: [{ via: 'group', label: 'Provoz tým', groupId: 'g-ops', level: 'write', effective: true }] }],
  })],
  ['GET', /^\/api\/vaults$/, () => []],
  ['GET', /^\/api\/vault-locations$/, () => []],
  // Profile
  ['GET', /^\/api\/profile\/account$/, ({ model }) => model.account],
  ['PUT', /^\/api\/profile\/account$/, ({ model, body }) => {
    model.account = { ...model.account, displayName: body.displayName, email: body.email, version: model.version() };
    return model.account;
  }],
  ['PUT', /^\/api\/profile\/appearance$/, ({ model, body }) => (model.appearance = { ...body })],
  ['GET', /^\/api\/profile\/security$/, () => ({ hasBuiltinCredential: true, credentialState: 'active' })],
  ['GET', /^\/api\/profile\/identities$/, () => ({ items: [] })],
  ['GET', /^\/api\/profile\/api-tokens$/, () => ({ canIssueTokens: true, permissionVersion: 'perm1', apiBaseUrl: '/api/v1', openApiUrl: '/api/profile/api-document', level: 'write', mcpEnabled: false, mcpUrl: null, tokens: [
    { id: 't1', name: 'Noční export', prefix: 'atl_7Hq', scopes: ['read'], channels: ['rest'], effectiveScopes: ['read'], usable: true, activeFrom: '2026-09-01T00:00:00Z', expiresAt: '2026-11-30T00:00:00Z', activeTo: null },
  ] })],
  ['GET', /^\/api\/analytics\/activity$/, () => ({ revisions: 42, pages: 17, days: [{ day: '2026-09-25', count: 6 }, { day: '2026-09-26', count: 3 }], authors: [] })],
  ['GET', /^\/api\/analytics\/changes$/, () => ({ items: [], hasMore: false })],
  ['GET', /^\/api\/analytics\/popular$/, () => ({ items: [] })],
];

async function mockApi(page: Page, combo: Combo, model: Model, extra: [string, RegExp, Handler][] = []) {
  await page.route('**/api/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
    model.calls.push({ method, path: `${url.pathname}${url.search}`, body });
    if (url.pathname === '/api/auth/me')
      return route.fulfill({ json: { id: 'u-martin', username: 'marek.musil', displayName: model.account.displayName, isWikiAdmin: true, appearance: { ...model.appearance, mode: combo.theme } } });
    for (const [verb, pattern, handler] of [...extra, ...routes]) {
      const match = url.pathname.match(pattern);
      if (verb !== method || !match) continue;
      const result = handler({ method, url, body, match, model }) as unknown;
      if (result === undefined) return route.fulfill({ status: 204 });
      if (result && typeof result === 'object' && 'status' in (result as Json) && 'json' in (result as Json)) {
        const { status, json } = result as { status: number; json: unknown };
        return route.fulfill({ status, json });
      }
      return route.fulfill({ json: result });
    }
    return route.fulfill({ status: 404, json: { title: `Mock: ${method} ${url.pathname}`, detail: `Mock: ${method} ${url.pathname}` } });
  });
}

async function open(page: Page, combo: Combo, hash: string, model = createModel(), extra: [string, RegExp, Handler][] = []) {
  await page.setViewportSize(viewport(combo.device));
  await mockApi(page, combo, model, extra);
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
    await page.waitForTimeout(350);
    await page.screenshot({ path: `${shots}/p3-${name}-${combo.theme}-${combo.device}.png`, fullPage: true });
  };
}
/** The explicit final action of a progressive flow (earlier sections reveal themselves as they become valid). */
const confirm = (page: Page) => page.locator('[data-flow-next]').click();
const heading = (page: Page, name: string) => page.locator('.settings-page').getByRole('heading', { level: 2, name }).first();

test('mobile member selection survives the reveal finishing during a pointer press', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await open(page, { theme: 'light', device: 'mobile' }, '#/settings/people/groups/g-ops/members');
  // Stretch only this reveal to reliably press during it; complete the real animation while the pointer is down.
  await page.addStyleTag({ content: '.group-detail .inline-reveal { --atlas-motion-slow: 2s; }' });
  await page.locator('[data-add-member]').click();
  const candidate = page.locator('[data-member-candidates]').getByRole('radio', { name: /Petr Svoboda/ });
  await candidate.evaluate((element) => {
    element.addEventListener('mousedown', () => {
      for (const animation of element.closest('[data-inline-reveal]')!.getAnimations()) animation.finish();
    }, { once: true });
  });
  await candidate.click({ delay: 50 });
  await expect(candidate).toBeChecked();
  await page.locator('[data-add-member-confirm]').click();
  await expect(page.locator('[data-member="u-petr"]')).toBeVisible();
});

for (const combo of combos) {
  test.describe(`${combo.theme} ${combo.device}`, () => {
    test('space settings: Obecné, Sdílení, Přístupy', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/spaces/provoz/settings/general');
      await expect(heading(page, 'Obecné')).toBeVisible();
      await shot('10-space-general');
      await page.locator('[data-space-name]').fill('Provoz a síť');
      await page.locator('[data-appearance-icons]').getByRole('radio', { name: 'Průvodce' }).click();
      await expect(page.locator('[data-appearance-preview]')).toContainText('Provoz a síť');
      await shot('11-space-general-dirty');
      await page.locator('[data-space-save] [data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      expect(model.calls.find((call) => call.method === 'PUT' && call.path === '/api/spaces/provoz/settings')?.body).toMatchObject({ name: 'Provoz a síť', icon: 'compass', expectedVersion: 'v1' });

      await go(page, '#/spaces/provoz/settings/sharing');
      await expect(page.locator('[data-share-link="l1"]')).toContainText('Pro dodavatele');
      await shot('12-space-sharing');
      await page.locator('[data-share-link="l1"] [data-revoke-link]').click();
      await expect(page.getByRole('dialog', { name: 'Zneplatnit odkaz' })).toBeVisible();
      await shot('13-space-sharing-revoke');
      await page.locator('dialog[open] [data-confirm-action]').click();
      await expect(page.locator('[data-share-link="l1"]')).not.toContainText('Platí do');

      await go(page, '#/spaces/provoz/settings/access');
      const jana = page.locator('[data-access-entry="user:u-jana"]');
      await expect(jana).toBeVisible();
      await expect(page.locator('[data-access-entry="automatic:0"]')).toContainText('Automaticky');
      await shot('14-space-access');
      await jana.hover();
      await jana.locator('[data-change-access]').click();
      await jana.locator('[data-access-level-picker]').getByRole('radio', { name: /^Čte/ }).click();
      await shot('15-space-access-change');
      await jana.locator('[data-save-access]').click();
      await expect(page.locator('[data-result-banner]')).toBeVisible();
      expect(model.calls.find((call) => call.method === 'PUT' && call.path === '/api/access/resources/space/provoz/grants')?.body).toMatchObject({ subjectKind: 'user', subjectId: 'u-jana', expectedVersion: 'a1' });

      await page.locator('[data-add-access]').first().click();
      await page.getByRole('radio', { name: /^Uživatel/ }).click();
      await page.getByRole('searchbox', { name: 'Uživatelé: hledat' }).fill('Eva');
      await page.getByRole('radio', { name: /Eva Malá/ }).click();
      await page.locator('[data-access-level-picker]').getByRole('radio', { name: /Čte/ }).click();
      await shot('16-space-access-grant-summary');
      await confirm(page);
      await expect(page.locator('[data-access-entry="user:u-eva"]')).toBeVisible();

      const ops = page.locator('[data-access-entry="group:g-ops"]');
      await ops.hover();
      await ops.locator('[data-revoke-access]').click();
      await page.locator('dialog[open] [data-confirm-action]').click();
      await expect(ops).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport(combo.device).width);
    });

    test('people: accounts, invite wizard, account detail, groups and access history', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/settings/people/accounts');
      await expect(page.locator('[data-account="u-eva"]')).toContainText('Čeká na aktivaci');
      await shot('20-accounts');
      await page.locator('[data-invite]').first().click();
      await page.locator('[data-invite-username]').fill('lucie.cerna');
      await page.locator('[data-invite-display-name]').fill('Lucie Černá');
      await shot('21-invite-who');
      // A filled name reveals the role, preset to a regular account, and the summary with the explicit create.
      await expect(page.locator('[data-invite-role]').getByRole('radio', { name: /Běžný účet/ })).toHaveAttribute('aria-checked', 'true');
      await shot('22-invite-summary');
      await confirm(page);
      const code = page.locator('[data-invite-code]');
      await code.getByRole('button', { name: 'Zobrazit' }).click();
      await expect(code.locator('[data-one-time-value]')).toHaveText('ACT-9Qm7-vT2k-9Lp4-xR8s-Hw3n-5dF1-a8Zq-3pLm');
      await shot('23-invite-code');
      await code.getByRole('checkbox').check();
      await code.locator('[data-one-time-done]').click();
      await expect(page.locator('[data-account="u-new"]')).toContainText('Lucie Černá');
      expect(model.calls.find((call) => call.method === 'POST' && call.path === '/api/admin/users')?.body).toMatchObject({ username: 'lucie.cerna', displayName: 'Lucie Černá', isWikiAdmin: false });

      await go(page, '#/settings/people/accounts/u-jana');
      await expect(heading(page, 'Jana Nováková')).toBeVisible();
      await expect(page.locator('[data-access-entry="space:provoz"]')).toContainText('Celý prostor');
      await shot('24-person-card');
      await go(page, '#/settings/people/accounts/u-jana/account');
      await page.getByRole('radiogroup', { name: 'Úroveň osobního API', exact: true }).getByRole('radio', { name: 'Zápis', exact: true }).click();
      await shot('24-account-tab');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      expect(model.calls.find((call) => call.method === 'PUT' && call.path === '/api/admin/users/u-jana/api-access')?.body).toMatchObject({ level: 'write', expectedVersion: 'api1' });

      await go(page, '#/settings/people/groups');
      await expect(page.locator('[data-group="g-ops"]')).toBeVisible();
      await shot('25-groups');
      await go(page, '#/settings/people/groups/g-ops/members');
      await page.locator('[data-add-member]').click();
      const candidate = page.locator('[data-member-candidates]').getByRole('radio', { name: /Petr Svoboda/ });
      await candidate.check();
      await expect(candidate).toBeChecked();
      await shot('26-group-add-member');
      await page.locator('[data-add-member-confirm]').click();
      await expect(page.locator('[data-member="u-petr"]')).toBeVisible();
      await page.locator('[data-remove-member="u-jana"]').click();
      await page.locator('dialog[open] [data-confirm-action]').click();
      await expect(page.locator('[data-member="u-jana"]')).toHaveCount(0);

      await go(page, '#/settings/security/audit');
      await expect(page.getByText('udělil(a) roli v prostoru')).toBeVisible();
      await shot('27-access-history');
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport(combo.device).width);
    });

    test('modules and backups', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/settings/general/modules');
      await expect(page.locator('[data-module="oidc"]')).toHaveCount(0);
      await shot('30-modules');
      await go(page, '#/settings/general/authentication/login');
      await expect(page.locator('[data-provider="hub"]')).toContainText('JetBrains Hub');
      await page.locator('[data-provider="hub"] [data-provider-test]').click();
      await expect(page.locator('[data-provider-test-result]').first()).toContainText('Poskytovatel odpovídá');
      await shot('31-oidc');
      await page.locator('[data-add-provider]').first().click();
      await page.locator('[data-provider-name]').fill('Google Workspace');
      await shot('32-oidc-wizard-identity');
      // The name reveals the connection; access and admin rules are prefilled or optional, so the summary follows.
      await page.locator('[data-provider-issuer]').fill('https://accounts.google.test');
      await page.locator('[data-provider-client]').fill('atlas-google');
      await page.locator('[data-provider-secret]').fill('tajny-klic');
      await expect(page.locator('[data-provider-scopes]')).toHaveValue(/openid/);
      await shot('33-oidc-wizard-summary');
      await confirm(page);
      await expect(page.locator('[data-provider]').filter({ hasText: 'Google Workspace' })).toBeVisible();
      await page.locator('[data-save-bar-save]').click();
      await page.locator('[data-reauth-dialog] input[type="password"]').fill('heslo');
      await page.locator('[data-reauth-dialog] [data-confirm-reauth]').click();
      await expect(page.locator('[data-oidc-restart]')).toBeVisible();
      const saved = model.calls.find((call) => call.method === 'PUT' && call.path === '/api/admin/modules/oidc')?.body as { providers: { displayName: string }[] };
      expect(saved.providers.map((provider) => provider.displayName)).toEqual(['JetBrains Hub', 'Google Workspace']);

      await go(page, '#/settings/general/modules/youtrack');
      await expect(heading(page, 'YouTrack')).toBeVisible();
      await shot('34-youtrack');
      await go(page, '#/settings/general/modules/wiki-js-import');
      await expect(heading(page, 'Import Wiki.js')).toBeVisible();
      await shot('35-wiki-import');

      await go(page, '#/settings/general/backups');
      await expect(page.locator('[data-backup-history]')).toContainText('Sdílená složka neodpověděla.');
      await shot('36-backups');
      await page.locator('[data-backup-interval]').getByRole('radio', { name: /Vlastní/ }).click();
      await page.locator('[data-backup-custom-minutes]').fill('240');
      await shot('37-backups-dirty');
      await page.locator('[data-save-bar-save]').click();
      await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo');
      expect(model.calls.find((call) => call.method === 'PUT' && call.path.startsWith('/api/admin/backups'))?.body).toMatchObject({ intervalMinutes: 240 });
      await page.locator('[data-backup-run]').click();
      await expect(page.locator('[data-backup-download="r4"]')).toBeVisible();
      // Git is a tab of the same page, and both ways stay on the overview above it.
      await page.locator('a[data-tab="git"]').click();
      await expect(page).toHaveURL(/#\/settings\/general\/backups\/git$/);
      await expect(page.locator('[data-git-public-key]')).toBeVisible();
      await expect(page.locator('[data-git-status]')).toHaveAttribute('data-state', 'ok');
      await shot('38-backups-git');
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport(combo.device).width);
    });

    test('profile in the settings shell', async ({ page }) => {
      const shot = shooter(page, combo);
      const model = await open(page, combo, '#/profile');
      // The bare old address lands on its first section.
      await expect(page).toHaveURL(/#\/profile\/account$/);
      // With the sections in the application rail the shell heading is for assistive technology only.
      await expect(page.getByRole('heading', { level: 1, name: 'Profil' })).toBeAttached();
      await shot('40-profile-account');
      await page.locator('[data-profile-name]').fill('Marek M.');
      const account = page.locator('[data-profile-section="account"]');
      await account.locator('[data-save-bar-save]').click();
      // Saved: the bar leaves (the profile has no "Uloženo" state) and the form keeps the saved name.
      await expect(account.getByRole('region', { name: 'Uložení změn' })).toHaveCount(0);
      await expect(page.locator('[data-profile-name]')).toHaveValue('Marek M.');
      expect(model.calls.find((call) => call.method === 'PUT' && call.path === '/api/profile/account')?.body).toMatchObject({ displayName: 'Marek M.', expectedVersion: 'acc1' });
      for (const [section, name] of [['security', '41-profile-security'], ['visits', '42-profile-visits'], ['activity', '43-profile-activity'], ['api', '45-profile-api']] as const) {
        await go(page, `#/profile/${section}`);
        await expect(page.locator(`[data-profile-section="${section}"]`)).toBeVisible();
        await shot(name);
      }
      await go(page, '#/profile/appearance');
      await page.locator('[data-appearance="accent"]').getByRole('radio', { name: /Tyrkysová/ }).click();
      await expect(page.locator('html')).toHaveAttribute('data-accent', 'ocean');
      await shot('44-profile-appearance');
      // Leaving a dirty page asks, never window.confirm; Zahodit restores the saved look. Vzhled is the second half of
      // Účet a vzhled (board 12), so the way out is another section.
      page.on('dialog', (dialog) => { throw new Error(`Native dialog: ${dialog.message()}`); });
      if (combo.device === 'mobile') {
        await page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true }).click();
        await page.locator('#atlas-settings-rail').getByRole('link', { name: 'Zabezpečení' }).click();
      } else await page.locator('.settings-sidebar-rail [data-path="/profile/security"]').click();
      const leave = page.getByRole('dialog', { name: 'Neuložené změny' });
      await expect(leave).toBeVisible();
      await leave.getByRole('button', { name: 'Zahodit' }).click();
      await expect(page).toHaveURL(/#\/profile\/security$/);
      await expect(page.locator('html')).toHaveAttribute('data-accent', 'blue');
    });
  });
}
