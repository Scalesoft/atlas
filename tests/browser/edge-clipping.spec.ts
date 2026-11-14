import { test, expect, type Page } from '@playwright/test';

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace: 'off', screenshot: 'off', video: 'off' });

// Walks every settings, profile and space-settings screen reachable by link, opens each screen's
// "Nový/Přidat…" flows, and fails when a clipping ancestor cuts a control or its keyboard focus
// ring. A permanent `overflow: hidden` around revealed form content once cut rings on 44 screens.

async function signIn(page: Page, username: string, password: string) {
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno', { exact: true }).fill(username);
  await page.getByLabel('Heslo', { exact: true }).evaluate((element, secret) => {
    const input = element as HTMLInputElement;
    input.value = secret;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }, password);
  await page.getByRole('button', { name: 'Přihlásit se', exact: true }).click();
  await expect(page.getByRole('button', { name: /^Hledat ve wiki/ })).toBeVisible();
}

interface Clipped { sides: string; control: string; label: string; clipper: string }

/** Focuses every control and reports the ones whose box or focus ring the nearest clipping ancestor cuts. */
function clippedControls(): Clipped[] {
  if (!document.getElementById('edge-clipping-scan')) {
    const style = document.createElement('style');
    style.id = 'edge-clipping-scan';
    style.textContent = '*,*::before,*::after{transition:none!important}';
    document.head.append(style);
  }
  const describe = (element: Element) => element.tagName.toLowerCase() + (typeof element.className === 'string' && element.className.trim()
    ? '.' + element.className.trim().split(/\s+/).filter(name => !/[:[/]/.test(name)).slice(0, 3).join('.') : '');
  // Rings are outlines or outer box-shadows (Tailwind's ring utilities).
  const ringOf = (style: CSSStyleDeclaration) => {
    const ring = { left: 0, right: 0, top: 0, bottom: 0 };
    const width = parseFloat(style.outlineWidth) || 0;
    if (style.outlineStyle !== 'none' && width > 0) {
      const outline = Math.max(0, width + (parseFloat(style.outlineOffset) || 0));
      ring.left = ring.right = ring.top = ring.bottom = outline;
    }
    if (style.boxShadow !== 'none') for (const shadow of style.boxShadow.split(/,(?![^()]*\))/)) {
      if (/\binset\b/.test(shadow)) continue;
      const [x = 0, y = 0, blur = 0, spread = 0] = (shadow.replace(/[a-z]+\([^)]*\)|#[0-9a-f]+/gi, '').match(/-?[\d.]+px/g) ?? []).map(parseFloat);
      if (spread <= 0 && blur <= 0) continue;
      const reach = spread + blur / 2;
      ring.left = Math.max(ring.left, reach - x); ring.right = Math.max(ring.right, reach + x);
      ring.top = Math.max(ring.top, reach - y); ring.bottom = Math.max(ring.bottom, reach + y);
    }
    return ring;
  };
  const found: Clipped[] = [];
  const selector = 'input:not([type=hidden]),textarea,select,button,a[href],[tabindex]:not([tabindex="-1"]),[role=radio],[role=checkbox],[role=switch],[role=tab],[role=option]';
  for (const element of document.querySelectorAll<HTMLElement>(selector)) {
    const box = element.getBoundingClientRect();
    if (box.width < 2 || box.height < 2 || (element as HTMLButtonElement).disabled) continue;
    const own = getComputedStyle(element);
    if (own.visibility === 'hidden' || own.opacity === '0' || element.closest('[inert],[aria-hidden=true],.sr-only,.st-sr-only,fieldset:disabled')) continue;
    let clipper: HTMLElement | null = element.parentElement;
    while (clipper && clipper !== document.documentElement && getComputedStyle(clipper).overflowX === 'visible' && getComputedStyle(clipper).overflowY === 'visible') clipper = clipper.parentElement;
    if (!clipper || clipper === document.documentElement) continue;
    element.focus({ preventScroll: true });
    if (document.activeElement !== element) continue;
    const ring = ringOf(getComputedStyle(element));
    const style = getComputedStyle(clipper), bounds = clipper.getBoundingClientRect();
    const left = bounds.left + parseFloat(style.borderLeftWidth), right = bounds.right - parseFloat(style.borderRightWidth);
    const top = bounds.top + parseFloat(style.borderTopWidth), bottom = bounds.bottom - parseFloat(style.borderBottomWidth);
    if (box.right <= left || box.left >= right || box.bottom <= top || box.top >= bottom) continue; // scrolled out of view
    // In a scroller only the content's own edges count; an item cut mid-scroll is fine.
    const scrollsX = /auto|scroll/.test(style.overflowX) && clipper.scrollWidth > clipper.clientWidth + 1;
    const scrollsY = /auto|scroll/.test(style.overflowY) && clipper.scrollHeight > clipper.clientHeight + 1;
    const edge = {
      left: scrollsX ? left - clipper.scrollLeft : left, right: scrollsX ? left - clipper.scrollLeft + clipper.scrollWidth : right,
      top: scrollsY ? top - clipper.scrollTop : top, bottom: scrollsY ? top - clipper.scrollTop + clipper.scrollHeight : bottom,
    };
    const cut = {
      left: style.overflowX === 'visible' ? 0 : edge.left - (box.left - ring.left),
      right: style.overflowX === 'visible' ? 0 : box.right + ring.right - edge.right,
      top: style.overflowY === 'visible' ? 0 : edge.top - (box.top - ring.top),
      bottom: style.overflowY === 'visible' ? 0 : box.bottom + ring.bottom - edge.bottom,
    };
    const sides = Object.entries(cut).filter(([, by]) => by > 0.5).map(([side, by]) => `${side} ${by.toFixed(1)}px`);
    if (sides.length) found.push({
      sides: sides.join(', '), control: describe(element), clipper: describe(clipper),
      label: (element.getAttribute('aria-label') || element.textContent || (element as HTMLInputElement).placeholder || '').trim().replace(/\s+/g, ' ').slice(0, 40),
    });
  }
  (document.activeElement as HTMLElement | null)?.blur();
  return found;
}

const opener = /^(Nový|Nová|Nové|Přidat|Vytvořit|Založit|Upravit|Změnit|Připojit)/;

async function scan(page: Page, where: string, problems: string[]) {
  // Let reveals and panels finish opening, then take keyboard modality so rings show.
  await page.waitForTimeout(700);
  await page.keyboard.press('Tab');
  for (const clipped of await page.evaluate(clippedControls))
    problems.push(`${where}: ${clipped.control} "${clipped.label}" cut ${clipped.sides} by ${clipped.clipper}`);
}

test.beforeEach(async ({ page }) => {
  const password = process.env.ATLAS_E2E_PASSWORD;
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== '1' || !password, 'Requires the initialized disposable development wiki and its private test credential.');
  await signIn(page, process.env.ATLAS_E2E_USER ?? 'browser-admin', password!);
});

