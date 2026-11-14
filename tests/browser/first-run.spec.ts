import { test, expect, type Locator, type Page } from '@playwright/test'
import { expectSaved, publishPage, PAGE_TEXT, LIVE_EDITOR } from './editing'

// This flow handles real installation and activation secrets. Failure traces
// capture request bodies/DOM and screenshots can expose a one-time token.
// Only the explicit screenshots below are permitted, after secrets are cleared.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 })

async function enterSecret(field: Locator, secret: string) {
  await expect(field).toBeVisible()
  // Avoid literal secret values in Playwright's fill() timeout call logs.
  await field.evaluate((element, value) => {
    const input = element as HTMLInputElement
    input.value = value
    input.dispatchEvent(new Event('input', { bubbles: true }))
    input.dispatchEvent(new Event('change', { bubbles: true }))
  }, secret)
}

async function readApiSpacesWithToken(page: Page, secret: string) {
  // Browser fetch deliberately omits the administrator session. Return only
  // public result metadata, never headers or a Playwright request call log.
  return page.evaluate(async token => {
    try {
      const response = await fetch('/api/v1/spaces', {
        credentials: 'omit', headers: { Authorization: `Bearer ${token}` },
      })
      const body = response.ok ? await response.json() : null
      return { status: response.status, hasExpectedSpace: Array.isArray(body?.items) && body.items.some((space: {name:string}) => space.name === 'Začínáme') }
    } catch { return { status: 0, hasExpectedSpace: false } }
  }, secret)
}

