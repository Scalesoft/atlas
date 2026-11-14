// Atlas 3.5 package A, as of 3.6 one editor: a page
// is the shared ProseMirror editor, built at once from the page bundle's Yjs state, editable from its first frame (a click puts the caret
// there, a key lands). Test A opens a synthetic page with the structure of the biggest real ones (an overview, a heading every two or three
// requirement blocks, notes and history lines, dependencies, parties as contacts, contact cards, tables) in its own space, in a fresh tab,
// and records the timeline against the page bundle's response (/api/pages/<id>?bundle=true, responseEnd): bundle, first content (the
// first block of the editor in the DOM) and editable (contenteditable=true, no longer hydrating), plus the longest animation frame, the
// total blocking time, the page's DOM nodes and keydown-to-next-paint while typing into a block's title (the typed text must land).
// A page whose live state is not in the bundle yet (the first open by any writer since its last reset) shows the local editor and builds
// the shared one hidden (.editor-hydrating): every test opens the page once as the writer first (warmUp) until no fallback appears, and
// reports whether the first open hit it.
//
// Not part of a suite run: it skips unless ATLAS_PERF_DEPLOYMENT=1. Run it from tests/browser against the build under test and against
// the deployed tag on the same machine (an isolated wiki, never the shared instance; it writes its own space and moves its page to the trash):
//   ATLAS_URL=http://127.0.0.1:5135 ATLAS_PERF_DEPLOYMENT=1 ATLAS_UX_DEV_LOGIN=1 npx playwright test perf-deployment-edit -g "A:"
// Sign-in: ATLAS_UX_DEV_LOGIN=1 (a Development host) or ATLAS_E2E_USER + ATLAS_E2E_PASSWORD. ATLAS_PERF_BLOCKS (130), ATLAS_PERF_RUNS (5),
// ATLAS_PERF_CPU (1; 4 is a typical office laptop against a fast desktop), ATLAS_PERF_REPORT=<file> writes the raw measurements (one file
// per test and page beside it, <file>.A-<page id>.json),
// ATLAS_PERF_KEEP=1 leaves the page out of the trash (its id is in the summary) to look at by hand.
// The budgets are asserted at 1x CPU only (editable - bundle within 300 ms, first content and editable at the same moment, within 50 ms);
// a slowed run reports, and so do the other measurements.
//
// Atlas 3.6 package G (3.6-G): test G opens the page from the loaded app
// (one open timeline: bundle, first content, editable), counts the live editor's DOM per component, and types into a block's title and a
// paragraph in the middle of the page under a Chromium trace: keydown -> next paint and the forced layouts and style recalculations per
// keystroke with the script frames that forced them. ATLAS_PERF_PAGES=<id,id> measures those existing pages (all tests) instead of a
// synthetic one: a copy of real data, never the shared instance (typing writes a draft).
// G also prices a stub against the full component per kind (stubCost). Test L loads the page in a new profile (cold cache) and again
// (warm), then switches to the next page in the app: paints, the three timeline points, requests, JS files and bytes. Test R holds the
// page live in ATLAS_PERF_READERS (1,5) reader tabs while a writer types: writer's key -> reader's text, and what it costs the reader.
// 3.6 perf budgets: L reads the page once per load; V switches the requirements' level locked (read-only) and unlocked, each press to the
// next paint within 100 ms at 1x and 300 ms at 4x CPU.
import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { writeFileSync } from "node:fs";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", viewport: { width: 1440, height: 900 } });
// A run takes up to ~40 s at 1× (test G: three tabs and two traced typings), longer the slower the CPU; one page more is one page longer.
test.describe.configure({ timeout: Math.max(600_000, 120_000 * (Number(process.env.ATLAS_PERF_RUNS ?? 5) + 1) * Number(process.env.ATLAS_PERF_CPU ?? 1)
  * ((process.env.ATLAS_PERF_PAGES ?? "").split(",").filter(Boolean).length || 1)) });
test.skip(process.env.ATLAS_PERF_DEPLOYMENT !== "1", "Perf test of package A: set ATLAS_PERF_DEPLOYMENT=1 (isolated wiki only).");

const blocks = Number(process.env.ATLAS_PERF_BLOCKS ?? 130), runs = Number(process.env.ATLAS_PERF_RUNS ?? 5), cpu = Number(process.env.ATLAS_PERF_CPU ?? 1);
/** Budgets for 130 blocks at 1x CPU on a fast desktop, medians over the runs: editable within 300 ms of the bundle, first content and
 * editable at the same moment (50 ms), no long task over 200 ms (a long animation frame holds its long tasks, so its duration bounds theirs). */
const budget = { editableAfterBundleMs: 300, firstToEditableMs: 50, longestFrameMs: 200 };
/** 3.6-G (owner's outlook, 2 Nov 2026): at most one forced layout per keystroke, a component block of at most 15 elements of its own. */
const liveBudget = { forcedLayoutsPerKey: 1, blockElements: 15 };
/** 3.6 perf (test L): the page read once per load. */
const loadBudget = { pageReads: 1 };
const pageIds = (process.env.ATLAS_PERF_PAGES ?? "").split(",").map(id => id.trim()).filter(Boolean);

async function signIn(page: Page) {
  await page.goto("/");
  if (process.env.ATLAS_UX_DEV_LOGIN === "1") await page.getByRole("button", { name: "Přihlásit jako vývojář" }).click();
  else {
    await page.getByLabel("Uživatelské jméno", { exact: true }).fill(process.env.ATLAS_E2E_USER ?? "browser-admin");
    const field = page.getByLabel("Heslo", { exact: true });
    await field.evaluate((element, secret) => { (element as HTMLInputElement).value = secret; element.dispatchEvent(new Event("input", { bubbles: true })); }, process.env.ATLAS_E2E_PASSWORD ?? "");
    await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  }
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" }), search = page.getByRole("button", { name: /^Hledat ve wiki/ }).first();
  await expect(onboarding.or(search)).toBeVisible();
  if (await onboarding.isVisible()) await onboarding.click();
  await expect(search).toBeVisible();
}

async function json<T>(page: Page, method: "post" | "put", path: string, data?: unknown): Promise<T> {
  const csrf = await (await page.context().request.get("/api/auth/csrf")).json();
  const response = await page.context().request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token }, timeout: 120_000 });
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return response.json() as Promise<T>;
}

// --- the synthetic deployment page ---------------------------------------------------------------------------------------------

const words = "nemocnice portál integrace přístup server certifikát konfigurace objednávání pacient lékař rozhraní dokumentace síť účet registr provoz".split(" ");
/** Deterministic Czech-looking text of about `length` characters. */
const text = (seed: number, length: number) => {
  let out = "";
  for (let i = 0; out.length < length; i++) out += (out ? " " : "") + words[(seed * 7 + i * 3) % words.length];
  return out[0]!.toUpperCase() + out.slice(1);
};
const run = (value: string, marks?: unknown[]) => ({ type: "text", text: value, ...(marks ? { marks } : {}) });
const paragraph = (...content: unknown[]) => ({ type: "paragraph", content });
const heading = (level: number, title: string) => ({ type: "heading", attrs: { level }, content: [run(title)] });
const states = ["done", "planned", "blocked", "in_progress", "waiting_for_input", "not_applicable", "done", "planned", "planned", "done", "blocked"];
const directions = ["inbound", "internal", "outbound", "inbound", "internal"];