for (const [width, height] of [[1440, 900], [390, 844]] as const) {
  test(`no control or focus ring is cut at the edges of settings and profile screens (${width}px)`, async ({ page }) => {
    test.setTimeout(600_000);
    await page.setViewportSize({ width, height });
    const spaceId = await page.evaluate(async () => {
      const body = await (await fetch('/api/spaces', { credentials: 'include' })).json();
      return (Array.isArray(body) ? body : body.items ?? body.spaces ?? [])[0]?.id as string | undefined;
    });
    const queue = ['#/settings/security/overview', '#/profile/account', ...(spaceId ? [`#/spaces/${spaceId}/settings/general`] : [])];
    const seen = new Set(queue);
    const problems: string[] = [];
    while (queue.length) {
      const hash = queue.shift()!;
      await page.goto('/' + hash);
      await scan(page, hash, problems);
      for (const href of await page.evaluate(() => [...document.querySelectorAll('a[href^="#/"]')].map(link => link.getAttribute('href')!.split('?')[0]!)))
        if (/^#\/(settings|profile|spaces\/[^/]+\/settings)\//.test(href) && !seen.has(href)) { seen.add(href); queue.push(href); }
      for (let index = 0; index < 4; index++) {
        const button = (await page.getByRole('button', { name: opener }).all())[index];
        if (!button || !(await button.isVisible()) || !(await button.isEnabled())) continue;
        const label = (await button.textContent())?.trim().slice(0, 30);
        await button.click({ timeout: 3_000 }).catch(() => undefined);
        await scan(page, `${hash} → ${label}`, problems);
        await page.keyboard.press('Escape');
        await page.goto('/' + hash);
      }
    }
    expect(seen.size).toBeGreaterThan(20);
    expect([...new Set(problems)]).toEqual([]);
  });
}
