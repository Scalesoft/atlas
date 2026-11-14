import { test, expect } from '@playwright/test'

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off', actionTimeout: 15_000 })

test('published Production host behind HTTPS proxy completes the initial wizard', async ({ page, request }) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_SETUP !== '1', 'Requires a new disposable installation.')
  test.setTimeout(120_000)
  const token = process.env.ATLAS_E2E_BOOTSTRAP_TOKEN!
  const password = process.env.ATLAS_E2E_PASSWORD!
  expect(token?.length).toBeGreaterThanOrEqual(32)
  expect(password?.length).toBeGreaterThanOrEqual(12)
  expect(process.env.ATLAS_URL).toMatch(/^https:\/\//)
  const status = await (await request.get('/api/setup/status')).json()
  expect(status.databaseReady).toBe(true)
  expect(status.requiresSetup).toBe(true)
  expect(status.tokenSource.filePath).toMatch(/(?:^|[/\\])bootstrap-token\.txt$/)
  expect((await request.get('/health/ready')).status()).toBe(200)
  await page.goto('/')
  await page.getByLabel('Název wiki', { exact: true }).fill('Deployment acceptance')
  await page.getByRole('button', { name: 'Pokračovat', exact: true }).click()
  await page.getByLabel('Vaše jméno', { exact: true }).fill('Deployment Admin')
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill('deploy-admin')
  // Avoid literal secret values in Playwright's fill() timeout call logs.
  for (const [field, value] of [[page.getByLabel(/^Heslo/), password], [page.getByLabel(/^Instalační token/), token]] as const) {
    await field.evaluate((element, secret) => {
      const input = element as HTMLInputElement
      input.value = secret
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }, value)
  }
  const completed = page.waitForResponse(r => r.url().endsWith('/api/setup/complete') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Vytvořit wiki', exact: true }).click()
  expect((await completed).status()).toBe(200)
  await expect(page.getByRole('heading', { name: 'Připojte své nástroje' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Připojte své nástroje' })).toBeVisible()
  for (const step of [1, 2, 3]) {
    await expect(page.getByText(`Krok ${step} z 4`, { exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Další možnost', exact: true }).click()
  }
  await expect(page.getByText(/^Krok 4 z 4/)).toBeVisible()
  await page.locator('footer').getByRole('button', { name: 'Otevřít wiki', exact: true }).click()
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible()
  expect((await (await request.get('/api/setup/status')).json()).requiresSetup).toBe(false)
  const cookies = await page.context().cookies()
  expect(cookies.some(c => c.httpOnly && c.secure)).toBe(true)
})