interface Contact { id: string; name: string; organization: string; title: string; email: string }
const contactData = (contact: Contact) => ({ version: 1, contacts: [{ name: contact.name, organization: contact.organization, title: contact.title, role: contact.title,
  objectId: contact.id, emails: [contact.email], phones: [] }] });

/** The proportions of typical deployment pages: per block ~2.6 notes, 0.6 history lines, 1.5 dependencies. */
function deploymentDocument(count: number, contacts: Contact[]) {
  const content: unknown[] = [{ type: "overview", attrs: { kind: "requirements" } }, heading(2, "Rozsah zakázky"), paragraph(run(text(1, 160)))];
  const keys: string[] = [];
  for (let i = 0; i < count; i++) {
    if (i % 2 === 0) content.push(heading(i % 6 === 0 ? 2 : 3, `Oddíl ${i / 2 + 1}: ${text(i, 18)}`));
    if (i % 9 === 4) content.push(paragraph(run(text(i + 3, 120))));
    const key = `perf.r${i}`, contact = i % 3 === 0 ? contacts[i % contacts.length] : undefined;
    const requires = [keys[i - 1], i % 2 ? keys[i - 3] : undefined].filter(Boolean);
    const notes: unknown[] = [paragraph(run("Kdo: ", [{ type: "bold" }]), run(text(i + 1, 12))), paragraph(run(text(i + 2, 110)))];
    if (i % 3 !== 2) notes.push(paragraph(run(text(i + 5, 60))));
    if (i % 5 < 3) notes.push({ type: "bulletList", content: [{ type: "listItem", content: [paragraph(run(`${1 + (i % 28)}. 10. 2026 10:${String(i % 60).padStart(2, "0")} — ${text(i + 4, 40)}`))] }] });
    content.push({ type: "requirement", attrs: { id: crypto.randomUUID(), key, direction: directions[i % directions.length], state: states[i % states.length],
      date: i % 5 < 3 ? `2026-${String(1 + (i % 12)).padStart(2, "0")}-${String(1 + (i % 27)).padStart(2, "0")}` : "",
      party: i % 5 < 3 ? (contact?.name ?? text(i + 6, 14)) : "", partyContactId: contact?.id ?? "", where: i % 5 < 3 ? `O:\\Zakázky\\${text(i, 20)}` : "",
      requires: requires.join(",") }, content: [paragraph(run(text(i + 9, 40))), ...notes] });
    keys.push(key);
  }
  content.push(heading(2, "Kontakty"), ...contacts.map(contact => ({ type: "contactCard", attrs: { data: contactData(contact) } })));
  for (let t = 0; t < 3; t++) content.push(heading(2, `Tabulka ${t + 1}`), { type: "table", content: Array.from({ length: 10 }, (_, row) => ({ type: "tableRow",
    content: Array.from({ length: 4 }, (_, col) => ({ type: row ? "tableCell" : "tableHeader", content: [paragraph(run(text(row * 4 + col + t, row ? 18 : 8)))] })) })) });
  return JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }, { id: "heading-anchors", version: 1 }, { id: "pages", version: 1 },
    { id: "atlas-requirements", version: 2 }], document: { type: "doc", content } });
}

const created: string[] = [];

async function seed(page: Page) {
  const stamp = Date.now().toString(36);
  const space = await json<{ id: string }>(page, "post", "/api/spaces", { name: `Perf nasazení ${stamp}`, icon: "code-xml", color: "blue" });
  const contacts: Contact[] = [];
  for (let i = 0; i < 16; i++) {
    const contact = { name: `Kontakt ${stamp} ${i + 1}`, organization: `Dodavatel ${i % 4 + 1}`, title: text(i, 30), email: `perf-${stamp}-${i}@example.invalid` };
    const resolved = await json<{ contactId: string }>(page, "post", "/api/contacts/resolve", { contact: { version: 1, name: contact.name, organization: contact.organization,
      title: contact.title, emails: [contact.email], phones: [] } });
    contacts.push({ ...contact, id: resolved.contactId });
  }
  const contentJson = deploymentDocument(blocks, contacts);
  const markdown = [...contentJson.matchAll(/"text":"([^"]*)"/g)].map(match => match[1]).join("\n\n");
  const page1 = await json<{ id: string; version: number }>(page, "post", "/api/pages", { spaceId: space.id, parentId: null, title: `Zakázka perf ${stamp}`, contentJson, markdown });
  await json(page, "post", `/api/pages/${page1.id}/publish`, { expectedVersion: page1.version });
  created.push(page1.id);
  return { id: page1.id, bytes: contentJson.length };
}

// --- one measurement -----------------------------------------------------------------------------------------------------------

interface Measurement { bundleMs: number; firstMs: number; editableMs: number; editableAfterBundleMs: number; firstToEditableMs: number; fellBack: boolean;
  longestFrameMs: number; blockingMs: number; domNodes: number; textFallbacksInView: number; typedLanded: boolean; keyMs: number[] }

/** The editor's block elements as shown, and the editor taking input (3.6: one editor; the hidden one building behind a local one is .editor-hydrating). */
const FIRST = ".document-body .md-editor:not(.editor-hydrating) .ProseMirror > *";
const LIVE = '.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]';

const observe = () => {
  const w = window as unknown as { __frames: { s: number; d: number; b: number }[]; __keys: number[]; __clicks: number[] };
  w.__frames = []; w.__keys = []; w.__clicks = [];
  // When the first block of the editor is in the DOM, when the editor takes input, and whether a hidden one was ever built behind a local one.
  const marks: { first?: number; editable?: number; hydrating?: boolean } = {};
  (window as unknown as { __marks: typeof marks }).__marks = marks;
  const mark = new MutationObserver(() => {
    if (document.querySelector(".editor-hydrating")) marks.hydrating = true;
    marks.first ??= document.querySelector(".document-body .md-editor:not(.editor-hydrating) .ProseMirror > *") ? performance.now() : undefined;
    marks.editable ??= document.querySelector('.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]') ? performance.now() : undefined;
    if (marks.first !== undefined && marks.editable !== undefined) mark.disconnect();
  });
  mark.observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ["contenteditable", "class"] });
  new PerformanceObserver(list => w.__frames.push(...list.getEntries().map(entry => ({ s: entry.startTime, d: entry.duration,
    b: (entry as PerformanceEntry & { blockingDuration: number }).blockingDuration })))).observe({ type: "long-animation-frame", buffered: true });
  // A click's duration is from the press to the next paint after its handlers (Event Timing): a switch of level, test V.
  new PerformanceObserver(list => { for (const entry of list.getEntries()) if (entry.name === "keydown") w.__keys.push(entry.duration); else if (entry.name === "click") w.__clicks.push(entry.duration); })
    .observe({ type: "event", durationThreshold: 16, buffered: true } as PerformanceObserverInit);
};

/** The page bundle's response end on the page's clock (Resource Timing), -1 when none was fetched since `since`. Evaluated in the page. */
const bundleEnd = ({ id, since }: { id: string; since: number }) => {
  const entry = (performance.getEntriesByType("resource") as PerformanceResourceTiming[])
    .find(item => item.startTime >= since - 1 && new URL(item.name).pathname === `/api/pages/${id}` && new URL(item.name).search.includes("bundle=true"));
  return entry ? entry.responseEnd : -1;
};

