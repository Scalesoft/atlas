import { test, expect, type Browser, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { writeFileSync } from "node:fs";

// Interaction budgets of the page-load performance audit, measured on an isolated acceptance wiki
// (tests/browser/README.md): seeds its own space, then times cold and warm loads, page switches and search.
// ATLAS_PERF_REPORT=<file> also writes the raw measurements; ATLAS_PERF_CPU=<n> repeats them with the CPU
// slowed n times (budgets are asserted at 1×, the slowed run only reports).
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });

// Stage 1 levels measured on 2026-09-26 (1× CPU, fast desktop) with headroom against noise; they catch
// regressions. The audit's targets (chapter 1) are lower and arrive with the read-mode renderer.
const budget = {
  // 2026-10-28 (round 2): the shell's first screen on the real wiki, measured on a
  // production build (brotli): 276 KiB with the one icon-pack part it draws, plus the 5 % margin of every bundle budget.
  eagerTransferKiB: 290,
  deepLinkMs: 600,
  switchMs: { "6 KB": 150, "80 KB": 200, "220 KB": 300, "1 MB": 1000 },
  switchBlockingMs: { "6 KB": 50, "80 KB": 80, "220 KB": 200 },
  requestsPerOpen: 7, // pages/{id}, tree, personal-state, visit, tags, connections, relations
  // A cold open of an 80 KB page for reading, whole (2026-10-28 review round 1, VYK1): requests, brotli transfer, largest contentful paint.
  readOpen: { requests: 150, transferKiB: 915, lcpMs: 600 },
};
const sizes = { "6 KB": 6_000, "80 KB": 80_000, "220 KB": 220_000, "1 MB": 1_000_000 } as const;
type Size = keyof typeof sizes;

async function enterSecret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, secret) => { (element as HTMLInputElement).value = secret; element.dispatchEvent(new Event("input", { bubbles: true })); }, value);
}

async function signIn(page: Page) {
  await page.goto("/");
  if (process.env.ATLAS_UX_DEV_LOGIN === "1") await page.getByRole("button", { name: "Přihlásit jako vývojář" }).click();
  else {
    await page.getByLabel("Uživatelské jméno", { exact: true }).fill("browser-admin");
    await enterSecret(page.getByLabel("Heslo", { exact: true }), process.env.ATLAS_E2E_PASSWORD!);
    await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  }
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
}

/** A document of roughly `target` bytes of ContentJson built from the block types pages commonly hold. */
function documentOf(target: number, seed: string) {
  const text = (n: number) => ({ type: "text", text: `${seed} ${"Příliš žluťoučký kůň úpěl ďábelské ódy nad konfigurací serveru. ".repeat(n)}` });
  const paragraph = (n: number) => ({ type: "paragraph", content: [text(n)] });
  const cell = (type: string, n: number) => ({ type, content: [paragraph(n)] });
  const blocks: unknown[] = [];
  for (let i = 0; JSON.stringify(blocks).length < target; i++) {
    switch (i % 5) {
      case 0: blocks.push({ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: `${seed} oddíl ${i}` }] }); break;
      case 1: blocks.push(paragraph(6)); break;
      case 2: blocks.push({ type: "bulletList", content: Array.from({ length: 5 }, () => ({ type: "listItem", content: [paragraph(1)] })) }); break;
      case 3: blocks.push({ type: "codeBlock", attrs: { language: "csharp" }, content: [{ type: "text", text: Array.from({ length: 16 }, (_, line) => `var value${line} = await client.GetAsync("/api/pages/${line}");`).join("\n") }] }); break;
      default: blocks.push({ type: "table", content: Array.from({ length: 6 }, (_, row) => ({ type: "tableRow", content: Array.from({ length: 4 }, () => cell(row === 0 ? "tableHeader" : "tableCell", 1)) })) });
    }
  }
  return JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: blocks } });
}

