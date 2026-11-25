import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { startEditing, PAGE_TEXT } from './editing';

// AI of Atlas 2.0, phase 2 (plan §8, scenarios 1-7), against the published Atlas and the fake OpenAI-compatible model server of
// tests/browser/fake-ai (no model is ever called). The scenarios share one wiki and run in this order; scenario 1 must come
// before AI is configured, so run the file as a whole (e2e-podman.ps1 -Batch ai does).
//
//   ATLAS_E2E_FAKE_AI_URL      the address Atlas gets in the embeddings profile (default http://fake-ai:1234/v1, the pod's alias)
//   ATLAS_E2E_FAKE_AI_CONTROL  where this test steers the fake server (default http://localhost:1234)
//
// Scenarios 1, 3 and 4 run with and without reduced motion; the outage scenarios once, under the project's motion setting.
// Selectors follow the merged phase-2 web code: W1 capabilities (useAiCapabilities), W2 SemanticBadge and ResultPassage in the
// omnisearch, W3 PageConnections ("Podobné tématem"), W6 Administrace -> AI (AiModulePage and its Index StepFlow).

test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 });
test.describe.configure({ mode: 'serial' });

const password = process.env.ATLAS_E2E_PASSWORD ?? '';
// The config's baseURL; browser.newContext() does not inherit it, and beforeAll cannot use the test-scoped baseURL fixture.
const baseURL = process.env.ATLAS_URL ?? 'http://127.0.0.1:5219';
const fakeUrl = process.env.ATLAS_E2E_FAKE_AI_URL ?? 'http://fake-ai:1234/v1';
const control = process.env.ATLAS_E2E_FAKE_AI_CONTROL ?? 'http://localhost:1234';
const base = '#/settings/general/modules/ai';
const stamp = Date.now();
const concept = 'backup restore database disaster recovery';
const paraphrase = 'kudy ven z katastrofy';
// Page of the meaning scenario: its passage shares no word with the paraphrase; the fake maps both texts to one concept.
const passageMarker = 'záchrany dat po výpadku';
const recoveryTitle = `Havarijní obnova provozu ${stamp}`;
const exactTitle = `Zálohování databáze ${stamp}`;
const siblingBody = 'Vedoucí týmu pravidelně kontroluje plán služeb, předává provozní poznámky a po každé směně uzavírá denní hlášení. '.repeat(3);

type Motion = 'no-preference' | 'reduce';
type Seeded = { id: string; title: string };
const seeded: { space?: string; recovery?: Seeded; exact?: Seeded; first?: Seeded; second?: Seeded } = {};
let baselinePublishMs = Infinity;

type Block = { kind: 'h2' | 'p'; text: string };
const richDocument = (blocks: Block[]) => JSON.stringify({
  format: 'atlas-rich', version: 1, plugins: [{ id: 'heading-anchors', version: 1 }],
  document: { type: 'doc', content: blocks.map((block, index) => block.kind === 'h2'
    ? { type: 'heading', attrs: { level: 2, headingId: 'section-' + index }, content: [{ type: 'text', text: block.text }] }
    : { type: 'paragraph', content: [{ type: 'text', text: block.text }] }) },
});
const markdownOf = (blocks: Block[]) => blocks.map(block => (block.kind === 'h2' ? `## ${block.text}` : block.text)).join('\n\n');

async function fake(path: string, body?: unknown) {
  const response = await fetch(control + path, body === undefined ? undefined : { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  expect(response.status, `fake-ai ${path}`).toBe(200);
  return response.json() as Promise<Record<string, any>>;
}
const fakeRequests = () => fake('/__requests') as Promise<{ total: number; embeddingInputs: number }>;

async function secret(page: Page, value: string) {
  // Typed through the input event: a password never goes through the keyboard log of a failing run.
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, text) => {
    (element as HTMLInputElement).value = text;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, value);
}

async function signIn(browser: Browser, motion: Motion, user = 'browser-admin', pass = password) {
  const context = await browser.newContext({ baseURL, reducedMotion: motion, viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(user);
  await secret(page, pass);
  const capabilitiesReady = page.waitForResponse(response => new URL(response.url()).pathname === '/api/ai/capabilities');
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  // A following reload must not destroy the still-reading initial capability response.
  await (await capabilitiesReady).json();
  return { context, page };
}

async function mutate(context: BrowserContext, path: string, data: unknown, method: 'post' | 'put' = 'post') {
  const csrf = await (await context.request.get('/api/auth/csrf')).json();
  return context.request[method](path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } });
}
async function getJson(context: BrowserContext, path: string) {
  const response = await context.request.get(path);
  expect(response.status(), path).toBe(200);
  return response.json();
}

async function reauthenticateSecurity(context: BrowserContext, page: Page) {
  const challengeResponse = await mutate(context, '/api/security/reauth', { operation: 'security' });
  expect(challengeResponse.status(), 'start security reauthentication').toBe(200);
  const challenge = await challengeResponse.json() as { id: string; method: string };
  expect(challenge.method).toBe('password');
  const csrf = await getJson(context, '/api/auth/csrf') as { token: string };
  // Keep the password out of request diagnostics in a failing Playwright report.
  const verified = await page.evaluate(async ({ id, token, password }) => {
    const response = await fetch(`/api/security/reauth/${id}/password`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
      body: JSON.stringify({ password }),
    });
    return { status: response.status, state: (await response.json()).state };
  }, { id: challenge.id, token: csrf.token, password });
  expect(verified).toEqual({ status: 200, state: 'verified' });
}