/** Opens the page by its address in a fresh tab (dismissing a fresh installation's onboarding) and waits for its text. */
async function gotoPage(page: Page, id: string, query = "") {
  await page.goto(`/${query}#/pages/${id}`);
  const content = page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first(), onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await expect(content.or(onboarding)).toBeVisible({ timeout: 60_000 });
  if (await onboarding.isVisible()) {
    // It takes the reader home once dismissed; then the page again, loaded afresh.
    await onboarding.click();
    await expect(page.getByRole("button", { name: /^Hledat ve wiki/ }).first()).toBeVisible();
    await page.goto(`/?opened=${Date.now()}#/pages/${id}`);
  }
  await expect(content).toBeVisible({ timeout: 60_000 });
}

/** The three points of a load on the page's clock: bundle, first content, editable (-1 until it is), and whether a hidden editor was built. */
async function timeline(page: Page, id: string) {
  const { first, editable, hydrating } = await page.evaluate(() => (window as unknown as { __marks: { first?: number; editable?: number; hydrating?: boolean } }).__marks);
  const end = await page.evaluate(bundleEnd, { id, since: 0 });
  return { bundleMs: Math.round(end), firstMs: Math.round(first ?? -1), editableMs: Math.round(editable ?? -1), fellBack: !!hydrating };
}

/** The page is opened once as the writer before anything is measured, until it opens without the fallback (its live state is stored,
 * so the bundle carries it). Returns whether the very first open hit the fallback and how many opens it took. */
async function warmUp(browser: Browser, storageState: Awaited<ReturnType<BrowserContext["storageState"]>>, id: string) {
  const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 } });
  await context.addInitScript(observe);
  const page = await context.newPage();
  let firstOpenFellBack = false, attempts = 0;
  while (attempts < 5) {
    attempts++;
    await gotoPage(page, id, attempts > 1 ? `?warmup=${attempts}` : "");
    await page.waitForFunction(() => (window as unknown as { __marks: { editable?: number } }).__marks.editable !== undefined, null, { timeout: 90_000 });
    // The shared session stores its state a moment after the first writer built it.
    await page.waitForTimeout(3_000);
    const fellBack = await page.evaluate(() => !!(window as unknown as { __marks: { hydrating?: boolean } }).__marks.hydrating);
    if (attempts === 1) firstOpenFellBack = fellBack;
    if (!fellBack) break;
  }
  await context.close();
  return { firstOpenFellBack, warmUps: attempts };
}

async function measure(browser: Browser, storageState: Awaited<ReturnType<BrowserContext["storageState"]>>, id: string): Promise<Measurement> {
  const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 } });
  await context.addInitScript(observe);
  const page = await context.newPage();
  page.on("dialog", dialog => dialog.accept());
  const cdp = await context.newCDPSession(page);
  await cdp.send("Emulation.setCPUThrottlingRate", { rate: cpu });
  await gotoPage(page, id);
  await page.waitForFunction(() => (window as unknown as { __marks: { editable?: number } }).__marks.editable !== undefined, null, { timeout: 60_000 });
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(2_000 * cpu);
  const points = await timeline(page, id);
  const entry = await page.evaluate(() => {
    const w = window as unknown as { __frames: { s: number; d: number; b: number }[] };
    // The contacts in view: a module still showing its text fallback is the "text form" flash.
    const live = document.querySelector(".document-body .md-editor .ProseMirror")!;
    const textFallbacksInView = [...live.querySelectorAll("[data-module-fallback]")].filter(element => {
      const box = element.getBoundingClientRect();
      return box.height > 0 && box.bottom > 0 && box.top < innerHeight;
    }).length;
    return { textFallbacksInView, longestFrameMs: Math.max(0, ...w.__frames.map(frame => frame.d)), blockingMs: w.__frames.reduce((sum, frame) => sum + frame.b, 0),
      domNodes: document.querySelector(".document-body")?.getElementsByTagName("*").length ?? 0 };
  });
  // Typing into the title of a block in the middle of the page: the keys must land.
  const title = '.document-body .md-editor .ProseMirror :is(h1, h2, h3, h4, h5, h6)[data-heading-id]:has(.rq-chips)';
  const index = await page.evaluate(async title => {
    const titles = document.querySelectorAll(title);
    const element = titles[Math.floor(titles.length / 2)]!;
    element.scrollIntoView({ block: "center" });
    // The caret at the end of its text through the editor: a script's focus gets the editor's own selection back (DocumentEditor selectionOnFocus).
    const editor = (element.closest(".ProseMirror") as HTMLElement & { editor: { view: { posAtDOM(node: Node, offset: number): number }; commands: { setTextSelection(at: number): boolean; focus(at?: null, options?: { scrollIntoView: boolean }): boolean } } }).editor;
    editor.commands.setTextSelection(editor.view.posAtDOM(element, element.childNodes.length));
    editor.commands.focus(null, { scrollIntoView: false });
    await new Promise(resolve => setTimeout(resolve, 500));
    (window as unknown as { __keys: number[] }).__keys.length = 0;
    return titles.length ? Math.floor(titles.length / 2) : -1;
  }, title);
  for (const key of "abcdefghij") { await page.keyboard.type(key); await page.waitForTimeout(150 * cpu); }
  await page.waitForTimeout(1_000);
  const { keyMs, landed } = await page.evaluate(({ index, title }) => ({ keyMs: (window as unknown as { __keys: number[] }).__keys,
    landed: index >= 0 && !!document.querySelectorAll(title)[index]?.textContent?.endsWith("abcdefghij") }), { index, title });
  await context.close();
  return { ...points, editableAfterBundleMs: points.editableMs - points.bundleMs, firstToEditableMs: Math.abs(points.editableMs - points.firstMs), ...entry,
    longestFrameMs: Math.round(entry.longestFrameMs), blockingMs: Math.round(entry.blockingMs), typedLanded: landed, keyMs };
}

const median = (values: number[]) => { const sorted = [...values].sort((a, b) => a - b); return sorted[Math.floor(sorted.length / 2)] ?? 0; };

test("A: opening a big deployment page: bundle, first content, editable", async ({ page, browser }) => {
  await signIn(page);
  const seeded = pageIds.length ? pageIds.map(id => ({ id, bytes: 0 })) : [await seed(page)];
  const storageState = await page.context().storageState();
  for (const target of seeded) await measurePage(browser, storageState, target);
});

async function measurePage(browser: Browser, storageState: Awaited<ReturnType<BrowserContext["storageState"]>>, seeded: { id: string; bytes: number }) {
  // Unmeasured: the server founds the page's shared session from its content and stores its state, which the bundle then carries.
  const warmed = await warmUp(browser, storageState, seeded.id);
  const results: Measurement[] = [];
  for (let i = 0; i < runs; i++) results.push(await measure(browser, storageState, seeded.id));
  const of = (pick: (result: Measurement) => number) => median(results.map(pick));
  const summary = {
    url: test.info().project.use.baseURL, pageId: seeded.id, blocks: pageIds.length ? undefined : blocks, cpu, runs, bytes: seeded.bytes, ...warmed,
    bundleMs: of(result => result.bundleMs), firstMs: of(result => result.firstMs), editableMs: of(result => result.editableMs),
    editableAfterBundleMs: of(result => result.editableAfterBundleMs), firstToEditableMs: of(result => result.firstToEditableMs),
    measuredRunsThatFellBack: results.filter(result => result.fellBack).length, longestFrameMs: of(result => result.longestFrameMs),
    blockingMs: of(result => result.blockingMs), domNodes: of(result => result.domNodes),
    textFallbacksInView: Math.max(...results.map(result => result.textFallbacksInView)), typedLanded: results.every(result => result.typedLanded),
    keyMs: median(results.map(result => median(result.keyMs.length ? result.keyMs : [0]))),
  };
  console.log(JSON.stringify(summary));
  if (process.env.ATLAS_PERF_REPORT) writeFileSync(report(`A-${seeded.id}`), JSON.stringify({ summary, results }, null, 2));
  expect.soft(summary.typedLanded, "typed keys land in the block's title").toBe(true);
  if (cpu !== 1) return;
  expect.soft(summary.bundleMs, "the page bundle was fetched").toBeGreaterThan(0);
  // A fresh tab: the bundle is read with the session check, long before the app's chunks have arrived, so bundle -> editable here is the
  // JS download and is reported; the 300 ms budget is gated on an in-app open (test G, `openAgain`).
  expect.soft(summary.firstToEditableMs, "first content and editable at the same moment, median ms").toBeLessThanOrEqual(budget.firstToEditableMs);
}

