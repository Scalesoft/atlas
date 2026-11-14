// Shared by the projector-*.spec.ts stories (Projector v1: requirement blocks in pages, spec 2026-10-27). Not a spec itself.
// Every story makes its own space, so catalogs and overviews of one story never see another's pages.
import { expect, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { expectSaved, liveEditor, PAGE_TEXT, startEditing as editInPlace } from "./editing";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";

export const adminPassword = process.env.ATLAS_E2E_PASSWORD;
export const readerPassword = process.env.ATLAS_E2E_READER_PASSWORD ?? `${adminPassword}-reader`;
export const stories = process.env.ATLAS_E2E_ALLOW_COLLAB === "1" && !!adminPassword;
export const skipReason = "Requires an installed isolated acceptance wiki (browser-admin, browser-reader), ATLAS_E2E_PASSWORD, ATLAS_E2E_ALLOW_COLLAB=1 and the projector module (Modules__Projector__Enabled=true).";
export const stamp = () => `${Date.now().toString(36)}`;

// --- session ---------------------------------------------------------------------------------------------------------------

async function enterSecret(field: Locator, secret: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, value) => {
    const input = element as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }, secret);
}

/** A fresh tab of the context signed in as `username` (the first tab of a context signs the whole context in). */
export async function login(context: BrowserContext, username = "browser-admin", password = adminPassword!) {
  const page = await context.newPage();
  page.on("dialog", dialog => dialog.accept());
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await enterSecret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  return page;
}

type Method = "post" | "put" | "delete";
export async function send(context: BrowserContext, method: Method, path: string, data?: unknown) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  return context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
}
async function ok<T>(response: Awaited<ReturnType<typeof send>>, what: string): Promise<T> {
  expect(response.status(), `${what}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text() ? response.json() : undefined) as Promise<T>;
}

export async function createSpace(context: BrowserContext, name: string): Promise<string> {
  return (await ok<{ id: string }>(await send(context, "post", "/api/spaces", { name: `${name} ${stamp()}`, icon: "code-xml", color: "blue" }), "create space")).id;
}

// --- documents -------------------------------------------------------------------------------------------------------------

export interface BlockSeed {
  title: string;
  key?: string;
  direction?: "internal" | "inbound" | "outbound";
  state?: "planned" | "waiting_for_input" | "in_progress" | "done" | "blocked" | "not_applicable";
  date?: string;
  party?: string;
  where?: string;
  requires?: string[];
  /** Notes under the title (plain paragraphs) and nested blocks. */
  notes?: string[];
  children?: BlockSeed[];
}
const uuid = () => globalThis.crypto.randomUUID();
const paragraph = (text?: string) => ({ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) });

export const heading = (text: string, level = 2) => ({ type: "heading", attrs: { level }, content: [{ type: "text", text }] });
export const summaryNode = () => ({ type: "overview", attrs: { id: uuid(), kind: "requirements" } });
/** The page's own id in a page not created yet: the server sets it on create (RequirementHeadings.NewPage). */
const NEW_PAGE = "ffffffff-ffff-ffff-ffff-ffffffffffff";
const day = (iso: string) => { const [year, month, date] = iso.split("-").map(Number); return `${date}. ${month}. ${year}`; };
/**
 * A requirement as the editor writes it (shape 2026-11-04 §1, §6): its heading with state, kind and party, the dependency line (same-page
 * keys), the deadline line „do d. M. yyyy“, „Kde uloženo: …“ and the notes; children as requirement headings one level under. Several
 * nodes: pass them to `createPage` / `richDocument` as they are (they flatten one level).
 */
export const requirementNode = (seed: BlockSeed, level = 2): Record<string, unknown>[] => [
  { type: "heading", attrs: { level, headingId: seed.key || `r-${uuid()}`, state: seed.state ?? "planned", direction: seed.direction ?? "internal",
    ...(seed.party ? { party: seed.party } : {}) }, content: [{ type: "text", text: seed.title }] },
  ...(seed.requires?.length ? [{ type: "paragraph", attrs: { dependencies: true }, content: seed.requires.flatMap((key, at) => [
    ...(at ? [{ type: "text", text: ", " }] : []), { type: "pageReference", attrs: { id: NEW_PAGE, title: key, url: `#/pages/${NEW_PAGE}?heading=${key}` } }]) }] : []),
  ...(seed.date ? [paragraph(`do ${day(seed.date)}`)] : []),
  ...(seed.where ? [paragraph(`Kde uloženo: ${seed.where}`)] : []),
  ...(seed.notes ?? []).map(paragraph),
  ...(seed.children ?? []).flatMap(child => requirementNode(child, Math.min(level + 1, 6))),
];
export const richDocument = (...blocks: unknown[]) => JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 4 }, { id: "heading-anchors", version: 2 }, { id: "pages", version: 1 }],
  document: { type: "doc", content: blocks.flat() } });

