import { startEditing as editInPlace, PAGE_TEXT, LIVE_EDITOR } from "./editing";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// Collaboration soak (campaign 2026-11-05, opt-in): three writers type at a human cadence for ATLAS_E2E_SOAK_SECONDS (default 120) over
// WebSockets delayed like a real network (FIFO, ~RTT ATLAS_E2E_SOAK_RTT ms with jitter: CDP network emulation does not delay WebSocket
// frames), while an agent rewrites another section over MCP every ~10 s. Two writers share a paragraph (one at its start, one at its
// end), the third types in its own one, with typos corrected by Backspace. Watched: the editor turning read-only (a dropped key),
// caret jumps, console errors; at the end every window shows the same document, every typed token is there once, and the stored page
// after a reload holds them. Writes a JSON report to ATLAS_E2E_SOAK_REPORT. Needs ATLAS_E2E_SOAK=1 besides the collab variables.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
const user = process.env.ATLAS_E2E_USER ?? "", password = process.env.ATLAS_E2E_PASSWORD ?? "";
const mcpUrl = `${process.env.ATLAS_E2E_MCP_URL ?? process.env.ATLAS_URL ?? ""}/mcp`;
const seconds = Number(process.env.ATLAS_E2E_SOAK_SECONDS ?? 120), rtt = Number(process.env.ATLAS_E2E_SOAK_RTT ?? 110);
test.skip(!user || !password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1" || process.env.ATLAS_E2E_SOAK !== "1",
  "Opt-in soak: ATLAS_E2E_SOAK=1 with ATLAS_E2E_USER, ATLAS_E2E_PASSWORD and ATLAS_E2E_ALLOW_COLLAB=1.");

async function send(request: APIRequestContext, method: "post" | "put" | "get", path: string, data?: unknown) {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBeLessThan(300);
  return response.status() === 204 ? null : response.json();
}
async function mcpToken(request: APIRequestContext): Promise<string> {
  const mcp = await send(request, "get", "/api/admin/mcp");
  if (!mcp.enabled) await send(request, "put", "/api/admin/mcp", { enabled: true, expectedVersion: mcp.version ?? null });
  const me = await send(request, "get", "/api/auth/me");
  const access = await send(request, "get", `/api/admin/users/${me.id}/api-access`);
  if (access.level !== "write" && access.level !== "publish") await send(request, "put", `/api/admin/users/${me.id}/api-access`, { level: "write", expectedVersion: access.version ?? null });
  const challenge = await send(request, "post", "/api/security/reauth", { operation: "security" });
  await send(request, "post", `/api/security/reauth/${challenge.id}/password`, { password });
  return (await send(request, "post", "/api/profile/api-tokens", { name: "Soak", scopes: ["read", "write"], channels: ["mcp"], expiresInDays: 1 })).secret;
}
async function tool(request: APIRequestContext, token: string, name: string, args: Record<string, unknown>) {
  const response = await request.post(mcpUrl, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/json, text/event-stream", "Content-Type": "application/json" },
    data: { jsonrpc: "2.0", id: Date.now(), method: "tools/call", params: { name, arguments: args } },
  }).catch((error: Error) => error); // the server restarting (ATLAS_E2E_SOAK_RESTART): the agent's write fails and the next one goes on
  if (response instanceof Error) return { error: true, text: response.message.split("\n")[0]! };
  const body = await response.text();
  if (response.status() !== 200) return { error: true, text: `${response.status()} ${body}` };
  const payload = JSON.parse(body.startsWith("{") ? body : body.split("\n").find(line => line.startsWith("data:"))!.slice(5));
  const result = payload.result as { isError?: boolean; content?: { text?: string }[] };
  return { error: result.isError === true, text: (result.content ?? []).map(part => part.text ?? "").join("\n") };
}
/** The agent's rewrite of section B: read, write; a conflict is read again. Returns the attempts it took, or an error text. */
async function writeSection(request: APIRequestContext, token: string, pageId: string, text: string) {
  for (let attempt = 1; attempt <= 8; attempt++) {
    const read = await tool(request, token, "atlas_get_page", { pageId, version: "working" });
    if (read.error) return read.text;
    const expectedVersion = Number(/expectedVersion \(pro zápis\): (\d+)/.exec(read.text)![1]);
    const written = await tool(request, token, "atlas_update_section", { pageId, expectedVersion, headingId: "b", markdown: `## Sekce B\n\n${text}\n` });
    if (!written.error) return attempt;
    if (!written.text.includes("target_changed")) return written.text;
  }
  return "kept conflicting";
}
const heading = (id: string, text: string) => ({ type: "heading", attrs: { level: 2, headingId: id }, content: [{ type: "text", text }] });
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });

