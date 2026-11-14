import { test, expect, type BrowserContext, type Locator, type Page } from '@playwright/test'
import { startEditing, expectSaved, PAGE_TEXT, LIVE_EDITOR } from './editing'
import { createHash, randomBytes } from 'node:crypto'
import { chmod, mkdir, writeFile } from 'node:fs/promises'
import { dirname } from 'node:path'
import { deflateSync } from 'node:zlib'

// Průběžná záloha do Gitu (docs/git-backup.md) against the SSH git server of e2e-podman.ps1: the deploy key Atlas generates
// goes into the server's authorized_keys, and a saved page with an image must arrive in the bare repository. The script
// then checks the pushed commits itself (Test-GitBackupRepository), with the evidence this spec writes.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 })

// Správa wiki → Provoz → Zálohy, tab Git (BackupsPage.vue with GitBackupPanel.vue).
const gitBackupRoute = '/#/settings/general/backups/git'
// The SaveBar exists only while the form is dirty; after saving it shows "Uloženo" briefly and disappears.
const saveSelector = '[data-git-save] [data-save-bar-save]'
const marker = 'git-backup-e2e-marker'
const imageName = 'git-backup-e2e.png'

async function enterSecret(field: Locator, secret: string) {
  await expect(field).toBeVisible()
  await field.evaluate((element, value) => {
    const input = element as HTMLInputElement
    input.value = value
    input.dispatchEvent(new Event('input', { bubbles: true }))
    input.dispatchEvent(new Event('change', { bubbles: true }))
  }, secret)
}

async function mutate(context: BrowserContext, path: string, data: unknown) {
  const csrf = await (await context.request.get('/api/auth/csrf')).json()
  return context.request.post(path, { data, headers: { 'X-CSRF-TOKEN': csrf.token } })
}

async function dropFile(page: Page, name: string, type: string, bytes: Buffer) {
  const editor = page.locator(LIVE_EDITOR)
  await editor.click(); await editor.press('Control+End'); await editor.press('Enter')
  await editor.evaluate((element, file) => {
    const data = Uint8Array.from(atob(file.base64), c => c.charCodeAt(0))
    const transfer = new DataTransfer(); transfer.items.add(new File([data], file.name, { type: file.type }))
    const box = element.getBoundingClientRect()
    element.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer, clientX: box.left + 30, clientY: box.bottom - 12 }))
  }, { name, type, base64: bytes.toString('base64') })
}

// A backup destination change may ask for the password first (403 reauth_required → ReauthDialog); `done` tells the change went through.
async function confirmIfAsked(page: Page, password: string, done: () => Promise<boolean>) {
  const dialog = page.locator('[data-reauth-dialog]')
  await expect.poll(async () => await dialog.isVisible() || await done(), { timeout: 30_000 }).toBe(true)
  if (!(await dialog.isVisible())) return
  const field = dialog.locator('input[type="password"]')
  if (await field.isVisible()) await enterSecret(field, password)
  await dialog.locator('[data-confirm-reauth]').click()
  // The modal <dialog> keeps the page inert until its exit animation ends: typing before that goes nowhere.
  await expect(page.locator('dialog[open]')).toHaveCount(0)
}

// The value of an input or textarea (itself or inside the element), otherwise the element's text.
async function valueOf(locator: Locator) {
  return locator.evaluate(element => {
    const field = element.matches('input, textarea') ? element : element.querySelector('input, textarea')
    return (field as HTMLInputElement | null)?.value || element.textContent || ''
  })
}

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
function crc32(bytes: Buffer) {
  let c = 0xffffffff
  for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}
function pngChunk(type: string, data: Buffer) {
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
  const frame = Buffer.alloc(8 + data.length + 4)
  frame.writeUInt32BE(data.length, 0); body.copy(frame, 4); frame.writeUInt32BE(crc32(body), 8 + data.length)
  return frame
}
/** A valid 8×8 RGB PNG with random pixels: its bytes, and so its git blob, exist only in this run. */
function uniquePng(size = 8) {
  const header = Buffer.alloc(13)
  header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 2
  const rows = Buffer.concat(Array.from({ length: size }, () => Buffer.concat([Buffer.from([0]), randomBytes(size * 3)])))
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), pngChunk('IHDR', header), pngChunk('IDAT', deflateSync(rows)), pngChunk('IEND', Buffer.alloc(0))])
}