async function seed(context: BrowserContext) {
  const csrf = (await (await context.request.get("/api/auth/csrf")).json()).token as string;
  const post = async (path: string, data: unknown) => {
    // Seeding hundreds of revisions can meet a checkpoint on a small test database; only the measurements are timed.
    const response = await context.request.post(path, { headers: { "X-CSRF-TOKEN": csrf }, data, timeout: 120_000 });
    expect(response.status(), path).toBe(200);
    return response.json();
  };
  const space = await post("/api/spaces", { name: `Výkon ${Date.now()}` });
  const publish = async (title: string, contentJson: string, parentId: string | null) => {
    // Search indexes the Markdown projection, so it carries the body text the editor would have written.
    const markdown = [title, ...[...contentJson.matchAll(/"text":"([^"]*)"/g)].map(match => match[1])].join("\n\n");
    const created = await post("/api/pages", { spaceId: space.id, parentId, title, contentJson, markdown });
    await post(`/api/pages/${created.id}/publish`, { expectedVersion: created.version });
    return created.id as string;
  };
  const pages = {} as Record<Size, string>;
  for (const size of Object.keys(sizes) as Size[]) pages[size] = await publish(`Stránka ${size}`, documentOf(sizes[size], size), null);
  // A tree of the size of the imported wiki: 12 sections of 20 pages.
  for (let section = 0; section < 12; section++) {
    const parent = await publish(`Oddíl ${section + 1}`, documentOf(2_000, `oddíl ${section}`), null);
    for (let child = 0; child < 20; child++) await publish(`Podstránka ${section + 1}.${child + 1}`, documentOf(1_500, `pod ${section}.${child}`), parent);
  }
  return { space: space.id as string, pages };
}

const observeFrames = () => {
  (window as unknown as { __frames: { s: number; b: number }[] }).__frames = [];
  new PerformanceObserver(list => (window as unknown as { __frames: { s: number; b: number }[] }).__frames.push(
    ...list.getEntries().map(entry => ({ s: entry.startTime, b: (entry as PerformanceEntry & { blockingDuration: number }).blockingDuration })),
  )).observe({ type: "long-animation-frame", buffered: true });
};

async function switchTo(page: Page, id: string) {
  await page.evaluate(() => { location.hash = "#/"; });
  await page.waitForTimeout(600);
  return page.evaluate(async id => {
    const frames = (window as unknown as { __frames: { s: number; b: number }[] }).__frames;
    performance.clearResourceTimings();
    const t0 = performance.now();
    location.hash = `#/pages/${id}`;
    const visible = await new Promise<number>(resolve => {
      const done = () => { const content = document.querySelector(".ProseMirror"); if (location.hash.includes(id) && content && content.textContent!.length > 200) { resolve(performance.now() - t0); return true; } return false; };
      if (done()) return;
      const observer = new MutationObserver(() => { if (done()) observer.disconnect(); });
      observer.observe(document.body, { subtree: true, childList: true });
    });
    await new Promise(resolve => setTimeout(resolve, 1200));
    const blocking = frames.filter(frame => frame.s >= t0).reduce((sum, frame) => sum + frame.b, 0);
    const requests = performance.getEntriesByType("resource").filter(entry => entry.name.includes("/api/")).length;
    return { visibleMs: Math.round(visible), blockingMs: Math.round(blocking), requests };
  }, id);
}

async function measure(browser: Browser, storageState: Awaited<ReturnType<BrowserContext["storageState"]>>, seeded: Awaited<ReturnType<typeof seed>>, cpu: number) {
  const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 } });
  await context.addInitScript(observeFrames);
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send("Network.clearBrowserCache");
  await cdp.send("Emulation.setCPUThrottlingRate", { rate: cpu });
  await page.goto("/", { waitUntil: "load" });
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  const cold = await page.evaluate(() => {
    const assets = performance.getEntriesByType("resource").filter(entry => /\.(js|css)$/.test(entry.name)) as PerformanceResourceTiming[];
    const paint = performance.getEntriesByType("paint").find(entry => entry.name === "first-contentful-paint");
    return { transferKiB: Math.round(assets.reduce((sum, entry) => sum + entry.encodedBodySize, 0) / 1024), files: assets.length, fcpMs: Math.round(paint?.startTime ?? -1) };
  });
  const deep = await context.newPage();
  const deepStart = Date.now();
  await deep.goto(`/?deep#/pages/${seeded.pages["80 KB"]}`, { waitUntil: "commit" });
  await deep.waitForFunction(() => (document.querySelector(".ProseMirror")?.textContent?.length ?? 0) > 200, null, { timeout: 60_000, polling: 50 });
  const deepLinkMs = Date.now() - deepStart;
  await deep.waitForLoadState("networkidle");
  // encodedBodySize, as for the first screen: the headers of the server in use do not count.
  const readOpen = await deep.evaluate(async () => {
    const resources = performance.getEntriesByType("resource") as PerformanceResourceTiming[];
    const lcp = await new Promise<number>(resolve => new PerformanceObserver(list => resolve(list.getEntries().at(-1)?.startTime ?? -1)).observe({ type: "largest-contentful-paint", buffered: true }));
    return { requests: resources.length, transferKiB: Math.round(resources.reduce((sum, entry) => sum + entry.encodedBodySize, 0) / 1024), lcpMs: Math.round(lcp) };
  });
  await deep.close();
  const switches: Record<string, { visibleMs: number; blockingMs: number; requests: number }[]> = {};
  for (let round = 0; round < 3; round++)
    for (const size of Object.keys(sizes) as Size[]) (switches[size] ??= []).push(await switchTo(page, seeded.pages[size]));
  const median = (values: number[]) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)]!;
  const switchSummary = Object.fromEntries(Object.entries(switches).map(([size, runs]) => [size, {
    visibleMs: median(runs.map(run => run.visibleMs)), blockingMs: median(runs.map(run => run.blockingMs)), requests: median(runs.map(run => run.requests)),
  }]));
  await page.evaluate(() => { location.hash = "#/"; });
  await page.getByRole("button", { name: /^Hledat ve wiki/ }).click();
  const box = page.getByRole("combobox", { name: /^Hledat ve wiki/ });
  const searchStart = Date.now();
  await box.pressSequentially("konfigurac", { delay: 60 });
  const typed = Date.now();
  await expect(page.getByRole("listbox", { name: "Výsledky hledání" }).getByRole("option").first()).toBeVisible({ timeout: 10_000 });
  const search = { typingMs: typed - searchStart, resultsAfterLastKeyMs: Date.now() - typed };
  await context.close();
  return { cpu, cold, deepLinkMs, readOpen, switches: switchSummary, search };
}

