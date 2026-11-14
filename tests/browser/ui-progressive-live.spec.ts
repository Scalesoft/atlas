import { expect, test, type BrowserContext, type Page } from '@playwright/test';

// Real published Atlas + throwaway Podman database. There are no route fulfillments/API mocks.
// The fake external model belongs to the acceptance pod; the test never sends a key or wiki content to a provider.
test.use({ trace: 'off', screenshot: 'off', video: 'off' });

async function json(context: BrowserContext, path: string) {
  const response = await context.request.get(path);
  expect(response.status(), path).toBe(200);
  return response.json();
}

async function put(context: BrowserContext, path: string, data: unknown) {
  const csrf = await json(context, '/api/auth/csrf');
  const response = await context.request.put(path, { headers: { 'X-CSRF-TOKEN': csrf.token }, data });
  expect(response.status(), path).toBe(200);
  return response.json();
}

async function overflowingControls(page: Page) {
  return page.locator('input:visible, [role="radio"]:visible, [data-save-bar-save]:visible').evaluateAll(elements => elements.filter(element => {
    const box = element.getBoundingClientRect();
    return box.width > 0 && (box.left < -1 || box.right > innerWidth + 1);
  }).map(element => {
    const style = getComputedStyle(element);
    const box = element.getBoundingClientRect();
    const parent = element.parentElement;
    return {
      tag: element.tagName, id: element.id, type: element.getAttribute('type'), role: element.getAttribute('role'),
      classes: element.className, label: element.getAttribute('aria-label'),
      rect: { x: box.x, y: box.y, width: box.width, height: box.height, right: box.right },
      style: { position: style.position, clip: style.clip, clipPath: style.clipPath, opacity: style.opacity, visibility: style.visibility, transform: style.transform },
      parent: parent ? { tag: parent.tagName, classes: parent.className, ariaHidden: parent.getAttribute('aria-hidden') } : null,
      inertAncestor: element.closest('[inert]')?.tagName ?? null,
      ariaHiddenAncestor: element.closest('[aria-hidden="true"]')?.tagName ?? null,
    };
  }));
}

async function fits(page: Page, width: number) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width + 1);
  // Layout can still be transitioning after a viewport change. A closing navigation drawer stays
  // painted during its exit, but its inert/aria-hidden descendants are no longer actionable.
  await expect.poll(async () => (await overflowingControls(page)).filter(element => !element.inertAncestor && !element.ariaHiddenAncestor), {
    message: 'Interactive choices and inputs settle inside the viewport.',
  }).toEqual([]);
}

