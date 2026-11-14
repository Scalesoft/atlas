import { expect, test } from '@playwright/test';

// Isolated UI fixtures: run against a Vite server, without signing in or writing
// to a wiki. API responses are deliberately held until layout has been measured.
const space = {
  id: 'motion-space', name: 'Vývoj', description: 'Dokumentace týmu', icon: 'book', color: 'blue', role: 'Reader',
  pinPolicy: 'Default', pinned: true, pageCount: 8, lastChangedAt: '2026-09-10T10:00:00Z',
};
const items = Array.from({ length: 8 }, (_, index) => ({
  id: `motion-${index}`, spaceId: space.id, spaceName: space.name,
  title: `Stránka ${index + 1}`, parentId: null, version: 1, isPublished: true,
  isFavorite: false, publishedAt: '2026-09-10T10:00:00Z',
  changedAt: '2026-09-10T10:00:00Z', visitedAt: '2026-09-10T10:00:00Z',
}));

for (const width of [1440, 390]) {
  test(`Knihovna keeps its layout through delayed loading and refresh at ${width}px`, async ({ page }) => {
    // The loops are checked running first, also on a host that reports reduced motion (README, "Konfigurace").
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.setViewportSize({ width, height: 1000 });
    let release!: () => void;
    let ready = new Promise<void>(resolve => { release = resolve; });
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname;
      let json: unknown;
      if (path === '/api/setup/status') json = { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true };
      else if (path === '/api/auth/me') json = {
        id: 'motion-user', username: 'motion', displayName: 'Martin', isWikiAdmin: false,
        appearance: { layout: 'connected', accent: 'blue', mode: width === 1440 ? 'light' : 'dark' },
      };
      else if (path === '/api/spaces') json = [space];
      else if (path.startsWith('/api/me/library/')) {
        await ready;
        json = { items: path.endsWith('/favorites') ? [] : items, hasMore: false, nextOffset: null };
      } else if (path === '/api/ai/capabilities') json = { answer: false, assistant: false, pageSummary: false };
      else throw new Error(`Unexpected fixture request: ${path}`);
      await route.fulfill({ json });
    });

    // The wiki root is Knihovna on Prostory, which has nothing to wait for (the spaces come with the start-up). Nedávné
    // is its first personal list; the pinned space waits collapsed in the sidebar.
    await page.goto('/#/library/recent');
    const library = page.locator('.personal-library');
    await expect(library.locator('.loading-skeleton')).toHaveCount(1);
    await page.evaluate(() => document.fonts.ready);
    // The page rises in once (st-rise-in on .library-overview itself) while the list is still held: it is measured at
    // rest, so that the entrance is not read as a shift.
    await page.locator('.library-overview').evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)));
    const geometry = () => page.evaluate(() => ['.library-tabs', '.library-heading', '.library-content'].map(selector => {
      const rect = document.querySelector(selector)!.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width };
    }));
    const before = await geometry();
    expect(await page.locator('.loading-skeleton-rows').first().evaluate(element => getComputedStyle(element).animationName)).toBe('atlas-loading-pulse');
    release();
    await expect(library.locator('.library-item')).toHaveCount(8);
    await expect(page.locator('.personal-library[aria-busy="true"]')).toHaveCount(0);
    await expect(library.locator('.loading-skeleton')).toHaveCount(0);
    const after = await geometry();
    for (let index = 0; index < before.length; index++) {
      expect(Math.abs(after[index]!.y - before[index]!.y)).toBeLessThan(2);
      expect(Math.abs(after[index]!.x - before[index]!.x)).toBeLessThan(2);
      expect(Math.abs(after[index]!.width - before[index]!.width)).toBeLessThan(2);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 1440) {
      await expect(page.locator('.sidebar-space-link')).toHaveText(space.name);
      await expect(page.locator('.sidebar-space-row .tree-toggle')).toHaveAttribute('aria-expanded', 'false');
    }

    // Refreshing keeps the loaded rows in place while the next answer is on its way.
    ready = new Promise<void>(resolve => { release = resolve; });
    const refresh = page.locator('.library-refresh');
    await refresh.click();
    await expect(refresh).toHaveAttribute('aria-busy', 'true');
    await expect(library.locator('.library-item')).toHaveCount(8);
    await expect(library.locator('.loading-skeleton')).toHaveCount(0);
    release();
    await expect(refresh).toHaveAttribute('aria-busy', 'false');
    await page.screenshot({ path: `../../artifacts/wiki-motion-${width}.png`, fullPage: true });

    // Reduced motion: loops stand still and the progress line only fades in (motion.css).
    await page.emulateMedia({ reducedMotion: 'reduce' });
    ready = new Promise<void>(resolve => { release = resolve; });
    await refresh.click();
    await expect(refresh).toHaveAttribute('aria-busy', 'true');
    expect(await refresh.locator('svg').evaluate(element => getComputedStyle(element).animationName)).not.toContain('atlas-spin');
    expect(await library.evaluate(element => getComputedStyle(element, '::after').animationName)).not.toContain('atlas-progress');
    release();
    await expect(refresh).toHaveAttribute('aria-busy', 'false');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.locator('.breadcrumb-root').hover();
    const tooltip = page.locator('[data-slot="tooltip-content"]');
    await expect(tooltip).toBeVisible();
    expect(await tooltip.evaluate(element => getComputedStyle(element).animationName)).toBe('atlas-backdrop-in');
    expect(await tooltip.evaluate(element => getComputedStyle(element).animationDuration)).toBe('0.14s');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    // Reduced motion keeps the fade without movement: min(d, 160 ms), linear (motion.css tokens, plan O3).
    expect(await tooltip.evaluate(element => getComputedStyle(element).animationName)).toBe('atlas-backdrop-in');
    expect(await tooltip.evaluate(element => getComputedStyle(element).animationDuration)).toBe('0.14s');
    expect(await tooltip.evaluate(element => getComputedStyle(element).animationTimingFunction)).toBe('linear');
  });
}

