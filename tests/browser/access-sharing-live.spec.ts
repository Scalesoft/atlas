import { randomBytes } from 'node:crypto';
import { test, expect } from '@playwright/test';

test.use({ trace: 'off', screenshot: 'off', video: 'off' });

test('real server: issue fixed publication, open anonymously, revoke, and refuse reload', async ({ page, browser }) => {
  test.skip(process.env.ATLAS_SHARING_LIVE !== '1', 'Requires the disposable sharing acceptance host.');
  test.setTimeout(120_000);
  const request = page.context().request;
  async function send(method: string, path: string, data?: unknown) {
    const csrf = await (await request.get('/api/auth/csrf')).json();
    return request.fetch(path, { method, data, headers: { 'X-CSRF-TOKEN': csrf.token } });
  }
  const setup = await (await request.get('/api/setup/status')).json();
  expect(setup.databaseReady).toBe(true);
  expect(setup.requiresSetup, 'Use an empty isolated database for this acceptance test.').toBe(true);
  const installed = await send('POST', '/api/setup/complete', {
    bootstrapToken: 'browser-sharing-disposable-bootstrap-fixture', workspaceName: 'Sharing acceptance',
    username: 'sharing-admin', displayName: 'Správce sdílení', password: randomBytes(24).toString('hex'),
  });
  expect(installed.status()).toBe(200);
  await page.goto('/');
  for (let step = 0; step < 3; step++) {
    await page.getByRole('button', { name: 'Další možnost', exact: true }).click();
  }
  await page.locator('footer').getByRole('button', { name: 'Otevřít wiki', exact: true }).click();
  const spaces = await (await request.get('/api/spaces')).json();
  const spaceId = spaces[0].id;
  const contentJson = JSON.stringify({ format: 'atlas-rich', version: 1, plugins: [{ id: 'youtrack', version: 1 }, { id: 'math', version: 1 }], document: { type: 'doc', content: [
    { type: 'paragraph', content: [{ type: 'text', text: 'Ověřená veřejná publikace.' },
      { type: 'youtrackIssue', attrs: { id: 'internal-fixture', key: 'APP-42', title: 'Kontrola vydání', state: 'cached-private-fixture', url: 'https://private.invalid/issue/APP-42' } }] },
    { type: 'mathBlock', attrs: { data: { version: 1, expression: 'E = mc^2' } } },
  ] } });
  const created = await send('POST', '/api/pages', { spaceId, title: 'Příručka pro partnera', contentJson, markdown: 'Ověřená veřejná publikace.' });
  expect(created.status()).toBe(200);
  const document = await created.json();
  expect((await send('POST', `/api/pages/${document.id}/publish`, { expectedVersion: document.version })).status()).toBe(200);
  expect((await send('PUT', `/api/spaces/${spaceId}/external-sharing`, { enabled: true, expectedVersion: null })).status()).toBe(200);
  await page.goto(`/#/pages/${document.id}`);
  await page.getByRole('button', { name: 'Kdo má přístup', exact: true }).first().click();
  // „Kdo má přístup“ is a view of the page (#/pages/<id>/access), its parts are links.
  const view = page.locator('[data-page-sharing]');
  await expect(view).toContainText('Správce sdílení');
  await view.getByRole('link', { name: /Anonymní odkazy/ }).click();
  await view.getByRole('button', { name: 'Nový odkaz', exact: true }).click();
  await view.getByLabel('Název odkazu', { exact: true }).fill('Partnerská kontrola');
  await view.getByRole('button', { name: 'Vytvořit odkaz', exact: true }).click();
  const secret = view.locator('[data-issued-link]');
  await secret.getByRole('button', { name: 'Zobrazit', exact: true }).click();
  const sharedUrl = (await secret.locator('[data-one-time-value]').textContent())!.trim();
  expect(sharedUrl).toMatch(/\/s\/sas_/);
  await secret.getByLabel('Odkaz mám uložený').check();
  await secret.getByRole('button', { name: 'Pokračovat', exact: true }).click();
  const anonymous = await browser.newContext();
  const visitor = await anonymous.newPage();
  const privateCalls: string[] = [];
  visitor.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/api/') && !path.startsWith('/api/shared/')) privateCalls.push(path);
  });
  try {
    const response = await visitor.goto(sharedUrl);
    expect(response?.headers()['cache-control']).toBe('no-store');
    expect(response?.headers()['referrer-policy']).toBe('no-referrer');
    await expect(visitor.getByRole('heading', { name: 'Příručka pro partnera', exact: true })).toBeVisible();
    await expect(visitor.getByRole('document', { name: 'Sdílený dokument pouze pro čtení', exact: true })).toContainText('Ověřená veřejná publikace.');
    await expect(visitor.getByRole('document', { name: 'Sdílený dokument pouze pro čtení', exact: true })).toContainText('APP-42: Kontrola vydání');
    await expect(visitor.locator('math')).toBeVisible();
    await expect(visitor.getByRole('document', { name: 'Sdílený dokument pouze pro čtení', exact: true })).not.toContainText('cached-private-fixture');
    await expect(visitor.locator('[contenteditable=true]')).toHaveCount(0);
    const current = await (await request.get(`/api/pages/${document.id}`)).json();
    const revised = await (await send('PUT', `/api/pages/${document.id}`, { title: 'Novější verze', contentJson: contentJson.replace('Ověřená veřejná publikace.', 'Novější neveřejný text.'), markdown: 'Novější neveřejný text.', expectedVersion: current.version })).json();
    expect((await send('POST', `/api/pages/${document.id}/publish`, { expectedVersion: revised.version })).status()).toBe(200);
    await visitor.reload();
    await expect(visitor.getByRole('document', { name: 'Sdílený dokument pouze pro čtení', exact: true })).toContainText('Ověřená veřejná publikace.');
    await expect(visitor.getByRole('document', { name: 'Sdílený dokument pouze pro čtení', exact: true })).not.toContainText('Novější neveřejný text.');
    await view.getByRole('button', { name: 'Zneplatnit odkaz Partnerská kontrola', exact: true }).click();
    await page.getByRole('dialog', { name: 'Zneplatnit odkaz', exact: true }).getByRole('button', { name: 'Zneplatnit odkaz', exact: true }).click();
    await expect(view.getByText('Zneplatněný', { exact: true })).toBeVisible();
    await visitor.reload();
    await expect(visitor.getByRole('alert')).toContainText('Odkaz není dostupný');
    expect(privateCalls).toEqual([]);
  } finally {
    const policy = await (await request.get(`/api/spaces/${spaceId}/external-sharing`)).json();
    await send('PUT', `/api/spaces/${spaceId}/external-sharing`, { enabled: false, expectedVersion: policy.version });
    await anonymous.close();
  }
});