export interface PageRef { id: string; version: number; title: string }
export async function createPage(context: BrowserContext, spaceId: string, title: string, ...blocks: unknown[]): Promise<PageRef> {
  const page = await ok<{ id: string; version: number }>(await send(context, "post", "/api/pages", {
    spaceId, parentId: null, title, contentJson: richDocument(...(blocks.length ? blocks : [paragraph()])), markdown: title }), `create page ${title}`);
  return { id: page.id, version: page.version, title };
}
export async function readPage(context: BrowserContext, id: string) {
  return await ok<{ id: string; version: number; publishedVersion: number | null; title: string; contentJson: string; spaceId: string }>(await context.request.get(`/api/pages/${id}`), "read page");
}
export async function publish(context: BrowserContext, id: string) {
  const current = await readPage(context, id);
  await ok(await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version }), "publish");
}
export async function markCatalog(context: BrowserContext, pageId: string, isCatalog = true) {
  await ok(await send(context, "put", `/api/projector/pages/${pageId}/catalog`, { isCatalog }), "mark catalog");
}

// --- server state of requirement blocks (spec §4) --------------------------------------------------------------------------

export interface ServerBlock {
  blockId: string; key: string; title: string; direction: string; state: string; date: string; party: string; where: string;
  requires: string[]; parentBlockId: string | null; position: number; waitingOn: string[]; completed?: string;
}
export interface ServerRequirements {
  pageId: string; version: number; working: boolean; blocks: ServerBlock[];
  summary: { total: number; done: number; percent: number; states: Record<string, number>; waiting: number; inboundMissing: ServerBlock[] };
}
export async function requirements(context: BrowserContext, pageId: string): Promise<ServerRequirements> {
  return await ok<ServerRequirements>(await context.request.get(`/api/projector/pages/${pageId}/requirements`), "requirements");
}
export const byKey = (data: ServerRequirements, key: string) => {
  const block = data.blocks.find(item => item.key === key);
  expect(block, `block with key ${key} (${data.blocks.map(item => item.key).join(", ")})`).toBeTruthy();
  return block!;
};

interface RichNode { type?: string; text?: string; attrs?: Record<string, unknown>; content?: RichNode[] }
/**
 * The requirement headings (a heading with a state) of a page's contentJson in document order: `attrs` are the heading's attributes with
 * `key` = its heading id, `title` its text, `notes` the paragraphs under it that are neither its dependency line nor its facts line („do 15. 11. 2026“; the party is the attribute `party`).
 */
export function blocksOfContent(page: { contentJson: string }) {
  const found: { attrs: Record<string, string>; title: string; notes: string[]; parent: string | null; depth: number }[] = [];
  const text = (node: RichNode): string => node.text ?? (node.content ?? []).map(text).join("");
  const nodes = (JSON.parse(page.contentJson).document.content ?? []) as RichNode[];
  nodes.forEach((node, at) => {
    if (node.type !== "heading" || !node.attrs?.state) return;
    const notes: string[] = [];
    for (const next of nodes.slice(at + 1)) {
      if (next.type === "heading") break;
      if (next.type === "paragraph" && !next.attrs?.dependencies && !/^(do|hotovo) \d/.test(text(next))) notes.push(text(next));
    }
    found.push({ attrs: { ...(node.attrs as Record<string, string>), key: String(node.attrs.headingId ?? node.attrs.id ?? "") }, title: text(node), notes, parent: null, depth: 0 });
  });
  return found;
}
export const hasNode = (page: { contentJson: string }, type: string) => page.contentJson.includes(`"type":"${type}"`);

// --- the page editor -------------------------------------------------------------------------------------------------------

export const editorOf = liveEditor;

/** Opens the page; a writer then edits it in place (editing.ts). */
export async function openPage(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT).first()).toBeVisible();
}
/** Opens the page and clicks into its text: the live editor takes input. */
export async function startEditing(page: Page, id: string) {
  await openPage(page, id);
  await editInPlace(page);
}
/** For a page that may already take input (a page just created opens that way): clicks into its text only when it does not. */
export async function ensureEditing(page: Page) {
  await expect(page.locator(PAGE_TEXT).first()).toBeVisible();
  if (!(await editorOf(page).isVisible())) await editInPlace(page);
  await expect(editorOf(page)).toBeVisible();
}
/** The page saves by itself (3.6-B): what Uložit koncept did is waiting until it says Uloženo. */
export const saveDraft = expectSaved;
/** Publikovat: what Hotovo did. */
export async function finishEditing(page: Page) {
  await page.locator(".document-primary-actions .publish-main").click();
  // The pre-publication check (3.6) asks first when it has findings (an empty section is one): confirmed here, the test is about the publication.
  const checks = page.getByRole("dialog", { name: "Před publikací" });
  // A requirement page may change again while it publishes (the overview recounts), which App.vue says as „Uložená verze byla publikována.“.
  const published = page.getByText(/^(Stránka|Uložená verze) byla publikována\./);
  await expect(published.or(checks)).toBeVisible();
  if (await checks.isVisible()) await checks.getByRole("button", { name: "Publikovat", exact: true }).click();
  await expect(published).toBeVisible();
}