/** Creates and publishes a page; returns it and how long the publication took (the API call, with the index task queued). */
async function publish(context: BrowserContext, spaceId: string, title: string, blocks: Block[]) {
  const created = await mutate(context, '/api/pages', { spaceId, parentId: null, title, contentJson: richDocument(blocks), markdown: markdownOf(blocks) });
  expect(created.status()).toBe(200);
  const pageView = await created.json();
  const started = Date.now();
  const published = await mutate(context, `/api/pages/${pageView.id}/publish`, { expectedVersion: pageView.version ?? 1 });
  const took = Date.now() - started;
  expect(published.status()).toBe(200);
  return { page: { id: pageView.id as string, title }, took };
}

/** Names of every data-ai-* attribute in the DOM. */
const aiAttributes = (page: Page) => page.evaluate(() => {
  const names = new Set<string>();
  for (const element of document.querySelectorAll('*')) for (const name of element.getAttributeNames()) if (name.startsWith('data-ai-')) names.add(name);
  return [...names];
});

async function openSearch(page: Page, query: string) {
  // A document navigation completes before Vue mounts its global keyboard handler.
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true });
  const field = dialog.getByRole('combobox', { name: /^Hledat ve wiki – / });
  await expect(field).toBeVisible();
  await field.fill(query);
  const list = dialog.getByRole('listbox', { name: 'Výsledky hledání', exact: true });
  await expect(list).toHaveAttribute('aria-busy', 'false');
  return { dialog, field, list };
}