/** One report file per test and page next to ATLAS_PERF_REPORT (`perf.json` → `perf.A-<page id>.json`). */
const report = (name: string) => process.env.ATLAS_PERF_REPORT!.replace(/(\.json)?$/, `.${name}.json`);

// --- 3.6-G: the page's DOM per component, typing under a trace --------------------------------------------------------------------

interface OpenMeasurement { bundleMs: number; firstMs: number; editableMs: number; fellBack: boolean; longestFrameMs: number; blockingMs: number; domNodes: number }
interface TypingMeasurement { target: string; keys: number; keyMs: number[]; forcedLayouts: number; layouts: number; forcedStyles: number; styles: number;
  layoutMs: number; styleMs: number; forcers: Record<string, number>; invalidators: Record<string, number> }
interface Census { total: number; svgElements: number; topLevel: Record<string, { count: number; elements: number; median: number; max: number; offscreen: number }>;
  islands: Record<string, { count: number; elements: number; median: number; max: number }>;
  requirementChrome?: { count: number; elements: number; median: number; max: number } }

/** A fresh tab on the home page, the app loaded and idle: the page is then opened the way a reader opens it from the tree. */
async function homeTab(browser: Browser, storageState: Awaited<ReturnType<BrowserContext["storageState"]>>, rate = cpu) {
  const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 } });
  await context.addInitScript(observe);
  const page = await context.newPage();
  page.on("dialog", dialog => dialog.accept());
  const cdp = await context.newCDPSession(page);
  await cdp.send("Emulation.setCPUThrottlingRate", { rate });
  await page.goto("/");
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" }), search = page.getByRole("button", { name: /^Hledat ve wiki/ }).first();
  await expect(onboarding.or(search)).toBeVisible({ timeout: 60_000 });
  if (await onboarding.isVisible()) await onboarding.click();
  await expect(search).toBeVisible();
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(2_000 * cpu);
  return { context, page };
}

/** Opens the page by its hash from the loaded app (what a click in the tree does): the page bundle's response end, the first block of the
 * editor in the DOM and the editor taking input, all from the hash change. */