const HEADINGS = "h1, h2, h3, h4, h5, h6";
const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
/** The requirement heading whose title starts with `title` (the checkbox and the chips are widgets around the text, so a prefix match). The last one when several share it. */
export function block(page: Page, title: string): Locator {
  return page.locator(HEADINGS).filter({ has: page.locator(".rq-chips"), hasText: new RegExp(`^\\s*${escapeRegExp(title)}`) }).last();
}
/** The requirement heading with this heading id (its key; an old block's id is in `data-heading-aliases`). */
export const blockById = (page: Page, id: string): Locator => page.locator(`:is(${HEADINGS})[data-heading-id=${JSON.stringify(id)}]`);
/** The caret at the end of the heading's title. A click can be swallowed while the page is still scrolling or a heading is arriving, so it is retried until the selection is there. */
export async function focusTitle(page: Page, item: Locator) {
  await expect(async () => {
    const inside = () => item.evaluate(element => element.contains(window.getSelection()?.anchorNode ?? null));
    await item.click({ position: { x: 4, y: 4 } });
    expect(await inside()).toBe(true);
    // The editor was seen to put the caret back where it was a moment after a click: it must still be here.
    await page.waitForTimeout(300);
    expect(await inside()).toBe(true);
  }).toPass();
  await page.keyboard.press("End");
}
export const allBlocks = (page: Page) => page.locator(`:is(${HEADINGS})[data-heading-id]:has(.rq-chips)`);
export const titlesOnPage = (page: Page) => allBlocks(page).evaluateAll(blocks => blocks.map(item => {
  // The checkbox, the chips and remote carets are widgets inside the heading; their labels are not part of the title.
  const title = item.cloneNode(true) as HTMLElement;
  title.querySelectorAll(".collaboration-carets__caret, .rq-chips, .rq-check").forEach(widget => widget.remove());
  return title.textContent ?? "";
}));
export const summaryOf = (page: Page) => page.locator("[data-requirement-summary-view]").first();
export const percentOf = (page: Page) => summaryOf(page).locator("[data-summary-percent]");
/** The heading's facts line („do 15. 11. 2026“): the paragraph right under it, after the optional dependency line. The party is in `kindOf`. */
export const metaOf = (item: Locator) => item.locator("xpath=following-sibling::*[position() <= 2][self::p and not(@data-dependencies)][1]");
/** The kind chip after the title: the kind (úkol, vstup, předání) with the party („od IT KBN“), the label names both. */
export const kindOf = (item: Locator) => item.locator('[data-rq-chip="kind"]');
/** The heading's dependency line („Závislosti: …“ with page-reference links), when it has one. */
export const depsOf = (item: Locator) => item.locator("xpath=following-sibling::*[1][@data-dependencies]");
export const checkOf = (item: Locator) => item.locator(".rq-check");
/** The state chip after the title; a button in the editor, inert (aria-disabled) when read. */
export const stateOf = (item: Locator) => item.locator('[data-rq-chip="state"]');
/** A part of the summary under its bar, with its count; a click opens its list, one part at a time („Zobrazit vše“ opens every one). */
export const partOf = (page: Page, part: "blocked" | "waiting" | "notAsked" | "missing" | "sections") => summaryOf(page).locator(`[data-summary-part="${part}"]`);
/** The summary's „Nedodané vstupy“ count; clicking it opens the list of those inputs (`missingListOf`). */
export const missingOf = (page: Page) => partOf(page, "missing");
export const missingListOf = (page: Page) => summaryOf(page).locator("[data-summary-missing-list]");
/** The „čeká na N“ chip: there only while the heading waits for something (its dependency line, `depsOf`, names what). */
export const waitingOf = (item: Locator) => item.locator('[data-rq-chip="ready"]');

/** Puts the caret at the end of the page, on a fresh empty paragraph after whatever is last. */
export async function emptyLineAtEnd(page: Page) {
  const editor = editorOf(page);
  await editor.focus();
  await page.waitForTimeout(60); // ProseMirror re-applies its own selection right after the editor gains focus
  await editor.press("Control+End");
  await editor.press("Enter");
}