test('live AI forms reveal the selected branch, recover hidden errors and preserve personal drafts across widths', async ({ page, context }, info) => {
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1', 'Requires an isolated initialized acceptance wiki.');
  test.setTimeout(120_000);
  const status = await json(context, '/api/setup/status');
  expect(status.requiresSetup).toBe(false);
  expect(status.version).toMatch(/^2\.6\.0-dev/);
  await info.attach('live-build.json', { body: JSON.stringify({ version: status.version, apiMocks: false }), contentType: 'application/json' });
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(process.env.ATLAS_E2E_USER ?? 'browser-admin');
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, value) => {
    (element as HTMLInputElement).value = value!;
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, process.env.ATLAS_E2E_PASSWORD);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  const endpoint = '/api/admin/ai/profiles/quick';
  const original = await json(context, endpoint);
  const prefsEndpoint = '/api/me/ai-preferences';
  const originalPrefs = await json(context, prefsEndpoint);
  const profileRequest = (view: Record<string, unknown>, version: unknown) => ({ ...view,
    provider: view.provider ?? 'openai-compatible', location: view.location ?? 'company', model: view.model ?? '',
    apiKey: null, clearApiKey: false, expectedVersion: version,
  });
  try {
    // Isolate this role without changing the stored connection/key or depending on another spec's mode.
    await put(context, endpoint, { ...profileRequest(original, original.version), enabled: false, useChatProfile: false });
    await page.goto('/#/settings/general/modules/ai/quick');
    const role = page.locator('[data-ai-profile="quick"]');
    await expect(role.locator('[data-ai-source]')).toBeVisible();
    await role.locator('[data-ai-saved-toggle]').click();
    await role.locator('[data-ai-saved-new]').click();
    const prepared = role.locator('[data-ai-saved-editor]');
    await expect(prepared).toBeVisible();
    await expect(prepared.locator('[data-ai-model]')).toBeHidden();
    await prepared.getByRole('radio', { name: /Firemní LM Studio/ }).click();
    await expect(prepared.locator('[data-ai-model]')).toBeVisible();
    const advanced = prepared.getByRole('button', { name: /^Další nastavení/ });
    await expect(advanced).toHaveAttribute('aria-expanded', 'false');
    for (const width of [390, 1366, 3440]) {
      await page.setViewportSize({ width, height: 1000 });
      await info.attach(`resize-controls-${width}.json`, { body: JSON.stringify(await overflowingControls(page)), contentType: 'application/json' });
      await fits(page, width);
      await info.attach(`provider-${width}.png`, { body: await page.screenshot({ animations: 'disabled' }), contentType: 'image/png' });
    }
    await prepared.locator('[data-save-bar-cancel]').click();
    await expect(prepared).toBeHidden();
    await role.locator('[data-ai-saved-toggle]').click();

    await role.locator('[data-ai-source] [data-value="custom"] [role="radio"]').click();
    const current = role.locator('[data-ai-current-fields]');
    await current.getByRole('radio', { name: /Firemní LM Studio/ }).click();
    await current.locator('[data-ai-url]').fill(process.env.ATLAS_E2E_FAKE_AI_URL ?? 'http://fake-ai:1234/v1');
    await current.locator('[data-ai-model]').fill('unfinished-local-draft');
    const currentAdvanced = current.getByRole('button', { name: /^Další nastavení/ });
    await currentAdvanced.click();
    await current.locator('[data-ai-timeout]').fill('0');
    await currentAdvanced.click();
    await role.locator('[data-save-bar-save]').click();
    await expect(currentAdvanced).toHaveAttribute('aria-expanded', 'true');
    await expect(current.locator('[data-ai-timeout]')).toBeFocused();
    await expect(current.locator('[data-ai-timeout]')).toHaveAttribute('aria-invalid', 'true');
    await role.locator('[data-ai-source] [data-value="chat"] [role="radio"]').click();
    await expect(current).toBeHidden();
    await role.locator('[data-ai-source] [data-value="custom"] [role="radio"]').click();
    await expect(current.locator('[data-ai-model]')).toHaveValue('unfinished-local-draft');
    await role.locator('[data-ai-source] [data-value="chat"] [role="radio"]').click();
    await role.locator('[data-save-bar-save]').click();
    await expect.poll(async () => (await json(context, endpoint)).useChatProfile).toBe(true);
    const stored = await json(context, endpoint);
    expect(stored.model ?? '').toBe(original.model ?? '');
    expect(stored.timeoutSeconds).toBe(original.timeoutSeconds);

    await page.goto('/#/profile/ai');
    const preferences = page.getByRole('region', { name: 'Předvolby AI', exact: true });
    const mode = preferences.locator('[data-ai-suggestions-mode]');
    await expect(mode).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await mode.getByRole('radio', { name: 'Zapnuto', exact: true }).click();
    const types = preferences.locator('[data-ai-suggestion-types]');
    const contact = types.getByRole('checkbox', { name: /^Kontakty/ });
    const initiallyChecked = await contact.getAttribute('aria-checked');
    await contact.click();
    await mode.getByRole('radio', { name: 'Vypnuto', exact: true }).click();
    await expect(types).toBeHidden();
    await mode.getByRole('radio', { name: 'Zapnuto', exact: true }).click();
    await expect(contact).toHaveAttribute('aria-checked', initiallyChecked === 'true' ? 'false' : 'true');
    await mode.getByRole('radio', { name: 'Vypnuto', exact: true }).click();
    await preferences.getByRole('button', { name: 'Uložit předvolby', exact: true }).click();
    await expect.poll(async () => (await json(context, prefsEndpoint)).editorSuggestions).toBe('off');
    await page.reload();
    await expect(mode.getByRole('radio', { name: 'Vypnuto', exact: true })).toHaveAttribute('aria-checked', 'true');
    await expect(types).toBeHidden();
    await mode.getByRole('radio', { name: 'Zapnuto', exact: true }).click();
    await expect(contact).toHaveAttribute('aria-checked', initiallyChecked === 'true' ? 'false' : 'true');
    await fits(page, 390);
    await info.attach('profile-draft-390.png', { body: await page.screenshot({ animations: 'disabled' }), contentType: 'image/png' });
    await preferences.locator('[data-save-bar-cancel]').click();
    expect(errors).toEqual([]);
  } finally {
    const latest = await json(context, endpoint);
    await put(context, endpoint, profileRequest(original, latest.version));
    const latestPrefs = await json(context, prefsEndpoint);
    await put(context, prefsEndpoint, { ...originalPrefs, expectedVersion: latestPrefs.version });
  }
});
