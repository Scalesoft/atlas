import { startEditing as editInPlace, PAGE_TEXT, LIVE_EDITOR } from "./editing";
import { expect, test, type APIRequestContext, type BrowserContext, type Page } from "@playwright/test";

// Campaign 2026-11-05 (opt-in probe, ATLAS_E2E_SOAK=1): the snapshotter's tab goes silent (frozen in the background, a laptop lid)
// while a colleague goes on typing. Measures how long the colleague's edits stay unsaved and how many saves the server answers with
// collaboration_pending meanwhile, once when the colleague stops after a few words and once when they type on past the hub's quiet
// window (MarkdownSyncHub.QuietSnapshotter, 8 s). Logs a JSON line "SILENT {...}"; asserts only that nothing is lost.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
const user = process.env.ATLAS_E2E_USER ?? "", password = process.env.ATLAS_E2E_PASSWORD ?? "";
test.skip(!user || !password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1" || process.env.ATLAS_E2E_SOAK !== "1", "Opt-in probe (ATLAS_E2E_SOAK=1).");

async function send(request: APIRequestContext, method: "post" | "get", path: string, data?: unknown) {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBeLessThan(300);
  return response.json();
}
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
async function open(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await expect(page.locator(PAGE_TEXT).or(onboarding).first()).toBeVisible();
  if (await onboarding.isVisible()) { await onboarding.click(); await page.goto(`/?opened=1#/pages/${id}`); }
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await editInPlace(page);
}
async function caretAtEnd(page: Page, text: string) {
  await page.locator(LIVE_EDITOR).locator("p", { hasText: text }).first().click();
  await page.keyboard.press("End");
  await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), LIVE_EDITOR);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
/**
 * A frozen tab as the server sees it (headless Chrome ignores Page.setWebLifecycleState): while `window.silent` its sockets hold every
 * frame either way (TCP keeps them; the tab's own sends wait as its timers would) and no new socket connects, so it neither snapshots
 * nor answers pings; `window.resume()` delivers what waited, in order, as a thawed tab gets it.
 */
function silenceableWebSocket() {
  const Native = window.WebSocket;
  const w = window as unknown as { silent: boolean; resume(): void };
  const held: (() => void)[] = [];
  w.silent = false;
  w.resume = () => { w.silent = false; for (const run of held.splice(0)) run(); };
  class Silenceable extends Native {
    constructor(url: string | URL, protocols?: string | string[]) {
      if (w.silent) throw new DOMException("offline", "NetworkError");
      super(url, protocols);
      const nativeSend = Native.prototype.send.bind(this);
      this.send = data => { if (w.silent) held.push(() => { if (this.readyState === Native.OPEN) nativeSend(data); }); else nativeSend(data); };
      this.addEventListener("message", event => {
        if (!w.silent || (event as MessageEvent & { thawed?: boolean }).thawed) return;
        event.stopImmediatePropagation();
        held.push(() => { const later = new MessageEvent("message", { data: event.data }) as MessageEvent & { thawed?: boolean }; later.thawed = true; this.dispatchEvent(later); });
      });
    }
  }
  window.WebSocket = Silenceable as typeof WebSocket;
}
const saveState = (page: Page) => page.locator("[data-page-save-state]").getAttribute("data-save-state");

for (const typeOnSeconds of [0, 14]) {
  test(`a frozen snapshotter: the colleague types ${typeOnSeconds ? `on for ${typeOnSeconds} s` : "a few words and stops"}`, async ({ browser }) => {
    test.setTimeout(180_000);
    const contexts: BrowserContext[] = [];
    try {
      const pages: Page[] = [];
      for (let i = 0; i < 2; i++) {
        const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
        await context.addInitScript(silenceableWebSocket);
        contexts.push(context);
        await send(context.request, "post", "/api/auth/login", { username: user, password });
        pages.push(await context.newPage());
      }
      const [a, b] = pages as [Page, Page];
      const spaces = await send(a.context().request, "get", "/api/spaces") as { id: string }[];
      const created = await send(a.context().request, "post", "/api/pages", { spaceId: spaces[0]!.id, title: `Zamrzlý ${Date.now().toString(36)}`, parentId: null,
        contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [paragraph("Alfa."), paragraph("Beta.")] } }), markdown: null }) as { id: string };
      // A opens first and writes: it holds the snapshotter role (a writer that has written keeps it against a newcomer's first write).
      await open(a, created.id);
      await caretAtEnd(a, "Alfa.");
      await a.keyboard.type(" a1", { delay: 80 });
      await expect.poll(() => saveState(a), { timeout: 30_000 }).toBe("saved");
      await open(b, created.id);
      const puts: { at: number; status: number; code?: string }[] = [];
      b.on("response", async response => {
        if (response.request().method() !== "PUT" || !new URL(response.url()).pathname.startsWith(`/api/pages/${created.id}`)) return;
        let code: string | undefined;
        try { code = (await response.json())?.code; } catch { /* no body */ }
        puts.push({ at: Date.now(), status: response.status(), code });
      });
      // A was typing when its tab froze: its last update is fresh, so the hub's quiet window has not started for B's first write.
      await a.keyboard.type(" a2", { delay: 80 });
      await a.evaluate(() => { (window as unknown as { silent: boolean }).silent = true; });
      const frozenAt = Date.now();

      await caretAtEnd(b, "Beta.");
      const words: string[] = [];
      for (let n = 1; n === 1 || Date.now() - frozenAt < typeOnSeconds * 1000; n++) {
        const word = `b${n}`;
        await b.keyboard.type(` ${word}`, { delay: 90 });
        words.push(word);
        await new Promise(done => setTimeout(done, 600));
      }
      const stoppedAt = Date.now();
      let savedAt: number | null = null, errorShown = false;
      while (Date.now() - stoppedAt < 75_000) {
        if (await saveState(b) === "saved") { savedAt = Date.now(); break; }
        errorShown ||= await b.locator("[data-page-save-state]").getAttribute("data-save-state") === "error";
        await new Promise(done => setTimeout(done, 250));
      }
      const pending = puts.filter(put => put.status === 409 && put.code === "collaboration_pending").length;
      console.log(`SILENT ${JSON.stringify({ typeOnSeconds, words: words.length, unsavedAfterLastKeyMs: savedAt ? savedAt - stoppedAt : null,
        puts: puts.length, pending409: pending, other: puts.filter(put => put.status >= 300 && put.code !== "collaboration_pending").map(put => `${put.status} ${put.code}`),
        errorShown })}`);

      await a.evaluate(() => { (window as unknown as { resume(): void }).resume(); });
      const text = await b.locator(PAGE_TEXT).innerText();
      for (const word of words) expect(text, word).toContain(word);
      await expect.poll(async () => (await a.locator(PAGE_TEXT).innerText()).includes(words.at(-1)!), { timeout: 60_000 }).toBe(true);
      expect(savedAt, "B's edits saved within 75 s after the last key").not.toBeNull();
      // 3.6: the server stores the page itself at the latest 30 s after its first unsaved update and tells B, whose indicator follows
      // at once and never shows an error meanwhile.
      expect(savedAt! - stoppedAt, "B saved within the server's checkpoint interval").toBeLessThan(35_000);
      expect(errorShown, "B's indicator showed no error while waiting").toBe(false);
    } finally { for (const context of contexts) await context.close(); }
  });
}