/** Slash menu → "Požadavek", then the title. Leaves the caret at the end of the title. */
export async function insertRequirement(page: Page, title: string) {
  await page.keyboard.type("/Požadavek");
  const menu = page.getByTestId("slash-menu");
  // Several items may match („z nasazení“), so the plain requirement is picked by name.
  const plain = menu.getByRole("option", { name: /^Požadavek(?! z )/ });
  await expect(plain).toBeVisible();
  await plain.click();
  await expect(menu).toBeHidden();
  await page.keyboard.type(title);
}
/** Slash menu → "Moduly produktu" (the catalog) → the catalog panel. */
export async function openCatalog(page: Page, how: "click" | "enter" = "click") {
  const option = page.getByTestId("slash-menu").getByRole("option", { name: /^Moduly produktu/ });
  // A late editor update (the summary just inserted, the catalog size arriving) can close the menu under the typing: the line is cleared and typed again.
  await expect(async () => {
    await page.keyboard.type("/Moduly");
    try { await expect(option).toBeVisible({ timeout: 3000 }); } catch (error) { await page.keyboard.press("Shift+Home"); await page.keyboard.press("Backspace"); throw error; }
  }).toPass({ timeout: 20_000 });
  if (how === "click") await option.click(); else await page.keyboard.press("Enter");
  await expect(page.locator("[data-catalog-query]")).toBeVisible();
}
/** In the open catalog panel: type, take the first (preselected) match with Enter; it is inserted at once with what it needs. */
export async function pickFromCatalog(page: Page, query: string, expectedTitle: string) {
  const search = page.locator("[data-catalog-query]");
  await search.fill(query);
  await expect(page.getByTestId("requirement-catalog-list").locator("[data-active]")).toContainText(expectedTitle); // the preselected match; a category row of the tree may stand above it
  await search.press("Enter");
}
/** In the open catalog panel: type and tick the first (preselected) match with Space; Enter then inserts what is ticked (J2). */
export async function tickInCatalog(page: Page, query: string, expectedTitle: string) {
  const search = page.locator("[data-catalog-query]");
  await search.fill(query);
  await expect(page.getByTestId("requirement-catalog-list").locator("[data-active]")).toContainText(expectedTitle);
  await search.press(" ");
  await expect(page.getByTestId("requirement-catalog-list").getByRole("option", { selected: true }).first()).toContainText(expectedTitle);
}
export async function insertSummary(page: Page) {
  await page.keyboard.type("/Souhrn");
  await expect(page.getByTestId("slash-menu").getByRole("option", { name: /^Souhrn nasazení/ })).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(summaryOf(page)).toBeVisible();
}
/**
 * A note paragraph inside the block the caret is in the title of (Shift+Enter), and then out of the blocks: Enter on the empty last
 * line makes the next block, Enter on its empty title a paragraph after them (J2).
 */
export async function writeNote(page: Page, note: string, leave = true) {
  await page.keyboard.press("Shift+Enter");
  await page.keyboard.type(note);
  if (leave) for (let step = 0; step < 3; step++) await page.keyboard.press("Enter");
}

export interface PanelFields {
  state?: string;
  party?: string;
}
/** The state on the chip (the party is asked with „čeká na vstup“). Key, kind, date, place and dependencies were the removed block's pencil card: no helper for them. */
export async function setAttributes(page: Page, item: Locator, fields: PanelFields) {
  if (!fields.state) return;
  await pickState(page, item, fields.state, fields.party);
  const anyway = page.locator("[data-requirement-done-hint] button");
  if (fields.state === "done" && await anyway.isVisible()) await anyway.click();
}
/** The state chip's quick pick. "čeká na vstup" asks the party first. */
export async function pickState(page: Page, item: Locator, state: string, party?: string) {
  await stateOf(item).click();
  await page.locator(`[data-state-option="${state}"]`).click();
  if (state === "waiting_for_input") {
    await page.locator("[data-requirement-party-input]").fill(party ?? "");
    await page.getByRole("button", { name: "Čeká na vstup", exact: true }).click();
  }
}
/** Today in Europe/Prague as YYYY-MM-DD, the date a block completed now gets (spec §2.1). */
export const pragueToday = () => new Intl.DateTimeFormat("sv-SE", { timeZone: "Europe/Prague" }).format(new Date());
/** 2026-09-10 → "10. 9. 2026", as the blocks show it. */
export const czechDate = (date: string) => { const [year, month, day] = date.split("-"); return `${Number(day)}. ${Number(month)}. ${year}`; };

/** Gets the caret out of any requirement block onto an empty paragraph after the last block (Enter on an empty line leaves a block). */
export async function outsideBlocks(page: Page) {
  await emptyLineAtEnd(page);
  for (let attempt = 0; attempt < 3; attempt++) {
    const inside = await page.evaluate(() => !!window.getSelection()?.anchorNode?.parentElement?.closest("h1, h2, h3, h4, h5, h6"));
    if (!inside) return;
    await page.keyboard.press("Enter");
  }
}