// 2.6.1: the card around an expanded space switched its border, padding and margin in the frame the tree was inserted
// or removed. The rows below jumped 16 px before the unfolding and 12 px after the fold, and the row itself moved 4 px
// sideways.
// 2.6.2: the row still sank 6 px from under the pointer while its tree unfolded (the card's room above it came with
// the tree), and the unfolding covered a quarter of its way in the first frame. The room above the row is now there
// all the time, and the motion leaves from rest.
test('a pinned space unfolds and folds in the sidebar without a jump, a leap or a row that moves', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.setViewportSize({ width: 1440, height: 1000 });
  const below = { ...space, id: 'motion-space-below', name: 'Zápisy' };
  const tree = Array.from({ length: 6 }, (_, index) => ({
    id: `motion-tree-${index}`, spaceId: space.id, parentId: null, title: `Stránka ${index + 1}`, version: 1,
    isPublished: true, sortOrder: (index + 1) * 1024, canEdit: false,
  }));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let json: unknown;
    if (path === '/api/setup/status') json = { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true };
    else if (path === '/api/auth/me') json = { id: 'motion-user', username: 'motion', displayName: 'Martin', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } };
    else if (path === '/api/spaces') json = [space, below];
    else if (path === `/api/spaces/${space.id}/tree`) json = { spaceId: space.id, version: 1, items: tree };
    else if (path.startsWith('/api/me/library/')) json = { items: [], hasMore: false, nextOffset: null };
    else if (path === '/api/ai/capabilities') json = { answer: false, assistant: false, pageSummary: false };
    else throw new Error(`Unexpected fixture request: ${path}`);
    await route.fulfill({ json });
  });
  await page.goto('/');
  const item = page.locator(`[data-sidebar-space="${space.id}"]`);
  const toggle = item.locator('.sidebar-space-row .tree-toggle');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await page.evaluate(() => document.fonts.ready);

  // The row, its name and the space below: as they stand before the click, and then on every frame until every
  // animation in the item has ended. With `turnAfter` the chevron is clicked again that many milliseconds later, so
  // the motion turns around midway and ends where it started (`expanded` is the state it ends in).
  type Frame = { t: number; x: number; y: number; width: number; name: number; below: number; frame: number };
  const toggled = (expanded: boolean, turnAfter?: number) => item.evaluate((element, [expanded, turnAfter]) => new Promise<Frame[]>(resolve => {
    const row = element.querySelector('.sidebar-space-row')!, name = element.querySelector('.sidebar-space-name')!, below = element.nextElementSibling!;
    const chevron = element.querySelector<HTMLElement>('.sidebar-space-row .tree-toggle')!;
    const frames: Frame[] = [];
    let settled = 0, turned = turnAfter === null;
    const sample = (t: number) => {
      const box = row.getBoundingClientRect();
      frames.push({ t, x: box.x, y: box.y, width: box.width, name: name.getBoundingClientRect().x, below: below.getBoundingClientRect().y, frame: Number(getComputedStyle(element, '::before').opacity) });
    };
    const tick = (t: number) => {
      sample(t);
      const done = turned && !!element.querySelector('.sidebar-space-tree') === expanded && !element.getAnimations({ subtree: true }).length;
      settled = done ? settled + 1 : 0;
      if (settled > 3 || frames.length > 600) resolve(frames); else requestAnimationFrame(tick);
    };
    sample(performance.now());
    requestAnimationFrame(tick);
    chevron.click();
    if (turnAfter !== null) setTimeout(() => { chevron.click(); turned = true; }, turnAfter);
  }), [expanded, turnAfter ?? null] as const);
  // The space below leaves from rest, gathers speed once and comes to rest. Measured against time, so a dropped frame
  // is not read as a jump: each way it moves, its speed rises to one peak and only falls after it. A step taken before
  // the unfolding starts, after the fold has come to rest, or a catch-up after a turn, is a second rise; a motion that
  // begins at its full speed (the leap of a plain ease-out) has no rise at all.
  const expectOneMotionEachWay = (frames: Frame[], label: string, least = 3, whole = false) => {
    // The row stays where it is, at every frame: not sideways, and not down from under the pointer.
    for (const key of ['x', 'y', 'width', 'name'] as const) expect(new Set(frames.map(frame => frame[key])).size, `${key} ${label}`).toBe(1);
    const steps = frames.slice(1).map((frame, index) => ({ way: Math.sign(frame.below - frames[index]!.below), speed: Math.abs(frame.below - frames[index]!.below) / Math.max(frame.t - frames[index]!.t, 1) }));
    const moving = steps.filter(step => step.speed > 0.01);
    expect(moving.length, `the space below moves ${label}`).toBeGreaterThanOrEqual(least);
    const runs: (typeof moving)[] = [];
    for (const step of moving) if (runs.at(-1)?.[0]!.way === step.way) runs.at(-1)!.push(step); else runs.push([step]);
    for (const run of runs) {
      const peak = run.reduce((best, step, index) => step.speed > run[best]!.speed ? index : best, 0);
      run.forEach((step, index) => {
        const before = run[index - 1];
        if (!before) return;
        if (index <= peak) expect(step.speed, `step ${index + 1} of ${run.length} gathers speed ${label}`).toBeGreaterThanOrEqual(before.speed * 0.8 - 0.02);
        else expect(step.speed, `step ${index + 1} of ${run.length} slows down ${label}`).toBeLessThanOrEqual(before.speed * 1.2 + 0.02);
      });
      // A whole motion starts well under its top speed.
      if (whole) expect(run[0]!.speed, `the first step is not a leap ${label}`).toBeLessThanOrEqual(run[peak]!.speed * 0.7);
    }
    return moving;
  };
  for (const expanded of [true, false]) {
    const frames = await toggled(expanded);
    await expect(toggle).toHaveAttribute('aria-expanded', String(expanded));
    const moving = expectOneMotionEachWay(frames, expanded ? 'while unfolding' : 'while folding', 3, true);
    expect(new Set(moving.map(step => step.way)).size).toBe(1);
    // The card's frame fades with the tree instead of appearing or vanishing at full strength.
    expect(frames.some(frame => frame.frame > 0.05 && frame.frame < 0.95)).toBe(true);
    expect(frames.at(-1)!.frame).toBe(expanded ? 1 : 0);
  }
  // Turned around midway, it goes back from where it is: the tree, the card's room and its frame alike.
  for (const turnAfter of [40, 80, 150]) {
    for (const expanded of [false, true]) {
      if (expanded) await toggled(true);
      const label = `${expanded ? 'folding, then unfolding' : 'unfolding, then folding'} after ${turnAfter} ms`;
      const frames = await toggled(expanded, turnAfter);
      await expect(toggle).toHaveAttribute('aria-expanded', String(expanded));
      // On a busy machine the second click of the quickest turn can come before the first motion has left its first frame:
      // then there is nothing to turn, which is as good as a clean turn. The later turns had time to paint and must move.
      const ways = expectOneMotionEachWay(frames, label, turnAfter === 40 ? 0 : 1).map(step => step.way);
      // At most one turn: there and back.
      expect(ways.filter((way, index) => index && way !== ways[index - 1]).length, label).toBeLessThanOrEqual(1);
      expect(Math.abs(frames.at(-1)!.below - frames[0]!.below), label).toBeLessThan(0.5);
      expect(frames.at(-1)!.frame).toBe(expanded ? 1 : 0);
      if (expanded) await toggled(false);
    }
  }
});

