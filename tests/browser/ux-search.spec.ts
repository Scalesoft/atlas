// UX review cycle 3, area search (findings SE3-n): the Ctrl+K palette against a
// local Development instance with the module Boards, signed in by „Přihlásit jako vývojář“:
// `ATLAS_URL=http://localhost:<vite> ATLAS_UX_DEV_LOGIN=1 npx playwright test ux-search`. Pages and boards go to the space „UX search“
// with titles „UX-search …“; the run moves them to the trash at the end.
import { expect, test, type BrowserContext, type Page, type Request } from "@playwright/test";
import { startEditing } from "./editing";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
test.describe.configure({ timeout: 120_000 });
const stamp = () => Date.now().toString(36);

async function signIn(page: Page) {
  await page.goto("/");
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  test.skip(!process.env.ATLAS_UX_DEV_LOGIN || !(await developer.waitFor({ timeout: 15_000 }).then(() => true, () => false)), "Needs a Development instance with the developer sign-in.");
  await developer.click();
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await Promise.race([onboarding.waitFor().then(() => onboarding.click()).catch(() => {}), page.getByRole("heading", { level: 1, name: "Knihovna" }).waitFor()]);
  await expect(page.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
}
async function send(context: BrowserContext, method: "post" | "delete", path: string, data?: unknown) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const response = await context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text()) ? response.json() : undefined;
}
async function searchSpace(context: BrowserContext): Promise<string> {
  const spaces: { id: string; name: string }[] = await (await context.request.get("/api/spaces")).json();
  return spaces.find(space => space.name === "UX search")?.id ?? (await send(context, "post", "/api/spaces", { name: "UX search", icon: "code-xml", color: "blue" })).id;
}
const pages: string[] = [], boards: string[] = [];
async function createPage(context: BrowserContext, title: string, text = title, heading?: string): Promise<string> {
  const blocks: unknown[] = [{ type: "paragraph", content: [{ type: "text", text }] }];
  if (heading) blocks.unshift({ type: "heading", attrs: { level: 2, headingId: `h-${stamp()}` }, content: [{ type: "text", text: heading }] });
  const content = JSON.stringify({ format: "atlas-rich", version: 1, plugins: heading ? [{ id: "heading-anchors", version: 1 }] : [], document: { type: "doc", content: blocks } });
  const { id } = await send(context, "post", "/api/pages", { spaceId: await searchSpace(context), parentId: null, title, contentJson: content, markdown: heading ? `## ${heading}

${text}` : text });
  const current = await (await context.request.get(`/api/pages/${id}`)).json();
  await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
  pages.push(id);
  return id;
}
async function openPalette(page: Page) {
  await page.keyboard.press("Control+k");
  await expect(page.locator("dialog.search-dialog input").first()).toBeFocused();
}

