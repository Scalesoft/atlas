import { randomUUID } from "node:crypto";
import { PAGE_TEXT } from "./editing";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// UX review cycle 1, boards: one test per finding BD-n. Runs against a
// local Development instance with the module Boards (and Projector for the deployment board), signed in by „Přihlásit jako
// vývojář“; everything lives in the space „UX boards“, titled „UX-boards …“. Skipped where the developer sign-in is not offered.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 },
  ...(process.env.PLAYWRIGHT_REDUCED_MOTION ? { reducedMotion: process.env.PLAYWRIGHT_REDUCED_MOTION as "reduce" | "no-preference" } : {}) });

const SPACE = "UX boards";
let developerSignIn = false;
test.beforeAll(async ({ request }) => {
  const methods = await request.get(`${process.env.ATLAS_URL ?? ""}/api/auth/methods`).then(response => response.ok() ? response.json() : {}).catch(() => ({}));
  developerSignIn = !!(methods as { developerSignIn?: boolean }).developerSignIn;
});
test.beforeEach(() => { test.skip(!developerSignIn, "Requires a local Development instance with the developer sign-in and the module Boards."); });

async function signIn(context: BrowserContext, phone = false): Promise<Page> {
  const page = await context.newPage();
  await page.goto("/");
  const search = page.getByRole("button", { name: /^Hledat ve wiki/ });
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  if (phone) {
    // On a phone the search is an icon with another name: signed in is when the API answers.
    await developer.waitFor({ timeout: 8000 }).catch(() => undefined);
    if (await developer.isVisible()) await developer.click();
    await expect.poll(async () => (await context.request.get("/api/spaces")).ok()).toBe(true);
    return page;
  }
  await expect(search.or(developer)).toBeVisible();
  if (await developer.isVisible()) await developer.click();
  await expect(search).toBeVisible();
  return page;
}
async function send(request: APIRequestContext, method: "post" | "delete", path: string, data?: unknown) {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBeLessThan(300);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}
async function space(request: APIRequestContext): Promise<string> {
  const spaces = await (await request.get("/api/spaces")).json() as { id: string; name: string }[];
  return spaces.find(item => item.name === SPACE)?.id ?? (await send(request, "post", "/api/spaces", { name: SPACE, icon: "code-xml", color: "blue" })).id;
}
const box = { x: 0, y: 0, width: 8, height: 8 };
const connector = (id: string, from: string, to: string) =>
  ({ type: "create", item: { id, kind: "connector", ...box, z: 0, from, to, props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" } } });
/** A board with a frame holding notes A and B and a shape, a free text right of it, and connectors A→B and B→text. */
async function seedBoard(request: APIRequestContext, title: string, extra: unknown[] = []) {
  const board = await send(request, "post", `/api/spaces/${await space(request)}/boards`, { title: `UX-boards ${title} ${Date.now().toString(36)}` });
  const id = { board: board.id as string, frame: randomUUID(), a: randomUUID(), b: randomUUID(), shape: randomUUID(), text: randomUUID(), c1: randomUUID(), c2: randomUUID() };
  await send(request, "post", `/api/boards/${id.board}/operations`, { operationId: randomUUID(), changes: [
    { type: "create", item: { id: id.frame, kind: "frame", x: 0, y: 0, width: 700, height: 420, z: 1, text: "Rám A", props: {} } },
    { type: "create", item: { id: id.a, kind: "note", x: 40, y: 80, width: 200, height: 200, z: 2, text: "Poznámka A", color: "yellow", props: { fontSize: "auto" } } },
    { type: "create", item: { id: id.b, kind: "note", x: 420, y: 80, width: 200, height: 200, z: 3, text: "Poznámka B", color: "blue", props: { fontSize: "auto" } } },
    { type: "create", item: { id: id.shape, kind: "shape", x: 260, y: 300, width: 140, height: 90, z: 4, text: "Tvar", props: { shape: "rounded" } } },
    { type: "create", item: { id: id.text, kind: "text", x: 800, y: 80, width: 240, height: 60, z: 5, text: "Volný text", props: { fontSize: "m", align: "left" } } },
    connector(id.c1, id.a, id.b), connector(id.c2, id.b, id.text),
    { type: "parent", itemId: id.a, parentId: id.frame }, { type: "parent", itemId: id.b, parentId: id.frame }, { type: "parent", itemId: id.shape, parentId: id.frame },
    ...extra,
  ] });
  return id;
}
async function openBoard(page: Page, boardId: string) {
  await page.goto(`/#/boards/${boardId}`);
  await expect(page.locator("[data-board-surface]")).toBeVisible();
  await expect(page.locator(".vue-flow__node").first()).toBeVisible();
}
/**
 * The editor's `focus("end")` (tiptap) focuses its view in the next animation frame, after the element already reports focus; a key
 * typed before that frame was lost (BD-12: „lávesnicí“ in 3 of 15 runs under reduced motion). A person cannot type within a frame.
 */
const editorFocusSettled = (page: Page) => page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
const node = (page: Page, id: string) => page.locator(`.vue-flow__node[data-id="${id}"]`);
const focusInside = (page: Page, selector: string) => page.evaluate(selector => !!document.activeElement?.closest(selector), selector);
/** Clicks the middle of a connector's line. */
async function clickEdge(page: Page, id: string, shift = false) {
  const point = await page.locator(`.vue-flow__edge[data-id="${id}"] path.vue-flow__edge-path`).evaluate(path => {
    const line = path as SVGPathElement, at = line.getPointAtLength(line.getTotalLength() / 2), matrix = line.getScreenCTM()!;
    return { x: at.x * matrix.a + matrix.e, y: at.y * matrix.d + matrix.f };
  });
  if (shift) await page.keyboard.down("Shift");
  await page.mouse.click(point.x, point.y);
  if (shift) await page.keyboard.up("Shift");
}

test("BD-1 an opened board takes the keys: the surface has the focus, also from the page menu", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-1");
    await openBoard(page, ids.board);
    await expect(page.locator("[data-board-surface]")).toBeFocused();

    // A deployment page's board from „Další akce stránky“, keyboard only.
    const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
    const requirement = (title: string, key: string) => ({ type: "requirement", attrs: { id: randomUUID(), key, direction: "internal", state: "planned", date: "", party: "", where: "", requires: "" }, content: [paragraph(title)] });
    // The board draws a product's card: requirements without a product heading over them compose nothing (deploymentMap.ts).
    const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 1 }],
      document: { type: "doc", content: [{ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Portál" }] }, requirement("Napojení na NIS", "nis"), requirement("Školení", "training"), { type: "paragraph" }] } });
    const created = await send(context.request, "post", "/api/pages", { spaceId: await space(context.request), parentId: null, title: `UX-boards BD-1 nasazení ${Date.now().toString(36)}`, contentJson, markdown: "x" });
    const current = await (await context.request.get(`/api/pages/${created.id}`)).json();
    await send(context.request, "post", `/api/pages/${created.id}/publish`, { expectedVersion: current.version });
    await page.goto(`/#/pages/${created.id}`);
    await expect(page.locator(PAGE_TEXT)).toBeVisible();
    await page.getByRole("button", { name: "Další akce stránky" }).focus();
    await page.keyboard.press("Enter");
    await page.locator("[data-page-deployment-board]").focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/#\/boards\//);
    await expect(page.locator("[data-board-surface]")).toBeFocused();
  } finally { await context.close(); }
});

test("BD-2 Tab past the last item leaves the board instead of circling between items", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-2");
    await openBoard(page, ids.board);
    await page.locator("[data-board-surface]").focus();
    // Reading order: Rám A, Poznámka A, Poznámka B, Volný text, Tvar; then out.
    for (let step = 0; step < 5; step++) { await page.keyboard.press("Tab"); await page.waitForTimeout(120); }
    await expect(node(page, ids.shape)).toBeFocused();
    await page.keyboard.press("Tab");
    await page.waitForTimeout(120);
    expect(await focusInside(page, "[data-board-surface]")).toBe(false);
  } finally { await context.close(); }
});

