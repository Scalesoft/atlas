import { test, expect } from '@playwright/test';

// API fixtures isolate the UI contract: no server data is created or restored.
for (const mode of ['light', 'dark']) {
test(`history stays separate from the document on desktop and mobile (${mode})`, async ({ page }) => {
  const failures: string[] = [];
  page.on('pageerror', error => failures.push(error.stack ?? error.message));
  const text = (version: number) => `Nasazení aplikace\n\nPřed nasazením ověřte zálohu databáze.\n\n${version < 38 ? 'Starý postup: restartujte všechny služby ručně.\n\n' : ''}Po nasazení zkontrolujte dostupnost služby${version === 38 ? ' a chybové logy' : ''}.\n\n${version === 38 ? 'Výsledek zapište do provozního deníku.' : ''}`;
  const document = (version: number) => JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: text(version).split('\n\n').map(value => ({ type: 'paragraph', content: [{ type: 'text', text: value || ' ' }] })) } });
  // The per-version change summary (3.6) the list shows as +N −M lines.
  const changes = { added: 1, removed: 2, blocks: 1, sections: [], states: [], requirementsAdded: 0, requirementsRemoved: 0, contactsAdded: [], contactsRemoved: [], attachmentsAdded: 0, attachmentsRemoved: 0, titleBefore: null, contributors: [] };
  const revisions = Array.from({ length: 27 }, (_, index) => {
    const version = 38 - index;
    return { id: `r${version}`, pageId: 'page', version, title: 'Provozní příručka', authorId: index % 3 ? 'petr' : 'jana', authorName: index % 3 ? 'Petr Svoboda' : 'Jana Nováková', activeFrom: new Date(Date.UTC(2026, 8, 10 - Math.floor(index / 5), 10 - index % 5, 42)).toISOString(), activeTo: index ? '2026-09-10T10:42:00Z' : null, isCurrent: index === 0, isPublished: index === 0, lastPublishedAt: index === 0 ? '2026-09-10T10:42:00Z' : null, markdown: text(version), contentJson: document(version), changes };
  });
  // The header's history entry ("Aktualizováno …") needs a writer (canEdit) and the live revision's activeFrom.
  const live = { id: 'page', spaceId: 'space', parentId: null, title: 'Provozní příručka', version: 38, isPublished: true, publishedVersion: 38, canEdit: true, activeFrom: revisions[0]!.activeFrom, markdown: text(38), contentJson: document(38) };
  const writes: string[] = [];
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (route.request().method() !== 'GET') writes.push(path);
    let value: unknown;
    if (path === '/api/setup/status') value = { databaseReady: true, requiresSetup: false };
    else if (path === '/api/auth/me') value = { id: 'jana', username: 'jana', displayName: 'Jana Nováková', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode } };
    else if (path === '/api/auth/csrf') value = { token: 'fixture-csrf' };
    else if (path === '/api/spaces') value = [{ id: 'space', name: 'Engineering', icon: 'book', color: 'blue', role: 'Writer' }];
    else if (path === '/api/spaces/space/tree') value = { spaceId: 'space', version: 1, items: [{ ...live, sortOrder: 1024, canEdit: true }] };
    else if (path === '/api/spaces/space/pages') value = [live];
    else if (path === '/api/pages/page') value = live;
    else if (path === '/api/pages/page/history') {
      const before = Number(url.searchParams.get('beforeVersion')) || 999;
      const items = revisions.filter(item => item.version < before).slice(0, 20);
      value = { pageId: 'page', currentVersion: 38, items, nextBeforeVersion: items.at(-1)!.version > 12 ? items.at(-1)!.version : null };
    } else if (path.startsWith('/api/pages/page/history/')) value = revisions.find(item => item.id === path.split('/').at(-1));
    else if (/\/personal-state$|\/visit$/.test(path)) value = { isFavorite: false, visitedAt: null };
    else if (path.endsWith('/tags')) value = { pageId: 'page', tags: [] };
    else if (path.startsWith('/api/me/library/')) value = { items: [], hasMore: false, nextOffset: null };
    else { await route.fulfill({ status: 404, json: { message: 'Fixture endpoint not available' } }); return; }
    await route.fulfill({ json: value });
  });
  await page.goto('/#/pages/page');
  const documentBody = page.locator('.document-body .ProseMirror');
  await expect(documentBody).toContainText('Výsledek zapište');
  const original = await documentBody.textContent();
  const entry = page.getByRole('button', { name: 'Zobrazit historii změn stránky' });
  await entry.click();
  const panel = page.getByRole('dialog', { name: 'Historie stránky' });
  await expect(panel).toBeVisible();
  await expect(panel).toHaveCSS('border-radius', '0px');
  await expect(panel).toHaveCSS('height', '900px');
  // Changes render as the page reads (blocks marked Přidáno/Odstraněno); Markdown keeps the exact word diff.
  await expect(panel.locator('.rendered-item[data-kind="removed"]').filter({ hasText: 'restartujte' })).toBeVisible();
  await expect(panel.locator('.rendered-item[data-kind="added"]').filter({ hasText: 'Výsledek zapište' })).toBeVisible();
  // An edited paragraph stays one block; the added words are marked in place (CSS Custom Highlight API).
  await expect(panel.locator('.rendered-item[data-kind="modified"] [data-side="after"]')).toContainText('a chybové logy');
  await expect.poll(() => page.evaluate(() => [...(CSS.highlights.get('atlas-history-added') ?? [])].map(range => (range as Range).toString().trim()).join('|'))).toBe('a chybové logy');
  // Only words were added: the earlier wording stays folded away.
  await expect(panel.locator('.rendered-before')).toBeHidden();
  await panel.getByRole('radio', { name: 'Markdown', exact: true }).click();
  await expect(panel.locator('.text-diff del').filter({ hasText: 'restartujte' })).toBeVisible();
  await panel.getByRole('radio', { name: 'Náhled', exact: true }).click();
  await expect(panel.locator('.rendered-item[data-kind="removed"]').filter({ hasText: 'restartujte' })).toBeVisible();
  await expect(panel.locator('.history-context')).toContainText('Historická verze');
  await expect(panel.locator('.history-restore-confirm')).toHaveCount(0);
  // The restore confirmation unfolds the footer; it does not insert its 70 px under the preview in one frame.
  const unfold = await page.evaluate(() => new Promise<{ reduced: boolean; steps: number[] }>(resolve => {
    const toggle = document.querySelector<HTMLElement>('.history-restore-toggle')!;
    const tops: number[] = [], started = performance.now();
    toggle.click();
    const tick = () => {
      tops.push(toggle.getBoundingClientRect().top);
      if (performance.now() - started < 600) requestAnimationFrame(tick);
      else resolve({ reduced: matchMedia('(prefers-reduced-motion: reduce)').matches, steps: tops.slice(1).map((top, index) => Math.abs(top - tops[index]!)) });
    };
    requestAnimationFrame(tick);
  }));
  if (!unfold.reduced) expect(Math.max(...unfold.steps)).toBeLessThan(40);
  await panel.locator('.history-restore-toggle').click();
  await expect(panel.locator('.history-restore-confirm')).toHaveCount(0);
  await expect(panel.locator('.hv-size').first()).toContainText('+1 −2');
  await page.screenshot({ path: `artifacts/history-desktop-${mode}.png`, animations: 'disabled' });
  await panel.locator('[data-revision="r37"]').click();
  await panel.getByRole('radio', { name: 'Celá verze', exact: true }).click();
  await expect(panel.locator('.md-renderer')).toContainText('restartujte');
  // The full version reads rendered or as its stored Markdown; the choice is shared with the change view.
  await panel.getByRole('radio', { name: 'Markdown', exact: true }).click();
  await expect(panel.locator('.history-source')).toContainText('Starý postup: restartujte');
  await panel.getByRole('radio', { name: 'Změny', exact: true }).click();
  await expect(panel.locator('.text-diff')).toBeVisible();
  await panel.getByRole('radio', { name: 'Náhled', exact: true }).click();
  await panel.getByRole('radio', { name: 'Celá verze', exact: true }).click();
  await expect(panel.locator('.md-renderer')).toContainText('restartujte');
  await panel.locator('[data-revision="r37"]').focus();
  await page.keyboard.press('ArrowDown');
  await expect(panel.locator('[data-revision="r36"]')).toHaveAttribute('aria-current', 'true');
  expect(await documentBody.textContent()).toBe(original);
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(entry).toBeFocused();
  expect(await documentBody.textContent()).toBe(original);
  expect(writes.filter(path => path.includes('/restore'))).toEqual([]);
  await page.setViewportSize({ width: 390, height: 844 });
  // The existing mobile shell hides metadata; the menu remains a second entry.
  await page.getByRole('button', { name: 'Další akce stránky', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Historie', exact: true }).click();
  await expect(panel.locator('.rendered-item[data-kind="removed"]').filter({ hasText: 'restartujte' })).toBeVisible();
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  expect(await panel.locator('.history-preview-body').evaluate(element => element.getBoundingClientRect().bottom <= window.innerHeight)).toBe(true);
  await page.screenshot({ path: `artifacts/history-mobile-${mode}.png`, animations: 'disabled' });
  await panel.getByRole('button', { name: 'Zavřít', exact: true }).click();
  await expect(panel).toHaveCount(0);
  expect(failures).toEqual([]);
});
}
