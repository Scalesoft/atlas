import { startEditing as editInPlace, PAGE_TEXT, LIVE_EDITOR } from "./editing";
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";

// 3.6, the server without Node.js: three editors type on one page while an agent's MCP writes land as collaborators' updates, made by
// the server's managed document authority, in the block next to them and in the one they type in; one write is undone as a whole. Every
// editor ends with the same document, nothing typed is lost or doubled, the page opened from the server's seed (no editor seeded it).
// Needs ATLAS_URL (the app), ATLAS_E2E_USER / ATLAS_E2E_PASSWORD (a builtin wiki administrator; the token needs a password re-auth) and
// ATLAS_E2E_ALLOW_COLLAB=1. ATLAS_E2E_MCP_URL is the server's own origin when ATLAS_URL is a dev server that proxies only /api and /hubs.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
const user = process.env.ATLAS_E2E_USER ?? "", password = process.env.ATLAS_E2E_PASSWORD ?? "";
const mcpUrl = `${process.env.ATLAS_E2E_MCP_URL ?? process.env.ATLAS_URL ?? ""}/mcp`;
test.skip(!user || !password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1", "Requires ATLAS_E2E_USER, ATLAS_E2E_PASSWORD and ATLAS_E2E_ALLOW_COLLAB=1.");
const newContext = (browser: Browser) => browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });

async function send(request: APIRequestContext, method: "post" | "put" | "get", path: string, data?: unknown) {
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const response = await request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBeLessThan(300);
  return response.status() === 204 ? null : response.json();
}
async function signIn(context: BrowserContext): Promise<Page> {
  await send(context.request, "post", "/api/auth/login", { username: user, password });
  return context.newPage();
}
/** A token of the signed-in administrator for MCP drafts, after the password re-auth issuing one needs. */
async function mcpToken(request: APIRequestContext): Promise<string> {
  const mcp = await send(request, "get", "/api/admin/mcp");
  if (!mcp.enabled) await send(request, "put", "/api/admin/mcp", { enabled: true, expectedVersion: mcp.version ?? null });
  const me = await send(request, "get", "/api/auth/me");
  const access = await send(request, "get", `/api/admin/users/${me.id}/api-access`);
  if (access.level !== "write" && access.level !== "publish") await send(request, "put", `/api/admin/users/${me.id}/api-access`, { level: "write", expectedVersion: access.version ?? null });
  const challenge = await send(request, "post", "/api/security/reauth", { operation: "security" });
  await send(request, "post", `/api/security/reauth/${challenge.id}/password`, { password });
  return (await send(request, "post", "/api/profile/api-tokens", { name: "Claude Code", scopes: ["read", "write"], channels: ["mcp"], expiresInDays: 1 })).secret;
}
/** One MCP tool call (stateless Streamable HTTP): its structured result, or its error text. */
async function tool(request: APIRequestContext, token: string, name: string, args: Record<string, unknown>) {
  const response = await request.post(mcpUrl, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/json, text/event-stream", "Content-Type": "application/json" },
    data: { jsonrpc: "2.0", id: Date.now(), method: "tools/call", params: { name, arguments: args } },
  });
  const body = await response.text();
  expect(response.status(), body).toBe(200);
  const payload = JSON.parse(body.startsWith("{") ? body : body.split("\n").find(line => line.startsWith("data:"))!.slice(5));
  const result = payload.result as { isError?: boolean; content?: { text?: string }[] };
  return { error: result.isError === true, text: (result.content ?? []).map(part => part.text ?? "").join("\n") };
}
/**
 * An agent's section write as an agent makes it: read the section (MCP), keep its component placeholders ⟦c:N⟧ (a paragraph Markdown
 * cannot carry, e.g. one ending in a typed space, is one), write it; a conflict with someone's edit of that section is read again.
 */
async function writeSection(request: APIRequestContext, token: string, pageId: string, headingId: string, title: string, text: string) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const read = await tool(request, token, "atlas_get_page", { pageId, version: "working" });
    expect(read.error, read.text).toBe(false);
    const page = /<wiki-page [^>]*>\n([\s\S]*?)<\/wiki-page>/.exec(read.text)![1]!;
    const section = page.split(/^(?=## )/m).find(part => part.startsWith(`## ${title}\n`)) ?? "";
    const kept = section.match(/⟦c:\d+⟧/g) ?? [];
    const expectedVersion = Number(/expectedVersion \(pro zápis\): (\d+)/.exec(read.text)![1]);
    const markdown = `## ${title}\n\n${[...kept, text].join("\n\n")}\n`;
    const written = await tool(request, token, "atlas_update_section", { pageId, expectedVersion, headingId, markdown });
    if (!written.error) return;
    expect(written.text, written.text).toContain("target_changed");
  }
  throw new Error(`The agent's write of ${headingId} kept conflicting.`);
}
const heading = (id: string, text: string) => ({ type: "heading", attrs: { level: 2, headingId: id }, content: [{ type: "text", text }] });
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
async function seedPage(request: APIRequestContext) {
  const spaces = await send(request, "get", "/api/spaces") as { id: string }[];
  const content = [heading("a", "Sekce A"), paragraph("Text A."), heading("b", "Sekce B"), paragraph("Text B."), heading("c", "Sekce C"), paragraph("Text C.")];
  return send(request, "post", "/api/pages", { spaceId: spaces[0]!.id, title: `Souběh s agentem ${Date.now().toString(36)}`, parentId: null,
    contentJson: JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 2 }], document: { type: "doc", content } }),
    markdown: null }) as Promise<{ id: string }>;
}
async function open(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  // A wiki just set up greets its administrator with the onboarding first.
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await expect(page.locator(PAGE_TEXT).or(onboarding).first()).toBeVisible();
  if (await onboarding.isVisible()) { await onboarding.click(); await page.goto(`/?opened=1#/pages/${id}`); }
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await editInPlace(page);
}
async function caretAfter(page: Page, text: string) {
  await page.locator(LIVE_EDITOR).locator("p", { hasText: text }).first().click();
  await page.keyboard.press("End");
  await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), LIVE_EDITOR);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