test("BD-3 Alt+F10 goes into the selection's toolbar and Esc back to the item", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-3");
    await openBoard(page, ids.board);
    await node(page, ids.a).click();
    await page.keyboard.press("Alt+F10");
    await expect(page.locator("[data-board-context-toolbar] [data-board-color-trigger]")).toBeFocused();
    await page.keyboard.press("ArrowRight");
    await page.keyboard.press("Escape");
    await expect(node(page, ids.a)).toBeFocused();
  } finally { await context.close(); }
});

test("BD-4 „Vybrat obsah“ keeps the focus on the board, and Esc goes up to the frame", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-4");
    await openBoard(page, ids.board);
    await node(page, ids.frame).locator("text=Rám A").click();
    await expect(node(page, ids.frame)).toHaveClass(/selected/);
    await page.locator("[data-board-select-members]").click();
    await expect(page.locator(".vue-flow__node.selected")).toHaveCount(3);
    await expect.poll(() => focusInside(page, "[data-board-surface]")).toBe(true);
    await page.keyboard.press("Escape");
    await expect(page.locator(".vue-flow__node.selected")).toHaveCount(1);
    await expect(node(page, ids.frame)).toBeFocused();
  } finally { await context.close(); }
});

test("BD-5 the board's toolbar and command panel have no empty gutter on the left", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-5");
    await openBoard(page, ids.board);
    await node(page, ids.a).click();
    const toolbar = page.locator("[data-board-context-toolbar]");
    await expect(toolbar).toBeVisible();
    expect(await toolbar.evaluate(element => parseFloat(getComputedStyle(element).paddingLeft))).toBeLessThanOrEqual(8);
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("Escape");
    await page.keyboard.press("/");
    const panel = page.locator("[data-board-command-panel]");
    await expect(panel).toBeVisible();
    const offset = await panel.evaluate(element => element.querySelector("input")!.getBoundingClientRect().left - element.getBoundingClientRect().left);
    expect(offset).toBeLessThanOrEqual(8);
  } finally { await context.close(); }
});

