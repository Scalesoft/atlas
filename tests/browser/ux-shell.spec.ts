// UX review cycle 1, area shell: cross-app consistency guards and the
// keyboard-pass leftovers of the shell. Runs against a local build with „Přihlásit jako vývojář“ (ATLAS_URL); every test makes its
// own pages in the space „UX shell“ (created when missing) and moves them to the trash at the end.
import { expect, test, type BrowserContext, type Locator, type Page } from "@playwright/test";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
const stamp = () => Date.now().toString(36);

async function signIn(page: Page) {
  await page.goto("/");
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  if (!(await developer.isVisible({ timeout: 15_000 }).catch(() => false))) test.skip(true, "Needs a local build with developer sign-in.");
  await developer.click();
  // A restored wiki (the real-data instance) first offers its onboarding.
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await Promise.race([onboarding.waitFor().then(() => onboarding.click()).catch(() => {}), page.getByRole("heading", { level: 1, name: "Knihovna" }).waitFor()]);
  await expect(page.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
}
async function send(context: BrowserContext, method: "post" | "put", path: string, data?: unknown) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const response = await context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text()) ? response.json() : undefined;
}
async function shellSpace(context: BrowserContext): Promise<string> {
  const spaces: { id: string; name: string }[] = await (await context.request.get("/api/spaces")).json();
  return spaces.find(space => space.name === "UX shell")?.id ?? (await send(context, "post", "/api/spaces", { name: "UX shell", icon: "code-xml", color: "blue" })).id;
}
const made: string[] = [];
async function createPage(context: BrowserContext, spaceId: string, title: string, parentId: string | null = null, publish = true): Promise<string> {
  const content = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: `${title} text.` }] }] } });
  const { id } = await send(context, "post", "/api/pages", { spaceId, parentId, title, contentJson: content, markdown: title });
  if (publish) {
    const current = await (await context.request.get(`/api/pages/${id}`)).json();
    await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
  }
  made.push(id);
  return id;
}
const focused = (page: Page) => page.evaluate(() => {
  const active = document.activeElement;
  return active && active !== document.body ? `${active.tagName.toLowerCase()} ${(active.getAttribute("aria-label") || active.textContent || "").trim().replace(/\s+/g, " ").slice(0, 60)}` : "body";
});
/** Whether `locator` is focused by the keyboard (focus-visible) and shows it: an outline or shadow on itself, a pseudo-element or a wrapper. */
const ringOf = (locator: Locator) => locator.evaluate(element => {
  const shows = (style: CSSStyleDeclaration) => (style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0) || (!!style.boxShadow && style.boxShadow !== "none");
  const holders = [element, element.parentElement, element.closest(".space-card, .settings-card, tr, [role=row], [data-focus-ring]")].filter(Boolean) as Element[];
  return holders.some(holder => shows(getComputedStyle(holder)) || shows(getComputedStyle(holder, "::before")) || shows(getComputedStyle(holder, "::after")));
});

test.afterAll(async ({ browser }) => {
  if (!made.length) return;
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const page = await context.newPage();
  await signIn(page);
  for (const id of made.reverse()) {
    const csrf = await (await context.request.get("/api/auth/csrf")).json();
    const headers = { "X-CSRF-TOKEN": csrf.token };
    // A board goes to the trash by DELETE; a page by the tree's trash with the tree's version.
    const current = await context.request.get(`/api/pages/${id}`);
    if (!current.ok()) { await context.request.delete(`/api/boards/${id}`, { headers }); continue; }
    const page = await current.json();
    const tree = await (await context.request.get(`/api/spaces/${page.spaceId}/tree`)).json();
    await context.request.post(`/api/pages/${id}/trash`, { data: { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: page.version }, headers });
  }
  await context.close();
});

