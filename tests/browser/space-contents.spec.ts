import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// Space contents and the tree's reference nodes (owner
// fixes 2026-10-28): an object dragged onto a board is its live card, a reference to another space's board opens in that space, and
// Obsah loads its object dock and relations graph only when they are shown. Runs against a local Development instance with the
// module Boards, signed in by „Přihlásit jako vývojář“; everything is titled „Obsah-e2e …“. Skipped where that sign-in is not offered.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 },
  ...(process.env.PLAYWRIGHT_REDUCED_MOTION ? { reducedMotion: process.env.PLAYWRIGHT_REDUCED_MOTION as "reduce" | "no-preference" } : {}) });

let developerSignIn = false;
test.beforeAll(async ({ request }) => {
  const methods = await request.get(`${process.env.ATLAS_URL ?? ""}/api/auth/methods`).then(response => response.ok() ? response.json() : {}).catch(() => ({}));
  developerSignIn = !!(methods as { developerSignIn?: boolean }).developerSignIn;
});
test.beforeEach(() => { test.skip(!developerSignIn, "Requires a local Development instance with the developer sign-in and the module Boards."); });

async function signIn(context: BrowserContext): Promise<Page> {
  const page = await context.newPage();
  await page.goto("/");
  const search = page.getByRole("button", { name: /^Hledat ve wiki/ });
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  await expect(search.or(developer)).toBeVisible();
  if (await developer.isVisible()) await developer.click();
  await expect(search).toBeVisible();
  return page;
}
async function send<T = { id: string }>(request: APIRequestContext, path: string, data: unknown): Promise<T> {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request.post(path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `POST ${path}: ${await response.text()}`).toBeLessThan(300);
  return await response.json() as T;
}
const stamp = () => Date.now().toString(36);
const newSpace = (request: APIRequestContext, name: string) => send<{ id: string; name: string }>(request, "/api/spaces", { name: `Obsah-e2e ${name} ${stamp()}`, icon: "code-xml", color: "blue" });
const newBoard = (request: APIRequestContext, spaceId: string, title: string) => send(request, `/api/spaces/${spaceId}/boards`, { title: `Obsah-e2e ${title}` });
const newPage = (request: APIRequestContext, spaceId: string, title: string) =>
  send(request, "/api/pages", { spaceId, parentId: null, title, markdown: `# ${title}\n\nText.`, contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Text." }] }] } }) });
const newContact = (request: APIRequestContext, name: string) => send(request, "/api/objects", { typeId: "atlas.contact", scope: "wiki", data: { name, emails: [`${stamp()}@obsah-e2e.example`] } });
const newReference = (request: APIRequestContext, spaceId: string, targetId: string, parentId?: string) => send(request, `/api/spaces/${spaceId}/tree/references`, { targetId, ...(parentId && { parentId }) });

/** The shell reads its spaces once at sign-in: an address of what the test made after it needs a fresh load. */
async function open(page: Page, hash: string) { await page.goto(`/${hash}`); await page.reload(); }
const treeRow = (page: Page, id: string) => page.locator(`.tree-row[data-tree-row="${id}"]`);
const boardItems = async (request: APIRequestContext, boardId: string) =>
  ((await (await request.get(`/api/boards/${boardId}`)).json()) as { items: { kind: string; props?: { objectId?: string; typeId?: string } }[] }).items;

test("a contact and a board dragged from the tree onto a board become their live cards, not notes", async ({ browser }) => {
  const context = await newContext(browser);
  const page = await signIn(context);
  const space = await newSpace(context.request, "drop");
  await newPage(context.request, space.id, "Obsah-e2e stránka");
  const board = await newBoard(context.request, space.id, "plátno");
  const other = await newBoard(context.request, space.id, "jiná tabule");
  const contact = await newContact(context.request, "Jana Obsahová");
  const reference = await newReference(context.request, space.id, contact.id);

  await open(page, `#/boards/${board.id}`);
  const surface = page.locator("[data-board-surface]");
  await expect(surface).toBeVisible();
  await expect(treeRow(page, reference.id)).toBeVisible();

  await treeRow(page, reference.id).dragTo(surface, { targetPosition: { x: 300, y: 250 } });
  await expect(page.locator(".vue-flow__node").filter({ hasText: "Jana Obsahová" })).toBeVisible();
  await treeRow(page, other.id).dragTo(surface, { targetPosition: { x: 700, y: 250 } });
  await expect.poll(async () => (await boardItems(context.request, board.id)).map(item => `${item.kind}:${item.props?.typeId}:${item.props?.objectId}`).sort())
    .toEqual([`object:atlas.board:${other.id}`, `object:atlas.contact:${contact.id}`].sort());
  expect((await boardItems(context.request, board.id)).some(item => item.kind === "note")).toBe(false);

  // The other board's card opens that board: a double-click, and Enter on the selected card.
  const boardCard = page.locator(".vue-flow__node").filter({ hasText: "Obsah-e2e jiná tabule" });
  await boardCard.dblclick();
  await expect(page).toHaveURL(new RegExp(`#/boards/${other.id}`));
  await page.goBack();
  await expect(page).toHaveURL(new RegExp(`#/boards/${board.id}`));
  await boardCard.click();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(new RegExp(`#/boards/${other.id}`));
  await context.close();
});

test("a reference to another space's board opens the board in that board's space", async ({ browser }) => {
  const context = await newContext(browser);
  const page = await signIn(context);
  const here = await newSpace(context.request, "zde");
  await newPage(context.request, here.id, "Obsah-e2e zde");
  const there = await newSpace(context.request, "tam");
  await newPage(context.request, there.id, "Obsah-e2e tam");
  const board = await newBoard(context.request, there.id, "cizí tabule");
  const reference = await newReference(context.request, here.id, board.id);

  await open(page, `#/spaces/${here.id}`);
  await expect(treeRow(page, reference.id)).toBeVisible();
  const crumbs = page.getByRole("navigation", { name: "Drobečková navigace" });
  await expect(crumbs.getByRole("link", { name: here.name })).toBeVisible();
  // While the board loads, the reference does not name its space: the current one stays (it was dropped until the load said more).
  let release!: () => void;
  const held = new Promise<void>(done => { release = done; });
  await page.route(`**/api/boards/${board.id}`, async route => { await held; await route.continue(); });
  await treeRow(page, reference.id).click();
  await expect(page).toHaveURL(new RegExp(`#/boards/${board.id}`));
  await expect(crumbs.getByRole("link", { name: here.name })).toBeVisible();
  release();
  await expect(page.locator("[data-board-surface]")).toBeVisible();
  // Loaded, the frame and the tree turn to the board's own space and mark its row as the current one.
  await expect(crumbs.getByRole("link", { name: there.name })).toBeVisible();
  await expect(page.locator(`.tree-row.active[data-tree-row="${board.id}"]`)).toBeVisible();
  await context.close();
});

test("Obsah loads the object dock and the relations graph only when they are first shown", async ({ browser }) => {
  const context = await newContext(browser);
  const page = await signIn(context);
  const space = await newSpace(context.request, "líně");
  await newPage(context.request, space.id, "Obsah-e2e první");
  await newPage(context.request, space.id, "Obsah-e2e druhá");
  const requested: string[] = [];
  page.on("request", request => requested.push(request.url()));
  const loaded = (name: string) => requested.some(url => new URL(url).pathname.split("/").pop()!.startsWith(name));

  await open(page, `#/spaces/${space.id}`);
  const row = page.locator(".content-row").filter({ hasText: "Obsah-e2e druhá" });
  await expect(row).toBeVisible();
  expect(loaded("ContentDetail"), "the dock's code before any dock is open").toBe(false);
  expect(loaded("RelationsGraph"), "the graph's code before any graph is shown").toBe(false);

  // A row's detail button, not its title (the title opens the page).
  await row.hover();
  await row.locator("[data-content-detail]").click();
  await expect(page.getByRole("complementary", { name: "Detail objektu" }).getByText("Obsah-e2e druhá").first()).toBeVisible();
  expect(loaded("ContentDetail")).toBe(true);
  await context.close();
});

test("Obsah's tab counts what its tiles count, and the graph draws a contact as a person with its line leaving to the side", async ({ browser }) => {
  const context = await newContext(browser);
  const page = await signIn(context);
  const space = await newSpace(context.request, "graf");
  await newPage(context.request, space.id, "Obsah-e2e stránka");
  const board = await newBoard(context.request, space.id, "plátno");
  const contact = await newContact(context.request, "Eva Grafová");
  const reference = await newReference(context.request, space.id, contact.id);
  await open(page, `#/boards/${board.id}`);
  await treeRow(page, reference.id).dragTo(page.locator("[data-board-surface]"), { targetPosition: { x: 300, y: 250 } });
  await expect(page.locator(".vue-flow__node").filter({ hasText: "Eva Grafová" })).toBeVisible();

  await open(page, `#/spaces/${space.id}`);
  // A page, a board and a contact: the tree alone knew two of them.
  await expect(page.locator(`[data-space-tab="pages"] .space-tab-count`)).toHaveText("3");
  await page.locator(`[data-content-tile="type:atlas.contact"]`).click();
  await expect(page.locator(".content-row")).toHaveCount(1);
  await page.getByRole("button", { name: "Vazby", exact: true }).click();
  const graph = page.locator("[data-relations-graph]");
  await expect(graph.locator(`.vue-flow__node[data-id="${contact.id}"] .bcard-icon svg`)).toHaveAttribute("data-icon", "user");
  // „je na tabuli“ leaves the contact's right side level, not from its bottom in a loop (read-only nodes had no sides to attach to).
  const path = graph.locator(`[data-board-item="edge:placement:${contact.id}>${board.id}"] path:not([d$="z"])`).first();
  await expect.poll(async () => {
    const [x0, y0, x1, y1] = ((await path.getAttribute("d")) ?? "").match(/-?[\d.]+/g)!.map(Number);
    return x1! > x0! && Math.abs(y1! - y0!) < 1;
  }).toBe(true);
  await context.close();
});

test("a plain wheel over the relations graph in Obsah scrolls the page and leaves the graph where it is", async ({ browser }) => {
  const context = await newContext(browser);
  const page = await signIn(context);
  const space = await newSpace(context.request, "kolečko");
  const host = await newPage(context.request, space.id, "Obsah-e2e kolečko");
  const contact = await newContact(context.request, "Karel Kolečko");
  // Under the page, so the graph has an edge to draw (a reference at the root counts only as used).
  await newReference(context.request, space.id, contact.id, host.id);

  await open(page, `#/spaces/${space.id}`);
  // The graph's subject is the first row: the contacts alone make it the contact.
  await page.locator(`[data-content-tile="type:atlas.contact"]`).click();
  await expect(page.locator(".content-row")).toHaveCount(1);
  await expect(page.locator(".content-row").filter({ hasText: "Karel Kolečko" })).toBeVisible();
  await page.getByRole("button", { name: "Vazby", exact: true }).click();
  const graph = page.locator("[data-relations-graph]");
  await expect(graph.locator(".vue-flow__node")).toHaveCount(2);
  const pane = graph.locator(".vue-flow__transformationpane");
  await expect.poll(() => pane.evaluate(el => getComputedStyle(el).transform)).not.toBe("matrix(1, 0, 0, 1, 0, 0)");
  const view = await pane.evaluate(el => getComputedStyle(el).transform);
  const top = (await graph.boundingBox())!.y;

  await graph.hover();
  await page.mouse.wheel(0, 300);
  await expect.poll(async () => (await graph.boundingBox())!.y).toBeLessThan(top - 100);
  expect(await pane.evaluate(el => getComputedStyle(el).transform)).toBe(view);
  await context.close();
});