/** A WebSocket whose frames each way wait ~rtt/2 with jitter, in order (installed before the app's scripts). */
function delayedWebSocket(oneWay: number) {
  const Native = window.WebSocket;
  class Delayed extends Native {
    constructor(url: string | URL, protocols?: string | string[]) {
      super(url, protocols);
      let sendDue = 0, receiveDue = 0;
      const due = (last: number) => Math.max(performance.now() + oneWay * (0.6 + Math.random() * 0.8), last);
      const nativeSend = Native.prototype.send.bind(this);
      this.send = data => { sendDue = due(sendDue); setTimeout(() => { if (this.readyState === Native.OPEN) nativeSend(data); }, sendDue - performance.now()); };
      this.addEventListener("message", event => {
        if ((event as MessageEvent & { delayed?: boolean }).delayed) return;
        event.stopImmediatePropagation();
        receiveDue = due(receiveDue);
        setTimeout(() => { const later = new MessageEvent("message", { data: event.data }) as MessageEvent & { delayed?: boolean }; later.delayed = true; this.dispatchEvent(later); },
          receiveDue - performance.now());
      });
    }
  }
  window.WebSocket = Delayed as typeof WebSocket;
}
/** Records each time the editor stops taking input and every console error, from the page's first frame. */
function watchEditor() {
  const w = window as unknown as { soak: { readonly: number[]; writable: number[] } };
  w.soak = { readonly: [], writable: [] };
  new MutationObserver(records => {
    for (const record of records) {
      const target = record.target as HTMLElement;
      if (!target.classList?.contains("ProseMirror") || !target.closest(".document-body") || target.closest(".editor-hydrating")) continue;
      const now = target.getAttribute("contenteditable");
      if (record.oldValue === "true" && now === "false") w.soak.readonly.push(Date.now());
      if (record.oldValue === "false" && now === "true") w.soak.writable.push(Date.now());
    }
  }).observe(document, { subtree: true, attributes: true, attributeFilter: ["contenteditable"], attributeOldValue: true });
}

const documentOf = (page: Page) => page.evaluate(selector =>
  JSON.stringify((document.querySelector(selector) as unknown as { editor?: { getJSON(): unknown } }).editor?.getJSON() ?? null), LIVE_EDITOR);
const textOf = (page: Page) => page.evaluate(selector => {
  const doc = (document.querySelector(selector) as unknown as { editor: { state: { doc: { textBetween(a: number, b: number, s: string): string; content: { size: number } } } } }).editor.state.doc;
  return doc.textBetween(0, doc.content.size, "\n");
}, LIVE_EDITOR);
/** The text just before the caret (ProseMirror's selection), to tell a caret that jumped from one that stayed behind the typed token. */
const beforeCaret = (page: Page, length: number) => page.evaluate(([selector, n]) => {
  const { state } = (document.querySelector(selector as string) as unknown as { editor: { state: { selection: { from: number }; doc: { textBetween(a: number, b: number, s: string): string } } } }).editor;
  return state.doc.textBetween(Math.max(0, state.selection.from - (n as number)), state.selection.from, "\n");
}, [LIVE_EDITOR, length] as const);
const occurrences = (text: string, word: string) => text.split(word).length - 1;