test.afterAll(async ({ browser }) => {
  if (!pages.length && !boards.length) return;
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const page = await context.newPage();
  await signIn(page);
  for (const id of boards) await send(context, "delete", `/api/boards/${id}`).catch(() => {});
  for (const id of pages.reverse()) {
    const current = await context.request.get(`/api/pages/${id}`);
    if (!current.ok()) continue;
    const body = await current.json();
    const tree = await (await context.request.get(`/api/spaces/${body.spaceId}/tree`)).json();
    await send(context, "post", `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: body.version });
  }
  await context.close();
});

test.describe("search: Ctrl+K palette", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("SE3-1, SE3-2 one search request per query and no YouTrack request without the integration", async ({ page, context }) => {
    const capabilities = await (await context.request.get("/api/ai/capabilities")).json().catch(() => ({}));
    test.skip(capabilities?.semanticSearch === true, "With the meaning search on, the prefix preview is wanted.");
    const word = `uxsearch${stamp()}`;
    await createPage(context, `UX-search ${word} návod`);
    const searches: string[] = [], youTrack: string[] = [];
    page.on("request", (request: Request) => {
      const url = new URL(request.url());
      if (url.pathname === "/api/search") searches.push(url.search);
      if (url.pathname.startsWith("/api/integrations/youtrack/")) youTrack.push(url.pathname);
    });
    await openPalette(page);
    await page.keyboard.type(word, { delay: 120 });
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toContainText(word);
    await page.waitForTimeout(600);
    const forWord = searches.filter(search => new URLSearchParams(search).get("q") === word);
    expect(forWord, "the final query is asked once, without a prefix-only pass").toHaveLength(1);
    expect(searches.filter(search => new URLSearchParams(search).get("fuzzy") === "false")).toEqual([]);
    const listed = await context.request.get("/api/integrations/youtrack/spaces");
    const configured: string[] = listed.ok() ? await listed.json() : [];
    if (!configured.length) expect(youTrack.filter(path => path.endsWith("/search"))).toEqual([]);
    expect(youTrack.filter(path => path.endsWith("/spaces")).length).toBeLessThanOrEqual(1);
  });

  test("SE3-3 the exact title of a board is the preselected first hit, Enter opens it", async ({ page, context }) => {
    const title = `UX-search Tabule ${stamp()}`;
    // Pages that match every word but are no title match come back as Stránky, which used to go first.
    await createPage(context, `UX-search poznámky`, `${title} se probírá na poradě.`);
    const board = await send(context, "post", `/api/spaces/${await searchSpace(context)}/boards`, { title });
    boards.push(board.id);
    await openPalette(page);
    await page.keyboard.type(title);
    const dialog = page.locator("dialog.search-dialog");
    await expect(dialog.locator('.search-group[data-kind="boards"]')).toContainText(title);
    await expect(dialog.locator(".search-group").first()).toHaveAttribute("data-kind", "boards");
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toContainText(title);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`#/boards/${board.id}`));
  });

  test("SE-land a page opened by Enter closes the palette, lands on its heading or text and a click edits it", async ({ page, context }) => {
    const title = `UX-search Přistání ${stamp()}`;
    const id = await createPage(context, title);
    await openPalette(page);
    await page.keyboard.type(title);
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toContainText(title);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`#/pages/${id}`));
    await expect(page.locator("dialog.search-dialog[open]")).toHaveCount(0);
    // A reader lands on the heading, a writer of a page with text at the start of its text.
    await expect(page.locator(".document-header h1.document-title:focus, .document-body .ProseMirror:focus")).toHaveCount(1);
    await startEditing(page);
    await expect(page.locator('main [contenteditable="true"]').first()).toBeVisible();
  });

  test("SE3-5 Esc from the palette opened in the editor returns to the caret", async ({ page, context }) => {
    const id = await createPage(context, `UX-search editor ${stamp()}`, "Alfa beta gama");
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator("[data-edit-lock]")).toBeVisible();
    await page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
    await page.keyboard.press("Enter");
    const editor = page.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]');
    await expect(editor).toBeFocused();
    await expect(page.getByText("Čekáme na synchronizaci")).toHaveCount(0);
    await page.keyboard.press("End");
    await page.keyboard.press("ArrowLeft");
    await page.keyboard.press("ArrowLeft");
    // ProseMirror reads an arrow's caret move from the next selectionchange; a person is never faster than that.
    await page.waitForTimeout(100);
    await openPalette(page);
    await page.keyboard.press("Escape");
    await expect(page.locator("dialog.search-dialog")).toBeHidden();
    await expect(editor).toBeFocused();
    await page.keyboard.type("X");
    await expect(editor.locator("p").first()).toHaveText("Alfa beta gaXma");
    // The draft is thrown away with the test page.
  });
});

// The whole-page search and infinite results (2026-10-28).
const dialog = (page: Page) => page.locator("dialog.search-dialog");
const field = (page: Page) => dialog(page).locator(".omnisearch-input input");
const selected = (page: Page) => dialog(page).locator('#search-results [role="option"][aria-selected="true"]');
const view = (page: Page) => dialog(page).getAttribute("data-search-view");
const searchParams = (page: Page) => new URL(page.url()).searchParams;