test('git backup: deploy key, access test and a saved page with an image pushed over SSH', async ({ page, context }, info) => {
  test.setTimeout(240_000)
  const remote = process.env.ATLAS_E2E_GIT_REMOTE
  const keys = process.env.ATLAS_E2E_GIT_KEYS
  test.skip(!remote || !keys, 'Needs the SSH git server of e2e-podman.ps1 (ATLAS_E2E_GIT_REMOTE, ATLAS_E2E_GIT_KEYS).')
  test.skip(process.env.ATLAS_E2E_ALLOW_UPLOADS !== '1', 'Requires an isolated installed acceptance database and explicit ATLAS_E2E_ALLOW_UPLOADS=1.')
  const password = process.env.ATLAS_E2E_PASSWORD!
  expect(password?.length).toBeGreaterThanOrEqual(12)
  page.on('dialog', dialog => dialog.accept())
  await page.goto('/')
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin')
  await enterSecret(page.getByLabel('Heslo', { exact: true }), password)
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click()
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()

  // Deploy key: generated on the server; the public half becomes the git server's authorized_keys (sshd reads it as git).
  await page.getByRole('button', { name: /^Správa wiki/ }).click()
  await page.locator('nav[aria-label="Správa wiki"] a[data-path="general/backups"]').click()
  await page.locator('a[data-tab="git"]').click()
  await expect(page).toHaveURL(/#\/settings\/general\/backups\/git$/)
  await expect(page.locator('[data-git-backup]')).toBeVisible()
  // The first key needs no replace confirmation (a new one would open ConfirmDialog → [data-confirm-action]), only the password.
  await page.locator('[data-git-generate-key]').click()
  const publicKeyField = page.locator('[data-git-public-key]')
  await confirmIfAsked(page, password, () => publicKeyField.isVisible())
  await expect.poll(() => valueOf(publicKeyField)).toMatch(/ssh-ed25519 [A-Za-z0-9+/]+/)
  const publicKey = (await valueOf(publicKeyField)).match(/ssh-ed25519 [A-Za-z0-9+/]+={0,2}/)![0]
  await mkdir(dirname(keys!), { recursive: true })
  await writeFile(keys!, `${publicKey}\n`, { mode: 0o644 })
  await chmod(keys!, 0o644)

  // Access test (ls-remote and push --dry-run) with the unsaved form values; it stores the SSH host key (trust on first use).
  await page.locator('[data-git-remote-url]').fill(remote!)
  await page.locator('[data-git-branch]').fill('main')
  await page.locator('[data-git-test]').click()
  await expect(page.locator('[data-git-test-result][data-ok="true"]')).toBeVisible({ timeout: 60_000 })
  await expect(page.locator('[data-git-action-error]')).toBeHidden()

  const enabled = page.locator('[data-git-enabled]')
  if (!(await enabled.isChecked())) await enabled.click()
  await expect(enabled).toBeChecked()
  await page.locator(saveSelector).click()
  await confirmIfAsked(page, password, async () => !(await page.locator(saveSelector).isVisible()))
  await expect(page.locator(saveSelector)).toBeHidden({ timeout: 30_000 })
  await expect(page.locator('[data-git-action-error]')).toBeHidden()
  // Saved, not just edited: the settings and the server key stored by the test survive a reload.
  await page.reload()
  await expect(page.locator('[data-git-backup]')).toBeVisible()
  await expect(page.locator('[data-git-enabled]')).toBeChecked()
  await expect(page.locator('[data-git-remote-url]')).toHaveValue(remote!)
  await expect(page.locator('[data-git-host-keys]')).toContainText(/ssh-ed25519 SHA256:[A-Za-z0-9+/]{43}/)

  // A page (revision 1 through the API) whose saved draft (revision 2) adds an image uploaded in the editor. It stays in the
  // wiki: moving it to the trash would delete its Markdown from the repository before the script checks it.
  const spaces = await (await context.request.get('/api/spaces')).json()
  expect(spaces.length).toBeGreaterThan(0)
  const title = `Záloha do Gitu ${Date.now()}`
  const text = `Obsah zálohovaný do Gitu: ${marker}.`
  const created = await mutate(context, '/api/pages', {
    spaceId: spaces[0].id, parentId: null, title, markdown: text,
    contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } }),
  })
  expect(created.status()).toBe(200)
  const { id } = await created.json()
  await page.goto(`/#/pages/${id}`)
  await expect(page.locator(PAGE_TEXT)).toContainText(marker)
  await startEditing(page)
  const png = uniquePng()
  const upload = page.waitForResponse(response => response.url().endsWith(`/api/pages/${id}/attachments`) && response.request().method() === 'POST')
  await dropFile(page, imageName, 'image/png', png)
  const uploaded = await upload
  expect(uploaded.status()).toBe(201)
  const image = await uploaded.json()
  // For the script's repository check: the page's marker and the exact image bytes (git blob id = sha1("blob <size>\0" + bytes)).
  // The commit author's address is the account's contact e-mail, which an earlier spec may have set (workspace-consistency.spec.ts).
  const account = await (await context.request.get('/api/profile/account')).json()
  const evidence: Record<string, unknown> = {
    pageId: id, title, marker, authorEmail: account.email || null,
    image: {
      name: imageName, size: png.length,
      sha256: createHash('sha256').update(png).digest('hex'),
      gitBlob: createHash('sha1').update(`blob ${png.length}\0`).update(png).digest('hex'),
    },
  }
  await saveEvidence(evidence)
  const shown = page.locator(`${PAGE_TEXT} img[src="${image.url}"]`)
  await expect(shown).toBeVisible()
  await expect.poll(() => shown.evaluate(element => (element as HTMLImageElement).naturalWidth)).toBe(8)
  await expectSaved(page)
  const saved = await (await context.request.get(`/api/pages/${id}`)).json()
  expect(saved.version).toBeGreaterThan(1) // autosave may store more than one checkpoint
  expect(JSON.stringify(JSON.parse(saved.contentJson).document)).toContain(image.url)

  // Synchronizovat teď commits and pushes the saved revisions and answers when done. The scheduled sync (every 15 s) may have
  // pushed them already. Settings deliberately refresh on load/focus rather than polling; reload an observed
  // running state until the scheduled run finishes before exercising the real manual action.
  await page.goto(gitBackupRoute)
  await expect(page.locator('[data-git-backup]')).toBeVisible()
  await expect.poll(async () => {
    if (await page.locator('[data-git-sync]').isEnabled()) return true
    await page.reload()
    await expect(page.locator('[data-git-backup]')).toBeVisible()
    return page.locator('[data-git-sync]').isEnabled()
  }, { timeout: 90_000, intervals: [1000] }).toBe(true)
  const synced = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname.endsWith('/backups/git/sync'), { timeout: 150_000 })
  await page.locator('[data-git-sync]').click({ timeout: 90_000 })
  const sync = await synced
  expect(sync.status()).toBe(200)
  const result = await sync.json()
  expect(result.lastError || null).toBeNull()
  expect(result.lastCommit).toMatch(/^[0-9a-f]{40}$/)
  await saveEvidence({ ...evidence, lastCommit: result.lastCommit })
  await expect(page.locator('[data-git-status]')).toHaveAttribute('data-state', 'ok', { timeout: 30_000 })
  await expect(page.locator('[data-git-last-commit]')).toHaveText(result.lastCommit.slice(0, 7))
  await expect(page.locator('[data-git-error]')).toBeHidden()
  await expect(page.locator('[data-git-action-error]')).toBeHidden()

  async function saveEvidence(value: Record<string, unknown>) {
    const json = JSON.stringify(value, null, 2)
    const file = process.env.ATLAS_E2E_GIT_EVIDENCE
    if (file) {
      await mkdir(dirname(file), { recursive: true })
      await writeFile(file, json)
    }
    await info.attach('git-backup.json', { body: json, contentType: 'application/json' })
  }
})