test("BD-6 BD-7 connectors: the elbow route is not drawn as people, and connectors alone get no align menu", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-6");
    await openBoard(page, ids.board);
    await clickEdge(page, ids.c1);
    await clickEdge(page, ids.c2, true);
    await expect(page.locator(".vue-flow__edge.selected")).toHaveCount(2);
    const route = page.locator("[data-board-route-menu] svg").first();
    await expect(route).toBeVisible();
    await expect(route).not.toHaveAttribute("data-icon", "users");
    await expect(page.locator("[data-board-dash]")).toBeVisible();
    await expect(page.locator("[data-board-align-menu]")).toHaveCount(0);
  } finally { await context.close(); }
});

test("BD-8 a board with a contact in a note opens straight after a reload without errors", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const note = randomUUID();
    const ids = await seedBoard(context.request, "BD-8", [{ type: "create", item: { id: note, kind: "note", x: 1100, y: 80, width: 200, height: 200, z: 6,
      text: "Volá [Petr Zkušební](a-contact:?name=Petr%20Zku%C5%A1ebn%C3%AD)", color: "green", props: { fontSize: "auto" } } }]);
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("about:blank");
    await page.goto(`/#/boards/${ids.board}`);
    await expect(node(page, note)).toBeVisible();
    await expect(node(page, note)).toContainText("Petr Zkušební");
    await page.waitForTimeout(500);
    expect(errors).toEqual([]);
  } finally { await context.close(); }
});

test("BD-9 the help lists the board's keys under „Tabule“ (Ctrl+/ on the board)", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-9");
    await openBoard(page, ids.board);
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("Control+/");
    const dialog = page.getByRole("dialog", { name: "Klávesové zkratky" });
    await expect(dialog).toBeVisible();
    const board = dialog.getByRole("region", { name: "Tabule" });
    await expect(board).toContainText("Alt F10");
    await expect(board).toContainText("Nástroje");
  } finally { await context.close(); }
});

