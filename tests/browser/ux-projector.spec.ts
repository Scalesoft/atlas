import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { block, editorOf, openPage, publish, requirementNode, richDocument, send, summaryNode } from "./projector-helpers";

// UX cycle 1, area Projector: one test per finding, keyboard only.
// Runs against a local instance with „Přihlásit jako vývojář“ and the projector module on; its pages go into the space „UX projector“.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(process.env.ATLAS_UX_DEV_LOGIN !== "1", "Requires a local instance with developer sign-in and Modules__Projector__Enabled=true (ATLAS_UX_DEV_LOGIN=1).");

async function signIn(context: BrowserContext): Promise<Page> {
  const page = await context.newPage();
  page.on("dialog", dialog => dialog.accept());
  await page.goto("/");
  const developer = page.getByRole("button", { name: /Přihlásit jako vývojář/ });
  const signedIn = page.getByRole("button", { name: /^Hledat ve wiki/ });
  await expect(developer.or(signedIn).first()).toBeVisible();
  if (await developer.isVisible()) await developer.click();
  await expect(signedIn).toBeVisible();
  return page;
}
async function uxSpace(context: BrowserContext): Promise<string> {
  const answer = await (await context.request.get("/api/spaces")).json() as { id: string; name: string }[] | { items: { id: string; name: string }[] };
  const found = (Array.isArray(answer) ? answer : answer.items).find(space => space.name === "UX projector");
  if (found) return found.id;
  return (await (await send(context, "post", "/api/spaces", { name: "UX projector", icon: "code-xml", color: "blue" })).json()).id;
}
async function uxPage(context: BrowserContext, title: string, ...blocks: unknown[]): Promise<string> {
  const response = await send(context, "post", "/api/pages", { spaceId: await uxSpace(context), parentId: null, title: `UX-projector ${title} ${Date.now().toString(36)}`,
    contentJson: richDocument(...blocks), markdown: title });
  expect(response.status(), await response.text()).toBeLessThan(300);
  const id = (await response.json()).id;
  created.push(id);
  return id;
}
const created: string[] = [];
const createdPages = created; // for a test that creates its pages by hand instead of through uxPage
/** Moves the pages a test made to the trash, as the brief asks of test data. */
async function trashCreated(context: BrowserContext) {
  for (const id of created.splice(0)) {
    const current = await (await context.request.get(`/api/pages/${id}`)).json();
    const tree = await (await context.request.get(`/api/spaces/${current.spaceId}/tree`)).json();
    await send(context, "post", `/api/pages/${id}/trash`, { expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false });
  }
}
/** Opens the page and enters its text with Enter on the focused page (3.6), keyboard only. */
async function editByKeyboard(page: Page, id: string) {
  await openPage(page, id);
  await page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
  await page.keyboard.press("Enter");
  await expect(editorOf(page)).toBeVisible();
  await expect(editorOf(page)).toBeFocused();
}
/** Ctrl+End until the caret stays at the end of `last` (joining the collaboration puts it back at the start once). */
async function caretAtEnd(page: Page, last: string) {
  await expect(async () => {
    await page.keyboard.press("Control+End");
    await page.waitForTimeout(250);
    expect(await caretText(page)).toBe(last);
  }).toPass();
}
/** The text of the block holding the caret (its title, for a requirement block), or of the top-level block. */
const caretText = (page: Page) => page.evaluate(() => {
  const anchor = window.getSelection()?.anchorNode ?? null;
  return (anchor instanceof Element ? anchor : anchor?.parentElement)?.closest(".tiptap p, .tiptap h1, .tiptap h2, .tiptap h3, .tiptap li")?.textContent ?? "";
});

let context: BrowserContext;
let page: Page;
test.beforeEach(async ({ browser }) => {
  context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  page = await signIn(context);
});
test.afterEach(async () => {
  await page.close(); // leaves the editing session before its page goes to the trash
  await trashCreated(context);
  await context.close();
});

test("PR-1: po výběru kontaktu z /kontakt se dál píše pod kartou", async () => {
  const id = await uxPage(context, "Kontakt", { type: "paragraph", content: [{ type: "text", text: "Úvod" }] });
  await editByKeyboard(page, id);
  await caretAtEnd(page, "Úvod");
  await page.keyboard.press("Enter");
  await page.keyboard.type("/kontakt jan kovarik");
  await expect(page.getByTestId("slash-menu").getByRole("option").first()).toContainText("jan kovarik");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("option").first()).toContainText("Honza Kovařík");
  await page.keyboard.press("Enter");
  await expect(page.locator(".document-body .md-contact-card").first()).toBeVisible();
  await page.keyboard.type("Další řádek");
  await expect.poll(() => caretText(page)).toBe("Další řádek");
  // Úvod, the card, then the new line, in this order.
  expect(await editorOf(page).evaluate(root => [...root.children].map(child => child.querySelector(".md-contact-card") ? "card" : child.textContent).filter(Boolean)))
    .toEqual(["Úvod", "card", "Další řádek"]);
  expect(page.url()).toContain(`/pages/${id}`);
});