// Intentionally requires an explicit disposable environment; never resets or seeds an existing wiki.
test('first install → editor/search/theme → account activation → group Reader access', async ({ page, request }, info) => {
  test.setTimeout(180_000)
  test.skip(process.env.ATLAS_E2E_ALLOW_SETUP !== '1', 'Requires a disposable empty database and explicit ATLAS_E2E_ALLOW_SETUP=1.')
  const bootstrapToken = process.env.ATLAS_E2E_BOOTSTRAP_TOKEN!
  const password = process.env.ATLAS_E2E_PASSWORD!
  const readerPassword = process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`
  expect(bootstrapToken?.length).toBeGreaterThanOrEqual(32)
  expect(password?.length).toBeGreaterThanOrEqual(12)
  const state = await (await request.get('/api/setup/status')).json()
  expect(state.databaseReady).toBe(true)
  expect(state.requiresSetup, 'Run against a fresh disposable database, not an existing installation.').toBe(true)
  await page.goto('/')
  await page.getByLabel('Název wiki', { exact: true }).fill('Atlas browser acceptance')
  await page.getByRole('button', { name: 'Pokračovat' }).click()
  // The token hint is collapsed; it names the server log and, for a generated token, the file path once.
  await page.getByText('Kde najdu instalační token?', { exact: true }).click()
  await expect(page.getByText('Ve výpisu serveru při spuštění aplikace.', { exact: true })).toBeVisible()
  expect(typeof state.tokenSource.filePath).toBe('string')
  expect(state.tokenSource.filePath.length).toBeGreaterThan(0)
  await expect(page.locator('.recovery-path')).toContainText(state.tokenSource.filePath)
  expect((await page.locator('.setup-wizard').textContent())!.split(state.tokenSource.filePath).length - 1).toBe(1)
  await page.getByLabel('Vaše jméno', { exact: true }).fill('Test správce')
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin')
  await enterSecret(page.getByLabel(/^Heslo/), password)
  await enterSecret(page.getByLabel(/^Instalační token/), bootstrapToken)
  const installStarted = performance.now()
  const [installed] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === '/api/setup/complete' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Vytvořit wiki', exact: true }).click(),
  ])
  expect(installed.status()).toBe(200)
  await info.attach('installation-response-time.json', { body: JSON.stringify({ milliseconds: performance.now() - installStarted, scope: 'First setup POST on an empty local acceptance database, including initialization and password hashing' }), contentType: 'application/json' })
  await expect(page.getByRole('heading', { name: 'Připojte své nástroje' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Firemní přihlášení', exact: true })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Připojte své nástroje' })).toBeVisible()
  await page.getByRole('button', { name: 'Další možnost', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'YouTrack', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Další možnost', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Import Wiki.js', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Další možnost', exact: true }).click()
  await expect(page.getByText(/^Krok 4 z 4/)).toBeVisible()
  // The last step's footer button; the wizard header offers Otevřít wiki at any step too.
  await page.locator('footer').getByRole('button', { name: 'Otevřít wiki', exact: true }).click()
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
  // Spaces are picked in Knihovna, whose first tab lists them all; the sidebar keeps the pinned ones and the open one.
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click()
  await page.locator('.space-directory').getByRole('link', { name: /^Začínáme/ }).click()
  await expect(page.getByRole('complementary', { name: 'Hlavní navigace', exact: true }).getByRole('link', { name: /^Začínáme/ })).toHaveAttribute('aria-current', 'page')
  await page.getByRole('button', { name: 'Vytvořit první stránku', exact: true }).click()
  // Page creation is a routed ProgressiveFlow: the blank template is ready, so title and location show at once.
  await expect(page).toHaveURL(/#\/spaces\/[^/]+\/new-page$/)
  const create = page.getByRole('region', { name: 'Nová stránka', exact: true })
  await create.getByLabel('Název stránky', { exact: true }).fill('Přihlášení a přístupy')
  await create.getByRole('button', { name: 'Vytvořit stránku', exact: true }).click()
  const editor = page.locator(LIVE_EDITOR)
  await expect(editor).toBeVisible()
  await editor.fill('Firemní autentizace a přístup do aplikací. Toto je skutečný obsah editoru.')
  await expectSaved(page)
  await publishPage(page)
  await page.reload()
  await expect(page.locator(PAGE_TEXT)).toContainText('skutečný obsah editoru')
  await page.getByRole('button', { name: /Test správce.*Profil a vzhled/ }).click()
  // The profile opens on Účet a vzhled; Vzhled is its second half (board 12).
  const appearance = page.getByRole('region', { name: 'Profil', exact: true })
  await appearance.getByRole('radio', { name: /^Ostrovy/ }).click()
  await appearance.getByRole('radio', { name: /^Zelená/ }).click()
  await appearance.getByRole('radio', { name: /^Tmavý/ }).click()
  await page.getByRole('button', { name: 'Uložit vzhled', exact: true }).click()
  await expect(page.getByText('Vzhled uložen.', { exact: true })).toBeVisible()
  await page.locator('[data-profile-back]').click()
  await page.reload()
  await expect(page.locator('[data-layout="islands"][data-accent="green"]')).toBeVisible()
  await expect(page.locator(PAGE_TEXT)).toContainText('skutečný obsah editoru')
  await page.getByRole('button', { name: /^Hledat ve wiki/ }).click()
  const searchField = page.getByRole('combobox', { name: /^Hledat ve wiki/ })
  await searchField.fill('pristupi')
  await expect(page.getByRole('listbox', { name: 'Výsledky hledání' }).getByRole('option')).toContainText('Přihlášení a přístupy')
  await expect(searchField).toBeFocused()
  await page.screenshot({ path: '../../artifacts/atlas-accepted-search.png', fullPage: true })
  await searchField.press('Enter')
  await expect(page.getByRole('dialog', { name: /^Hledat ve wiki/ })).not.toBeVisible()
  await expect(page.locator(PAGE_TEXT)).toContainText('skutečný obsah editoru')
  const searchButton = page.getByRole('button', { name: /^Hledat ve wiki/ })
  await searchButton.click()
  await searchField.press('Escape')
  await expect(page.getByRole('dialog', { name: /^Hledat ve wiki/ })).not.toBeVisible()
  await expect(searchButton).toBeFocused()
  await page.screenshot({ path: '../../artifacts/atlas-accepted-editor.png', fullPage: true })

  // Create an unprivileged account through the actual administration UI. The
  // activation code is read into memory only; never attach it to test output.
  await page.getByRole('button', { name: /^Správa wiki/ }).click()
  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Lidé(\s|$)/ }).click()
  await expect(page.locator('.settings-shell-content').getByRole('heading', { name: 'Lidé', exact: true })).toBeVisible()
  // Pozvat člověka is a ProgressiveFlow: who reveals role and summary in place; only the summary
  // confirms, then the one-time activation code (OneTimeSecret) follows.
  await page.locator('[data-invite]').first().click()
  await page.locator('[data-invite-username]').fill('browser-reader')
  await page.locator('[data-invite-display-name]').fill('E2E Čtenář')
  const role = page.locator('[data-invite-role]')
  await role.getByRole('radio', { name: /Běžný účet/ }).click()
  await expect(role.getByRole('radio', { name: /Správce wiki/ })).toHaveAttribute('aria-checked', 'false')
  await page.locator('[data-flow-next]').click()
  const tokenPanel = page.locator('[data-invite-code]')
  await expect(tokenPanel).toBeVisible()
  await tokenPanel.getByRole('button', { name: 'Zobrazit', exact: true }).click()
  let activationToken = ((await tokenPanel.locator('[data-one-time-value]').textContent()) ?? '').trim()
  expect(activationToken.length, 'The server must issue a nonempty opaque activation code.').toBeGreaterThanOrEqual(32)
  await tokenPanel.getByRole('checkbox', { name: 'Kód jsem předal(a) nebo bezpečně uložil(a)', exact: true }).check()
  await tokenPanel.locator('[data-one-time-done]').click()
  await expect(tokenPanel).not.toBeVisible()
  await expect(page.locator('[data-account]').filter({ hasText: 'browser-reader' })).toContainText('Čeká na aktivaci')

  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Skupiny(\s|$)/ }).click()
  // Nová skupina: the name reveals the summary in place → "Přidat členy" opens the detail, where members are picked from tiles.
  await page.locator('[data-new-group]').click()
  await page.locator('[data-group-name]').fill('E2E Čtenáři')
  await page.locator('[data-flow-next]').click()
  await expect(page.locator('[data-group-created]')).toContainText('E2E Čtenáři')
  await page.locator('[data-group-open]').click()
  await expect(page.getByRole('heading', { level: 2, name: 'E2E Čtenáři', exact: true })).toBeVisible()
  await page.locator('[data-add-member]').click()
  const candidates = page.locator('[data-member-candidates]')
  await candidates.getByRole('searchbox').fill('browser-reader')
  await candidates.getByRole('radio', { name: /E2E Čtenář/ }).click()
  await candidates.locator('[data-add-member-confirm]').click()
  const membershipConfirmation = page.getByRole('dialog', { name: 'Přidat do skupiny', exact: true })
  await enterSecret(membershipConfirmation.getByLabel('Heslo k vašemu účtu', { exact: true }), password)
  await membershipConfirmation.locator('[data-confirm-reauth]').click()
  await expect(membershipConfirmation).not.toBeVisible()
  await expect(page.locator('[data-member]').filter({ hasText: 'browser-reader' })).toBeVisible()

  // The only content grant assigned to the new account is inherited from this
  // group. Reader must not acquire global or space administration privileges.
  // Spaces are picked in Knihovna, whose first tab lists them all; the sidebar keeps the pinned ones and the open one.
  // Settings hold the application rail (2.5); its way back returns to the wiki navigation with Knihovna.
  await page.getByRole('button', { name: 'Zpět do wiki', exact: true }).click()
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click()
  await page.locator('.space-directory').getByRole('link', { name: /^Začínáme/ }).click()
  await page.getByRole('button', { name: 'Akce prostoru: Začínáme', exact: true }).click()
  // ActionDisclosure now marks its slotted buttons role="menuitem" (a real
  // ARIA menu widget), so the panel's actions are no longer plain buttons.
  await page.getByRole('menuitem', { name: 'Přístupy', exact: true }).click()
  // Přístupy is a section of Nastavení prostoru; a grant is added through GrantFlow, a
  // ProgressiveFlow: each choice reveals the next step, the summary saves.
  await expect(page).toHaveURL(/#\/spaces\/[^/]+\/settings\/access$/)
  // Kdo má přístup (ResourceAccessPanel): the automatic administrators are locked rows; people and groups for a new
  // grant are searched on the server (/api/access/candidates), never listed as the whole directory.
  await expect(page.locator('[data-access-entry^="automatic:"]').first()).toBeVisible()
  await page.locator('[data-add-access]').first().click()
  await page.getByRole('radio', { name: /^Skupina/ }).click()
  await page.getByRole('searchbox', { name: 'Skupiny: hledat', exact: true }).fill('E2E Čtenáři')
  await page.getByRole('radio', { name: /E2E Čtenáři/ }).click()
  const picker = page.locator('[data-access-level-picker]')
  await expect(picker.getByRole('radio', { checked: true })).toHaveCount(0)
  await picker.getByRole('radio', { name: /^Čte/ }).click()
  await page.locator('[data-flow-next]').click()
  // The group is called "E2E Čtenáři", so assert the level badge of its row, not the name.
  const grant = page.locator('[data-access-entry^="group:"]').filter({ hasText: 'E2E Čtenáři' })
  await expect(grant).toBeVisible()
  await expect(grant.locator('.level-badge')).toContainText('Čte')
  await page.screenshot({ path: '../../artifacts/atlas-accepted-access.png', fullPage: true })

  // Even WikiAdmin must receive explicit API issuing permission. The separate
  // permission form must save through its real endpoint before token creation.
  await page.getByRole('button', { name: /^Správa wiki/ }).click()
  await page.getByRole('navigation', { name: 'Správa wiki', exact: true }).getByRole('link', { name: /^Lidé(\s|$)/ }).click()
  const ownAccount = page.locator('[data-account]').filter({ hasText: 'browser-admin' })
  await ownAccount.locator('[data-open-account]').click()
  // On a wide screen the name opens a preview beside the table first (2.5); its button opens the whole card.
  const peek = page.locator('[data-person-peek]')
  await expect(page.locator('[data-person-peek], .settings-tabs [data-tab="account"]').first()).toBeVisible()
  if (await peek.isVisible()) await peek.locator('[data-person-peek-open]').click()
  // The person's card opens on Přístupy; the API permission is on its Účet tab.
  await page.locator('.settings-tabs [data-tab="account"]').click()
  // The ceiling is a radio group (Bez API · Čtení · Zápis); the account starts without the API.
  const apiLevel = page.getByRole('radiogroup', { name: 'Úroveň osobního API', exact: true })
  await expect(apiLevel.getByRole('radio', { name: 'Bez API', exact: true })).toBeChecked()
  const apiPermission = apiLevel.getByRole('radio', { name: 'Zápis', exact: true })
  await apiPermission.click()
  await page.locator('[data-save-bar-save]').click()
  await expect(page.getByRole('region', { name: 'Uložení změn' })).toContainText('Uloženo')
  await expect(apiPermission).toBeChecked()
  await page.getByRole('button', { name: /Test správce.*Profil a vzhled/ }).click()
  const profile = page.locator('.profile-page')
  await page.locator('.settings-sidebar-rail [data-path="/profile/api"]').click()
  await profile.getByRole('button', { name: 'Nový API token', exact: true }).click()
  // Nový API token is a ProgressiveFlow in place of the lists (Název · Rozsah · Platnost · Souhrn);
  // the one-time value follows in OneTimeSecret, masked until Zobrazit.
  const issueToken = profile.getByRole('region', { name: 'Nový osobní API token', exact: true })
  await issueToken.getByLabel('Název tokenu', { exact: true }).fill('E2E čtení')
  // Scopes are offered up to the level the administrator set (Zápis here); Čtení is the default and REST API the channel.
  await expect(issueToken.getByRole('radio', { name: /^Čtení/ })).toHaveAttribute('aria-checked', 'true')
  await expect(issueToken.getByRole('radio')).toHaveCount(2 + 5)
  await expect(issueToken.getByRole('checkbox', { name: /^REST API/ })).toBeChecked()
  await expect(issueToken.getByRole('checkbox', { name: /^MCP/ })).not.toBeChecked()
  await issueToken.getByRole('radio', { name: 'Vlastní', exact: true }).click()
  await issueToken.getByLabel('Počet dní', { exact: true }).fill('1')
  await expect(issueToken.locator('[data-step="summary"]')).toContainText(/1 den, do \d/)
  // Issuing a token needs a fresh sign-in (SEC-A-2); the membership confirmation above opened the session's ten-minute window.
  await expect(issueToken.locator('[data-reauth-window]')).toBeVisible()
  await issueToken.getByRole('button', { name: 'Vytvořit token', exact: true }).click()
  const issuedToken = issueToken.getByRole('group', { name: 'Nově vydaný API token', exact: true })
  await expect(issuedToken).toBeVisible()
  await issuedToken.getByRole('button', { name: 'Zobrazit', exact: true }).click()
  let personalToken = ((await issuedToken.locator('[data-one-time-value]').textContent()) ?? '').trim()
  expect(personalToken.length, 'API token must contain sufficient opaque secret material.').toBeGreaterThanOrEqual(32)
  await issuedToken.getByRole('checkbox', { name: 'Token mám bezpečně uložený', exact: true }).check()
  await issuedToken.locator('[data-one-time-done]').click()
  await expect(issuedToken).not.toBeVisible()
  await expect(profile.locator('[data-result-banner]')).toContainText('Token „E2E čtení“ je vydaný')
  const authorizedSpaces = await readApiSpacesWithToken(page, personalToken)
  expect(authorizedSpaces.status, 'A personal read token must authorize the versioned spaces API without cookies.').toBe(200)
  expect(authorizedSpaces.hasExpectedSpace).toBe(true)
  // Použití tokenu is an advanced section, folded until opened.
  await profile.getByRole('button', { name: /^Použití tokenu/ }).click()
  const downloaded = page.waitForEvent('download')
  await profile.getByRole('link', { name: 'Stáhnout popis API (OpenAPI JSON)', exact: true }).click()
  const download = await downloaded
  const stream = await download.createReadStream()
  expect(stream, 'Cookie-authenticated OpenAPI download must be available.').not.toBeNull()
  const chunks: Buffer[] = []
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk))
  const openApi = JSON.parse(Buffer.concat(chunks).toString('utf8'))
  expect(openApi.openapi).toMatch(/^3\./)
  expect(openApi.paths).toHaveProperty('/api/v1/spaces')
  // Revoking is the one destructive step: a small ConfirmDialog, then the result line.
  await profile.getByRole('button', { name: 'Zneplatnit token E2E čtení', exact: true }).click()
  await page.getByRole('dialog', { name: 'Zneplatnit API token', exact: true }).getByRole('button', { name: 'Zneplatnit token', exact: true }).click()
  await expect(profile.locator('[data-result-banner]')).toContainText('Token „E2E čtení“ je trvale zneplatněný.')
  await expect(profile.getByRole('list', { name: 'Neplatné tokeny', exact: true })).toContainText('E2E čtení')
  const revokedSpaces = await readApiSpacesWithToken(page, personalToken)
  personalToken = ''
  expect(revokedSpaces.status, 'UI revocation must immediately stop bearer authentication.').toBe(401)
  // Sign-out lives in the profile rail.
  // Odhlásit se sits at the bottom of the profile's sections in the application rail (2.5).
  await page.getByRole('button', { name: 'Odhlásit se', exact: true }).click()

  // Recovery instructions are available without loading any recovery secret
  // into the browser. Actual recovery/token rotation is covered by HTTP tests.
  // The recovery entry waits in the version details at the foot of the login card.
  await page.getByRole('button', { name: /^Atlas \d+\.\d+\.\d+/ }).click()
  await page.getByRole('button', { name: 'Obnovit přístup správce', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Obnova přístupu správce', exact: true })).toBeVisible()
  await expect(page.locator('.recovery-path')).toContainText('administrator-recovery.json')
  await expect(page.getByLabel('Obnovovací token', { exact: true })).toHaveValue('')
  await page.getByRole('button', { name: 'Zpět k přihlášení', exact: true }).click()

  await page.getByRole('button', { name: 'Aktivovat účet / obnovit heslo', exact: true }).click()
  const activation = page.getByRole('form', { name: 'Aktivace účtu nebo obnova hesla', exact: true })
  await enterSecret(activation.getByLabel('Aktivační kód', { exact: true }), activationToken)
  await enterSecret(activation.getByLabel('Nové heslo', { exact: true }), readerPassword)
  await enterSecret(activation.getByLabel('Potvrzení nového hesla', { exact: true }), readerPassword)
  const activationResponse = page.waitForResponse(response =>
    new URL(response.url()).pathname === '/api/auth/activate' && response.request().method() === 'POST')
  await activation.getByRole('button', { name: 'Nastavit heslo', exact: true }).click()
  const activated = await activationResponse
  expect(activated.ok(), `account activation: HTTP ${activated.status()}`).toBe(true)
  await expect(page.getByText(/^Heslo bylo nastaveno\. Nyní se přihlaste/)).toBeVisible()
  await expect(page.getByLabel('Uživatelské jméno', { exact: true })).toBeVisible()

  // A consumed code cannot reset that password again, even through the public
  // activation UI. Clear it from the form before continuing to login.
  await page.getByRole('button', { name: 'Aktivovat účet / obnovit heslo', exact: true }).click()
  await enterSecret(activation.getByLabel('Aktivační kód', { exact: true }), activationToken)
  await enterSecret(activation.getByLabel('Nové heslo', { exact: true }), readerPassword)
  await enterSecret(activation.getByLabel('Potvrzení nového hesla', { exact: true }), readerPassword)
  await activation.getByRole('button', { name: 'Nastavit heslo', exact: true }).click()
  await expect(page.locator('.login-methods').getByRole('alert')).toBeVisible()
  expect(page.url().includes(activationToken), 'Activation codes must never enter navigation URLs.').toBe(false)
  activationToken = ''
  await page.getByRole('button', { name: 'Zpět k přihlášení', exact: true }).click()
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-reader')
  await enterSecret(page.getByLabel('Heslo', { exact: true }), readerPassword)
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click()
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Správa wiki/ })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Nový prostor', exact: true })).toHaveCount(0)
  // A new account starts from the defaults: connected layout and, since 2.5, the ocean accent (Tyrkysová).
  await expect(page.locator('[data-layout="connected"][data-accent="ocean"]')).toBeVisible()
  await page.getByRole('button', { name: 'Knihovna', exact: true }).click()
  await page.locator('.space-directory').getByRole('link', { name: /^Začínáme/ }).click()
  // The page table states every page's state in its own column, outside the title link (refresh 2.5).
  await page.getByRole('main').getByRole('link', { name: 'Přihlášení a přístupy', exact: true }).click()
  await expect(page.locator(PAGE_TEXT)).toContainText('skutečný obsah editoru')
  await expect(page.locator(PAGE_TEXT)).toHaveAttribute('contenteditable', 'false')
  await expect(page.locator('[data-edit-lock]')).toHaveCount(0)
  for (const name of ['Publikovat', 'Zveřejnit změny', 'Nová stránka', 'Přístupy']) {
    await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0)
  }
  const deniedAdministration = await page.request.get('/api/admin/users')
  expect(deniedAdministration.status(), 'The server must enforce global administration independently of hidden controls.').toBe(403)
  await page.screenshot({ path: '../../artifacts/atlas-accepted-reader.png', fullPage: true })
})
