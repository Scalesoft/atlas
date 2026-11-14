import { randomUUID } from "node:crypto";
import { startEditing, PAGE_TEXT, LIVE_EDITOR } from "./editing";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Locator, type Page } from "@playwright/test";

// COL2-9 (roadmap 3.0-D „jeden ve wiki a druhý v tabuli“): a page edited in the wiki and from its card on a board at the same time is
// one document. Both editors see each other's typing, end identical, and the draft saved from the board holds both; a board note two
// people edit at once keeps both texts. Runs against a local Development instance with the developer sign-in and the module Boards
// (each context signs in on its own), in the space „COL2 spolupráce“.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });

const SPACE = "COL2 spolupráce";
const WIKI_EDITOR = LIVE_EDITOR;
const BOARD_EDITOR = "[data-board-page-editor] .tiptap";
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
async function send(request: APIRequestContext, method: "post" | "put", path: string, data?: unknown) {
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
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
/** A page of two paragraphs and a board with its card (`display`: "card" or "preview"), plus a note. */
async function seed(request: APIRequestContext, display: "card" | "preview") {
  const spaceId = await space(request);
  const stamp = Date.now().toString(36);
  const page = await send(request, "post", "/api/pages", { spaceId, title: `COL2-9 dokument ${stamp}`, parentId: null, markdown: null,
    contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [paragraph("Odstavec wiki."), paragraph("Odstavec tabule.")] } }) }) as { id: string };
  const board = await send(request, "post", `/api/spaces/${spaceId}/boards`, { title: `COL2-9 tabule ${stamp}` }) as { id: string };
  const card = randomUUID(), note = randomUUID();
  await send(request, "post", `/api/boards/${board.id}/operations`, { operationId: randomUUID(), changes: [
    { type: "create", item: { id: card, kind: "page", x: 0, y: 0, width: 320, height: 220, z: 1, targetResourceId: page.id, props: { display } } },
    { type: "create", item: { id: note, kind: "note", x: 400, y: 0, width: 220, height: 180, z: 2, text: "Nákup:", color: "yellow", props: { fontSize: "auto" } } },
  ] });
  return { page: page.id, board: board.id, card, note };
}
async function openBoard(page: Page, boardId: string) {
  await page.goto(`/#/boards/${boardId}`);
  await expect(page.locator("[data-board-surface]")).toBeVisible();
  await expect(page.locator(".vue-flow__node").first()).toBeVisible();
  await page.waitForTimeout(800);
}
async function editInWiki(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await startEditing(page);
}
/**
 * The page edited from the board, editable once its live session is joined (3.6: no „Upravit“). A card: a double-click opens the page's
 * editor over the board. A preview: the first click into its text selects the card, the second enters the text in place, there.
 */
async function editOnBoard(page: Page, cardId: string, display: "card" | "preview") {
  const card = page.locator(`.vue-flow__node[data-id="${cardId}"]`);
  if (display === "card") await card.dblclick();
  else {
    const text = card.locator(".page-preview-content p", { hasText: "Odstavec tabule." });
    await text.click();
    await text.click();
    await expect(card.locator(".board-page-editor:not(.is-entering)")).toBeVisible({ timeout: 20_000 });
  }
  await expect(page.locator(`${BOARD_EDITOR}[contenteditable="true"]`)).toBeVisible({ timeout: 20_000 });
  if (display === "card") await expect(page.locator("[data-board-page-editor-state]")).toHaveText("Změny sdílíte živě", { timeout: 20_000 });
}
/** The caret at the end of the paragraph holding `text`; waits until the editor really has the focus (tiptap focuses in a rAF). */
async function caretAfter(page: Page, editor: string, text: string) {
  await page.locator(editor).locator("p", { hasText: text }).first().click();
  await page.keyboard.press("End");
  await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), editor);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
const type = (page: Page, text: string) => page.keyboard.type(text, { delay: 60 });
const textOf = (page: Page, editor: string) => page.evaluate(selector => {
  const view = (document.querySelector(selector) as unknown as { editor?: { state: { doc: { textBetween(a: number, b: number, s: string): string; content: { size: number } } } } }).editor!;
  return view.state.doc.textBetween(0, view.state.doc.content.size, "\n");
}, editor);
const occurrences = (text: string, word: string) => text.split(word).length - 1;