const type = (page: Page, text: string) => page.keyboard.type(text, { delay: 60 });
const documentOf = (page: Page) => page.evaluate(selector =>
  JSON.stringify((document.querySelector(selector) as unknown as { editor?: { getJSON(): unknown } }).editor?.getJSON() ?? null), LIVE_EDITOR);
const textOf = async (page: Page) => (await page.locator(LIVE_EDITOR).innerText()).replace(/\s+/g, " ");
const occurrences = (text: string, word: string) => text.split(word).length - 1;

test("three editors type while an agent writes next to them and into their block; one write undone: all identical, nothing lost or doubled", async ({ browser }) => {
  test.setTimeout(240_000);
  const contexts = [await newContext(browser), await newContext(browser), await newContext(browser)];
  try {
    const [a, b, c] = await Promise.all(contexts.map(signIn));
    const request = contexts[0].request;
    const token = await mcpToken(request);
    const created = await seedPage(request);
    // The server seeded the page: the bundle every editor opens names its live session (no editor seeds it).
    await expect.poll(async () => !!(await send(request, "get", `/api/pages/${created.id}?bundle=true`)).bundle?.collaboration, { timeout: 15_000 }).toBe(true);
    for (const p of [a, b, c]) await open(p, created.id);

    // A and B type in section A, C in section C, while the agent rewrites section B, between them.
    await Promise.all([caretAfter(a, "Text A."), caretAfter(b, "Text A."), caretAfter(c, "Text C.")]);
    await Promise.all([type(a, " alfa1 alfa2"), type(b, " beta1 beta2"), type(c, " gama1 gama2"),
      writeSection(request, token, created.id, "b", "Sekce B", "Agent beta.")]);
    // The agent writes into section C, where C keeps typing (the agent reads again when C's edit came first).
    await caretAfter(c, "gama2");
    await Promise.all([type(c, " gama3"), writeSection(request, token, created.id, "c", "Sekce C", "Agent cé.")]);
    await caretAfter(c, "Agent cé.");
    await type(c, " gama4");

    await expect.poll(async () => new Set(await Promise.all([a, b, c].map(documentOf))).size, { timeout: 30_000 }).toBe(1);
    const text = await textOf(a);
    for (const word of ["alfa1", "alfa2", "beta1", "beta2", "Agent beta.", "Agent cé.", "gama4"]) expect(occurrences(text, word), `${word} in\n${text}`).toBe(1);
    // What C typed in C before the agent's write of C the agent kept or replaced (it read it first); never doubled.
    for (const word of ["gama1", "gama2", "gama3"]) expect(occurrences(text, word), `${word} in\n${text}`).toBeLessThanOrEqual(1);

    // The agent's write of section B, which nobody touched since, undone as a whole: B is back, everything else stays, still live.
    const history = await send(request, "get", `/api/pages/${created.id}/history`) as { items: { id: string; version: number; channel?: string }[] };
    const agentWrites = history.items.filter(item => item.channel === "mcp").sort((x, y) => x.version - y.version);
    expect(agentWrites.length, JSON.stringify(history.items.map(item => [item.version, item.channel]))).toBe(2);
    const current = await send(request, "get", `/api/pages/${created.id}`);
    const undone = await tool(request, token, "atlas_undo_change", { pageId: created.id, revisionId: agentWrites[0]!.id, expectedVersion: current.version });
    expect(undone.error, undone.text).toBe(false);
    await expect.poll(async () => occurrences(await textOf(b), "Agent beta."), { timeout: 20_000 }).toBe(0);
    await expect.poll(async () => new Set(await Promise.all([a, b, c].map(documentOf))).size, { timeout: 30_000 }).toBe(1);
    const after = await textOf(c);
    for (const word of ["Text B.", "alfa1", "alfa2", "beta1", "beta2", "Agent cé.", "gama4"]) expect(occurrences(after, word), `${word} after the undo in\n${after}`).toBe(1);
  } finally { for (const context of contexts) await context.close(); }
});