test("soak: three writers at a human cadence over a delayed network while an agent writes every ~10 s", async ({ browser }) => {
  test.setTimeout((seconds + 240) * 1000);
  const contexts: BrowserContext[] = [];
  const newContext = async (browser: Browser) => {
    const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
    await context.addInitScript(delayedWebSocket, rtt / 2);
    await context.addInitScript(watchEditor);
    contexts.push(context);
    return context;
  };
  const report: Record<string, unknown> = { seconds, rtt };
  try {
    const pages: Page[] = [];
    const consoleErrors: string[][] = [[], [], []];
    const failedRequests: Record<string, number>[] = [{}, {}, {}];
    report.failedRequests = failedRequests;
    for (let i = 0; i < 3; i++) {
      const context = await newContext(browser);
      await send(context.request, "post", "/api/auth/login", { username: user, password });
      const page = await context.newPage();
      page.on("console", message => { if (message.type() === "error") consoleErrors[i]!.push(message.text().slice(0, 300)); });
      page.on("pageerror", error => consoleErrors[i]!.push(`pageerror ${String(error).slice(0, 300)}`));
      page.on("response", async response => {
        if (response.status() < 400) return;
        let code = "";
        try { code = (await response.json())?.code ?? ""; } catch { /* no JSON body */ }
        const key = `${response.request().method()} ${new URL(response.url()).pathname.replace(/[0-9a-f-]{36}/g, "{id}")} ${response.status()} ${code}`;
        failedRequests[i]![key] = (failedRequests[i]![key] ?? 0) + 1;
      });
      pages.push(page);
    }
    const request = contexts[0]!.request;
    const token = await mcpToken(request);
    const spaces = await send(request, "get", "/api/spaces") as { id: string }[];
    const content = [heading("a", "Sekce A"), paragraph("Společný."), heading("b", "Sekce B"), paragraph("Agent 0."), heading("c", "Sekce C"), paragraph("Vlastní.")];
    const created = await send(request, "post", "/api/pages", { spaceId: spaces[0]!.id, title: `Soak ${Date.now().toString(36)}`, parentId: null,
      contentJson: JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 2 }], document: { type: "doc", content } }),
      markdown: null }) as { id: string };
    report.pageId = created.id;
    for (const page of pages) {
      await page.goto(`/#/pages/${created.id}`);
      const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
      await expect(page.locator(PAGE_TEXT).or(onboarding).first()).toBeVisible();
      if (await onboarding.isVisible()) { await onboarding.click(); await page.goto(`/?opened=1#/pages/${created.id}`); }
      await expect(page.locator(PAGE_TEXT)).toBeVisible();
      await editInPlace(page);
    }

    // A at the start of the shared paragraph, B at its end, C at the end of its own.
    const place = async (page: Page, text: string, key: "Home" | "End") => {
      await page.locator(LIVE_EDITOR).locator("p", { hasText: text }).first().click();
      await page.keyboard.press(key);
      await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), LIVE_EDITOR);
      await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
    };
    await place(pages[0]!, "Společný.", "Home");
    await place(pages[1]!, "Společný.", "End");
    await place(pages[2]!, "Vlastní.", "End");

    const deadline = Date.now() + seconds * 1000;
    const typed: string[][] = [[], [], []];
    const caretJumps: string[] = [];
    let seed = 20261105;
    const random = () => { seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; };
    const pause = (ms: number) => new Promise(done => setTimeout(done, ms));
    const spans: { token: string; from: number; to: number }[][] = [[], [], []];
    const waitedForLock: number[] = [0, 0, 0];
    const editable = (page: Page) => page.evaluate(selector => !!document.querySelector(selector), LIVE_EDITOR);
    const writer = async (index: number) => {
      const page = pages[index]!, letter = "abc"[index]!;
      for (let n = 1; Date.now() < deadline; n++) {
        // Locked (offline, a server restart): a person waits for the lock to go and puts the caret back where they were.
        if (!await editable(page)) {
          const since = Date.now();
          await expect.poll(() => editable(page), { timeout: 120_000, intervals: [250] }).toBe(true);
          waitedForLock[index]! += Date.now() - since;
          await place(page, index === 2 ? "Vlastní." : "Společný.", index === 0 ? "Home" : "End");
          if (index === 0) await page.keyboard.press("Home");
        }
        // A at the start types "token " (its caret stays after it), B and C type " token" after what is there.
        const token = `${letter}${String(n).padStart(3, "0")}`;
        const keys = index === 0 ? `${token} ` : ` ${token}`;
        const from = Date.now();
        for (const key of keys) {
          if (random() < 0.04) { await page.keyboard.type("q"); await pause(90 + random() * 120); await page.keyboard.press("Backspace"); }
          await page.keyboard.type(key);
          await pause(70 + random() * 150);
        }
        typed[index]!.push(token);
        spans[index]!.push({ token, from, to: Date.now() });
        const expected = index === 0 ? `${token} ` : ` ${token}`;
        const before = await beforeCaret(page, expected.length);
        if (before !== expected) caretJumps.push(`${token}: caret after "${before}"`);
        await pause(250 + random() * 900);
      }
    };
    const agentWrites: unknown[] = [];
    const agent = async () => {
      for (let n = 1; Date.now() < deadline - 5000; n++) {
        await pause(8000 + random() * 4000);
        const started = Date.now();
        agentWrites.push({ n, result: await writeSection(request, token, created.id, `Agent ${n}.`), ms: Date.now() - started });
      }
    };
    await Promise.all([writer(0), writer(1), writer(2), agent()]);
    const typingEnded = Date.now();
    // Revisions stored while the soak typed (the page's version counts them): the agent's (mcp) and people's checkpoints (web: the
    // browsers', the server's own and the one before each agent write).
    report.revisionsWhileTyping = ((await send(request, "get", `/api/pages/${created.id}`)) as { version: number }).version - 1;
    report.typed = typed.map(tokens => tokens.length);
    report.agentWrites = agentWrites;
    report.caretJumps = caretJumps;

    // Converged: every window shows the same document.
    const settled = Date.now();
    await expect.poll(async () => new Set(await Promise.all(pages.map(documentOf))).size, { timeout: 60_000 }).toBe(1);
    report.convergedAfterMs = Date.now() - settled;
    const text = await textOf(pages[0]!);
    // From the last keystroke until the stored working revision holds every token on the screens (with the windows still open).
    const onScreen = typed.flat().filter(token => occurrences(text, token) === 1);
    await expect.poll(async () => {
      const stored = await send(request, "get", `/api/pages/${created.id}`) as { markdown?: string; contentJson?: string };
      return onScreen.filter(token => !(stored.markdown ?? stored.contentJson ?? "").includes(token)).length;
    }, { timeout: 90_000, intervals: [500] }).toBe(0);
    report.storedAfterLastKeystrokeMs = Date.now() - typingEnded;
    const flips = await Promise.all(pages.map(page => page.evaluate(() => (window as unknown as { soak: { readonly: number[]; writable: number[] } }).soak)));
    report.readonlyFlips = flips.map(flip => flip.readonly.length);
    // A token whose keys were typed while its editor was locked lost them by design (the lock shows it): counted apart from real losses.
    const locked = (index: number, span: { from: number; to: number }) => flips[index]!.readonly.some(start => {
      const end = flips[index]!.writable.find(at => at >= start) ?? Infinity;
      return start <= span.to + 500 && end >= span.from - 500;
    });
    const missing = spans.flatMap((list, index) => list.filter(span => occurrences(text, span.token) !== 1).map(span => ({ ...span, index })));
    const lost = missing.filter(span => !locked(span.index, span)).map(span => span.token);
    report.lostOrDoubled = lost;
    report.droppedWhileLocked = missing.filter(span => locked(span.index, span)).map(span => span.token);
    report.waitedForLockMs = waitedForLock;
    // The browser logs each failed request as a console error; those are counted in failedRequests.
    report.consoleErrors = consoleErrors.map(list => list.filter(line => !line.startsWith("Failed to load resource")));
    const lastAgent = agentWrites.filter(write => typeof (write as { result: unknown }).result === "number").length;
    report.agentTextShown = text.includes(`Agent ${(agentWrites.at(-1) as { n?: number } | undefined)?.n ?? 0}.`);

    // Saved: the page's save state settles in every window, then a reload shows the same text and the stored working copy has it all.
    for (const page of pages) await expect(page.locator("[data-page-save-state]")).toHaveAttribute("data-save-state", "saved", { timeout: 60_000 });
    await pages[2]!.reload();
    await expect(pages[2]!.locator(PAGE_TEXT)).toBeVisible();
    await expect.poll(async () => (await pages[2]!.locator(PAGE_TEXT).innerText()).length > 0).toBe(true);
    const reloaded = await pages[2]!.locator(PAGE_TEXT).innerText();
    const shown = typed.flat().filter(token => occurrences(text, token) === 1);
    report.lostAfterReload = shown.filter(token => occurrences(reloaded, token) !== 1);
    const stored = await send(request, "get", `/api/pages/${created.id}`) as { markdown?: string; contentJson?: string };
    const storedText = stored.markdown ?? stored.contentJson ?? "";
    report.lostInStored = shown.filter(token => occurrences(storedText, token) !== 1);
    report.agentWritesApplied = lastAgent;
    console.log(`SOAK ${JSON.stringify(report)}`);
    if (process.env.ATLAS_E2E_SOAK_REPORT) (await import("node:fs")).writeFileSync(process.env.ATLAS_E2E_SOAK_REPORT, JSON.stringify(report, null, 2));

    expect(lost, text).toEqual([]);
    expect(report.lostAfterReload).toEqual([]);
    expect(report.lostInStored).toEqual([]);
    if (process.env.ATLAS_E2E_SOAK_RESTART !== "1") expect(report.readonlyFlips).toEqual([0, 0, 0]);
    expect(caretJumps).toEqual([]);
  } finally {
    if (process.env.ATLAS_E2E_SOAK_REPORT) (await import("node:fs")).writeFileSync(`${process.env.ATLAS_E2E_SOAK_REPORT}.partial`, JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
  }
});
