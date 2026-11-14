import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { createHash } from 'node:crypto'
import { readFile, readdir } from 'node:fs/promises'
import { join } from 'node:path'

// Real SMB bytes are read from the share's volume, mounted read-only only into Playwright.
// Atlas receives only smb://localhost/exports and must reach Samba over the network.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 })
const smbRoot = process.env.ATLAS_E2E_SMB_ROOT
const destination = process.env.ATLAS_E2E_SMB_DESTINATION
const folder = 'storage-export-' + Date.now()
const fileName = 'document.pdf'
// Every export writes the configured name with its date and time (Prague): "document 2026-11-08 14-30.pdf".
const datedName = /^document \d{4}-\d\d-\d\d \d\d-\d\d\.pdf$/
let context: BrowserContext, page: Page, pageId: string, profileId: string, csrf: string
let savedPage: { id: string; title: string; version: number; contentJson: string }
let initialName: string, initialBytes: Buffer
const hash = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex')
const rich = (text: string) => JSON.stringify({ format: 'atlas-rich', version: 1, document: {
  type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
} })
const pdf = (name: string) => readFile(join(smbRoot!, folder, name))
/** The file the last export wrote, as the export state names it. */
async function written() {
  const name = (await state()).lastFileName as string
  expect(name).toMatch(datedName)
  return name
}
async function files() {
  try { return (await readdir(join(smbRoot!, folder))).sort() }
  catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return []; throw error }
}
function expectCompletePdf(bytes: Buffer) {
  expect(bytes.subarray(0, 5).toString()).toBe('%PDF-')
  expect(bytes.subarray(-40).toString()).toContain('%%EOF')
  expect(bytes.toString('latin1')).toMatch(/\/Type\s*\/Page\b/)
}
async function state() {
  const response = await context.request.get('/api/pages/' + pageId + '/pdf-export')
  expect(response.status()).toBe(200)
  return response.json()
}
async function revisionId() {
  const response = await context.request.get('/api/pages/' + pageId + '/history')
  expect(response.status()).toBe(200)
  return (await response.json()).items[0].id as string
}
async function current(expectedRevision: string, debounce = false) {
  const errors: string[] = []
  await expect.poll(async () => {
    const result = await state()
    if (result.lastError) errors.push(result.lastError)
    return { status: result.status, revisionId: result.lastRevisionId, error: result.lastError || '' }
  }, { timeout: debounce ? 45_000 : 15_000, intervals: [1_000] }).toEqual({ status: 'current', revisionId: expectedRevision, error: '' })
  expect(errors, 'No transient remote export failure may be hidden by a later successful retry').toEqual([])
}
async function saveRevision(text: string) {
  const response = await context.request.put('/api/pages/' + pageId, {
    headers: { 'X-CSRF-TOKEN': csrf }, data: {
      title: savedPage.title, contentJson: rich(text), markdown: text, expectedVersion: savedPage.version,
    },
  })
  expect(response.status()).toBe(200)
  savedPage = await response.json()
  return revisionId()
}
async function runNow() {
  const response = await context.request.post('/api/pages/' + pageId + '/pdf-export/run', { headers: { 'X-CSRF-TOKEN': csrf } })
  expect(response.status()).toBe(200)
}