test("BD-13 C, a name, Enter, Enter places the contact card without the mouse", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const name = `UXboards Kontakt ${Date.now().toString(36)}`;
    await send(context.request, "post", "/api/objects", { typeId: "atlas.contact", scope: "wiki", data: { name, emails: [`ux-boards-${Date.now()}@example.cz`] } });
    const ids = await seedBoard(context.request, "BD-13");
    await openBoard(page, ids.board);
    const before = await page.locator(".vue-flow__node").count();
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("c");
    await expect(page.getByRole("searchbox", { name: "Kontakt z adresáře: hledat" })).toBeFocused();
    await page.keyboard.type(name);
    await expect(page.locator("[data-board-contact-picker] [data-first]")).toContainText(name);
    await page.keyboard.press("Enter");
    await expect(page.locator("[data-board-contact-confirm]")).toBeEnabled();
    await page.keyboard.press("Enter");
    await expect(page.locator("[data-board-contact-picker]")).toBeHidden();
    await expect(page.locator(".vue-flow__node")).toHaveCount(before + 1);
    await expect(page.locator(".vue-flow__node", { hasText: name })).toBeVisible();
  } finally { await context.close(); }
});

test("BD-14 the snapping switch in the board's settings is drawn as a switch, not as an empty box", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-14");
    await openBoard(page, ids.board);
    await page.locator("[data-board-settings-trigger]").click();
    const toggle = page.locator("[data-board-settings] [data-board-snap]");
    await expect(toggle).toBeVisible();
    const look = await toggle.evaluate(element => { const style = getComputedStyle(element); return { padding: parseFloat(style.paddingLeft), radius: parseFloat(style.borderTopLeftRadius), height: element.getBoundingClientRect().height }; });
    expect(look.padding).toBe(0);
    expect(look.radius).toBeGreaterThanOrEqual(look.height / 2 - 1);
  } finally { await context.close(); }
});

test("BD-15 the rail's Znovu that disables itself leaves the keys with the board, Ctrl+Z still undoes", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-15");
    await openBoard(page, ids.board);
    const count = await page.locator(".vue-flow__node").count();
    await node(page, ids.b).click();
    await page.keyboard.press("Delete");
    await expect(page.locator(".vue-flow__node")).toHaveCount(count - 1);
    await page.keyboard.press("Control+z");
    await expect(page.locator(".vue-flow__node")).toHaveCount(count);
    const redo = page.locator("[data-board-rail]").getByRole("button", { name: "Znovu", exact: true });
    await redo.click();
    await expect(page.locator(".vue-flow__node")).toHaveCount(count - 1);
    await expect(redo).toBeDisabled();
    await expect.poll(() => focusInside(page, "[data-board-canvas]")).toBe(true);
    await page.keyboard.press("Control+z");
    await expect(page.locator(".vue-flow__node")).toHaveCount(count);
  } finally { await context.close(); }
});

test("BD-12 a creation tool chosen by its key places its item with Enter", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD-12");
    await openBoard(page, ids.board);
    const before = await page.locator(".vue-flow__node").count();
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("n");
    await page.keyboard.press("Enter");
    await expect(page.locator(".vue-flow__node")).toHaveCount(before + 1);
    await expect(page.locator("[data-board-canvas] .tiptap[contenteditable='true']")).toBeFocused();
    await editorFocusSettled(page);
    await page.keyboard.type("Klávesnicí");
    await page.keyboard.press("Escape");
    await expect(page.locator(".vue-flow__node", { hasText: "Klávesnicí" })).toBeVisible();
  } finally { await context.close(); }
});