test("page loads, switches and search stay within the interaction budgets", async ({ browser, page, context }) => {
  test.setTimeout(900_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_PERF !== "1", "Only an installed isolated acceptance wiki.");
  await signIn(page);
  const seeded = await seed(context);
  const storageState = await context.storageState();
  const runs = [await measure(browser, storageState, seeded, 1)];
  const slowed = Number(process.env.ATLAS_PERF_CPU ?? 0);
  if (slowed > 1) runs.push(await measure(browser, storageState, seeded, slowed));
  const report = JSON.stringify({ url: test.info().project.use.baseURL, runs }, null, 2);
  await test.info().attach("performance.json", { body: report, contentType: "application/json" });
  if (process.env.ATLAS_PERF_REPORT) writeFileSync(process.env.ATLAS_PERF_REPORT, report);
  console.log(report);

  const [measured] = runs;
  expect.soft(measured!.cold.transferKiB, "eager transfer").toBeLessThanOrEqual(budget.eagerTransferKiB);
  expect.soft(measured!.deepLinkMs, "deep link to content").toBeLessThanOrEqual(budget.deepLinkMs);
  expect.soft(measured!.readOpen.requests, "requests to open a page for reading").toBeLessThanOrEqual(budget.readOpen.requests);
  expect.soft(measured!.readOpen.transferKiB, "transfer to open a page for reading").toBeLessThanOrEqual(budget.readOpen.transferKiB);
  expect.soft(measured!.readOpen.lcpMs, "largest contentful paint of a page for reading").toBeLessThanOrEqual(budget.readOpen.lcpMs);
  for (const [size, limit] of Object.entries(budget.switchMs)) expect.soft(measured!.switches[size]!.visibleMs, `switch ${size}`).toBeLessThanOrEqual(limit);
  for (const [size, limit] of Object.entries(budget.switchBlockingMs)) expect.soft(measured!.switches[size]!.blockingMs, `blocking ${size}`).toBeLessThanOrEqual(limit);
  for (const size of Object.keys(sizes)) expect.soft(measured!.switches[size]!.requests, `requests ${size}`).toBeLessThanOrEqual(budget.requestsPerOpen);
});
