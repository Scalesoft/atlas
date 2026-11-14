import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { publishPage, startEditing, PAGE_TEXT, LIVE_EDITOR } from './editing'
import { spawn, type ChildProcess } from 'node:child_process'
import { once } from 'node:events'
import path from 'node:path'

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off' })

test('a killed and restarted host restores acknowledged unsaved collaboration and existing login', async ({ browser, request }) => {
  test.setTimeout(180_000)
  test.skip(process.env.ATLAS_E2E_ALLOW_HOST_RESTART !== '1', 'Requires its own disposable database, paths and loopback host.')
  const url = new URL(process.env.ATLAS_URL!)
  expect(url.hostname).toBe('localhost'); expect(url.port).toBe('5224')
  const directory = path.resolve(process.env.ATLAS_E2E_HOST_DIRECTORY!)
  let host: ChildProcess | undefined
  const contexts: BrowserContext[] = []
  async function start() {
    host = spawn('dotnet', ['Atlas.Server.dll'], { cwd: directory, windowsHide: true, stdio: 'ignore', env: { ...process.env, ASPNETCORE_URLS: url.origin, ASPNETCORE_ENVIRONMENT: 'Production' } })
    await expect.poll(async () => {
      if (host?.exitCode !== null) return false
      try { const r = await request.get('/api/setup/status'); return r.ok() && (await r.json()).databaseReady === true } catch { return false }
    }, { timeout: 60_000 }).toBe(true)
  }
  async function stop() {
    if (!host || host.exitCode !== null) return
    const exited = once(host, 'exit'); host.kill('SIGKILL'); await exited
  }
  async function mutation(context: BrowserContext, route: string, data: unknown) {
    const csrf = await (await context.request.get('/api/auth/csrf')).json()
    return context.request.post(route, { data, headers: { 'X-CSRF-TOKEN': csrf.token } })
  }
  async function login(): Promise<Page> {
    const context = await browser.newContext({ baseURL: url.origin }); contexts.push(context)
    const page = await context.newPage()
    await page.goto('/')
    await page.getByLabel('Uživatelské jméno', { exact: true }).fill('restart-admin')
    await page.getByLabel('Heslo', { exact: true }).evaluate((el, value) => { (el as HTMLInputElement).value = value!; el.dispatchEvent(new Event('input', { bubbles: true })) }, process.env.ATLAS_E2E_PASSWORD)
    await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click()
    await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
    return page
  }
  try {
    await start()
    const status = await (await request.get('/api/setup/status')).json()
    expect(status.requiresSetup, 'The restart test never overwrites an existing installation.').toBe(true)
    // Sensitive request bodies are never included in traces or attached output.
    const setupCsrf = await (await request.get('/api/auth/csrf')).json()
    const setup = await request.post('/api/setup/complete', { headers: { 'X-CSRF-TOKEN': setupCsrf.token }, data: { bootstrapToken: process.env.ATLAS_E2E_BOOTSTRAP_TOKEN, workspaceName: 'Host restart acceptance', username: 'restart-admin', displayName: 'Restart administrator', password: process.env.ATLAS_E2E_PASSWORD } })
    expect(setup.status()).toBe(200)
    const csrf = await (await request.get('/api/auth/csrf')).json()
    expect((await request.post('/api/admin/setup/complete', { data: {}, headers: { 'X-CSRF-TOKEN': csrf.token } })).status()).toBe(204)
    const a = await login(), b = await login()
    const spaces = await (await a.context().request.get('/api/spaces')).json()
    const seed = 'Confirmed initial content.'
    const created = await mutation(a.context(), '/api/pages', { spaceId: spaces[0].id, title: 'Durable collaboration', markdown: seed, contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: seed }] }] } }) })
    expect(created.status()).toBe(200); const page = await created.json()
    for (const tab of [a, b]) {
      await tab.goto(`/#/pages/${page.id}`)
      await startEditing(tab)
    }
    const text = 'Acknowledged by another peer before the host was killed.'
    const editor = a.locator(LIVE_EDITOR); await editor.click(); await editor.press('Control+End'); await editor.press('Enter'); await a.keyboard.insertText(text)
    await expect(b.locator(PAGE_TEXT)).toContainText(text)
    // No "no revision yet" check: 3.6 saves a checkpoint 1.5 s after the last key, which the kill may or may not beat.
    await stop()
    await expect(a.locator(PAGE_TEXT)).toHaveAttribute('contenteditable', 'false', { timeout: 30_000 })
    await start()
    // Neither browser reloads: reconnect must load durable Yjs with persisted server keys.
    for (const tab of [a, b]) {
      await expect(tab.locator(LIVE_EDITOR)).toBeVisible({ timeout: 60_000 })
      await expect(tab.locator(PAGE_TEXT)).toContainText(text)
      expect((await tab.context().request.get('/api/auth/me')).status()).toBe(200)
    }
    await publishPage(a)
    const after = await (await a.context().request.get(`/api/pages/${page.id}`)).json()
    expect(after.contentJson).toContain(text); expect(after.version).toBeGreaterThan(page.version)
  } finally {
    for (const context of contexts) await context.close()
    await stop()
  }
})