test.describe.serial('PDF autosync over real SMB', () => {
  test.beforeAll(async ({ browser }) => {
    test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !smbRoot || !destination,
      'Requires the isolated storage-export batch of e2e-podman.ps1.')
    context = await browser.newContext({ baseURL: process.env.ATLAS_URL })
    page = await context.newPage()
    await page.goto('/')
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin')
    await page.getByLabel('Heslo', { exact: true }).evaluate((element, password) => {
      const input = element as HTMLInputElement
      input.value = password; input.dispatchEvent(new Event('input', { bubbles: true }))
    }, process.env.ATLAS_E2E_PASSWORD!)
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click()
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
    csrf = (await (await context.request.get('/api/auth/csrf')).json()).token
    const spaces = await (await context.request.get('/api/spaces')).json()

    // Authorize the connection through the real profile form; its password is cleared after saving.
    await page.goto('/#/settings/general/storage')
    await page.locator('[data-add-profile]').click()
    await page.locator('[data-profile-name]').fill('SMB acceptance ' + folder)
    await page.getByRole('radio', { name: /Síťová složka SMB/ }).click()
    await page.locator('[data-profile-destination]').fill(destination!)
    await page.locator('[data-profile-username]').fill('atlas')
    await page.locator('[data-profile-domain]').fill('WORKGROUP')
    await page.locator('[data-profile-password]').evaluate((element, password) => {
      const input = element as HTMLInputElement
      input.value = password; input.dispatchEvent(new Event('input', { bubbles: true }))
    }, process.env.ATLAS_E2E_SMB_PASSWORD!)
    await page.locator('[data-profile-all-spaces]').click()
    await page.locator('[data-save-bar-save]').click()
    await expect(page.locator('[data-profile]')).toContainText('SMB acceptance ' + folder)
    const profiles = await (await context.request.get('/api/storage-profiles')).json()
    const savedProfile = profiles.find((profile: { name: string }) => profile.name === 'SMB acceptance ' + folder)
    expect({ username: savedProfile.username, domain: savedProfile.domain, hasPassword: savedProfile.hasPassword })
      .toEqual({ username: 'atlas', domain: 'WORKGROUP', hasPassword: true })
    profileId = savedProfile.id
    const access = await context.request.post('/api/storage-profiles/' + profileId + '/test', { headers: { 'X-CSRF-TOKEN': csrf } })
    expect(access.status()).toBe(200)

    const response = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf }, data: {
      spaceId: spaces[0].id, parentId: null, title: 'PDF SMB acceptance', contentJson: rich('SMB revision one'), markdown: 'SMB revision one',
    } })
    expect(response.status()).toBe(200)
    savedPage = await response.json(); pageId = savedPage.id
  })
  test.afterAll(async () => { await context?.close() })

  test('saved export settings wait 30 seconds before publishing one complete PDF', async () => {
    await page.goto('/#/pages/' + pageId)
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click()
    await page.getByRole('menuitem', { name: 'Automatický export PDF…', exact: true }).click()
    await page.locator('[data-pdf-enabled]').click()
    await page.locator(`[data-pdf-profile] [data-value="${profileId}"]`).click()
    // The target is the folder the browser has open: a new subfolder of the root opens at once.
    await page.locator('[data-pdf-new-folder]').fill(folder)
    await page.locator('[data-pdf-create-folder]').click()
    await expect(page.getByRole('navigation', { name: 'Cesta ke složce', exact: true })).toContainText(folder)
    // The name offered is the page title; the spec sets one it can recognize in the dated file names.
    await page.locator('[data-pdf-filename]').fill(fileName)
    const expectedRevision = await revisionId()
    const started = Date.now()
    await page.locator('[data-save-bar-save]').click()
    await expect(page.locator('[data-save-bar-save]')).toBeHidden()
    const pending = await state()
    expect(pending.status).toBe('pending')
    expect(pending.settings.keepVersions).toBe(false)
    expect(await files()).toEqual([])
    await current(expectedRevision, true)
    expect(Date.now() - started, 'Remote PDF must wait for the 30-second debounce').toBeGreaterThanOrEqual(29_000)
    initialName = await written()
    initialBytes = await pdf(initialName); expectCompletePdf(initialBytes)
    expect(await files()).toEqual([initialName])
    await page.getByRole('dialog', { name: 'Automatický export PDF', exact: true }).getByRole('button', { name: 'Zavřít', exact: true }).click()
  })

  test('two saved revisions coalesce and replace the previous PDF without accumulating files', async () => {
    await saveRevision('Intermediate revision that must be coalesced')
    const expectedRevision = await saveRevision('SMB revision three: latest saved content')
    const started = Date.now()
    expect((await state()).status).toBe('pending')
    expect(hash(await pdf(initialName))).toBe(hash(initialBytes))
    await current(expectedRevision, true)
    expect(Date.now() - started).toBeGreaterThanOrEqual(29_000)
    const name = await written()
    const replaced = await pdf(name); expectCompletePdf(replaced)
    expect(hash(replaced)).not.toBe(hash(initialBytes))
    expect(await files()).toEqual([name])
  })

  test('opt-in versions keep every exported PDF beside the newest one', async () => {
    // Waits for the next minute below: dated names have minute precision.
    test.setTimeout(120_000)
    const before = await files()
    await page.reload()
    await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click()
    await page.getByRole('menuitem', { name: 'Automatický export PDF…', exact: true }).click()
    await page.getByRole('button', { name: /Verze a publikace/ }).click()
    await page.locator('[data-pdf-keep-versions]').click()
    await page.locator('[data-save-bar-save]').click()
    await expect(page.locator('[data-save-bar-save]')).toBeHidden()
    await page.locator('[data-pdf-run]').click()
    await current(await revisionId())
    const first = await written()
    // An export in the same minute as the previous one replaces it under the same name; otherwise both stay.
    expect(await files()).toEqual([...new Set([...before, first])].sort())
    const firstBytes = await pdf(first); expectCompletePdf(firstBytes)

    // The pod's containers share one clock: past the minute in which `first` was written, the next export gets a name of its own.
    await page.waitForTimeout(60_000 - Date.now() % 60_000 + 500)
    const nextRevision = await saveRevision('SMB revision four with retained versions')
    await runNow(); await current(nextRevision)
    const next = await written()
    expect(next).not.toBe(first)
    expect(await files()).toEqual([...new Set([...before, first, next])].sort())
    expect(hash(await pdf(first))).toBe(hash(firstBytes))
    const nextBytes = await pdf(next); expectCompletePdf(nextBytes)
    expect(hash(nextBytes)).not.toBe(hash(firstBytes))
  })
})