test.describe("shell consistency", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("SH-1 a header button that opens another view lands focus there, not on the body", async ({ page, context }) => {
    const id = await createPage(context, await shellSpace(context), `UX-shell Přístup ${stamp()}`);
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await page.getByRole("button", { name: "Kdo má přístup" }).first().focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/access$/);
    await expect.poll(() => focused(page)).toBe("input Hledat osobu nebo skupinu");
  });

  test("SH-17 a search result opened from the tree lands on the opened page's heading, not back on the tree row", async ({ page, context }) => {
    const space = await shellSpace(context);
    const title = `UX-shell Cíl ${stamp()}`;
    await createPage(context, space, title);
    await page.goto(`/#/spaces/${space}`);
    const row = page.locator(`[data-sidebar-space="${space}"] .tree-page`).first();
    await expect(row).toBeVisible();
    await row.focus();
    await page.keyboard.press("Control+k");
    await page.keyboard.type(title);
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toContainText(title);
    await page.keyboard.press("Enter");
    // A reader lands on the heading, a writer of a page with text at the start of its text.
    await expect(page.locator(".document-header h1.document-title:focus, .document-body .ProseMirror:focus")).toHaveCount(1);
  });

  test("SH-2 dialogs share AppDialog chrome and leave with motion (Klávesové zkratky)", async ({ page }) => {
    await page.locator("h1").first().focus();
    await page.keyboard.press("?");
    const help = page.locator("dialog.app-dialog[open]");
    await expect(help.locator(":scope > header.app-dialog-header > h2")).toHaveText("Klávesové zkratky");
    await expect(help.locator(":scope > header.app-dialog-header").getByRole("button", { name: "Zavřít" })).toBeVisible();
    await expect.poll(() => help.evaluate(el => el.getAnimations().filter(a => a.playState === "running").length)).toBe(0);
    await page.keyboard.press("Escape");
    // The exit runs on the element that is still there (an unmounted dialog would vanish in one frame).
    const leaving = await page.evaluate(() => { const dialog = document.querySelector("dialog.app-dialog[open]"); return !!dialog && dialog.getAnimations().length > 0; });
    expect(leaving).toBe(true);
    await expect(page.locator("dialog.app-dialog[open]")).toHaveCount(0);
    await expect.poll(() => focused(page)).toBe("h1 Knihovna");
  });

  // The shell's first screen (about 1.1 MB of JS and CSS before compression; the
  // icon pack adds a part of ~6 KB per icon drawn, round 2) needs no editor, which would add about 0.9 MB; the page's editor loads
  // with the page, without the 600 KB emoji catalog (round 2), and the collaboration join brings the document state compressed.
  // Since 3.6 (bf88dafd8, kept by the owner) index.html preloads the page route at low priority: the editor may arrive that way,
  // never above low priority, and the library renders while it is still held back.
  test("LS-1 a cold first screen needs no editor, and a page then opens for editing", async ({ page, context, browser }) => {
    const id = await createPage(context, await shellSpace(context), `UX-shell Načtení ${stamp()}`);
    const cold = await browser.newContext({ baseURL: process.env.ATLAS_URL, storageState: await context.storageState() });
    const first = await cold.newPage();
    const assets: string[] = [];
    const bodies: Promise<string>[] = [];
    first.on("response", response => {
      const path = new URL(response.url()).pathname;
      if (!/\.(js|css)$/.test(path)) return;
      assets.push(path);
      bodies.push(response.body().then(body => body.toString("latin1"), () => ""));
    });
    // Low (or very low) priority: the boot preload, which yields to the shell's own files.
    const lowPriority = new Set<string>();
    const cdp = await cold.newCDPSession(first);
    await cdp.send("Network.enable");
    cdp.on("Network.requestWillBeSent", ({ request }) => { if (/Low$/.test(request.initialPriority)) lowPriority.add(new URL(request.url).pathname); });
    const urgent = (pattern: RegExp) => assets.filter(path => pattern.test(path) && !lowPriority.has(path));
    // The editor's chunks are held back until the library shows: it must not wait for them.
    let libraryShown!: () => void;
    const shown = new Promise<void>(resolve => { libraryShown = resolve; });
    await first.route(/\/(DocumentEditor|documentWidgets)-[^/]*\.js$/, async route => { await shown; await route.continue(); });
    const joins: string[] = [];
    first.on("websocket", socket => { if (socket.url().includes("/hubs/markdown")) socket.on("framereceived", ({ payload }) => { if (String(payload).includes('"snapshot"')) joins.push(String(payload)); }); });
    // A held preload holds the load event too.
    await first.goto("/", { waitUntil: "commit" });
    await expect(first.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
    libraryShown();
    await first.waitForLoadState("networkidle");
    test.skip(assets.some(path => path.startsWith("/src/")), "Measures a production build (vite build + vite preview, or the server); a dev server serves modules one by one.");
    const screen = (await Promise.all(bodies)).filter((_, index) => !lowPriority.has(assets[index]));
    expect(urgent(/\/(DocumentEditor|documentWidgets)-/), "editor chunks above low priority on the first screen").toEqual([]);
    expect(screen.reduce((sum, body) => sum + body.length, 0), `first-screen JS and CSS, ${screen.length} files`).toBeLessThan(1.3 * 1024 * 1024);
    // One icon of the pack costs its part, never the whole pack (383 KB).
    expect(Math.max(...screen.map(body => body.length)), "the largest first-screen file").toBeLessThan(500 * 1024);
    // Obsah of a space is a first view too: no editor and no relations graph until a page or the Vazby tab asks for them.
    await first.goto(`/#/spaces/${await shellSpace(context)}`);
    await first.waitForLoadState("networkidle");
    expect(urgent(/\/(DocumentEditor|documentWidgets|RelationsGraph|cytoscape)/), "editor or graph chunks on Obsah").toEqual([]);
    // The graph itself is read-only cards: showing it brings the graph and canvas chunks, still none of the editor (editor blocks, text editor, widgets).
    await first.locator('[data-content-view="graph"]').click();
    await first.waitForLoadState("networkidle");
    expect(urgent(/\/(DocumentEditor|documentWidgets|editorBlocks|BoardBlock|BoardTextEditor|BoardMarkdown)-/), "editor chunks for the relations graph").toEqual([]);
    await first.goto(`/#/pages/${id}`);
    await expect(first.locator("[data-edit-lock]")).toBeVisible();
    await first.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
    await first.keyboard.press("Enter");
    await expect(first.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]')).toBeFocused();
    // The catalog's emoji groups (`people & body`) are in no other chunk.
    expect((await Promise.all(bodies)).filter(body => body.includes("people & body")), "the emoji catalog loads only for a search").toEqual([]);
    // The first join of a new page seeds it (no snapshot yet); the next one receives the stored state.
    await expect.poll(() => joins.length).toBeGreaterThan(0);
    await first.reload();
    await expect(first.locator("[data-edit-lock]")).toBeVisible();
    await first.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
    await first.keyboard.press("Enter");
    await expect(first.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]')).toBeFocused();
    await expect.poll(() => joins.some(join => /"snapshot":"/.test(join))).toBe(true);
    expect(joins.find(join => /"snapshot":"/.test(join)), "the join snapshot travels compressed").toContain('"snapshotEncoding":"gzip"');
    await cold.close();
  });

  test("SH-guard every menu trigger opens a menu and Esc returns to it; every Tab stop shows a focus ring", async ({ page, context }) => {
    const space = await shellSpace(context);
    const id = await createPage(context, space, `UX-shell Menu ${stamp()}`);
    for (const hash of ["#/library/spaces", `#/spaces/${space}`, `#/pages/${id}`]) {
      await page.goto(`/${hash}`);
      await expect(page.locator("h1, .document-header .document-title").first()).toBeVisible();
      await expect(page.locator('[aria-haspopup="menu"]:visible').first()).toBeVisible();
      await page.waitForLoadState("networkidle");
      const labels = [...new Set(await page.locator('[aria-haspopup="menu"]:visible:not(:disabled)').evaluateAll(els => els.map(el => el.getAttribute("aria-label") ?? "")))];
      expect(labels.length).toBeGreaterThan(0);
      for (const label of labels) {
        const trigger = page.locator(`[aria-haspopup="menu"][aria-label="${label}"]:visible`).first();
        // A label read while the previous view still faded out is gone now.
        if (!(await trigger.isVisible())) continue;
        await trigger.focus();
        await expect(trigger).toBeFocused();
        await page.keyboard.press("Enter");
        const menu = page.getByRole("menu");
        await expect(menu).toBeVisible();
        // atlas-menu-in (by side), the fade of reduced motion, or the editor's context-menu idiom (md-popover-enter, BoardMenu: the
        // tree row menu): never a menu that pops in without motion.
        await expect(menu).toHaveCSS("animation-name", /^(atlas-(menu-in|backdrop-in)|md-popover-enter)/);
        await page.keyboard.press("Escape");
        await expect(page.getByRole("menu")).toHaveCount(0);
        // A trigger out of the Tab order (the tree row „⋯“, tabindex -1) hands the focus back to its row's tab stop, the one the
        // keyboard opens the same menu from (Shift+F10); never to the body.
        if ((await trigger.getAttribute("tabindex")) === "-1") await expect.poll(() => trigger.evaluate(el => el !== document.activeElement && !!el.parentElement?.contains(document.activeElement))).toBe(true);
        else await expect(trigger).toBeFocused();
      }
      await page.locator("h1").first().focus();
      const missing: string[] = [];
      for (let stop = 0; stop < 45; stop++) {
        await page.keyboard.press("Tab");
        const current = page.locator(":focus");
        if (!(await current.count())) continue;
        const skip = await current.evaluate(el => el.matches('[contenteditable="true"], .workspace-panel-resizer, .sidebar-resizer'));
        if (!skip && !(await ringOf(current))) missing.push(await focused(page));
      }
      expect(missing, `${hash}: focus without a visible ring`).toEqual([]);
    }
  });

  test("SH-3 sidebar: → unfolds a space, ↓ enters its tree, ↑ ↓ cross between spaces, ← folds", async ({ page, context }) => {
    const space = await shellSpace(context);
    const first = await createPage(context, space, `UX-shell AA ${stamp()}`);
    await page.goto(`/#/library/spaces`);
    const item = page.locator(`[data-sidebar-space="${space}"]`);
    const link = item.locator(".sidebar-space-link");
    const toggle = item.locator(".sidebar-space-row .tree-toggle");
    if ((await toggle.getAttribute("aria-expanded")) === "true") await toggle.click();
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    await link.focus();
    await page.keyboard.press("ArrowRight");
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await expect(link).toBeFocused();
    await expect(item.locator(".tree-page").first()).toBeVisible();
    await page.keyboard.press("ArrowDown");
    await expect(item.locator(".tree-page").first()).toBeFocused();
    await page.keyboard.press("ArrowUp");
    await expect(link).toBeFocused();
    await page.keyboard.press("ArrowRight");
    await expect(item.locator(".tree-page").first()).toBeFocused();
    // From the last page of the tree ↓ goes on to the next space, ↑ comes back.
    await page.keyboard.press("End");
    const last = item.locator(".tree-page").last();
    await expect(last).toBeFocused();
    const next = item.locator("xpath=following-sibling::li[1]//a[contains(@class,'sidebar-space-link')]");
    if (await next.count()) {
      await page.keyboard.press("ArrowDown");
      await expect(next).toBeFocused();
      await page.keyboard.press("ArrowUp");
      await expect(last).toBeFocused();
    }
    await link.focus();
    await page.keyboard.press("ArrowLeft");
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    const previous = item.locator("xpath=preceding-sibling::li[1]//a[contains(@class,'sidebar-space-link')]");
    if (await previous.count()) {
      await page.keyboard.press("ArrowUp");
      await expect(page.locator(".sidebar-space-link:focus, .tree-page:focus")).toHaveCount(1);
      await expect(link).not.toBeFocused();
    }
    expect(first).toBeTruthy();
  });

  test("SH-4 Knihovna: arrows move along the tabs and open them", async ({ page }) => {
    const tabs = page.getByRole("navigation", { name: "Části knihovny" });
    await tabs.getByRole("link", { name: /^Prostory/ }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(page).toHaveURL(/#\/library\/recent$/);
    await expect(tabs.getByRole("link", { name: "Nedávné" })).toBeFocused();
    await page.keyboard.press("End");
    await expect(page).toHaveURL(/#\/library\/contacts$/);
    await expect(tabs.getByRole("link", { name: "Kontakty" })).toBeFocused();
    await page.keyboard.press("Home");
    await expect(page).toHaveURL(/#\/library\/spaces$/);
    await page.keyboard.press("ArrowLeft");
    await expect(page).toHaveURL(/#\/library\/contacts$/);
  });

  test("SH-5 Knihovna: arrows move between space cards", async ({ page }) => {
    const cards = page.locator(".space-directory-grid .space-card-link");
    await expect(cards.nth(3)).toBeVisible();
    await cards.nth(0).focus();
    await page.keyboard.press("ArrowRight");
    await expect(cards.nth(1)).toBeFocused();
    await page.keyboard.press("ArrowLeft");
    await expect(cards.nth(0)).toBeFocused();
    const columns = await cards.evaluateAll(links => { const top = (link: Element) => (link.closest("li") as HTMLElement).offsetTop; return links.filter(link => top(link) === top(links[0]!)).length; });
    await page.keyboard.press("ArrowDown");
    await expect(cards.nth(columns)).toBeFocused();
    await page.keyboard.press("ArrowUp");
    await expect(cards.nth(0)).toBeFocused();
    await page.keyboard.press("End");
    await expect(cards.last()).toBeFocused();
    await page.keyboard.press("Home");
    await expect(cards.first()).toBeFocused();
  });

  test("SH-6 Esc cancels the new space and new page flows", async ({ page, context }) => {
    const space = await shellSpace(context);
    await page.goto("/#/new-space");
    await expect(page.getByRole("textbox", { name: "Název prostoru" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page).not.toHaveURL(/new-space/);
    await page.goto(`/#/spaces/${space}/new-page`);
    await expect(page.locator('input[name="page-title"]')).toBeVisible();
    await page.locator('input[name="page-title"]').focus();
    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(new RegExp(`#/spaces/${space}$`));
  });

  test("SH-10 Nová stránka: an arrow on the start-content cards selects, as in every radio group", async ({ page, context }) => {
    await page.goto(`/#/spaces/${await shellSpace(context)}/new-page`);
    const cards = page.getByRole("radiogroup").first().getByRole("radio");
    await expect(cards.nth(1)).toBeVisible();
    await cards.first().focus();
    await page.keyboard.press("ArrowDown");
    await expect(cards.nth(1)).toBeFocused();
    await expect(cards.nth(1)).toBeChecked();
    await page.keyboard.press("ArrowUp");
    await expect(cards.first()).toBeChecked();
  });

  test("SH-11 Nová stránka: Tab reaches Vytvořit stránku before the name is filled, and pressing it leads to the name", async ({ page, context }) => {
    await page.goto(`/#/spaces/${await shellSpace(context)}/new-page`);
    const name = page.locator('input[name="page-title"]');
    await expect(name).toBeFocused();
    const create = page.getByRole("button", { name: "Vytvořit stránku" });
    const stops: string[] = [];
    for (let stop = 0; stop < 4 && !(await create.evaluate(el => el === document.activeElement)); stop++) { await page.keyboard.press("Tab"); stops.push(await focused(page)); }
    await expect(create, `Tab stops: ${stops.join(" → ")}`).toBeFocused();
    await expect(create).toBeDisabled(); // aria-disabled: reachable, and said to be unavailable
    await page.keyboard.press("Enter");
    await expect(name).toBeFocused();
    await expect(page).toHaveURL(/new-page$/);
  });

  test("SH-16 a shared primitive brings its own utilities: the Switch of a board opened first is 32×18, not 18×18", async ({ page, context }) => {
    const space = await shellSpace(context);
    const board = await send(context, "post", `/api/spaces/${space}/boards`, { title: `UX-shell Tabule ${stamp()}` });
    made.push(board.id);
    // A fresh document straight onto the board: nothing of the settings UI (and its lazy stylesheet) has loaded.
    await page.goto(`/#/boards/${board.id}`);
    await page.reload();
    await page.locator("[data-board-settings-trigger]").click();
    const toggle = page.locator("[data-board-snap]");
    await expect(toggle).toBeVisible();
    expect(await toggle.evaluate(el => { const box = el.getBoundingClientRect(); return `${Math.round(box.width)}x${Math.round(box.height)}`; })).toBe("32x18");
  });

  test("SH-7 Publikovat is a palette command and publishes the same way as the button", async ({ page, context }) => {
    const id = await createPage(context, await shellSpace(context), `UX-shell Publikovat ${stamp()}`);
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".ProseMirror").first()).toContainText("text.");
    // 3.6: the focused page text takes the writer in with Enter; the keys after it are typed into it.
    await page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
    await page.keyboard.press("Enter");
    await expect(page.locator('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]')).toBeFocused();
    await page.keyboard.type(" Doplněno z klávesnice.");
    await expect(page.locator(".publish-control .publish-main")).toBeEnabled();
    await page.keyboard.press("Control+k");
    await page.keyboard.type(">publikovat");
    await expect(page.getByRole("option").first()).toHaveAccessibleName(/^Publikovat změny/);
    await page.keyboard.press("Enter");
    await expect(page.getByText("Stránka byla publikována.", { exact: true })).toBeVisible();
    await expect(page.locator(".publish-control .publish-main")).toHaveCount(0);
    await expect.poll(async () => (await (await context.request.get(`/api/pages/${id}`)).json()).contentJson).toContain("Doplněno z klávesnice.");
  });

  test("SH2-1 the outline (Na této stránce) is one Tab stop; ↑ ↓ Home End walk its headings", async ({ page, context }) => {
    const heading = (text: string) => ({ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text }] });
    const paragraph = { type: "paragraph", content: [{ type: "text", text: "Text oddílu. ".repeat(40) }] };
    const doc = { type: "doc", content: ["Úvod", "Postup", "Závěr", "Přílohy"].flatMap(name => [heading(name), paragraph, paragraph]) };
    const { id } = await send(context, "post", "/api/pages", { spaceId: await shellSpace(context), parentId: null, title: `UX-shell Osnova ${stamp()}`, contentJson: JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: doc }), markdown: "x" });
    made.push(id);
    const current = await (await context.request.get(`/api/pages/${id}`)).json();
    await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
    await page.goto(`/#/pages/${id}`);
    const entries = page.locator(".page-outline-entry");
    await expect(entries).toHaveCount(4);
    await expect.poll(() => entries.evaluateAll(buttons => buttons.filter(button => (button as HTMLElement).tabIndex === 0).length)).toBe(1);
    await entries.nth(1).focus();
    await page.keyboard.press("ArrowDown");
    await expect(entries.nth(2)).toBeFocused();
    await page.keyboard.press("End");
    await expect(entries.nth(3)).toBeFocused();
    await page.keyboard.press("Home");
    await expect(entries.nth(0)).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toBe("h2 Postup");
  });

  test("SH2-2 the tree moves a page by keyboard: Alt+↓ reorders, Alt+→ nests under the sibling above, Alt+← lifts out; focus stays on the page", async ({ page, context }) => {
    const space = await shellSpace(context);
    const tag = stamp();
    const a = await createPage(context, space, `UX-shell Strom A ${tag}`);
    const b = await createPage(context, space, `UX-shell Strom B ${tag}`);
    const c = await createPage(context, space, `UX-shell Strom C ${tag}`);
    await page.goto(`/#/spaces/${space}`);
    const item = page.locator(`[data-sidebar-space="${space}"]`);
    const toggle = item.locator(".sidebar-space-row .tree-toggle");
    if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
    const row = (id: string) => item.locator(`.tree-page[data-tree-item="${id}"]`);
    const parentOf = async (id: string) => ((await (await context.request.get(`/api/pages/${id}`)).json()) as { parentId: string | null }).parentId;
    await row(b).focus();
    await page.keyboard.press("Alt+ArrowRight");
    await expect.poll(() => parentOf(b)).toBe(a);
    await expect(row(b)).toBeFocused();
    await expect(row(b)).toBeFocused();
    // A key pressed while the previous move still runs is ignored (the tree is busy), so press until it lands.
    await expect(async () => { await page.keyboard.press("Alt+ArrowLeft"); expect(await parentOf(b)).toBe(null); }).toPass();
    await expect(row(b)).toBeFocused();
    const order = async () => ((await (await context.request.get(`/api/spaces/${space}/tree`)).json()) as { items: { id: string }[] }).items.map(node => node.id).filter(id => [a, b, c].includes(id));
    // A key pressed while the previous move still runs is ignored (the tree is busy), so press until it lands.
    await expect(async () => { await page.keyboard.press("Alt+ArrowDown"); expect(await order()).toEqual([a, c, b]); }).toPass();
    await expect(row(b)).toBeFocused();
  });

  test("SH2-15 the deployment confirmation leaves with motion, and Esc returns to the page menu", async ({ page, context }) => {
    const id = await createPage(context, await shellSpace(context), `UX-shell Nasazení ${stamp()}`);
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await page.getByRole("button", { name: "Další akce stránky", exact: true }).click();
    const found = page.getByRole("menuitem", { name: /Založit nasazení podle této stránky/ });
    if (!(await found.isVisible().catch(() => false))) test.skip(true, "Needs the Projector module.");
    await found.click();
    const dialog = page.locator("dialog.app-dialog[open]");
    await expect(dialog.getByRole("heading", { name: "Založit nasazení podle této stránky" })).toBeVisible();
    await expect.poll(() => dialog.evaluate(el => el.getAnimations().filter(a => a.playState === "running").length)).toBe(0);
    await page.keyboard.press("Escape");
    const leaving = await page.evaluate(() => { const open = document.querySelector("dialog.app-dialog[open]"); return !!open && open.getAnimations().length > 0; });
    expect(leaving).toBe(true);
    await expect(page.locator("dialog.app-dialog[open]")).toHaveCount(0);
  });

  test("SH2-3 arrows walk the rows of a space's trash and Enter on Obnovit opens the restore of that page", async ({ page, context }) => {
    const space = await shellSpace(context);
    for (const name of ["AA", "BB", "CC"]) {
      const id = await createPage(context, space, `UX-shell Koš ${name} ${stamp()}`);
      const current = await (await context.request.get(`/api/pages/${id}`)).json();
      const tree = await (await context.request.get(`/api/spaces/${space}/tree`)).json();
      await send(context, "post", `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: current.version });
    }
    await page.goto(`/#/spaces/${space}`);
    await page.getByRole("button", { name: /^Akce prostoru: / }).click();
    await page.getByRole("menuitem", { name: "Koš" }).click();
    // The rows of the shared trash list (TrashList): ↑ ↓ Home End move between the rows' Obnovit.
    const items = page.locator("dialog[open] [data-trash-restore]");
    await expect(items.nth(2)).toBeVisible();
    await items.first().focus();
    await page.keyboard.press("ArrowDown");
    await expect(items.nth(1)).toBeFocused();
    await page.keyboard.press("End");
    await expect(items.last()).toBeFocused();
    await page.keyboard.press("Home");
    await expect(items.first()).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.locator("dialog[open] .organization-restore")).toBeVisible();
  });

  test("SH2-14 the page list of a space is one Tab stop; ↑ ↓ Home End walk the rows", async ({ page, context }) => {
    const space = await shellSpace(context);
    for (const name of ["AA", "BB", "CC", "DD"]) await createPage(context, space, `UX-shell Řádek ${name} ${stamp()}`);
    await page.goto(`/#/spaces/${space}`);
    const titles = page.locator(".space-page-title");
    await expect(titles.nth(3)).toBeVisible();
    await expect.poll(() => titles.evaluateAll(links => links.filter(link => (link as HTMLElement).tabIndex === 0).length)).toBe(1);
    await titles.first().focus();
    await page.keyboard.press("ArrowDown");
    await expect(titles.nth(1)).toBeFocused();
    await page.keyboard.press("End");
    await expect(titles.last()).toBeFocused();
    await page.keyboard.press("Home");
    await expect(titles.first()).toBeFocused();
    // The stop follows the focus: Tab leaves the list, Shift+Tab comes back to the row last used.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Tab");
    await expect(page.locator(".space-page-title:focus")).toHaveCount(0);
    await page.keyboard.press("Shift+Tab");
    await expect(titles.nth(1)).toBeFocused();
  });
});

// UX cycle 4, area shell-boards.
test.describe("shell cycle 4", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("SB4-2 a space page offers Nový once in its head; the bar does not repeat it under the same name", async ({ page, context }) => {
    const space = await shellSpace(context);
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`/#/spaces/${space}`);
      await expect(page.locator(".space-hero h1")).toBeVisible();
      const names = await page.locator("main [aria-label^='Nový obsah'], main [data-space-new-page], main .new-page-button").evaluateAll(els =>
        els.filter(el => (el as HTMLElement).offsetParent).map(el => el.getAttribute("aria-label") ?? el.textContent?.trim()));
      expect(names, `${width} px`).toEqual(["Nový obsah v prostoru"]);
      // The head's Nový is the one that stays.
      await expect(page.locator(".space-hero-actions").getByRole("button", { name: "Nový obsah v prostoru" })).toBeVisible();
    }
    // A page of the space keeps the bar's Nový (no head there).
    await page.setViewportSize({ width: 1280, height: 900 });
    const id = await createPage(context, space, `UX-shell Nový ${stamp()}`);
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await expect(page.locator(".workspace-bar").getByRole("button", { name: "Nový obsah v prostoru" })).toBeVisible();
  });

  test("SB4-5 Ctrl+K: a space whose whole name is the query is preselected before pages that only start with it; Enter opens it", async ({ page, context }) => {
    const space = await shellSpace(context);
    // A page whose title starts with the space's name: before the fix it led and the space came after every group.
    await createPage(context, space, `UX shell Prostor ${stamp()}`);
    await page.locator("h1").first().focus();
    await page.keyboard.press("Control+k");
    await page.keyboard.type("UX shell");
    const selected = page.locator('[role="option"][aria-selected="true"]');
    await expect(selected).toHaveCount(1);
    await expect(selected).toHaveAttribute("href", new RegExp(`#/spaces/${space}$`));
    await expect(page.locator("#search-results > .search-group").first()).toHaveAttribute("data-kind", "spaces");
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`#/spaces/${space}$`));
  });

  test("SB4-6 an unavailable page and board answer alike: one frame, Zpět and Knihovna, the focus on the message", async ({ page }) => {
    const missing = "00000000-0000-4000-8000-000000000999";
    const shapes: { width: number; actions: string[] }[] = [];
    for (const hash of [`#/pages/${missing}`, `#/boards/${missing}`]) {
      await page.goto(`/#/library/spaces`);
      await expect(page.locator("h1", { hasText: "Knihovna" })).toBeVisible();
      await page.goto(`/${hash}`);
      const message = page.locator(".page-unavailable .st-empty");
      await expect(message).toBeVisible();
      await expect.poll(() => message.evaluate(el => el === document.activeElement), { message: `${hash}: focus on the message` }).toBe(true);
      shapes.push({ width: Math.round((await message.boundingBox())!.width), actions: await message.getByRole("button").allTextContents() });
    }
    expect(shapes[1]).toEqual(shapes[0]);
    expect(shapes[0]!.actions.map(text => text.trim())).toEqual(["Zpět", "Knihovna"]);
    // Knihovna leads home from the board as from the page.
    await page.locator(".page-unavailable").getByRole("button", { name: "Knihovna" }).click();
    await expect(page.locator("h1", { hasText: "Knihovna" })).toBeVisible();
  });

  test("SB4-7 a link to a space that is gone or closed says so instead of showing Knihovna without a word", async ({ page }) => {
    const missing = "00000000-0000-4000-8000-000000000998";
    await page.goto(`/#/spaces/${missing}`);
    const message = page.locator(".page-unavailable .st-empty");
    await expect(message).toContainText("Prostor není dostupný");
    await expect(page).toHaveURL(new RegExp(`#/spaces/${missing}$`));
    await expect.poll(() => message.evaluate(el => el === document.activeElement)).toBe(true);
    await message.getByRole("button", { name: "Knihovna" }).click();
    await expect(page.locator("h1", { hasText: "Knihovna" })).toBeVisible();
    await expect(page.locator(".page-unavailable")).toHaveCount(0);
  });

  test("SB4-8 a session that ended on the server leads to the sign-in with the reason, not to a red „Přihlaste se.“ over a signed-in wiki", async ({ page, context }) => {
    const space = await shellSpace(context);
    const id = await createPage(context, space, `UX-shell Session ${stamp()}`);
    await page.goto(`/#/spaces/${space}`);
    await expect(page.locator(".space-hero h1")).toBeVisible();
    // The session ends (expired, revoked on another device): the next request answers 401.
    await context.clearCookies();
    await page.evaluate(target => { location.hash = `#/pages/${target}`; }, id);
    await expect(page.getByText("Vaše přihlášení skončilo. Přihlaste se prosím znovu.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Přihlásit jako vývojář" })).toBeVisible();
    // Signed in again for the trash in afterAll.
    await signIn(page);
  });
});