test.describe("search: dialog and whole page (SFP)", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("SFP-1 Ctrl+Shift+Enter turns the dialog into the page and back with the same search; Back returns to it, Esc closes", async ({ page, context }) => {
    const word = `uxpage${stamp()}`;
    const id = await createPage(context, `UX-search ${word} celá stránka`);
    await createPage(context, `UX-search ${word} druhá`);
    await openPalette(page);
    await page.keyboard.type(word);
    await expect(dialog(page).locator('#search-results [role="option"]')).toHaveCount(2);
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    const chosen = (await selected(page).textContent())!;
    const small = (await dialog(page).boundingBox())!;
    expect(small.width).toBeLessThan(1280);
    await expect(dialog(page).locator(".search-footer")).toContainText("Ctrl ⇧ Enter");

    await page.keyboard.press("Control+Shift+Enter");
    await expect.poll(() => view(page)).toBe("page");
    await expect.poll(async () => (await dialog(page).boundingBox())?.width).toBe(1280);
    expect(searchParams(page).get("searchView")).toBe("page");
    expect(searchParams(page).get("q")).toBe(word);
    await expect(field(page)).toBeFocused();
    await expect(field(page)).toHaveValue(word);
    await expect(selected(page)).toHaveText(chosen);
    await expect(dialog(page).getByRole("button", { name: "Zmenšit hledání do okna" })).toBeVisible();

    // The switch back: the button, focus back in the field.
    await dialog(page).getByRole("button", { name: "Zmenšit hledání do okna" }).click();
    await expect.poll(() => view(page)).toBe("dialog");
    await expect.poll(async () => (await dialog(page).boundingBox())?.width).toBe(small.width);
    expect(searchParams(page).get("searchView")).toBeNull();
    await expect(field(page)).toBeFocused();
    await expect(selected(page)).toHaveText(chosen);

    // From the page to a result and Back: the page view again, the same row selected.
    await page.keyboard.press("Control+Shift+Enter");
    await expect.poll(() => view(page)).toBe("page");
    await page.keyboard.press("ArrowUp");
    const opened = (await selected(page).textContent())!;
    await page.keyboard.press("Enter");
    await expect(dialog(page)).toBeHidden();
    await expect(page).toHaveURL(/#\/pages\//);
    await page.goBack();
    await expect(dialog(page)).toBeVisible();
    await expect.poll(() => view(page)).toBe("page");
    await expect(field(page)).toHaveValue(word);
    await expect(selected(page)).toHaveText(opened);

    // Esc leaves the search from the page view too.
    await page.keyboard.press("Escape");
    await expect(dialog(page)).toBeHidden();
    expect(searchParams(page).get("search")).toBeNull();
    expect(searchParams(page).get("searchView")).toBeNull();

    // The address is a bookmark of the page view.
    await page.goto(`/?search=1&searchView=page&q=${encodeURIComponent(word)}#/pages/${id}`);
    await expect(dialog(page)).toBeVisible();
    await expect.poll(() => view(page)).toBe("page");
    await expect(dialog(page).locator('#search-results [role="option"]')).toHaveCount(2);
    await expect(selected(page)).toHaveCount(1);
  });

  test("SFP-2 at 390 px the dialog is already the screen: no switch button, the key still changes the address; dark page view", async ({ page, context }) => {
    const word = `uxpage${stamp()}`;
    await createPage(context, `UX-search ${word} telefon`);
    await page.setViewportSize({ width: 390, height: 800 });
    await page.emulateMedia({ colorScheme: "dark" });
    await openPalette(page);
    await page.keyboard.type(word);
    await expect(selected(page)).toContainText(word);
    await expect(dialog(page).locator("[data-search-view-toggle]")).toBeHidden();
    await page.keyboard.press("Control+Shift+Enter");
    await expect.poll(() => view(page)).toBe("page");
    expect(searchParams(page).get("searchView")).toBe("page");
    const box = (await dialog(page).boundingBox())!;
    expect(box.width).toBe(390);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await expect(field(page)).toBeFocused();
    // Dark: the panel is the dark surface, not white.
    const background = await dialog(page).evaluate(element => getComputedStyle(element).backgroundColor);
    const [r, g, b] = background.match(/\d+/g)!.map(Number);
    if (await page.evaluate(() => document.querySelector(".atlas")?.getAttribute("data-mode") !== "light")) expect(r! + g! + b!).toBeLessThan(200);
  });

  test("SFP-3 Ctrl+Enter during the automatic answer starts one conversation and searches once", async ({ page, context }) => {
    const capabilities = await (await context.request.get("/api/ai/capabilities")).json().catch(() => ({}));
    test.skip(capabilities?.answer !== true, "Needs the answer capability (local LM Studio or Gateway).");
    const preference = await (await context.request.get("/api/me/ai-preferences")).json().catch(() => ({}));
    test.skip(preference?.searchAnswer !== "auto", "Needs the automatic search answer in the profile.");
    const conversations: string[] = [], searches: string[] = [];
    page.on("request", (request: Request) => {
      const url = new URL(request.url());
      if (url.pathname === "/api/ai/conversations" && request.method() === "POST") conversations.push(url.pathname);
      if (url.pathname === "/api/search" && !new URLSearchParams(url.search).get("fuzzy")) searches.push(url.search);
    });
    await openPalette(page);
    await page.keyboard.type("jak nasadit server");
    await expect.poll(() => conversations.length, { timeout: 10_000 }).toBe(1);
    const before = searches.length;
    await page.keyboard.press("Control+Enter");
    await expect(field(page)).toHaveValue("? jak nasadit server");
    await page.waitForTimeout(1500);
    expect(conversations).toHaveLength(1);
    expect(searches.length).toBe(before);
  });
});

