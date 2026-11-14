import { expectSaved, startEditing as editInPlace, PAGE_TEXT, LIVE_EDITOR } from "./editing";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// Review cycle 2, collaboration (COL2): three writers on one page. Concurrent typing in the same paragraph, in another one and in a
// table cell, each writer's own Undo, one writer offline and back: every editor ends with the same document, nothing typed and
// confirmed is lost or doubled, and the saved draft is that document. Runs against a local Development instance with the
// developer sign-in (each context signs in on its own), in the space „COL2 spolupráce“.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });

const SPACE = "COL2 spolupráce";
const EDITOR = LIVE_EDITOR;
let developerSignIn = false;
test.beforeAll(async ({ request }) => {
  const methods = await request.get(`${process.env.ATLAS_URL ?? ""}/api/auth/methods`).then(response => response.ok() ? response.json() : {}).catch(() => ({}));
  developerSignIn = !!(methods as { developerSignIn?: boolean }).developerSignIn;
});
test.beforeEach(() => { test.skip(!developerSignIn, "Requires a local Development instance with the developer sign-in."); });

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
  return response.json();
}
async function space(request: APIRequestContext): Promise<string> {
  const spaces = await (await request.get("/api/spaces")).json() as { id: string; name: string }[];
  return spaces.find(item => item.name === SPACE)?.id ?? (await send(request, "post", "/api/spaces", { name: SPACE, icon: "code-xml", color: "green" })).id;
}
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const cell = (text: string) => ({ type: "tableCell", content: [paragraph(text)] });
async function seedPage(request: APIRequestContext) {
  const content = [paragraph("Společný odstavec."), paragraph("Odstavec druhého autora."),
    { type: "table", content: [{ type: "tableRow", content: [cell("Buňka A"), cell("Buňka B")] }, { type: "tableRow", content: [cell("Buňka C"), cell("Buňka D")] }] },
    paragraph("Konec.")];
  return send(request, "post", "/api/pages", { spaceId: await space(request), title: `COL2 souběh ${Date.now().toString(36)}`, parentId: null,
    contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } }), markdown: null }) as Promise<{ id: string; version: number }>;
}
async function startEditing(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await editInPlace(page);
}
/** The caret at the end of the text block holding `text`; waits until the editor really has the focus (tiptap focuses in a rAF). */
async function caretAfter(page: Page, text: string) {
  await page.locator(EDITOR).locator("p", { hasText: text }).first().click();
  await page.keyboard.press("End");
  await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), EDITOR);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
/** Types like a person, a key at a time. */
const type = (page: Page, text: string) => page.keyboard.type(text, { delay: 60 });
const documentOf = (page: Page) => page.evaluate(selector => {
  const editor = (document.querySelector(selector) as unknown as { editor?: { getJSON(): unknown } }).editor;
  return JSON.stringify(editor?.getJSON() ?? null);
}, EDITOR);
const textOf = (page: Page) => page.evaluate(selector => {
  const editor = (document.querySelector(selector) as unknown as { editor?: { state: { doc: { textBetween(a: number, b: number, s: string): string; content: { size: number } } } } }).editor!;
  return editor.state.doc.textBetween(0, editor.state.doc.content.size, "\n");
}, EDITOR);
const occurrences = (text: string, word: string) => text.split(word).length - 1;

test("COL2-D1 three writers type in the same paragraph, another one and a table cell, undo their own, one goes offline: all end identical, nothing lost or doubled", async ({ browser }) => {
  test.setTimeout(180_000);
  const contexts = [await newContext(browser), await newContext(browser), await newContext(browser)];
  try {
    const [a, b, c] = await Promise.all(contexts.map(signIn));
    const page = await seedPage(contexts[0].request);
    for (const p of [a, b, c]) await startEditing(p, page.id);
    // Same paragraph (A and B), a table cell (C), at the same time.
    await Promise.all([caretAfter(a, "Společný odstavec."), caretAfter(b, "Společný odstavec."), caretAfter(c, "Buňka B")]);
    await Promise.all([type(a, " alfa1 alfa2"), type(b, " beta1 beta2"), type(c, " gama1")]);
    // B undoes its own typing only; A's and C's stay.
    await b.waitForTimeout(800);
    await b.keyboard.press("Control+z");
    // C goes offline: the editor stops taking keys (no unconfirmed offline edits), and comes back.
    await contexts[2].setOffline(true);
    await expect(c.locator(`${PAGE_TEXT}[contenteditable="false"]`)).toBeVisible({ timeout: 20_000 });
    await caretAfter(a, "Odstavec druhého autora.");
    await type(a, " delta1");
    await contexts[2].setOffline(false);
    await expect(c.locator(`${EDITOR}`)).toBeVisible({ timeout: 30_000 });
    await caretAfter(c, "Buňka D");
    await type(c, " gama2");
    // Everyone has the same document.
    await expect.poll(async () => new Set(await Promise.all([a, b, c].map(documentOf))).size, { timeout: 20_000 }).toBe(1);
    const text = await textOf(a);
    for (const word of ["alfa1", "alfa2", "gama1", "delta1", "gama2"]) expect(occurrences(text, word), `${word} in\n${text}`).toBe(1);
    for (const word of ["beta1", "beta2"]) expect(occurrences(text, word), `${word} (undone by B) in\n${text}`).toBeLessThanOrEqual(1);
    expect(occurrences(text, "beta2"), "B's Undo removed B's last word").toBe(0);
    // The saved draft is the shared document.
    await expectSaved(a);
    const saved = await (await contexts[0].request.get(`/api/pages/${page.id}`)).json() as { contentJson: string };
    type Node = { text?: string; content?: Node[]; type?: string };
    const plain = (node: Node): string => node.text ?? (node.content ?? []).map(plain).join("");
    const savedText = JSON.parse(saved.contentJson).document.content.map(plain).join("\n");
    for (const word of ["alfa1", "alfa2", "gama1", "delta1", "gama2"]) expect(occurrences(savedText, word), `${word} in the saved draft`).toBe(1);
  } finally { for (const context of contexts) await context.close(); }
});