// UX review cycle 2: findings BD2-n.
const viewportOf = (page: Page) => page.evaluate(() => document.querySelector<HTMLElement>(".vue-flow__transformationpane")?.style.transform ?? "");
/** About 450 items in six frames: notes, shapes, texts and the connectors between them (the size of a real project board). */
async function seedBig(request: APIRequestContext, title: string) {
  const board = await send(request, "post", `/api/spaces/${await space(request)}/boards`, { title: `UX-boards ${title} ${Date.now().toString(36)}` });
  const changes: unknown[] = []; let z = 1;
  for (let f = 0; f < 6; f++) {
    const fx = (f % 3) * 1500, fy = Math.floor(f / 3) * 1300, frame = randomUUID();
    changes.push({ type: "create", item: { id: frame, kind: "frame", x: fx, y: fy, width: 1400, height: 1200, z: z++, text: `Oddíl ${f + 1}`, color: "blue", props: {} } });
    let previous = "";
    for (let i = 0; i < 40; i++) {
      const id = randomUUID(), at = { id, x: fx + 40 + (i % 8) * 165, y: fy + 70 + Math.floor(i / 8) * 215, z: z++ };
      changes.push(i % 3 === 0 ? { type: "create", item: { ...at, kind: "note", width: 150, height: 150, text: `Poznámka ${f + 1}.${i}`, color: "yellow", props: { fontSize: "auto" } } }
        : i % 3 === 1 ? { type: "create", item: { ...at, kind: "shape", width: 150, height: 90, text: `Krok ${i}`, props: { shape: "rounded" } } }
        : { type: "create", item: { ...at, kind: "text", width: 150, height: 50, text: `Text ${f + 1}-${i}`, props: { fontSize: "m", align: "left" } } });
      changes.push({ type: "parent", itemId: id, parentId: frame });
      if (previous && i % 8 !== 0) changes.push(connector(randomUUID(), previous, id));
      previous = id;
    }
  }
  for (let from = 0; from < changes.length; from += 180) await send(request, "post", `/api/boards/${board.id}/operations`, { operationId: randomUUID(), changes: changes.slice(from, from + 180) });
  return board.id as string;
}

test("BD2-1 a board of 450 items pans without laying the whole canvas out again at every step", async ({ browser }) => {
  test.setTimeout(120_000);
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const board = await seedBig(context.request, "BD2-1");
    await openBoard(page, board);
    await page.waitForTimeout(1500);
    // Every item stays in the page: one that enters or leaves the view made the browser lay out all of them (80 ms a frame).
    await expect(page.locator(".vue-flow__node")).toHaveCount(246);
    const cdp = await context.newCDPSession(page);
    const events: { name?: string; ph?: string; dur?: number }[] = [];
    cdp.on("Tracing.dataCollected", chunk => events.push(...chunk.value));
    await cdp.send("Tracing.start", { traceConfig: { includedCategories: ["devtools.timeline"] } });
    const surface = await page.locator("[data-board-surface]").boundingBox();
    const x = surface!.x + surface!.width / 2, y = surface!.y + surface!.height / 2;
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.down("Space");
    await page.mouse.move(x, y);
    await page.mouse.down();
    for (let step = 1; step <= 50; step++) await page.mouse.move(x - step * 8, y - step * 4);
    await page.mouse.up();
    await page.keyboard.up("Space");
    for (let step = 0; step < 12; step++) { await page.mouse.wheel(0, step < 6 ? -120 : 120); await page.waitForTimeout(16); }
    await new Promise<void>(resolve => { cdp.once("Tracing.tracingComplete", () => resolve()); void cdp.send("Tracing.end"); });
    const layout = events.filter(event => event.name === "Layout" && event.ph === "X").reduce((sum, event) => sum + (event.dur ?? 0), 0) / 1000;
    // Was 1100 ms for the pan alone; 10–30 ms now. The limit is generous for a loaded runner.
    expect(layout, `layout time while panning and zooming: ${layout.toFixed(0)} ms`).toBeLessThan(350);
  } finally { await context.close(); }
});

