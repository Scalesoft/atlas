import { randomUUID } from "node:crypto";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// Review cycle 2, collaboration (COL2): two or three people on one board. Every screen and the server end with the same board
// after concurrent and offline moves; a colleague's move glides on the other screens instead of holding and jumping (COL2-2);
// a colleague's drag preview does not stall the canvas of a big board (COL2-1). Runs against a local Development instance with
// the module Boards and the developer sign-in (each context signs in on its own), in the space „COL2 spolupráce“.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });

const SPACE = "COL2 spolupráce";
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
async function send(request: APIRequestContext, method: "post" | "delete", path: string, data?: unknown) {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBeLessThan(300);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}
async function space(request: APIRequestContext): Promise<string> {
  const spaces = await (await request.get("/api/spaces")).json() as { id: string; name: string }[];
  return spaces.find(item => item.name === SPACE)?.id ?? (await send(request, "post", "/api/spaces", { name: SPACE, icon: "code-xml", color: "green" })).id;
}
const note = (id: string, x: number, y: number, z: number, text: string) =>
  ({ type: "create", item: { id, kind: "note", x, y, width: 160, height: 160, z, text, color: "yellow", props: { fontSize: "auto" } } });
/** A board of `count` notes in rows of six, 200 apart; returns the board and the note ids in order. */
async function seedNotes(request: APIRequestContext, title: string, count: number) {
  const board = await send(request, "post", `/api/spaces/${await space(request)}/boards`, { title: `COL2 ${title} ${Date.now().toString(36)}` });
  const ids = Array.from({ length: count }, () => randomUUID());
  const changes = ids.map((id, i) => note(id, (i % 6) * 200, Math.floor(i / 6) * 200, i + 1, `Poznámka ${i + 1}`));
  for (let from = 0; from < changes.length; from += 180) await send(request, "post", `/api/boards/${board.id}/operations`, { operationId: randomUUID(), changes: changes.slice(from, from + 180) });
  return { board: board.id as string, ids };
}
async function openBoard(page: Page, boardId: string) {
  await page.goto(`/#/boards/${boardId}`);
  await expect(page.locator("[data-board-surface]")).toBeVisible();
  await expect(page.locator(".vue-flow__node").first()).toBeVisible();
  // The canvas fits the board after its first layout.
  await page.waitForTimeout(800);
}
const node = (page: Page, id: string) => page.locator(`.vue-flow__node[data-id="${id}"]`);
/** Every node's board position as the screen draws it (Vue Flow's translate of the wrapper, in board pixels). */
const shown = (page: Page) => page.evaluate(() => Object.fromEntries([...document.querySelectorAll<HTMLElement>(".vue-flow__node[data-id]")].map(el => {
  const m = /translate\(([-\d.]+)px, ?([-\d.]+)px\)/.exec(el.style.transform);
  return [el.dataset.id!, m ? [Math.round(Number(m[1])), Math.round(Number(m[2]))] : null];
})));
async function server(request: APIRequestContext, boardId: string) {
  const board = await (await request.get(`/api/boards/${boardId}`)).json() as { items: { id: string; x: number; y: number }[] };
  return Object.fromEntries(board.items.map(item => [item.id, [Math.round(item.x), Math.round(item.y)]]));
}
/** Drags a node by its upper edge by (dx, dy) screen pixels in small steps, as a hand does. */
async function drag(page: Page, id: string, dx: number, dy: number) {
  const box = (await node(page, id).boundingBox())!;
  const x = box.x + box.width / 2, y = box.y + 8;
  await page.mouse.move(x, y);
  await page.mouse.down();
  for (let step = 1; step <= 12; step++) { await page.mouse.move(x + (dx * step) / 12, y + (dy * step) / 12); await page.waitForTimeout(16); }
  await page.mouse.up();
}
/** rAF sampler: frame times, and every frame the screen box of the given node (a glide shows as several boxes in between). */
const startSampling = (page: Page, id: string) => page.evaluate(id => {
  const w = window as unknown as { __frames: number[]; __boxes: { at: number; x: number }[] };
  w.__frames = []; w.__boxes = [];
  let last = performance.now();
  const tick = (t: number) => {
    w.__frames.push(t - last); last = t;
    const el = document.querySelector(`.vue-flow__node[data-id="${id}"]`);
    if (el) { const x = Math.round(el.getBoundingClientRect().x); if (w.__boxes.at(-1)?.x !== x) w.__boxes.push({ at: t, x }); }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}, id);
const sampled = (page: Page) => page.evaluate(() => { const w = window as unknown as { __frames: number[]; __boxes: { at: number; x: number }[] }; return { frames: w.__frames.slice(1), boxes: w.__boxes }; });

test("COL2-B1 concurrent moves of different and of the same card, and a move made offline, end identical on both screens and the server", async ({ browser }) => {
  test.setTimeout(120_000);
  const [ca, cb] = [await newContext(browser), await newContext(browser)];
  try {
    const [a, b] = [await signIn(ca), await signIn(cb)];
    const seeded = await seedNotes(ca.request, "B1", 6);
    await openBoard(a, seeded.board); await openBoard(b, seeded.board);
    const [n1, n2, n3, n4] = seeded.ids;
    // Different cards at the same time, then the same card at the same time: the server's order decides, both screens follow it.
    await Promise.all([drag(a, n1, 60, 40), drag(b, n2, -40, 60)]);
    await Promise.all([drag(a, n3, 80, 0), drag(b, n3, 0, 80)]);
    // B goes offline and moves a card; A moves another meanwhile; B comes back and its move is sent.
    await cb.setOffline(true);
    await drag(b, n4, 70, 70);
    await drag(a, n1, -30, -30);
    await b.waitForTimeout(1500);
    await cb.setOffline(false);
    await expect.poll(async () => JSON.stringify((await server(ca.request, seeded.board))[n4]), { timeout: 20_000 }).not.toBe(JSON.stringify([600, 0]));
    const expected = await server(ca.request, seeded.board);
    await expect.poll(() => shown(a), { timeout: 15_000 }).toEqual(expected);
    await expect.poll(() => shown(b), { timeout: 15_000 }).toEqual(expected);
    // None duplicated, none lost.
    expect(Object.keys(expected)).toHaveLength(6);
  } finally { await ca.close(); await cb.close(); }
});

test("COL2-B2 a colleague's move glides to its place on my screen instead of holding and jumping", async ({ browser }) => {
  test.setTimeout(90_000);
  const [ca, cb] = [await newContext(browser), await newContext(browser)];
  try {
    const [a, b] = [await signIn(ca), await signIn(cb)];
    const seeded = await seedNotes(ca.request, "B2", 4);
    await openBoard(a, seeded.board); await openBoard(b, seeded.board);
    const id = seeded.ids[0];
    await startSampling(a, id);
    await drag(b, id, 240, 0);
    await expect.poll(async () => (await server(ca.request, seeded.board))[id]?.[0], { timeout: 10_000 }).toBeGreaterThan(100);
    await a.waitForTimeout(1200);
    const { boxes } = await sampled(a);
    // The arrival on A: the first box that moved, then the boxes until it rests. A glide over --atlas-motion-base (200 ms) at 60 Hz
    // draws about a dozen; holding the card in place and then jumping (the glide played from the old place to the old place) draws one.
    expect(boxes.length, `boxes of the remote move on A: ${JSON.stringify(boxes)}`).toBeGreaterThanOrEqual(5);
  } finally { await ca.close(); await cb.close(); }
});

test("COL2-B3 a colleague dragging a card on a big board keeps my canvas fluid and shows the drop within a second", async ({ browser }) => {
  test.setTimeout(150_000);
  const [ca, cb] = [await newContext(browser), await newContext(browser)];
  try {
    const [a, b] = [await signIn(ca), await signIn(cb)];
    const seeded = await seedNotes(ca.request, "B3", 300);
    await openBoard(a, seeded.board); await openBoard(b, seeded.board);
    const id = seeded.ids[0];
    await a.waitForTimeout(1500);
    await startSampling(a, id);
    const before = (await server(ca.request, seeded.board))[id];
    await drag(b, id, 120, 0);
    // From the drop itself: B's own drag on a big board (headless, software rendering) took 0.5–1.7 s before it.
    const dropped = Date.now();
    // B's drop is on A within a second: each drag preview from B must not cost A a long frame.
    await expect.poll(async () => (await shown(a))[id]?.[0], { timeout: 10_000 }).not.toBe(before[0]);
    const arrival = Date.now() - dropped;
    await a.waitForTimeout(500);
    const { frames } = await sampled(a);
    const long = frames.filter(frame => frame > 100);
    expect(long.length, `frames over 100 ms on A while B dragged: ${long.map(f => f.toFixed(0)).join(", ")}`).toBeLessThanOrEqual(2);
    expect(arrival, "ms from B's drop until A shows the card at its place").toBeLessThan(1500);
  } finally { await ca.close(); await cb.close(); }
});
