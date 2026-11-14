import { test, expect } from '@playwright/test'
import { readFile, writeFile, readdir } from 'node:fs/promises'
import { join } from 'node:path'

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 })

// The runner interrupts the application between these two invocations while the 30-second queue is pending.
// Evidence stays in the browser report directory, not in the application's filesystem.
test('queued PDF export survives an application process restart', async ({ page, context }) => {
  const phase = process.env.ATLAS_E2E_STORAGE_RESTART_PHASE
  const evidencePath = process.env.ATLAS_E2E_STORAGE_RESTART_EVIDENCE
  test.skip(!phase || !evidencePath || !process.env.ATLAS_E2E_SMB_ROOT, 'Run by the storage-export Podman batch.')
  await page.goto('/')
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('browser-admin')
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, password) => {
    const input = element as HTMLInputElement
    input.value = password; input.dispatchEvent(new Event('input', { bubbles: true }))
  }, process.env.ATLAS_E2E_PASSWORD!)
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click()
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
  const csrf = (await (await context.request.get('/api/auth/csrf')).json()).token
  const headers = { 'X-CSRF-TOKEN': csrf }

  if (phase === 'prepare') {
    const spaces = await (await context.request.get('/api/spaces')).json()
    const profileResponse = await context.request.get('/api/storage-profiles')
    expect(profileResponse.status()).toBe(200)
    const profile = (await profileResponse.json()).find((candidate: { destination: string }) =>
      candidate.destination === process.env.ATLAS_E2E_SMB_DESTINATION)
    expect(profile, 'Reuse the same authorized SMB root; profiles cannot overlap destinations').toBeTruthy()
    const text = 'Durable queued revision after application restart'
    const created = await context.request.post('/api/pages', { headers, data: {
      spaceId: spaces[0].id, parentId: null, title: 'Restart PDF acceptance', markdown: text,
      contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: {
        type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
      } }),
    } })
    expect(created.status()).toBe(200)
    const saved = await created.json()
    const history = await context.request.get('/api/pages/' + saved.id + '/history')
    expect(history.status()).toBe(200)
    const revisionId = (await history.json()).items[0].id
    const folder = 'restart-' + saved.id.replace(/-/g, '')
    const queuedAt = Date.now()
    const configured = await context.request.put('/api/pages/' + saved.id + '/pdf-export', { headers, data: {
      enabled: true, storageProfileId: profile.id, folder, fileName: 'document.pdf',
      keepVersions: false, publishedOnly: false, expectedVersion: 0,
    } })
    expect(configured.status()).toBe(200)
    expect((await configured.json()).status).toBe('pending')
    await writeFile(evidencePath!, JSON.stringify({ pageId: saved.id, revisionId, folder, queuedAt }))
    return
  }

  expect(phase).toBe('verify')
  const evidence = JSON.parse(await readFile(evidencePath!, 'utf8'))
  const errors: string[] = []
  let fileName = ''
  await expect.poll(async () => {
    const response = await context.request.get('/api/pages/' + evidence.pageId + '/pdf-export')
    expect(response.status()).toBe(200)
    const status = await response.json()
    if (status.lastError) errors.push(status.lastError)
    fileName = status.lastFileName ?? ''
    return { status: status.status, revisionId: status.lastRevisionId, error: status.lastError || '' }
  }, { timeout: 45_000, intervals: [1_000] }).toEqual({ status: 'current', revisionId: evidence.revisionId, error: '' })
  expect(errors).toEqual([])
  expect(Date.now() - evidence.queuedAt).toBeGreaterThanOrEqual(29_000)
  // The written file carries the export's date and time (Prague): "document 2026-11-08 14-30.pdf".
  expect(fileName).toMatch(/^document \d{4}-\d\d-\d\d \d\d-\d\d\.pdf$/)
  const directory = join(process.env.ATLAS_E2E_SMB_ROOT!, evidence.folder)
  expect(await readdir(directory)).toEqual([fileName])
  const bytes = await readFile(join(directory, fileName))
  expect(bytes.subarray(0, 5).toString()).toBe('%PDF-')
  expect(bytes.subarray(-40).toString()).toContain('%%EOF')
  expect(bytes.toString('latin1')).toMatch(/\/Type\s*\/Page\b/)
})