test.describe('AI search, related pages and outages against a fake model server', () => {
  test.beforeAll(async ({ browser }) => {
    test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires the isolated initialized acceptance wiki.');
    await fake('/__reset', {});
    const { context, page } = await signIn(browser, 'no-preference');
    try {
      const space = await (await mutate(context, '/api/spaces', { name: `AI scénáře ${stamp}`, icon: 'book-open', color: 'blue' })).json();
      seeded.space = space.id;
      // Content before AI exists: scenario 2's index build embeds it. The passage mapping must be in place by then.
      await fake('/__meaning', { rules: [
        { contains: passageMarker, as: concept },
        { contains: paraphrase, as: concept },
      ] });
      const recovery = await publish(context, space.id, recoveryTitle, [
        { kind: 'h2', text: 'Záchrana dat' },
        { kind: 'p', text: `Postup ${passageMarker}: obnovte poslední ověřenou kopii a zkontrolujte, že služba znovu odpovídá.` },
      ]);
      seeded.recovery = recovery.page;
      seeded.exact = (await publish(context, space.id, exactTitle, [{ kind: 'p', text: 'Plán nočních kopií a jejich uchovávání.' }])).page;
      seeded.first = (await publish(context, space.id, `Denní hlášení směny ${stamp}`, [{ kind: 'h2', text: 'Předání' }, { kind: 'p', text: siblingBody }])).page;
      seeded.second = (await publish(context, space.id, `Provozní poznámky směny ${stamp}`, [{ kind: 'h2', text: 'Předání' }, { kind: 'p', text: siblingBody }])).page;
      expect(page.url()).toBeTruthy();
    } finally {
      await context.close();
    }
  });

  test.afterAll(async () => {
    // Whatever failed, leave the fake healthy for the next batch run in the same pod.
    try { await fake('/__reset', {}); } catch { /* the fake server may be gone with its pod */ }
  });

  for (const motion of ['no-preference', 'reduce'] as const) {
    test(`1 without AI configured nothing of AI is shown or requested (${motion})`, async ({ browser }) => {
      const { context, page } = await signIn(browser, motion);
      try {
        const requested: string[] = [];
        const capabilities: Record<string, unknown>[] = [];
        page.on('request', request => requested.push(new URL(request.url()).pathname));
        page.on('response', async response => {
          if (new URL(response.url()).pathname === '/api/ai/capabilities') capabilities.push(await response.json().catch(() => ({ unreadable: true })));
        });
        const before = await fakeRequests();
        // Listening is on: reload so that the capabilities are fetched again in view of the listeners.
        await page.reload();
        await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
        await expect.poll(() => capabilities.length).toBeGreaterThan(0);

        // Reading a page, the footer, the omnisearch, the creation flow and the profile.
        await page.goto(`/#/pages/${seeded.first!.id}`);
        await expect(page.locator(PAGE_TEXT)).toBeVisible();
        expect(await aiAttributes(page)).toEqual([]);
        await expect(page.locator('.connections-tags a[title^="Podobné tématem"]')).toHaveCount(0);

        const { dialog, list } = await openSearch(page, 'směny');
        // The results, not the <option>s of the Prostor chip's select in the field row (2.5).
        await expect(list.getByRole('option').first()).toBeVisible();
        await expect(page.getByText('Významová shoda')).toHaveCount(0);
        await expect(page.locator('[data-semantic-badge], [data-semantic-source], [data-search-passage]')).toHaveCount(0);
        expect(await aiAttributes(page)).toEqual([]);
        // The first Esc empties the field, the next closes the search (P3).
        await page.keyboard.press('Escape');
        await page.keyboard.press('Escape');
        await expect(dialog).toBeHidden();

        // With the Boards module Nová stránka is the header's Nový menu: Stránka | Tabule.
        await page.locator('.workspace-bar').getByRole('button', { name: 'Nový obsah v prostoru', exact: true }).click();
        await page.getByRole('menuitem', { name: 'Stránka', exact: true }).click();
        await expect(page.locator('.breadcrumb-title')).toHaveText('Nová stránka');
        expect(await aiAttributes(page)).toEqual([]);
        await page.keyboard.press('Escape');

        await page.goto('/#/profile/account');
        // The profile's h1 serves assistive technology only while the rail carries the surface (2.5).
        await expect(page.getByRole('region', { name: 'Profil', exact: true })).toBeVisible();
        await expect(page.getByText('AI', { exact: true })).toHaveCount(0);
        expect(await aiAttributes(page)).toEqual([]);

        // The wire: only the capabilities, all false; nothing from the assist or admin AI APIs; the model server saw nothing.
        await expect.poll(() => capabilities.length).toBeGreaterThan(0);
        for (const answer of capabilities) {
          expect(Object.keys(answer).length).toBeGreaterThan(0);
          for (const [name, value] of Object.entries(answer)) expect(value, `capability ${name}`).toBe(false);
        }
        expect(requested.filter(path => path.startsWith('/api/assist') || path.startsWith('/api/admin/ai'))).toEqual([]);
        expect(await fakeRequests()).toMatchObject({ total: before.total });
      } finally {
        await context.close();
      }
    });
  }

  test('1b publication without AI sets the baseline for scenario 5', async ({ browser }) => {
    const { context } = await signIn(browser, 'no-preference');
    try {
      const times: number[] = [];
      for (let index = 0; index < 3; index++) {
        times.push((await publish(context, seeded.space!, `Základní měření ${index} ${stamp}`, [{ kind: 'p', text: `Měření publikace bez AI číslo ${index}.` }])).took);
      }
      baselinePublishMs = Math.min(...times);
      test.info().annotations.push({ type: 'baseline publish (ms)', description: times.join(', ') });
    } finally {
      await context.close();
    }
  });

  test('2 the wiki admin sets up embeddings on fake-ai, tests them, turns AI on and builds the index through the StepFlow', async ({ browser }) => {
    test.setTimeout(240_000);
    const { context, page } = await signIn(browser, 'no-preference');
    try {
      // Setup saves the master switch, then folds that completed step and reveals embeddings.
      await page.goto(`/${base}`);
      const power = page.locator('[data-ai-setup-step="power"]');
      await expect(power.locator('[data-ai-overview]')).toBeVisible();
      await expect(power.locator('[data-ai-switch-state]')).toHaveText('Vypnuto');
      await power.locator('[data-ai-switch]').click();
      await power.locator('[data-save-bar-save]').click();
      await expect(power).toContainText('Zapnuto pro wiki');
      await expect(page.locator('[data-ai-setup-step="embeddings"]')).toBeVisible();
      expect(await getJson(context, '/api/admin/ai/switch')).toMatchObject({ enabled: true });

      // Personal AI is denied by default, including for wiki administrators. Grant the two scenario accounts
      // through one pilot group; the reader outage check must exercise an outage, not a missing permission.
      const users = await getJson(context, '/api/admin/users') as { id: string; username: string }[];
      const groupResponse = await mutate(context, '/api/admin/groups', { name: `AI pilot ${stamp}` });
      expect(groupResponse.ok()).toBe(true);
      const group = await groupResponse.json() as { id: string };
      await reauthenticateSecurity(context, page);
      for (const username of ['browser-admin', 'browser-reader']) {
        const user = users.find(item => item.username === username);
        expect(user, `seeded account ${username}`).toBeDefined();
        const membership = await mutate(context, `/api/admin/groups/${group.id}/members/${user!.id}`, { isMember: true, expectedVersion: null }, 'put');
        expect(membership.ok(), `membership ${username}: HTTP ${membership.status()}`).toBe(true);
      }
      const access = await getJson(context, '/api/admin/ai/access') as { version: string | null; groups: Record<string, unknown>[] };
      const granted = await mutate(context, '/api/admin/ai/access', { expectedVersion: access.version, groups: [...access.groups.map(({ groupName: _, ...entry }) => entry), { groupId: group.id, searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null }] }, 'put');
      expect(granted.ok()).toBe(true);

      // Embeddingy: the provider first, then its fields; a 3 s timeout so the outage scenario is quick.
      await page.goto(`/${base}/embeddings`);
      await expect(page.locator('[data-ai-url]')).toHaveCount(0);
      await page.getByRole('radio', { name: /Firemní LM Studio/ }).click();
      await page.locator('[data-ai-url]').fill(fakeUrl);
      await page.locator('[data-ai-models-load]').click();
      await page.getByRole('radio', { name: 'text-embedding-bge-m3', exact: true }).click();
      await expect(page.locator('[data-ai-model]')).toHaveValue('text-embedding-bge-m3');
      await page.locator('[data-ai-profile="embeddings"] .settings-section-header.is-toggle').click();
      await page.locator('[data-ai-timeout]').fill('3');
      await page.locator('[data-ai-enabled]').click();
      await page.locator('[data-save-bar-save]').click();
      await expect(page.locator('[data-ai-profile-state]')).toHaveText('Zapnuto');
      await expect(page.locator('[data-ai-test]')).toBeEnabled();
      await page.locator('[data-ai-test]').click();
      await expect(page.locator('[data-ai-test-result]')).toContainText('Spojení funguje');
      await expect(page.locator('[data-ai-test-result]')).toContainText(/1\s*024 rozměrů/);
      expect((await fakeRequests()).embeddingInputs).toBeGreaterThan(0);

      // Index: Profil -> Stavba -> Ověření -> Aktivace.
      await page.goto(`/${base}/index`);
      await page.locator('[data-ai-build-action]').click();
      const flow = page.locator('[data-ai-index-flow="build"]');
      await expect(flow).toBeVisible();
      await expect(flow.locator('[data-step-chip]')).toHaveText([/Profil/, /Stavba/, /Ověření/, /Aktivace/]);
      await expect(flow.locator('[data-step="profile"]')).toBeVisible();
      await page.locator('[data-flow-next]').click();
      await expect(flow.locator('[data-step="build"]')).toBeVisible();
      // The worker needs the probe to mark the profile available, then a few 5 s rounds; the next button waits for "Hotovo".
      await expect(page.locator('[data-flow-next]')).toBeEnabled({ timeout: 150_000 });
      await page.locator('[data-flow-next]').click();
      await expect(flow.locator('[data-step="verify"]')).toBeVisible();
      await page.locator('[data-ai-verify]').click();
      await expect(page.locator('[data-ai-verification]')).toContainText('Index jde aktivovat');
      await expect(page.locator('[data-flow-next]')).toBeEnabled();
      await page.locator('[data-flow-next]').click();
      await expect(flow.locator('[data-step="activate"]')).toBeVisible();
      await expect(page.locator('[data-ai-activate-impact]')).toContainText('Starý index se smaže po 24 hodinách');
      await page.locator('[data-flow-next]').click();
      await expect(page.locator('[data-result-banner]')).toContainText('Nový index je aktivní');
      await expect(page.locator('[data-ai-generation-state="Active"]')).toBeVisible();

      // Nothing the admin typed or the server answered leaks: no key field was ever filled, and the capabilities are now true.
      const capabilities = await getJson(context, '/api/ai/capabilities');
      expect(capabilities).toMatchObject({ semanticSearch: true, related: true });
    } finally {
      await context.close();
    }
  });

  for (const motion of ['no-preference', 'reduce'] as const) {
    test(`3 a paraphrase finds the page by meaning, with the label and the passage; the exact title stays first (${motion})`, async ({ browser }) => {
      const { context, page } = await signIn(browser, motion);
      try {
        await page.goto('/');
        const { dialog, list } = await openSearch(page, paraphrase);
        const row = list.getByRole('option').filter({ hasText: recoveryTitle });
        await expect(row).toBeVisible();
        await expect(row.locator('[data-semantic-badge]')).toContainText('Významová shoda');
        await expect(row.locator('[data-search-passage]')).toContainText(passageMarker);
        await expect(dialog.locator('[data-semantic-source]')).toContainText('Významové hledání');
        await row.click();
        await expect(page).toHaveURL(new RegExp(`#/pages/${seeded.recovery!.id}\\?heading=`));
        await expect(page.locator(PAGE_TEXT)).toBeVisible();
        await expect(page.getByRole('dialog', { name: 'Hledat ve wiki', exact: true })).not.toBeVisible();

        // The exact title is the first result and not a meaning-only one.
        const exact = await openSearch(page, exactTitle);
        await expect(exact.list.getByRole('option').first()).toContainText(exactTitle);
        await expect(exact.list.getByRole('option').first().locator('[data-semantic-badge]')).toHaveCount(0);
      } finally {
        await context.close();
      }
    });
  }

  for (const motion of ['no-preference', 'reduce'] as const) {
    test(`4 the reading footer offers Podobné tématem pages (${motion})`, async ({ browser }) => {
      const { context, page } = await signIn(browser, motion);
      try {
        // Related pages are precomputed from the index: the second page is listed under the first and the reverse.
        await page.goto(`/#/pages/${seeded.first!.id}`);
        await expect(page.locator(PAGE_TEXT)).toBeVisible();
        const related = page.locator('.connections-tags a[title^="Podobné tématem"]').filter({ hasText: seeded.second!.title });
        await expect(related).toBeVisible();
        await expect(related).toContainText(seeded.second!.title);
        await related.click();
        await expect(page).toHaveURL(new RegExp(`#/pages/${seeded.second!.id}`));
        await expect(page.locator('.connections-tags a[title^="Podobné tématem"]').filter({ hasText: seeded.first!.title })).toBeVisible();
      } finally {
        await context.close();
      }
    });
  }

  test('5, 7, 6 an outage removes the AI surface without a banner or a reload, the admin sees why, and it comes back by itself', async ({ browser }) => {
    test.setTimeout(480_000);
    const { context, page } = await signIn(browser, 'no-preference');
    const reader = await signIn(browser, 'no-preference', 'browser-reader', process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`);
    try {
      await page.goto(`/#/pages/${seeded.first!.id}`);
      await expect(page.locator('.connections-tags a[title^="Podobné tématem"]').filter({ hasText: seeded.second!.title })).toBeVisible();
      await page.evaluate(() => { (window as unknown as { __atlasNotReloaded: boolean }).__atlasNotReloaded = true; });
      const overview = async () => (await getJson(context, '/api/admin/ai')) as { profiles: { kind: string; state: string; lastErrorCode: string | null }[]; index: { queue: { pending: number; failing: number } } | null };
      const embeddings = async () => (await overview()).profiles.find(profile => profile.kind === 'embeddings')!;

      await test.step('5 outage: two failures and the probe open the breaker (fake-ai: timeout)', async () => {
        await fake('/__mode', { mode: 'timeout' });
        // Publication never waits for the model: it takes what it takes without AI, while its index task fails in the background.
        const outage = await publish(context, seeded.space!, `Publikace za výpadku ${stamp}`, [{ kind: 'p', text: 'Obsah publikovaný, zatímco model neodpovídá.' }]);
        test.info().annotations.push({ type: 'publish during outage (ms)', description: `${outage.took}, baseline ${baselinePublishMs}` });
        expect(outage.took).toBeLessThanOrEqual(Math.max(baselinePublishMs * 3, baselinePublishMs + 1500));
        await expect.poll(async () => (await embeddings()).state, { timeout: 150_000, intervals: [3000] }).toBe('Unavailable');

        // The page that is open loses Podobné tématem by itself: the 60 s refresh of the capabilities, no reload.
        await expect(page.locator('.connections-tags a[title^="Podobné tématem"]')).toHaveCount(0, { timeout: 100_000 });
        expect(await page.evaluate(() => (window as unknown as { __atlasNotReloaded?: boolean }).__atlasNotReloaded)).toBe(true);
        await expect(page.getByRole('alert')).toHaveCount(0);
        expect(await aiAttributes(page)).toEqual([]);

        // The omnisearch answers lexically: no label, no AI line, no passage, and no error.
        const { dialog, list } = await openSearch(page, 'Denní hlášení');
        await expect(list.getByRole('option').filter({ hasText: seeded.first!.title })).toBeVisible();
        await expect(dialog.locator('[data-semantic-source], [data-semantic-badge], [data-search-passage]')).toHaveCount(0);
        const meaningOnly = await openSearch(page, paraphrase);
        await expect(meaningOnly.list.getByRole('option').filter({ hasText: recoveryTitle })).toHaveCount(0);
        await expect(page.getByText('Významová shoda')).toHaveCount(0);
        await page.keyboard.press('Escape');
      });

      await test.step('7 Přehled shows the state, the time and the code; an ordinary user sees none of it', async () => {
        const admin = await context.newPage();
        await admin.goto(`/${base}/overview`);
        const tile = admin.locator('[data-ai-tile="embeddings"]');
        await expect(tile).toContainText('Nedostupné od', { timeout: 30_000 });
        await expect(tile.locator('[data-ai-tile-error]')).toContainText('timeout');
        await expect(tile.locator('[data-ai-tile-error]')).toContainText('Server modelu neodpověděl včas.');

        // What the model server says about its failure never reaches the screen, only the code does.
        await fake('/__mode', { mode: 'error' });
        await expect(tile.locator('[data-ai-tile-error]')).toContainText('http_5xx', { timeout: 120_000 });
        await expect(admin.locator('body')).not.toContainText('FAKE-UPSTREAM-DETAIL');
        await admin.close();

        const requested: string[] = [];
        reader.page.on('request', request => requested.push(new URL(request.url()).pathname));
        await reader.page.goto('/#/profile/account');
        await expect(reader.page.getByRole('region', { name: 'Profil', exact: true })).toBeVisible();
        await expect(reader.page.locator('body')).not.toContainText(/Nedostupné|AI/);
        expect(await aiAttributes(reader.page)).toEqual([]);
        expect((await reader.context.request.get('/api/admin/ai')).status()).toBeGreaterThanOrEqual(400);
        expect(requested.filter(path => path.startsWith('/api/admin/ai'))).toEqual([]);
        for (const value of Object.values(await getJson(reader.context, '/api/ai/capabilities'))) expect(value).toBe(false);
      });

      await test.step('6 recovery: the features return without a reload and the index queue drains', async () => {
        await fake('/__mode', { mode: 'healthy' });
        const recoveredAt = Date.now();
        await expect.poll(async () => (await embeddings()).state, { timeout: 120_000, intervals: [3000] }).toBe('Available');
        await expect(page.locator('.connections-tags a[title^="Podobné tématem"]').filter({ hasText: seeded.second!.title })).toBeVisible({ timeout: 120_000 });
        test.info().annotations.push({ type: 'recovery (s)', description: String(Math.round((Date.now() - recoveredAt) / 1000)) });
        expect(await page.evaluate(() => (window as unknown as { __atlasNotReloaded?: boolean }).__atlasNotReloaded)).toBe(true);
        await expect.poll(async () => (await overview()).index?.queue.pending ?? -1, { timeout: 180_000, intervals: [5000] }).toBe(0);
        const settled = await openSearch(page, paraphrase);
        await expect(settled.list.getByRole('option').filter({ hasText: recoveryTitle })).toBeVisible();
      });
    } finally {
      await reader.context.close();
      await context.close();
    }
  });

  test('8 chat profile enables answers without changing the active embedding index', async ({ browser }) => {
    const { context } = await signIn(browser, 'no-preference');
    try {
      const profile = await getJson(context, '/api/admin/ai/profiles/chat');
      await mutate(context, '/api/admin/ai/profiles/chat', {
        enabled: true, provider: 'openai-compatible', location: 'company', baseUrl: fakeUrl,
        model: 'google/gemma-4-26b-a4b', timeoutSeconds: 30, maxConcurrency: 4,
        nativeJsonSchema: true, clearApiKey: false, expectedVersion: profile.version,
      }, 'put');
      const tested = await (await mutate(context, '/api/admin/ai/profiles/chat/test', {})).json();
      expect(tested).toHaveProperty('ok', true);
      await expect.poll(async () => (await getJson(context, '/api/ai/capabilities')).answer, { timeout: 30_000 }).toBe(true);
    } finally { await context.close(); }
  });

  async function startHeldAnswer(page: Page) {
    await fake('/__mode', { answer: 'hold', chat: 'healthy' });
    const before = ((await fake('/__requests')).byPath['/v1/chat/completions'] ?? 0) as number;
    // The paraphrase, not the passage's own words: title words rank first (b974b3b2), so „dat“ and „výpadku“ would put the pages
    // Zálohování databáze and Publikace za výpadku before the passage; by meaning alone the recovery passage is the one source.
    const { dialog } = await openSearch(page, `${paraphrase}?`);
    await expect(dialog.locator('[data-search-ask]')).toBeVisible();
    await page.keyboard.press('Control+Enter');
    const answer = dialog.locator('[data-ai-answer]');
    await expect(answer).toHaveAttribute('data-state', 'preparing');
    await expect(answer.locator('[data-source-chip]').first()).toBeVisible();
    await expect(answer.locator('[data-ai-body]')).toHaveCount(0);
    await expect.poll(async () => ((await fake('/__requests')).byPath['/v1/chat/completions'] ?? 0) as number).toBeGreaterThan(before);
    return { dialog, answer };
  }

  for (const motion of ['no-preference', 'reduce'] as const) {
    test(`9 sources precede the answer, citations work and the search conversation continues (${motion})`, async ({ browser }) => {
      const { context, page } = await signIn(browser, motion);
      try {
        const { dialog, answer } = await startHeldAnswer(page);
        await fake('/__release', {});
        await expect(answer).toHaveAttribute('data-state', 'done');
        await expect(answer.locator('[data-ai-sentence]').first()).toBeVisible();
        const citation = answer.locator('[data-cite]').first();
        await citation.focus();
        await expect(citation).toBeFocused();
        await citation.click();
        const preview = page.locator('[data-ai-citation-preview]');
        await expect(preview).toBeVisible();
        await expect(preview).toContainText(recoveryTitle);
        await expect.poll(() => preview.evaluate(element => element.contains(document.activeElement))).toBe(true);
        await page.keyboard.press('Escape');
        await expect(preview).not.toBeVisible();
        await expect(dialog).toBeVisible();
        await expect(citation).toBeFocused();
        await answer.locator('[data-ai-feedback-up]').click();
        await expect(answer.locator('[data-ai-feedback-up]')).toHaveAttribute('aria-pressed', 'true');
        await dialog.locator('[data-search-continue]').click();
        await expect(dialog).not.toBeVisible();
        const panel = page.locator('[data-assistant-panel]');
        await expect(panel.locator('[data-ai-answer][data-state="done"]')).toBeVisible();
        await expect(panel.locator('[data-assistant-question]')).toHaveCount(1);
        await fake('/__mode', { answer: 'answerable' });
        // The native modal releases focus only after its fold completes.
        await expect(panel.getByRole('textbox', { name: 'Otázka pro asistenta' })).toBeFocused();
        await panel.getByRole('textbox', { name: 'Otázka pro asistenta' }).fill('A jak ověřit, že služba znovu odpovídá?');
        await panel.getByRole('textbox', { name: 'Otázka pro asistenta' }).press('Enter');
        await expect(panel.locator('[data-assistant-question]')).toHaveCount(2);
        await expect(panel.locator('[data-ai-answer]').last()).toHaveAttribute('data-state', 'done');
      } finally {
        await fake('/__mode', { answer: 'answerable', chat: 'healthy' });
        await fake('/__release', {});
        await context.close();
      }
    });
  }

  test('10 invalid citations are not rendered and unanswerable output offers ordinary search', async ({ browser }) => {
    const { context, page } = await signIn(browser, 'reduce');
    try {
      for (const mode of ['invalid-citation', 'unanswerable']) {
        await fake('/__mode', { answer: mode });
        const { dialog } = await openSearch(page, `Jak postupovat při ${passageMarker}?`);
        await dialog.locator('[data-search-ask]').click();
        const answer = dialog.locator('[data-ai-answer]');
        await expect(answer).toHaveAttribute('data-state', 'no_sources');
        await expect(answer.locator('[data-cite], [data-ai-sentence]')).toHaveCount(0);
        await answer.locator('[data-ai-try-other]').click();
        await expect(dialog.getByRole('combobox', { name: /^Hledat ve wiki – / })).toBeFocused();
        await page.keyboard.press('Escape');
      }
    } finally {
      await fake('/__mode', { answer: 'answerable' });
      await context.close();
    }
  });

  test('11 a chat outage during generation leaves a neutral answer state and lexical search usable', async ({ browser }) => {
    const { context, page } = await signIn(browser, 'no-preference');
    try {
      const { dialog, answer } = await startHeldAnswer(page);
      await fake('/__mode', { chat: 'error' });
      await fake('/__release', {});
      await expect(answer).toHaveAttribute('data-state', 'unavailable');
      await expect(answer.locator('[data-ai-unavailable]')).toHaveText('Tahle funkce teď není k dispozici.');
      await expect(dialog).not.toContainText('FAKE-UPSTREAM-DETAIL');
      const results = await openSearch(page, recoveryTitle);
      await expect(results.list.getByRole('option').first()).toContainText(recoveryTitle);
    } finally {
      await fake('/__mode', { chat: 'healthy', answer: 'answerable' });
      await fake('/__release', {});
      await context.close();
    }
  });

  test('12 withdrawing the source space while generating withholds the answer and history', async ({ browser }) => {
    const { context, page } = await signIn(browser, 'reduce');
    let changed = false;
    try {
      // The preceding scenario deliberately fails chat. Restore through the normal admin test so this ACL scenario
      // does not spend its own timeout waiting for the periodic health probe; automatic recovery is covered above.
      const restored = await (await mutate(context, '/api/admin/ai/profiles/chat/test', {})).json();
      expect(restored).toHaveProperty('ok', true);
      await expect.poll(async () => (await getJson(context, '/api/ai/capabilities')).answer).toBe(true);
      await page.reload();
      const preparedResponse = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/ai/conversations');
      const { answer } = await startHeldAnswer(page);
      const prepared = await (await preparedResponse).json();
      const current = await getJson(context, `/api/spaces/${seeded.space}/ai-settings`);
      await mutate(context, `/api/spaces/${seeded.space}/ai-settings`, { included: false, expectedVersion: current.version }, 'put');
      changed = true;
      await fake('/__release', {});
      await expect(answer).toHaveAttribute('data-state', 'withheld');
      await expect(answer.locator('[data-ai-body], [data-source-chip], [data-cite]')).toHaveCount(0);
      const history = await getJson(context, `/api/ai/conversations/${prepared.conversationId}`);
      expect(history.available).toBe(false);
      expect(JSON.stringify(history)).not.toContain(passageMarker);
      expect(JSON.stringify(history)).not.toContain(recoveryTitle);
    } finally {
      if (changed) {
        const current = await getJson(context, `/api/spaces/${seeded.space}/ai-settings`);
        await mutate(context, `/api/spaces/${seeded.space}/ai-settings`, { included: true, expectedVersion: current.version }, 'put');
      }
      await fake('/__mode', { chat: 'healthy', answer: 'answerable' });
      await fake('/__release', {});
      await context.close();
    }
  });
  test('13 page summary keeps citations and shows the published page as bullets', async ({browser})=>{
    const {context,page}=await signIn(browser,'no-preference');
    try {
      const quick=await getJson(context,'/api/admin/ai/profiles/quick');
      const configured=await mutate(context,'/api/admin/ai/profiles/quick',{enabled:true,useChatProfile:true,provider:'openai-compatible',location:'company',baseUrl:fakeUrl,model:'google/gemma-4-26b-a4b',timeoutSeconds:30,maxConcurrency:4,nativeJsonSchema:true,clearApiKey:false,expectedVersion:quick.version},'put');
      expect(configured.status()).toBe(200);
      const features=await getJson(context,'/api/admin/ai/features');
      const enabled=new Set(['page.summary','publish.change-summary','publish.check','page.tag-suggest']);
      const updated=await mutate(context,'/api/admin/ai/features',{expectedVersion:features.version,items:features.items.map((item:any)=>({task:item.task,enabled:enabled.has(item.task)||item.enabled,cloudWorkingText:item.cloudWorkingText,...(item.limitable?{dailyLimit:item.dailyLimit??null}:{})}))},'put');
      expect(updated.status()).toBe(200);
      await expect.poll(async()=>(await getJson(context,'/api/ai/capabilities')).pageSummary).toBe(true);
      // The summary reads the page from the index. Scenario 12 re-included its space, which only queues the space's pages
      // for the index again (TR_SpaceAiSetting_IndexTask); under load the runner had not rebuilt them yet (no_sources).
      await expect.poll(async()=>(await getJson(context,'/api/admin/ai')).index?.queue.pending ?? -1,{timeout:30_000}).toBe(0);
      await page.goto('/#/pages/'+seeded.recovery!.id);
      await page.locator('[data-page-summary-start]').click();
      const summary=page.locator('[data-page-summary] [data-ai-answer]');
      await expect(summary).toHaveAttribute('data-state','done');
      await expect(summary.locator('ul [data-ai-sentence]')).toHaveCount(3);
      await expect(summary.locator('[data-cite]').first()).toBeVisible();
      await summary.getByRole('button',{name:'Zavřít shrnutí',exact:true}).click();
      await expect(summary).not.toBeVisible();
    } finally {await context.close();}
  });

  test('14 change description is a proposal until explicit use and publishes normally',async({browser})=>{
    const {context,page}=await signIn(browser,'reduce');
    try {
      await fake('/__mode',{chat:'healthy',answer:'answerable'});
      await page.goto('/#/pages/'+seeded.recovery!.id);
      await startEditing(page);
      const editor=page.locator('.document-editor-surface .ProseMirror[contenteditable="true"]').first();
      await expect(editor).toBeVisible();await editor.focus();await page.keyboard.press('Control+End'); // focus: the centre can be a gap between blocks, where the block inserter takes the click
      await page.keyboard.press('Enter');
      await page.keyboard.insertText('Při kontrole obnovy ověřte také dostupnost služby z mobilního zařízení.');
      await expect(page.locator('[data-page-save-state]')).toBeVisible();
      await page.getByRole('button',{name:'Publikovat s popisem změny…',exact:true}).click();
      const panel=page.getByRole('dialog',{name:'Před publikací',exact:true});const field=panel.locator('input[name="changeSummary"]');
      await field.fill('Ručně napsaný popis.');
      await panel.locator('[data-change-summary-suggest]').click();
      const proposal=panel.locator('.suggestion-chip');await expect(proposal.locator('[data-suggestion-apply]')).toBeVisible();
      await expect(field).toHaveValue('Ručně napsaný popis.');
      await proposal.locator('[data-suggestion-apply]').click();await expect(field).not.toHaveValue('Ručně napsaný popis.');
      const value=await field.inputValue();
      const published=page.waitForResponse(response=>new URL(response.url()).pathname.endsWith('/publish')&&response.request().method()==='POST');
      await panel.getByRole('button',{name:/^Publikovat(?: i tak)?$/,exact:true}).click();const response=await published;
      expect(response.status()).toBe(200);expect(response.request().postDataJSON().changeSummary).toBe(value.trim());
      await expect(panel).not.toBeVisible();
    } finally {await context.close();}
  });

  test('15 model tags are written only when the author applies the proposal',async({browser})=>{
    const {context,page}=await signIn(browser,'no-preference');
    try {
      const oldTags=await getJson(context,'/api/pages/'+seeded.exact!.id+'/tags');
      const seed=await mutate(context,'/api/pages/'+seeded.exact!.id+'/tags',{names:[...oldTags.tags.map((tag:any)=>tag.name),'Obnova provozu'],expectedTagIds:oldTags.tags.map((tag:any)=>tag.id)},'put');expect(seed.status()).toBe(200);
      await page.goto('/#/pages/'+seeded.recovery!.id);const before=await getJson(context,'/api/pages/'+seeded.recovery!.id+'/tags');
      await page.getByRole('button',{name:/^(Přidat|Upravit) tagy$/}).click();
      // The tag panel is an anchored popover named by its trigger (Reka: aria-labelledby), no longer the Tagy stránky dialog.
      const dialog=page.getByRole('dialog',{name:/^(Přidat|Upravit) tagy$/});await dialog.locator('[data-tag-suggest]').click();
      await expect(dialog.locator('[data-suggestion-apply]')).toBeVisible();
      const names=await dialog.locator('.tag-suggestion-value').allTextContents();expect(names.length).toBeGreaterThan(0);
      // The proposal alone writes nothing; the author's „Použít“ is the confirmation, and tags save as they go (P5), so no further button.
      expect((await getJson(context,'/api/pages/'+seeded.recovery!.id+'/tags')).tags).toEqual(before.tags);
      await dialog.locator('[data-suggestion-apply]').click();
      await expect.poll(async()=>(await getJson(context,'/api/pages/'+seeded.recovery!.id+'/tags')).tags.map((tag:any)=>tag.name)).toEqual(expect.arrayContaining(names.map(name=>name.replace(/^#/,''))));
      await page.keyboard.press('Escape');await expect(dialog).not.toBeVisible();
    } finally {await context.close();}
  });

});