async function open(page: Page, id: string): Promise<OpenMeasurement & { t0: number }> {
  const at = await page.evaluate(async ({ id, first, live }) => {
    const w = window as unknown as { __frames: { s: number; d: number; b: number }[] };
    const t0 = performance.now();
    location.hash = `#/pages/${id}`;
    const times = await new Promise<{ firstMs: number; editableMs: number; fellBack: boolean }>((resolve, reject) => {
      const times = { firstMs: 0, editableMs: 0, fellBack: false };
      const tick = () => {
        const now = performance.now() - t0;
        if (document.querySelector(".editor-hydrating")) times.fellBack = true;
        if (!times.firstMs && document.querySelector(first)) times.firstMs = now;
        if (document.querySelector(live)) { times.editableMs = now; times.firstMs ||= now; return resolve(times); }
        if (now > 120_000) return reject(new Error("The page did not open in 120 s"));
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
    await new Promise(resolve => setTimeout(resolve, 3_000));
    const frames = w.__frames.filter(frame => frame.s >= t0);
    return { ...times, t0, longestFrameMs: Math.round(Math.max(0, ...frames.map(frame => frame.d))), blockingMs: Math.round(frames.reduce((sum, frame) => sum + frame.b, 0)),
      domNodes: document.querySelector(".document-body")?.getElementsByTagName("*").length ?? 0 };
  }, { id, first: FIRST, live: LIVE });
  const end = await page.evaluate(bundleEnd, { id, since: at.t0 });
  return { ...at, firstMs: Math.round(at.firstMs), editableMs: Math.round(at.editableMs), bundleMs: end < 0 ? -1 : Math.round(end - at.t0) };
}

/** The live editor's elements per component: its top-level nodes by kind (and how many of them lie over a screen away from the
 * viewport, what lazy node views would leave out), and the non-editable islands inside text (chips, widgets) by kind. */
const census = (live: string): Census => {
  const root = document.querySelector(live)!;
  const size = (element: Element) => element.getElementsByTagName("*").length + 1;
  const kindOf = (element: Element) => element.matches("h1, h2, h3, h4, h5, h6") && element.hasAttribute("data-heading-id") && element.querySelector(".rq-chips") ? "requirement"
    : element.hasAttribute("data-overview-kind") ? "overview"
    : element.hasAttribute("data-md-module") ? `module ${element.getAttribute("data-md-module")} (${element.getAttribute("data-md-render-state") ?? "drawn"})`
    : `${element.tagName.toLowerCase()}${element.classList[0] ? `.${element.classList[0]}` : ""}`;
  const group = (map: Record<string, number[]>, kind: string, value: number) => (map[kind] ??= []).push(value);
  const top: Record<string, number[]> = {}, offscreen: Record<string, number> = {}, islands: Record<string, number[]> = {};
  for (const element of root.children) {
    const kind = kindOf(element), box = element.getBoundingClientRect();
    group(top, kind, size(element));
    if (box.bottom < -innerHeight || box.top > 2 * innerHeight) offscreen[kind] = (offscreen[kind] ?? 0) + size(element);
  }
  for (const element of root.querySelectorAll('[contenteditable="false"]')) {
    if (element.parentElement === root || element.parentElement?.closest('[contenteditable="false"]')) continue;
    group(islands, kindOf(element), size(element));
  }
  const stats = (values: number[]) => { const sorted = [...values].sort((a, b) => a - b);
    return { count: values.length, elements: values.reduce((sum, value) => sum + value, 0), median: sorted[Math.floor(sorted.length / 2)]!, max: sorted.at(-1)! }; };
  // A requirement heading's own elements (its chrome: the checkbox and the chips box) apart from the text the editor renders.
  const chrome = [...root.querySelectorAll("h1, h2, h3, h4, h5, h6")].filter(heading => heading.querySelector(".rq-chips")).map(heading =>
    [...heading.querySelectorAll(".rq-check, .rq-chips")].reduce((sum, widget) => sum + size(widget), 0));
  return { total: size(root), svgElements: root.querySelectorAll("svg, svg *").length, topLevel: Object.fromEntries(Object.entries(top).map(([kind, values]) => [kind, { ...stats(values), offscreen: offscreen[kind] ?? 0 }])),
    islands: Object.fromEntries(Object.entries(islands).map(([kind, values]) => [kind, stats(values)])), requirementChrome: chrome.length ? stats(chrome) : undefined };
};

type StubCosts = Record<string, { count: number; fullElements: number; stubElements: number; fullMs: number; stubMs: number; heightDeltaPx: number }>;

/** What a stub costs against the full component, per kind (3.6-G lazy node views): up to 40 components of each kind are copied into a
 * hidden layer styled like the editor, as they are (full) and as a stub (the outer element with all its attributes — filter, sort, level
 * and search read them — plus the text the editor renders; an atom keeps only its size), then laid out. Milliseconds per component for
 * building the DOM, style and layout; a node view's own script (its draw) comes on top of the full cost. `heightDeltaPx` is how far the
 * stub's height is from the full one (median): what CSS must reserve so nothing moves when it hydrates. */
const stubCost = (live: string): StubCosts => {
  const root = document.querySelector(live)!;
  const kinds: Record<string, (element: Element) => boolean> = {
    overview: element => element.hasAttribute("data-overview-kind"),
    table: element => element.classList.contains("tableWrapper"),
    contactCard: element => element.getAttribute("data-md-module") === "contactCard",
    heading: element => /^H[1-6]$/.test(element.tagName),
  };
  const island = (node: Node) => node instanceof Element && node.getAttribute("contenteditable") === "false";
  const stub = (element: Element): Element => {
    const shell = element.cloneNode(false) as HTMLElement;
    const table = element.querySelector(":scope > table");
    if (table) shell.append(table.cloneNode(true));
    else if (/^H[1-6]$/.test(element.tagName)) { for (const child of element.childNodes) if (!island(child)) shell.append(child.cloneNode(true)); }
    else { shell.style.height = `${element.getBoundingClientRect().height}px`; shell.replaceChildren(); }
    return shell;
  };
  const layer = document.createElement("div");
  layer.className = root.className;
  Object.assign(layer.style, { position: "fixed", top: "0", left: "0", width: `${root.clientWidth}px`, visibility: "hidden", pointerEvents: "none", contain: "layout" });
  root.parentElement!.append(layer);
  const lay = (make: () => Element[]) => {
    const t0 = performance.now(), copies = make();
    // The editor skips off-screen blocks (content-visibility); a copy is laid out in full to count its cost.
    for (const copy of copies) (copy as HTMLElement).style.contentVisibility = "visible";
    layer.append(...copies);
    void layer.offsetHeight;
    const ms = performance.now() - t0, heights = copies.map(copy => copy.getBoundingClientRect().height);
    layer.replaceChildren();
    return { ms, heights, elements: copies.reduce((sum, copy) => sum + copy.getElementsByTagName("*").length + 1, 0) };
  };
  const out: StubCosts = {};
  for (const [kind, matches] of Object.entries(kinds)) {
    const sample = [...root.children].filter(matches).slice(0, 40);
    if (!sample.length) continue;
    const full: ReturnType<typeof lay>[] = [], stubbed: ReturnType<typeof lay>[] = [];
    for (let i = 0; i < 5; i++) { full.push(lay(() => sample.map(element => element.cloneNode(true) as Element))); stubbed.push(lay(() => sample.map(stub))); }
    const mid = (values: number[]) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)]!;
    const deltas = full[0]!.heights.map((height, index) => Math.abs(height - stubbed[0]!.heights[index]!));
    out[kind] = { count: sample.length, fullElements: full[0]!.elements, stubElements: stubbed[0]!.elements,
      fullMs: +(mid(full.map(run => run.ms)) / sample.length).toFixed(3), stubMs: +(mid(stubbed.map(run => run.ms)) / sample.length).toFixed(3), heightDeltaPx: Math.round(mid(deltas)) };
  }
  layer.remove();
  return out;
};

/** Types ten characters at the end of `target` (in the middle of the page) under a Chromium trace. */
async function typeTraced(browser: Browser, page: Page, target: string, selector: string): Promise<TypingMeasurement> {
  const placed = await page.evaluate(async selector => {
    const candidates = document.querySelectorAll(selector);
    const element = candidates[Math.floor(candidates.length / 2)];
    if (!element) return false;
    element.scrollIntoView({ block: "center" });
    // The caret at the end of its text through the editor: a script's focus gets the editor's own selection back (DocumentEditor selectionOnFocus).
    const editor = (element.closest(".ProseMirror") as HTMLElement & { editor: { view: { posAtDOM(node: Node, offset: number): number }; commands: { setTextSelection(at: number): boolean; focus(at?: null, options?: { scrollIntoView: boolean }): boolean } } }).editor;
    editor.commands.setTextSelection(editor.view.posAtDOM(element, element.childNodes.length));
    editor.commands.focus(null, { scrollIntoView: false });
    await new Promise(resolve => setTimeout(resolve, 1_000));
    (window as unknown as { __keys: number[] }).__keys.length = 0;
    return true;
  }, selector);
  if (!placed) return { target, keys: 0, keyMs: [], forcedLayouts: 0, layouts: 0, forcedStyles: 0, styles: 0, layoutMs: 0, styleMs: 0, forcers: {}, invalidators: {} };
  await browser.startTracing(page, { categories: ["devtools.timeline", "disabled-by-default-devtools.timeline", "disabled-by-default-devtools.timeline.stack"] });
  const keys = "abcdefghij";
  for (const key of keys) { await page.keyboard.type(key); await page.waitForTimeout(150 * cpu); }
  await page.waitForTimeout(1_000);
  const trace = JSON.parse((await browser.stopTracing()).toString()) as { traceEvents: { name: string; ph: string; ts: number; dur?: number; tid: number;
    args?: { data?: { type?: string }; beginData?: { stackTrace?: { functionName: string; url: string; lineNumber: number; columnNumber: number }[] } } }[] };
  const keyMs = await page.evaluate(() => (window as unknown as { __keys: number[] }).__keys);
  // The renderer's main thread is where the keydowns were dispatched; a layout or style pass with a script stack was forced by that script.
  const keydown = trace.traceEvents.find(event => event.name === "EventDispatch" && event.args?.data?.type === "keydown");
  const main = trace.traceEvents.filter(event => event.tid === keydown?.tid && (event.ph === "X" || event.ph === "B"));
  const layouts = main.filter(event => event.name === "Layout"), styles = main.filter(event => event.name === "UpdateLayoutTree");
  const forced = (event: (typeof main)[number]) => !!event.args?.beginData?.stackTrace?.length;
  const forcers: Record<string, number> = {}, invalidators: Record<string, number> = {};
  const where = (frame: { functionName: string; url: string; lineNumber: number; columnNumber: number }) =>
    `${frame.functionName || "(anonymous)"} ${frame.url.split("/").at(-1)}:${frame.lineNumber}:${frame.columnNumber}`;
  for (const event of [...layouts, ...styles].filter(forced)) {
    const at = `${event.name === "Layout" ? "layout" : "style"} ${where(event.args!.beginData!.stackTrace![0]!)}`;
    forcers[at] = (forcers[at] ?? 0) + 1;
  }
  // What dirtied layout or style in the first place (the script that wrote), whether a script or the next frame then paid for it.
  for (const event of trace.traceEvents) {
    if (event.tid !== keydown?.tid || (event.name !== "InvalidateLayout" && event.name !== "ScheduleStyleRecalculation")) continue;
    const frame = (event.args?.data as { stackTrace?: Parameters<typeof where>[0][] } | undefined)?.stackTrace?.[0];
    const at = `${event.name === "InvalidateLayout" ? "layout" : "style"} ${frame ? where(frame) : "(no script)"}`;
    invalidators[at] = (invalidators[at] ?? 0) + 1;
  }
  const ms = (events: typeof main) => Math.round(events.reduce((sum, event) => sum + (event.dur ?? 0), 0) / 1000);
  return { target, keys: keys.length, keyMs, forcedLayouts: layouts.filter(forced).length / keys.length, layouts: layouts.length / keys.length,
    forcedStyles: styles.filter(forced).length / keys.length, styles: styles.length / keys.length, layoutMs: ms(layouts), styleMs: ms(styles),
    forcers: top(forcers), invalidators: top(invalidators) };
}
const top = (counts: Record<string, number>) => Object.fromEntries(Object.entries(counts).sort((a, b) => b[1] - a[1]).slice(0, 15));

test("G: a long deployment page opened in the app, its DOM and typing on it", async ({ page, browser }) => {
  await signIn(page);
  const targets = pageIds.length ? pageIds : [(await seed(page)).id];
  const storageState = await page.context().storageState();
  for (const id of targets) {
    const warmed = await warmUp(browser, storageState, id);
    const opens: OpenMeasurement[] = [], again: OpenMeasurement[] = [];
    let lastCensus: Census | undefined;
    const typing: TypingMeasurement[] = [], stubs: StubCosts[] = [];
    for (let i = 0; i < runs; i++) {
      const { context, page: tab } = await homeTab(browser, storageState);
      const { t0, ...opened } = await open(tab, id);
      opens.push(opened);
      // The usual open: the app's editor chunks are there from an earlier page of the session.
      await tab.evaluate(() => { location.hash = "#/"; });
      await tab.waitForTimeout(2_000 * cpu);
      const { t0: _again, ...reopened } = await open(tab, id);
      again.push(reopened);
      lastCensus = await tab.evaluate(census, LIVE);
      stubs.push(await tab.evaluate(stubCost, LIVE));
      typing.push(await typeTraced(browser, tab, "requirement title", `${LIVE} :is(h1, h2, h3, h4, h5, h6)[data-heading-id]:has(.rq-chips)`));
      typing.push(await typeTraced(browser, tab, "paragraph", `${LIVE} > p`));
      await context.close();
    }
    const per = (target: string, pick: (measurement: TypingMeasurement) => number) => median(typing.filter(entry => entry.target === target && entry.keys).map(pick));
    const typed = Object.fromEntries(["requirement title", "paragraph"].map(target => [target, {
      keyMs: per(target, entry => median(entry.keyMs.length ? entry.keyMs : [0])), forcedLayoutsPerKey: per(target, entry => entry.forcedLayouts),
      layoutsPerKey: per(target, entry => entry.layouts), forcedStylesPerKey: per(target, entry => entry.forcedStyles), stylesPerKey: per(target, entry => entry.styles) }]));
    const summary = { url: test.info().project.use.baseURL, pageId: id, cpu, runs, ...warmed,
      open: { bundleMs: median(opens.map(entry => entry.bundleMs)), firstMs: median(opens.map(entry => entry.firstMs)), editableMs: median(opens.map(entry => entry.editableMs)),
        opensThatFellBack: opens.filter(entry => entry.fellBack).length, longestFrameMs: median(opens.map(entry => entry.longestFrameMs)),
        blockingMs: median(opens.map(entry => entry.blockingMs)), domNodes: median(opens.map(entry => entry.domNodes)) },
      openAgain: { bundleMs: median(again.map(entry => entry.bundleMs)), firstMs: median(again.map(entry => entry.firstMs)), editableMs: median(again.map(entry => entry.editableMs)),
        editableAfterBundleMs: median(again.map(entry => entry.editableMs - entry.bundleMs)), longestFrameMs: median(again.map(entry => entry.longestFrameMs)),
        opensThatFellBack: again.filter(entry => entry.fellBack).length },
      typed, requirementBlock: lastCensus?.topLevel.requirement, requirementChrome: lastCensus?.requirementChrome,
      stubs: Object.fromEntries(Object.keys(stubs[0] ?? {}).map(kind => [kind, { ...stubs[0]![kind]!,
        fullMs: median(stubs.map(entry => entry[kind]!.fullMs)), stubMs: median(stubs.map(entry => entry[kind]!.stubMs)) }])) };
    console.log(JSON.stringify(summary));
    if (process.env.ATLAS_PERF_REPORT) writeFileSync(report(`G-${id}`), JSON.stringify({ summary, census: lastCensus, opens, typing, stubs }, null, 2));
    if (cpu !== 1) continue;
    expect.soft(summary.openAgain.editableAfterBundleMs, "in-app open: bundle -> editable, median ms").toBeLessThanOrEqual(budget.editableAfterBundleMs);
    expect.soft(Math.abs(summary.openAgain.editableMs - summary.openAgain.firstMs), "in-app open: first content = editable").toBeLessThanOrEqual(budget.firstToEditableMs);
    expect.soft(summary.openAgain.longestFrameMs, "in-app open: longest animation frame, median ms").toBeLessThanOrEqual(budget.longestFrameMs);
    for (const [target, values] of Object.entries(typed)) expect.soft(values.forcedLayoutsPerKey, `forced layouts per keystroke (${target}), median`).toBeLessThanOrEqual(liveBudget.forcedLayoutsPerKey);
    // The budget is the component's own elements; the text of its notes is the page's content (reported in requirementBlock).
    if (summary.requirementChrome) expect.soft(summary.requirementChrome.max, "elements of the largest requirement block's chrome in the live editor").toBeLessThanOrEqual(liveBudget.blockElements);
  }
});

/** A press on a requirement heading's chip in view (`selector` inside the first heading on screen that has it) → the popover's frame painted, and
 * → usable: its keyboard inside it on a control of its own, no skeleton left (3.6-G stub, load, swap). */
const popoverTiming = (selector: string) => new Promise<{ framedMs: number; usableMs: number; framedPx: number; finalPx: number } | null>(resolve => {
  const block = [...document.querySelectorAll<HTMLElement>("h1, h2, h3, h4, h5, h6")].find(element => {
    const box = element.getBoundingClientRect();
    return box.top > 80 && box.bottom < innerHeight && element.querySelector(`.rq-chips ${selector}`);
  });
  const control = block?.querySelector<HTMLElement>(`.rq-chips ${selector}`);
  if (!control) return resolve(null);
  const t0 = performance.now();
  let framedMs = 0, framedPx = 0;
  control.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
  control.click();
  const tick = () => {
    const popover = document.querySelector(".md-context-popover:not([data-leaving])"), now = performance.now() - t0;
    if (popover && !framedMs) { framedMs = now; framedPx = Math.round(popover.getBoundingClientRect().height); }
    const active = document.activeElement;
    if (popover && !popover.querySelector(".md-stub") && active && popover.contains(active) && active !== popover && active.matches("button, input, textarea, [role=menuitem], [role=menuitemradio], [tabindex]"))
      // Its height a moment later, when the skeleton's hold is released: what moved from the first frame (0 = no jump).
      return void requestAnimationFrame(() => requestAnimationFrame(() => resolve({ framedMs: Math.round(framedMs), usableMs: Math.round(now), framedPx,
        finalPx: Math.round(popover.getBoundingClientRect().height) })));
    if (now > 10_000) return resolve(null);
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
});

test("P: a requirement heading's state menu from the press to usable, warm and with its chunk still on the way (3.6-G stub, load, swap)", async ({ page, browser }) => {
  await signIn(page);
  const targets = pageIds.length ? pageIds : [(await seed(page)).id];
  const storageState = await page.context().storageState(), delay = Number(process.env.ATLAS_PERF_CHUNK_DELAY_MS ?? 300);
  for (const id of targets) {
    await warmUp(browser, storageState, id);
    const runsOf: Record<string, { framedMs: number; usableMs: number; framedPx: number; finalPx: number }[]> = {};
    for (let i = 0; i < runs; i++) for (const cold of [false, true]) {
      const { context, page: tab } = await homeTab(browser, storageState);
      // Cold: a popover's chunk is held until its press and arrives `delay` ms after it (the prefetch has not landed yet).
      const gates = new Map(["state"].map(name => { let release!: () => void; const wait = new Promise<void>(resolve => { release = resolve; }); return [name, { wait, release }]; }));
      if (cold) await context.route(/\/assets\/RequirementStateMenu-[^/]*\.js$/, async route => {
        await gates.get("state")!.wait;
        await route.continue();
      });
      await open(tab, id);
      if (!cold) await tab.waitForTimeout(3_000 * cpu);
      // The state chip's menu.
      for (const [name, selector] of [["state", '[data-rq-chip="state"]']] as const) {
        const pending = tab.evaluate(popoverTiming, selector);
        if (cold) setTimeout(() => gates.get(name)!.release(), delay);
        const timing = await pending;
        if (timing) (runsOf[`${name} ${cold ? "cold" : "warm"}`] ??= []).push(timing);
        await tab.keyboard.press("Escape");
        await tab.waitForTimeout(400 * cpu);
      }
      await context.close();
    }
    const summary = { pageId: id, cpu, delay, popovers: Object.fromEntries(Object.entries(runsOf).map(([key, values]) => [key,
      { framedMs: median(values.map(value => value.framedMs)), usableMs: median(values.map(value => value.usableMs)), framedPx: median(values.map(value => value.framedPx)),
        finalPx: median(values.map(value => value.finalPx)), runs: values.length }])) };
    console.log(JSON.stringify(summary));
    if (process.env.ATLAS_PERF_REPORT) writeFileSync(report(`P-${id}`), JSON.stringify({ summary, runs: runsOf }, null, 2));
  }
});

// --- 3.6-G: loading a page (L) and readers of a live page (R) ---------------------------------------------------------------

/** Where a load stands: first contentful and largest contentful paint, and what came over the network (a resource served from the
 * HTTP cache transfers nothing, so a warm load shows what still goes to the server). The three points of the page come from `observe`. */
const loadObserve = () => {
  const w = window as unknown as { __lcp: number };
  w.__lcp = 0;
  new PerformanceObserver(list => { for (const entry of list.getEntries()) w.__lcp = entry.startTime; }).observe({ type: "largest-contentful-paint", buffered: true });
};
interface Transfer { requests: number; apiRequests: number; jsFiles: number; jsKB: number; transferKB: number }
/** The resources fetched since `since` (ms on the page's clock), counted in the page. */
const transferSince = (since: number): Transfer => {
  const resources = (performance.getEntriesByType("resource") as PerformanceResourceTiming[]).filter(entry => entry.startTime >= since);
  const js = resources.filter(entry => /\.m?js(\?|$)/.test(entry.name));
  const kb = (list: PerformanceResourceTiming[]) => Math.round(list.reduce((sum, entry) => sum + entry.transferSize, 0) / 1024);
  return { requests: resources.length, apiRequests: resources.filter(entry => entry.name.includes("/api/") || entry.name.includes("/hubs/")).length,
    jsFiles: js.length, jsKB: kb(js), transferKB: kb(resources) };
};
interface LoadMeasurement extends Transfer { fcpMs: number; lcpMs: number; bundleMs: number; firstMs: number; editableMs: number; fellBack: boolean; pageReads: number; repeated: Record<string, number> }

async function loaded(page: Page, id: string, query = ""): Promise<LoadMeasurement> {
  await gotoPage(page, id, query);
  await page.waitForFunction(() => (window as unknown as { __marks: { editable?: number } }).__marks.editable !== undefined, null, { timeout: 60_000 });
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(2_000 * cpu);
  const points = await timeline(page, id);
  const paints = await page.evaluate(() => {
    const w = window as unknown as { __lcp: number };
    const fcp = performance.getEntriesByName("first-contentful-paint")[0]?.startTime ?? 0;
    // The API addresses read more than once in the load (ids shortened), and how often the page itself was read (early-reads.js
    // reads it with its bundle as the app does; a read the app could not adopt was a second one).
    const counts: Record<string, number> = {};
    for (const entry of performance.getEntriesByType("resource")) {
      const url = new URL(entry.name);
      if (!url.pathname.startsWith("/api/")) continue;
      const key = (url.pathname + url.search).replace(/[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}/g, id => id.slice(0, 8));
      counts[key] = (counts[key] ?? 0) + 1;
    }
    const pageReads = Object.entries(counts).filter(([key]) => /^\/api\/pages\/[0-9a-f]{8}(\?|$)/.test(key)).reduce((sum, [, count]) => sum + count, 0);
    return { fcpMs: Math.round(fcp), lcpMs: Math.round(w.__lcp), pageReads,
      repeated: Object.fromEntries(Object.entries(counts).filter(([, count]) => count > 1)) };
  });
  return { ...points, ...paints, ...await page.evaluate(transferSince, 0) };
}

/** From one page to another inside the loaded app (a click in the tree): bundle, the other page's first block and the editor taking input. */
async function switched(page: Page, to: string): Promise<Transfer & { bundleMs: number; firstMs: number; editableMs: number; fellBack: boolean }> {
  const { t0, firstMs, editableMs, fellBack } = await page.evaluate(async ({ to, first, live }) => {
    const before = document.querySelector(first)?.parentElement, t0 = performance.now();
    location.hash = `#/pages/${to}`;
    const times = await new Promise<{ firstMs: number; editableMs: number; fellBack: boolean }>((resolve, reject) => {
      const times = { firstMs: 0, editableMs: 0, fellBack: false };
      const tick = () => {
        const now = performance.now() - t0, shown = document.querySelector(first)?.parentElement;
        if (document.querySelector(".editor-hydrating")) times.fellBack = true;
        if (!times.firstMs && shown && shown !== before && location.hash.includes(to)) times.firstMs = now;
        if (times.firstMs && shown && document.querySelector(live) === shown) { times.editableMs = now; return resolve(times); }
        if (now > 60_000) return reject(new Error("The other page did not open in 60 s"));
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
    await new Promise(resolve => setTimeout(resolve, 3_000));
    return { t0, firstMs: Math.round(times.firstMs), editableMs: Math.round(times.editableMs), fellBack: times.fellBack };
  }, { to, first: FIRST, live: LIVE });
  const end = await page.evaluate(bundleEnd, { id: to, since: t0 });
  return { bundleMs: end < 0 ? -1 : Math.round(end - t0), firstMs, editableMs, fellBack, ...await page.evaluate(transferSince, t0) };
}

const medians = <T extends object>(list: T[]) =>
  Object.fromEntries(Object.keys(list[0] ?? {}).map(key => [key, median(list.map(entry => (entry as Record<string, number>)[key]!))]));

test("L: loading a long page cold and warm, and switching between pages", async ({ page, browser }) => {
  await signIn(page);
  const targets = pageIds.length ? pageIds : [(await seed(page)).id, (await seed(page)).id];
  const storageState = await page.context().storageState();
  for (const [index, id] of targets.entries()) {
    const other = targets[(index + 1) % targets.length]!;
    const warmed = await warmUp(browser, storageState, id);
    if (other !== id) await warmUp(browser, storageState, other);
    const cold: LoadMeasurement[] = [], warm: LoadMeasurement[] = [], moves: Awaited<ReturnType<typeof switched>>[] = [];
    for (let i = 0; i < runs; i++) {
      // A new browser profile: the session cookie only, an empty HTTP cache.
      const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 } });
      await context.addInitScript(observe);
      await context.addInitScript(loadObserve);
      const tab = await context.newPage();
      await (await context.newCDPSession(tab)).send("Emulation.setCPUThrottlingRate", { rate: cpu });
      cold.push(await loaded(tab, id));
      warm.push(await loaded(tab, id, `?warm=${i}`));
      if (other !== id) moves.push(await switched(tab, other));
      await context.close();
    }
    const summary = { url: test.info().project.use.baseURL, pageId: id, cpu, runs, ...warmed, loadsThatFellBack: [...cold, ...warm].filter(load => load.fellBack).length, cold: medians(cold), warm: medians(warm), switchTo: other !== id ? { pageId: other, ...medians(moves) } : undefined };
    console.log(JSON.stringify(summary));
    if (process.env.ATLAS_PERF_REPORT) writeFileSync(report(`L-${id}`), JSON.stringify({ summary, cold, warm, moves }, null, 2));
    // The early read of the page is the app's own (?bundle=true), so the page is read once.
    expect.soft(Math.max(...cold.map(load => load.pageReads), ...warm.map(load => load.pageReads)), "reads of the page per load").toBeLessThanOrEqual(loadBudget.pageReads);
  }
});

/** Readers of a live page (3.6-I „Živě“): ATLAS_PERF_READERS (1,5) tabs hold the page live while a writer at full speed types ten
 * characters into a paragraph in the middle. The first reader (at ATLAS_PERF_CPU) is traced: from the writer's key to the reader's
 * text, and what the remote changes cost the reader. Until a read-only participant exists (3.6-I), a reader is the live editor left
 * alone, which receives the same updates. */
test("R: readers of a long live page receive a writer's typing", async ({ page, browser }) => {
  await signIn(page);
  const targets = pageIds.length ? pageIds.slice(0, 1) : [(await seed(page)).id];
  const storageState = await page.context().storageState();
  const readerCounts = (process.env.ATLAS_PERF_READERS ?? "1,5").split(",").map(Number);
  const target = `${LIVE} > p`;
  for (const id of targets) for (const readers of readerCounts) {
    await warmUp(browser, storageState, id);
    const results: Record<string, number>[] = [];
    for (let i = 0; i < Math.max(1, Math.ceil(runs / 2)); i++) {
      const tabs: Awaited<ReturnType<typeof homeTab>>[] = [];
      for (let r = 0; r < readers; r++) { const tab = await homeTab(browser, storageState, r === 0 ? cpu : 1); await open(tab.page, id); tabs.push(tab); }
      const writer = await homeTab(browser, storageState, 1);
      await open(writer.page, id);
      const reader = tabs[0]!.page;
      // The reader notes the moment the paragraph's text changes (wall clock, shared with the writer's tab).
      await reader.evaluate(target => {
        const candidates = document.querySelectorAll(target), at = Math.floor(candidates.length / 2);
        const w = window as unknown as { __seen: number[]; __frames: unknown[] };
        w.__seen = [];
        let length = candidates[at]?.textContent?.length ?? 0;
        new MutationObserver(() => {
          const now = document.querySelectorAll(target)[at]?.textContent?.length ?? 0;
          if (now !== length) { length = now; w.__seen.push(Date.now()); }
        }).observe(candidates[at]!.closest(".ProseMirror")!, { childList: true, characterData: true, subtree: true });
        w.__frames.length = 0;
      }, target);
      await writer.page.evaluate(async target => {
        const candidates = document.querySelectorAll(target), paragraph = candidates[Math.floor(candidates.length / 2)]!;
        paragraph.scrollIntoView({ block: "center" });
        // The caret at the end of its text through the editor: a script's focus gets the editor's own selection back (DocumentEditor selectionOnFocus).
        const editor = (paragraph.closest(".ProseMirror") as HTMLElement & { editor: { view: { posAtDOM(node: Node, offset: number): number }; commands: { setTextSelection(at: number): boolean; focus(at?: null, options?: { scrollIntoView: boolean }): boolean } } }).editor;
        editor.commands.setTextSelection(editor.view.posAtDOM(paragraph, paragraph.childNodes.length));
        editor.commands.focus(null, { scrollIntoView: false });
        await new Promise(resolve => setTimeout(resolve, 500));
        (window as unknown as { __keys: number[] }).__keys.length = 0;
      }, target);
      await browser.startTracing(reader, { categories: ["devtools.timeline", "disabled-by-default-devtools.timeline"] });
      const pressed: number[] = [];
      for (const key of "abcdefghij") { pressed.push(Date.now()); await writer.page.keyboard.type(key); await writer.page.waitForTimeout(150 * cpu); }
      await reader.waitForTimeout(2_000 * cpu);
      const trace = JSON.parse((await browser.stopTracing()).toString()) as { traceEvents: { name: string; ph: string; dur?: number; tid: number }[] };
      const seen = await reader.evaluate(() => (window as unknown as { __seen: number[] }).__seen);
      const frames = await reader.evaluate(() => (window as unknown as { __frames: { d: number; b: number }[] }).__frames);
      const writerKeys = await writer.page.evaluate(() => (window as unknown as { __keys: number[] }).__keys);
      // The reader's main thread is the one with the most style and layout work in its trace.
      const byThread = new Map<number, number>();
      for (const event of trace.traceEvents) if (event.name === "Layout" || event.name === "UpdateLayoutTree") byThread.set(event.tid, (byThread.get(event.tid) ?? 0) + 1);
      const main = [...byThread].sort((a, b) => b[1] - a[1])[0]?.[0];
      const on = (name: string) => trace.traceEvents.filter(event => event.tid === main && event.name === name && event.ph === "X");
      const perKey = (name: string) => Math.round(on(name).reduce((sum, event) => sum + (event.dur ?? 0), 0) / 1000 / 10);
      const latencies = pressed.map(at => (seen.find(time => time >= at) ?? Number.NaN) - at).filter(Number.isFinite);
      results.push({ updatesSeen: seen.length, latencyMs: median(latencies), latencyMaxMs: Math.max(0, ...latencies),
        readerFrameMsPerKey: Math.round(frames.reduce((sum, frame) => sum + frame.d, 0) / 10), readerLongestFrameMs: Math.round(Math.max(0, ...frames.map(frame => frame.d))),
        readerScriptMsPerKey: perKey("FunctionCall"), readerLayoutMsPerKey: perKey("Layout"), readerStyleMsPerKey: perKey("UpdateLayoutTree"),
        readerLayoutsPerKey: on("Layout").length / 10, writerKeyMs: median(writerKeys.length ? writerKeys : [0]) });
      await writer.context.close();
      for (const tab of tabs) await tab.context.close();
    }
    const summary = { url: test.info().project.use.baseURL, pageId: id, cpu, readers, ...medians(results) };
    console.log(JSON.stringify(summary));
    if (process.env.ATLAS_PERF_REPORT) writeFileSync(report(`R-${readers}-${id}`), JSON.stringify({ summary, results }, null, 2));
  }
});

test.afterAll(async ({ browser }) => {
  if (!created.length || process.env.ATLAS_PERF_KEEP === "1") return;
  const page = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  await signIn(page);
  for (const id of created) {
    const { spaceId, version } = await (await page.context().request.get(`/api/pages/${id}`)).json();
    const tree = await (await page.context().request.get(`/api/spaces/${spaceId}/tree`)).json();
    await json(page, "post", `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: version });
  }
  await page.close();
});