// 2.6.2: a space opened before its tree had loaded unfolded for „Načítání stránek…“ and caught up with the pages in the
// time that first unfolding had left. An answer late in it sent the whole tree across in a few frames. Now the space
// waits for the pages for a moment, and an answer that comes later goes on as a motion of its own length.
test('a pinned space whose tree answers late unfolds without catching up in a few frames', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.setViewportSize({ width: 1440, height: 1000 });
  const below = { ...space, id: 'motion-space-below', name: 'Zápisy' };
  const tree = Array.from({ length: 16 }, (_, index) => ({
    id: `motion-late-${index}`, spaceId: space.id, parentId: null, title: `Stránka ${index + 1}`, version: 1,
    isPublished: true, sortOrder: (index + 1) * 1024, canEdit: false,
  }));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let json: unknown;
    if (path === '/api/setup/status') json = { databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true };
    else if (path === '/api/auth/me') json = { id: 'motion-user', username: 'motion', displayName: 'Martin', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } };
    else if (path === '/api/spaces') json = [space, below];
    else if (path === `/api/spaces/${space.id}/tree`) {
      // Later than the wait (150 ms), while the space unfolds for the loading line.
      await new Promise(resolve => setTimeout(resolve, 220));
      json = { spaceId: space.id, version: 1, items: tree };
    } else if (path.startsWith('/api/me/library/')) json = { items: [], hasMore: false, nextOffset: null };
    else if (path === '/api/ai/capabilities') json = { answer: false, assistant: false, pageSummary: false };
    else throw new Error(`Unexpected fixture request: ${path}`);
    await route.fulfill({ json });
  });
  await page.goto('/');
  const item = page.locator(`[data-sidebar-space="${space.id}"]`);
  await expect(item.locator('.sidebar-space-row .tree-toggle')).toHaveAttribute('aria-expanded', 'false');
  await page.evaluate(() => document.fonts.ready);
  const frames = await item.evaluate(element => new Promise<{ t: number; y: number; below: number }[]>(resolve => {
    const row = element.querySelector('.sidebar-space-row')!, below = element.nextElementSibling!;
    const seen: { t: number; y: number; below: number }[] = [];
    const started = performance.now();
    const tick = (t: number) => {
      seen.push({ t, y: row.getBoundingClientRect().y, below: below.getBoundingClientRect().y });
      if (t - started < 1500) requestAnimationFrame(tick); else resolve(seen);
    };
    // A click as a pointer makes it: the press asks for the tree a moment ahead.
    element.querySelector<HTMLElement>('.sidebar-space-row .tree-toggle')!.click();
    requestAnimationFrame(tick);
  }));
  await expect(item.locator('.tree-page')).toHaveCount(16);
  // The row stays under the pointer the whole time.
  expect(new Set(frames.map(frame => frame.y)).size).toBe(1);
  const travelled = frames.at(-1)!.below - frames[0]!.below;
  expect(travelled).toBeGreaterThan(400);
  // The space below only goes down, and no frame's worth of time carries it a fifth of the way.
  const steps = frames.slice(1).map((frame, index) => (frame.below - frames[index]!.below) / Math.max(frame.t - frames[index]!.t, 1));
  expect(Math.min(...steps)).toBeGreaterThanOrEqual(0);
  expect(Math.max(...steps) * (1000 / 60) / travelled).toBeLessThan(0.2);
});