test("PR-5: „Čekáme na“ přenese fokus do požadavku", async () => {
  const id = await uxPage(context, "Odkazy", summaryNode(),
    requirementNode({ title: "VPN tunel", key: "vpn", direction: "inbound", state: "waiting_for_input", party: "IT KBN" }),
    requirementNode({ title: "Portál: aplikace", key: "portal", requires: ["vpn"] }), { type: "paragraph" });
  // Read: „Čekáme na“ → VPN tunel; the heading takes the focus.
  await openPage(page, id);
  await page.locator("[data-requirement-summary-view]").getByRole("button", { name: "VPN tunel", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest("[data-heading-id]")?.textContent ?? "")).toMatch(/^\s*VPN tunel/);
});

test("PR-10: G, pak C přistane v hledání kontaktů; ↓ na první kontakt", async () => {
  await page.locator("body").press("g");
  await page.keyboard.press("c");
  const search = page.locator("input[data-contact-search]");
  await expect(search).toBeFocused();
  await page.keyboard.type("novák");
  await expect(page.locator("[data-contact-row]").first()).toContainText("Novák");
  await page.keyboard.press("ArrowDown");
  await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest("[data-contact-row]"))).toBe(true);
});

test("A2 (ověření): Esc z katalogu a z výběru stavu vrátí kurzor nebo fokus do bloku", async () => {
  const id = await uxPage(context, "Esc", requirementNode({ title: "VPN tunel", key: "vpn" }), { type: "paragraph", content: [{ type: "text", text: "Konec" }] });
  await editByKeyboard(page, id);
  await caretAtEnd(page, "Konec");
  await page.keyboard.press("Enter");
  await page.keyboard.type("/Moduly");
  await expect(page.getByTestId("slash-menu").getByRole("option").first()).toContainText("Moduly produktu");
  await page.keyboard.press("Enter");
  await expect(page.locator("[data-catalog-query]")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(editorOf(page)).toBeFocused();
  await expect.poll(() => caretText(page)).toBe(""); // the empty line the slash was typed on
  const chip = block(page, "VPN tunel").locator('[data-rq-chip="state"]');
  await chip.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("[data-state-option]").first()).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(chip).toBeFocused();
});

test("PR-4: nápověda kláves má oddíl Požadavky", async () => {
  await page.locator("main h1").first().focus();
  await page.keyboard.press("?");
  const help = page.getByRole("dialog", { name: "Klávesové zkratky" });
  await expect(help).toBeVisible();
  const section = help.locator("section", { has: page.getByRole("heading", { name: "Požadavky", exact: true }) });
  await expect(section).toContainText("Nový požadavek");
  await expect(section).toContainText("Na čipu stavu");
  await page.keyboard.press("Escape");
  await expect(help).toBeHidden();
});

// --- UX cycle 2 --------------------------------------------------

test("PR2-4: názvy nasazení v přehledu se na 390 px zalomí, neořežou do stejného začátku", async () => {
  const space = await uxSpace(context);
  const stamp = Date.now().toString(36);
  for (const name of ["Alfa", "Beta"]) {
    const response = await send(context, "post", "/api/pages", { spaceId: space, parentId: null, title: `UX-projector Deployment in nemocnice ${name} ${stamp}`,
      contentJson: richDocument(requirementNode({ title: "VPN tunel", key: "vpn" })), markdown: name });
    expect(response.status(), await response.text()).toBeLessThan(300);
    const created = (await response.json()).id as string;
    createdPages.push(created);
    await publish(context, created);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/#/spaces/${space}/deployments`);
  const links = page.locator(".overview-page").filter({ hasText: stamp });
  await expect(links).toHaveCount(2, { timeout: 20_000 });
  for (const link of await links.all()) {
    const fits = await link.evaluate(element => ({ clipped: element.scrollWidth > element.clientWidth + 1, lines: Math.round(element.getBoundingClientRect().height / parseFloat(getComputedStyle(element).lineHeight)) }));
    expect(fits.clipped).toBe(false);
    expect(fits.lines).toBeGreaterThan(1);
  }
});