for (const display of ["card", "preview"] as const) {
  test(`COL2-9 the wiki and the board (${display === "card" ? "page card" : "live preview"}) edit one document at once and converge; the board's draft holds both`, async ({ browser }) => {
    test.setTimeout(150_000);
    const [cw, cb] = [await newContext(browser), await newContext(browser)];
    try {
      const [wiki, board] = [await signIn(cw), await signIn(cb)];
      const seeded = await seed(cw.request, display);
      await editInWiki(wiki, seeded.page);
      await openBoard(board, seeded.board);
      await editOnBoard(board, seeded.card, display);

      // Both type at once, each in its own paragraph, a word at a time.
      await caretAfter(wiki, WIKI_EDITOR, "Odstavec wiki.");
      await caretAfter(board, BOARD_EDITOR, "Odstavec tabule.");
      const wikiWords = ["alfa", "bravo", "charlie", "delta"].map(word => ` W-${word}`);
      const boardWords = ["echo", "foxtrot", "golf", "hotel"].map(word => ` T-${word}`);
      await Promise.all([
        (async () => { for (const word of wikiWords) await type(wiki, word); })(),
        (async () => { for (const word of boardWords) await type(board, word); })(),
      ]);
      // A change of focus inside the board (the panel's own controls) keeps the one session: the board goes on typing into it. In place,
      // a press outside ends the editing, so the preview only goes on typing.
      if (display === "card") await board.locator("[data-board-page-editor-state]").click();
      await caretAfter(board, BOARD_EDITOR, "T-hotel");
      await type(board, " T-india");

      // Every word on both screens, once, in the same document.
      for (const word of [...wikiWords, ...boardWords, " T-india"]) {
        await expect.poll(() => textOf(wiki, WIKI_EDITOR), { timeout: 15_000 }).toContain(word.trim());
        await expect.poll(() => textOf(board, BOARD_EDITOR), { timeout: 15_000 }).toContain(word.trim());
      }
      await expect.poll(async () => (await textOf(wiki, WIKI_EDITOR)) === (await textOf(board, BOARD_EDITOR)), { timeout: 15_000 }).toBe(true);
      const shared = await textOf(board, BOARD_EDITOR);
      for (const word of [...wikiWords, ...boardWords]) expect(occurrences(shared, word.trim()), word).toBe(1);

      // The board's editor saves by itself (3.6-B): the stored working revision is that document, with the wiki's words too. The
      // panel says so; in place the preview follows the page.
      if (display === "card") await expect(board.locator("[data-board-page-editor-save]")).toHaveAttribute("data-save-state", "saved", { timeout: 20_000 });
      const storedText = async () => ((await (await cb.request.get(`/api/pages/${seeded.page}`)).json()) as { contentJson: string }).contentJson;
      for (const word of [...wikiWords, ...boardWords, " T-india"]) await expect.poll(storedText, { timeout: 20_000 }).toContain(word.trim());

      // Closed, the board's card follows the page; the wiki keeps editing the same document.
      await board.keyboard.press("Escape");
      await expect(board.locator("[data-board-page-editor]")).toHaveCount(0);
      await caretAfter(wiki, WIKI_EDITOR, "W-delta");
      await type(wiki, " W-echo");
      await expect.poll(() => textOf(wiki, WIKI_EDITOR), { timeout: 10_000 }).toContain("W-echo");
      // The wiki's tab closes right after its last keystrokes: the server stores the tail itself (3.6; campaign 2026-11-05 F3).
      await cw.close();
      await expect.poll(storedText, { timeout: 15_000 }).toContain("W-echo");
    } finally { await cw.close(); await cb.close(); }
  });
}

test("COL2-9 a note two people edit at once keeps both texts on both screens and the server", async ({ browser }) => {
  test.setTimeout(120_000);
  const [ca, cb] = [await newContext(browser), await newContext(browser)];
  try {
    const [a, b] = [await signIn(ca), await signIn(cb)];
    const seeded = await seed(ca.request, "card");
    await openBoard(a, seeded.board); await openBoard(b, seeded.board);
    const noteOf = (page: Page): Locator => page.locator(`.vue-flow__node[data-id="${seeded.note}"]`);
    // Both start editing the same text; A appends and pauses (its text is saved), then B, still editing, appends at the same place.
    for (const page of [a, b]) {
      await noteOf(page).dblclick();
      await expect(noteOf(page).locator(".tiptap[contenteditable='true']")).toBeVisible();
      await page.keyboard.press("End");
    }
    await type(a, " mléko");
    await expect.poll(async () => ((await (await ca.request.get(`/api/boards/${seeded.board}`)).json()) as { items: { id: string; text?: string }[] })
      .items.find(item => item.id === seeded.note)?.text, { timeout: 10_000 }).toContain("mléko");
    await b.waitForTimeout(500);
    await type(b, " chléb");
    // B's save after its pause finds A's text: both versions stay, and B is told so while it goes on editing.
    await expect(noteOf(b).locator("[data-board-text-conflict]")).toBeVisible({ timeout: 10_000 });
    for (const page of [a, b]) {
      await page.keyboard.press("Escape");
      // A conflict found only by this Esc keeps the editing open once more.
      if (await noteOf(page).locator(".tiptap[contenteditable='true']").isVisible()) await page.keyboard.press("Escape");
    }
    const stored = async () => ((await (await ca.request.get(`/api/boards/${seeded.board}`)).json()) as { items: { id: string; text?: string }[] })
      .items.find(item => item.id === seeded.note)?.text ?? "";
    await expect.poll(stored, { timeout: 10_000 }).toContain("mléko");
    expect(await stored()).toContain("chléb");
    for (const page of [a, b]) await expect(noteOf(page)).toContainText("mléko", { timeout: 10_000 });
    for (const page of [a, b]) await expect(noteOf(page)).toContainText("chléb");
  } finally { await ca.close(); await cb.close(); }
});
