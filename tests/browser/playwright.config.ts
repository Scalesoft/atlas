import { defineConfig } from '@playwright/test'
// Set before workers start: automatic error-context DOM snapshots can contain
// setup passwords or one-time tokens even when screenshots and traces are off.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
// Specs read ATLAS_URL directly; BASE_URL/PLAYWRIGHT_BASE_URL are accepted as aliases (README, Podman).
const aliasURL = process.env.PLAYWRIGHT_BASE_URL ?? process.env.BASE_URL
if (!process.env.ATLAS_URL && aliasURL) process.env.ATLAS_URL = aliasURL
// Container runs pin this explicitly; unset keeps the host browser/OS default. Playwright has no top-level
// reducedMotion test option, only contextOptions; a spec's test.use({ contextOptions }) replaces this object whole.
const reducedMotion = process.env.PLAYWRIGHT_REDUCED_MOTION as 'reduce' | 'no-preference' | undefined
export default defineConfig({
  testDir: '.', testMatch: '*.spec.ts', workers: 1, retries: 0,
  timeout: 60_000, expect: { timeout: 15_000 },
  // The specs name the Czech interface: since 4.0 it follows the browser's language (launch argument: contexts a spec makes itself).
  use: { baseURL: process.env.ATLAS_URL ?? 'http://127.0.0.1:5219', headless: true, locale: 'cs-CZ', launchOptions: { args: ['--lang=cs-CZ'] }, viewport: { width: 1280, height: 900 }, ignoreHTTPSErrors: false, trace: 'retain-on-failure', screenshot: 'only-on-failure', ...(reducedMotion ? { contextOptions: { reducedMotion } } : {}) },
  outputDir: '../../artifacts/browser-tests',
  reporter: [['list']],
})