test("BD2-4 a note shrinks its text until its longest word fits, instead of breaking the word in the middle", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const note = randomUUID();
    const ids = await seedBoard(context.request, "BD2-4", [{ type: "create", item: { id: note, kind: "note", x: 800, y: 300, width: 200, height: 200, z: 7, text: "Zkontrolovat aktuálnost", color: "yellow", props: { fontSize: "auto" } } }]);
    await openBoard(page, ids.board);
    await expect(node(page, note)).toBeVisible();
    const lines = await node(page, note).locator("p").evaluate(paragraph => {
      const text = paragraph.firstChild as Text, range = document.createRange(), rows = new Map<number, string>();
      for (let index = 0; index < text.length; index++) { range.setStart(text, index); range.setEnd(text, index + 1); const top = Math.round(range.getBoundingClientRect().top); rows.set(top, (rows.get(top) ?? "") + text.data[index]); }
      return [...rows.values()].map(row => row.trim());
    });
    expect(lines).toEqual(["Zkontrolovat", "aktuálnost"]);
  } finally { await context.close(); }
});

test("BD2-3 coming back from a page card's page, the board is where it was: same view, same selection, the key on the card", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const spaceId = await space(context.request);
    const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Stránka karty" }] }] } });
    const created = await send(context.request, "post", "/api/pages", { spaceId, parentId: null, title: `UX-boards BD2-3 stránka ${Date.now().toString(36)}`, contentJson, markdown: "x" });
    const current = await (await context.request.get(`/api/pages/${created.id}`)).json();
    await send(context.request, "post", `/api/pages/${created.id}/publish`, { expectedVersion: current.version });
    const card = randomUUID();
    const ids = await seedBoard(context.request, "BD2-3", [{ type: "create", item: { id: card, kind: "page", x: 100, y: 500, width: 260, height: 150, z: 8, targetResourceId: created.id, props: { display: "card" } } }]);
    await openBoard(page, ids.board);
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("Shift+0");
    await page.mouse.wheel(60, 40);
    await expect(node(page, card)).toBeVisible();
    await node(page, card).click({ position: { x: 30, y: 20 } });
    await expect(node(page, card)).toHaveClass(/selected/);
    await page.waitForTimeout(500);
    const before = await viewportOf(page);
    await node(page, card).locator("[data-board-open-page]").click();
    await expect(page).toHaveURL(new RegExp(`#/pages/${created.id}`));
    await page.goBack();
    await expect(page.locator("[data-board-surface]")).toBeVisible();
    await expect(node(page, card)).toHaveClass(/selected/);
    expect(await viewportOf(page)).toBe(before);
    await expect(node(page, card)).toBeFocused();
  } finally { await context.close(); }
});

test("BD2-5 on a touch screen one finger on empty canvas pans the view and two fingers zoom it, without choosing the hand first", async ({ browser }) => {
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 390, height: 780 }, hasTouch: true, isMobile: true });
  try {
    const page = await signIn(context, true);
    await expect.poll(async () => (await context.request.get("/api/spaces")).ok()).toBe(true);
    const ids = await seedBoard(context.request, "BD2-5");
    await openBoard(page, ids.board);
    await page.waitForTimeout(800);
    const cdp = await context.newCDPSession(page);
    const touch = (type: string, points: [number, number][]) => cdp.send("Input.dispatchTouchEvent", { type, touchPoints: points.map(([x, y], id) => ({ x, y, id })) });
    const empty = await page.evaluate(() => { const box = document.querySelector("[data-board-surface]")!.getBoundingClientRect(); return { x: box.left + 40, y: box.bottom - 140 }; });
    const first = await viewportOf(page);
    await touch("touchStart", [[empty.x + 60, empty.y]]);
    for (let step = 1; step <= 12; step++) await touch("touchMove", [[empty.x + 60 - step * 4, empty.y - step * 6]]);
    await touch("touchEnd", []);
    await page.waitForTimeout(300);
    const panned = await viewportOf(page);
    expect(panned).not.toBe(first);
    await touch("touchStart", [[empty.x + 20, empty.y], [empty.x + 160, empty.y]]);
    for (let step = 1; step <= 10; step++) await touch("touchMove", [[empty.x + 20 - step * 3, empty.y], [empty.x + 160 + step * 3, empty.y]]);
    await touch("touchEnd", []);
    await page.waitForTimeout(300);
    expect(await viewportOf(page)).not.toBe(panned);
  } finally { await context.close(); }
});