// Real wiki (ATLAS_REAL=1, an instance over a COPY of the real-data database): infinite results by keyboard, no titles in the spec.
test.describe(`search: infinite results over the real wiki (SFP${process.env.ATLAS_REAL === "1" ? "" : ", skipped: needs ATLAS_REAL=1"})`, () => {
  test.beforeEach(async ({ page }) => {
    test.skip(process.env.ATLAS_REAL !== "1", "Set ATLAS_REAL=1 and ATLAS_URL to an instance over a copy of the real-data database.");
    await signIn(page);
  });

  test("SFP-4 PageDown loads the next pages as the list nears its end, selection and Enter work across batches, Back restores them", async ({ page, context }) => {
    // A query of the real wiki with more than one page of results, found at run time.
    let query = "";
    for (const candidate of ["server", "projekt", "nasazení", "data", "uživatel", "na"]) {
      const found = await (await context.request.get(`/api/search?q=${encodeURIComponent(candidate)}&limit=50`)).json();
      if (found.hasMore) { query = candidate; break; }
    }
    test.skip(!query, "No query of this wiki has more than 50 results.");
    const offsets: string[] = [];
    page.on("request", (request: Request) => {
      const url = new URL(request.url());
      if (url.pathname === "/api/search" && url.searchParams.get("q") === query) offsets.push(url.searchParams.get("offset") ?? "0");
    });
    await page.evaluate(() => {
      (window as unknown as { longTasks: number[] }).longTasks = [];
      new PerformanceObserver(list => { for (const entry of list.getEntries()) (window as unknown as { longTasks: number[] }).longTasks.push(entry.duration); }).observe({ type: "longtask", buffered: false });
    });
    await openPalette(page);
    await page.keyboard.type(query);
    const rows = dialog(page).locator('#search-results [role="option"]');
    await expect(selected(page)).toHaveCount(1);
    await expect.poll(() => offsets.includes("0")).toBe(true);
    await page.keyboard.press("Control+Shift+Enter");
    await expect.poll(() => view(page)).toBe("page");
    const first = await rows.count();
    // Keyboard only: PageDown through the list until a second and a third batch came.
    for (let step = 0; step < 40 && !offsets.includes("100"); step++) {
      await page.keyboard.press("PageDown");
      await page.waitForTimeout(120);
    }
    expect(offsets).toContain("50");
    await expect.poll(() => rows.count()).toBeGreaterThan(first);
    // The selection is a loaded row past the first batch, visible, and the field still owns the keys.
    const index = Number((await selected(page).getAttribute("id"))!.replace("search-result-", ""));
    expect(index).toBeGreaterThanOrEqual(50);
    await expect(selected(page)).toBeInViewport();
    await expect(field(page)).toBeFocused();
    const status = dialog(page).locator(".search-footer [role=status]");
    await expect(status).toHaveCount(1);
    const longTasks = await page.evaluate(() => (window as unknown as { longTasks: number[] }).longTasks);
    console.log(`SFP-4 query of ${query.length} letters: ${await rows.count()} rows, offsets ${offsets.join(",")}, long tasks ${longTasks.map(Math.round).join(",") || "none"}`);
    expect(Math.max(0, ...longTasks)).toBeLessThan(250);
    // Home and End stay in the list; Enter opens the selected row, Back restores the view with that row.
    const chosen = (await selected(page).textContent())!;
    await page.keyboard.press("Enter");
    await expect(dialog(page)).toBeHidden();
    await page.goBack();
    await expect.poll(() => view(page)).toBe("page");
    await expect(selected(page)).toHaveText(chosen, { timeout: 20_000 });
    await expect(selected(page)).toBeInViewport();
  });

  test("SFP-5 an arrow key over 3000 results stays under 100 ms and a row stays small (VYK1-1)", async ({ page }) => {
    // One synthetic answer with every row at once: the cost of a key press must not grow with the number of rows (no virtualization).
    await page.route(url => url.pathname === "/api/search", route => route.fulfill({ json: {
      items: Array.from({ length: 3000 }, (_, index) => ({ id: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`, spaceId: "00000000-0000-4000-8000-ffffffffffff", spaceName: "Synthetic", title: `UX-search row ${index}`, snippet: `Row ${index} of a long synthetic answer`, isDraft: false, changedAt: null })),
      hasMore: false, nextOffset: null,
    } }));
    await openPalette(page);
    await page.keyboard.type("row");
    const rows = dialog(page).locator('#search-results [role="option"]');
    await expect(rows).toHaveCount(3000, { timeout: 30_000 });
    const nodesPerRow = await page.evaluate(() => document.querySelectorAll("#search-results *").length / document.querySelectorAll('#search-results [role="option"]').length);
    expect(nodesPerRow).toBeLessThan(25);
    // 15 presses leave the first screen, so the ones that scroll the list count too; the first press after the rows arrived is measured apart.
    const times: number[] = [];
    for (let press = 0; press < 15; press++) {
      times.push(await page.evaluate(async () => {
        const started = performance.now();
        document.querySelector("dialog.search-dialog .omnisearch-input input")!.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        return performance.now() - started;
      }));
    }
    const [first, ...rest] = times, sorted = [...rest].sort((a, b) => a - b);
    const median = sorted[Math.floor(sorted.length / 2)]!, p90 = sorted[Math.ceil(sorted.length * 0.9) - 1]!;
    console.log(`SFP-5 ArrowDown over 3000 rows: first ${Math.round(first!)} ms, then ${rest.map(Math.round).join(", ")} ms (median ${Math.round(median)}, p90 ${Math.round(p90)}), ${nodesPerRow.toFixed(1)} nodes per row`);
    // Measured on the production build: first press 142-603 ms, scrolling presses 83-100 ms (VYK2-6), with headroom.
    expect(first!).toBeLessThan(1000);
    expect(median).toBeLessThan(100);
    expect(p90).toBeLessThan(200);
  });
});

test.describe("search: cycle 5 (SE5-n)", () => {
  test("SE5-1 a heading called as the query does not lead the pages whose titles start with it", async ({ page, context }) => {
    await signIn(page);
    const word = `uxsrch${stamp()}`;
    await createPage(context, `UX-search jiná ${stamp()}`, "text", word);
    await createPage(context, `${word}ová strana`);
    await openPalette(page);
    await page.keyboard.type(word);
    await expect(page.locator('[role="option"]').first()).toBeVisible();
    await expect(page.locator('.search-group[data-kind="headings"]')).toBeVisible();
    await expect(page.locator(".search-group").first()).toHaveAttribute("data-kind", "pages");
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toContainText(`${word}ová strana`);
  });
});

test.describe("search: cycle 5 on a touch phone", () => {
  test.use({ viewport: { width: 390, height: 780 }, hasTouch: true, isMobile: true });
  test("SE5-2 the search dialog's controls are 44 px touch targets", async ({ page }) => {
    await signIn(page);
    await page.keyboard.press("Control+k");
    await expect(page.locator("dialog.search-dialog input").first()).toBeVisible();
    await page.keyboard.type("příručka");
    await expect(page.locator('[role="option"]').first()).toBeVisible();
    const small = await page.locator("dialog.search-dialog button, dialog.search-dialog .search-scope-chip, dialog.search-dialog [role=tab]").evaluateAll(controls => controls
      .map(control => ({ name: (control.getAttribute("aria-label") || control.textContent || "").trim().slice(0, 24), box: control.getBoundingClientRect() }))
      .filter(({ box }) => box.width > 0 && box.height > 0 && box.height < 43.5).map(({ name, box }) => `${name} ${Math.round(box.width)}x${Math.round(box.height)}`));
    expect(small, "controls under 44 px").toEqual([]);
  });
});