// Real wiki (ATLAS_REAL=1 against the real-data instance): read-only journeys over the biggest space, no ids or titles in the spec.
test.describe("shell over a real wiki", () => {
  test.skip(process.env.ATLAS_REAL !== "1", "Set ATLAS_REAL=1 and ATLAS_URL to the real-data instance.");
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("SH-R1 a deep tree by keyboard, search over real titles, and the access view lands in its field", async ({ page, context }) => {
    const spaces: { id: string; pageCount: number }[] = await (await context.request.get("/api/spaces")).json();
    const biggest = spaces.reduce((a, b) => (b.pageCount > a.pageCount ? b : a));
    await page.goto(`/#/spaces/${biggest.id}`);
    const item = page.locator(`[data-sidebar-space="${biggest.id}"]`);
    const toggle = item.locator(".sidebar-space-row .tree-toggle");
    if ((await toggle.getAttribute("aria-expanded")) === "true") await toggle.click();
    await item.locator(".sidebar-space-link").focus();
    await page.keyboard.press("ArrowRight");
    await expect(item.locator(".tree-page").first()).toBeVisible();
    await page.keyboard.press("ArrowDown");
    await expect(item.locator(".tree-page").first()).toBeFocused();
    await page.keyboard.press("End");
    await expect(item.locator(".tree-page").last()).toBeFocused();
    const titles = (await item.locator(".tree-page").allTextContents()).map(title => title.trim());
    const fold = (text: string) => text.toLocaleLowerCase("cs").normalize("NFD").replace(/\p{M}/gu, "");
    const letter = titles[Math.min(5, titles.length - 1)]![0]!;
    // Since the tree filter (space contents spec §5.2) a letter on a row opens the filter with it; its preselected match holds it.
    const opener = (await page.locator(".tree-page:focus").textContent())?.trim();
    await page.keyboard.press(letter);
    await expect(item.locator("input:focus")).toHaveValue(letter);
    await expect.poll(async () => fold((await item.locator(".tree-row.tree-filter-selected .tree-page").first().textContent()) ?? "")).toContain(fold(letter));
    // Esc gives the focus back to the row the filter was opened from, also when the filter folded its branch away (SB4-9).
    await page.keyboard.press("Escape");
    await expect.poll(async () => (await item.locator(".tree-page:focus").textContent())?.trim()).toBe(opener);

    const title = titles.find(candidate => candidate.length > 12) ?? titles[0]!;
    await page.keyboard.press("Control+k");
    await page.keyboard.type(title.slice(0, 8));
    await expect(page.locator('[role="option"][aria-selected="true"]').first()).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#\/pages\//);
    await expect.poll(() => focused(page)).toMatch(/^h1 /);

    await page.getByRole("button", { name: "Kdo má přístup" }).first().focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toBe("input Hledat osobu nebo skupinu");
  });
});