test("BD2-6 an unselected frame dragged by its outline or by its name moves with its content (the outline does not draw a rectangle)", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const ids = await seedBoard(context.request, "BD2-6");
    await openBoard(page, ids.board);
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("Shift+1");
    await page.waitForTimeout(700);
    const frame = node(page, ids.frame), a = node(page, ids.a);
    // The name moves the frame with its content.
    const title = (await frame.locator(".board-frame-title").boundingBox())!, first = (await a.boundingBox())!;
    await page.mouse.move(title.x + 6, title.y + title.height / 2);
    await page.mouse.down();
    await page.mouse.move(title.x + 56, title.y + 30, { steps: 6 });
    await page.mouse.up();
    await expect.poll(async () => (await a.boundingBox())!.x).toBeGreaterThan(first.x + 20);
    // So does the outline of the frame (now selected; still the same hit area).
    await page.keyboard.press("Escape");
    const frameBox = (await frame.boundingBox())!, before = (await a.boundingBox())!;
    const startX = frameBox.x + frameBox.width * 0.93, startY = frameBox.y + frameBox.height - 1;
    await page.mouse.move(startX, startY);
    await page.mouse.down();
    await page.mouse.move(startX - 40, startY - 20, { steps: 6 });
    await page.mouse.up();
    await expect.poll(async () => (await a.boundingBox())!.x).toBeLessThan(before.x - 20);
  } finally { await context.close(); }
});

test("BD2-7 an item of a big board is found by its name from the command panel: / , a few letters, Enter brings it to the view, selected, with the key", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const far = randomUUID();
    const ids = await seedBoard(context.request, "BD2-7", [{ type: "create", item: { id: far, kind: "note", x: 4200, y: 3100, width: 200, height: 200, z: 9, text: "Vzdálená poznámka Zeta", color: "pink", props: { fontSize: "auto" } } }]);
    await openBoard(page, ids.board);
    const surface = page.locator("[data-board-surface]");
    await surface.focus();
    await page.keyboard.press("Shift+0");
    await page.keyboard.press("/");
    await expect(page.locator("[data-board-command-panel]")).toBeVisible();
    await page.keyboard.type("zeta");
    await expect(page.locator("[data-testid=board-command-list] [role=option]").first()).toContainText("Vzdálená poznámka Zeta");
    await page.keyboard.press("Enter");
    await expect(node(page, far)).toHaveClass(/selected/);
    await expect(node(page, far)).toBeFocused();
    const inside = await node(page, far).evaluate(element => { const box = element.getBoundingClientRect(), area = document.querySelector("[data-board-surface]")!.getBoundingClientRect(); return box.left >= area.left && box.right <= area.right && box.top >= area.top && box.bottom <= area.bottom; });
    expect(inside).toBe(true);
  } finally { await context.close(); }
});

test("BD2-8 on a board of 450 items select all, moving a frame and undo stay quick and exact", async ({ browser }) => {
  test.setTimeout(120_000);
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const board = await seedBig(context.request, "BD2-8");
    await openBoard(page, board);
    await page.waitForTimeout(1000);
    const surface = page.locator("[data-board-surface]");
    await surface.focus();
    const started = Date.now();
    await page.keyboard.press("Control+a");
    await expect(page.locator(".vue-flow__node.selected")).toHaveCount(246);
    expect(Date.now() - started, "select all of 246 nodes").toBeLessThan(4000);
    await page.keyboard.press("Escape");
    await expect(page.locator(".vue-flow__node.selected")).toHaveCount(0);
    // Move the first frame by its name: everything in it follows, and one Ctrl+Z puts it all back.
    const frame = page.locator(".vue-flow__node[data-kind=frame]").first(), child = page.locator(".vue-flow__node[data-kind=note]").first();
    const before = (await child.boundingBox())!, title = (await frame.locator(".board-frame-title").boundingBox())!;
    await page.mouse.move(title.x + 4, title.y + title.height / 2);
    await page.mouse.down();
    await page.mouse.move(title.x + 64, title.y + 40, { steps: 8 });
    await page.mouse.up();
    await expect.poll(async () => Math.abs((await child.boundingBox())!.x - before.x)).toBeGreaterThan(20);
    await surface.focus();
    await page.keyboard.press("Control+z");
    await expect.poll(async () => Math.abs((await child.boundingBox())!.x - before.x)).toBeLessThan(1);
  } finally { await context.close(); }
});

test("BD2-9 a contact card is named after the person for the keyboard, the reader and the finder, not „Karta objektu“", async ({ browser }) => {
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const name = `UXboards Osoba ${Date.now().toString(36)}`;
    const contact = await send(context.request, "post", "/api/objects", { typeId: "atlas.contact", scope: "wiki", data: { name, emails: [`ux-boards-${Date.now()}@example.cz`] } });
    const card = randomUUID();
    const ids = await seedBoard(context.request, "BD2-9", [{ type: "create", item: { id: card, kind: "object", x: 1100, y: 80, width: 340, height: 200, z: 6, targetResourceId: contact.id, props: { objectId: contact.id, typeId: "atlas.contact", view: "card" } } }]);
    await openBoard(page, ids.board);
    await expect(node(page, card)).toHaveAttribute("aria-label", `Kontakt: ${name}`);
    await page.locator("[data-board-surface]").focus();
    await page.keyboard.press("/");
    await page.keyboard.type(name.slice(0, 18));
    await expect(page.locator("[data-testid=board-command-list] [role=option]").first()).toContainText(name);
    await send(context.request, "delete", `/api/objects/${contact.id}`);
  } finally { await context.close(); }
});

// UX cycle 4, area shell-boards.
test("SB4-3 select all on a board of 450 items fades in no connection handles and reroutes nothing: far fewer elements restyled", async ({ browser }) => {
  test.setTimeout(120_000);
  const context = await newContext(browser);
  try {
    const page = await signIn(context);
    const board = await seedBig(context.request, "SB4-3");
    await openBoard(page, board);
    await page.waitForTimeout(1000);
    await page.locator("[data-board-surface]").focus();
    const cdp = await context.newCDPSession(page);
    // Counted in elements, not milliseconds, so a loaded runner does not move it: Chromium reports how many elements each style
    // recalculation touched. Before: the connection handles of all 246 items faded in (984 transitions restyled on every frame) and
    // every connector rerouted twice; now one pass over the selected items and their parts.
    const restyled = async (key: string, count: number) => {
      const events: { name?: string; ph?: string; args?: { elementCount?: number } }[] = [];
      const collect = (chunk: { value: typeof events }) => events.push(...chunk.value);
      cdp.on("Tracing.dataCollected", collect);
      await cdp.send("Tracing.start", { traceConfig: { includedCategories: ["devtools.timeline", "disabled-by-default-devtools.timeline"] } });
      await page.keyboard.press(key);
      await expect(page.locator(".vue-flow__node.selected")).toHaveCount(count);
      await page.waitForTimeout(500);
      await new Promise<void>(resolve => { cdp.once("Tracing.tracingComplete", () => resolve()); void cdp.send("Tracing.end"); });
      cdp.off("Tracing.dataCollected", collect);
      return events.filter(event => event.name === "UpdateLayoutTree" && event.ph === "X").reduce((sum, event) => sum + (event.args?.elementCount ?? 0), 0);
    };
    const all = await restyled("Control+a", 246);
    // Measured on 5209 (before) and the fix: 34 300 → 20 400 (the rest is the connectors' colour fade, 60 fps at most).
    expect(all, `elements restyled by select all: ${all}`).toBeLessThan(26_000);
    await expect(page.locator(".vue-flow__node.selected .board-handle").first()).toHaveCSS("opacity", "0");
    await restyled("Escape", 0);
    // One selected item still shows its connection handles (they moved from „any selected“ to „the only selected“).
    await page.locator(".vue-flow__node[data-kind=note]").first().click();
    await expect(page.locator(".vue-flow__node.selected .board-handle").first()).toHaveCSS("opacity", "1");
  } finally { await context.close(); }
});
