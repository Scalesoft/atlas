// UX review cycle 4, area AI (findings AI4-n): the assistant, the page summary
// and the AI setup, against a local Development instance with the developer sign-in:
// `ATLAS_URL=http://localhost:<vite> ATLAS_UX_DEV_LOGIN=1 npx playwright test ux-ai`. The capabilities and every model answer are
// routed here, so no model runs and no content reaches one; the run's own page goes to the space „UX ai“ and to the trash at the end.
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { createAiAdminMock } from "../../src/Atlas.Web/src/components/settings/general/ai/aiAdminMock";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
test.describe.configure({ timeout: 120_000 });

async function signIn(page: Page) {
  await page.goto("/");
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  test.skip(!process.env.ATLAS_UX_DEV_LOGIN || !(await developer.waitFor({ timeout: 15_000 }).then(() => true, () => false)), "Needs a Development instance with the developer sign-in.");
  await developer.click();
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await Promise.race([onboarding.waitFor().then(() => onboarding.click()).catch(() => {}), page.getByRole("heading", { level: 1, name: "Knihovna" }).waitFor()]);
  await expect(page.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
}
async function send(context: BrowserContext, method: "post" | "delete", path: string, data?: unknown) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const response = await context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text()) ? response.json() : undefined;
}
const pages: string[] = [];
async function createPage(context: BrowserContext, title: string, text: string): Promise<string> {
  const spaces: { id: string; name: string }[] = await (await context.request.get("/api/spaces")).json();
  const spaceId = spaces.find(space => space.name === "UX ai")?.id ?? (await send(context, "post", "/api/spaces", { name: "UX ai", icon: "code-xml", color: "blue" })).id;
  const content = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text }] }] } });
  const { id } = await send(context, "post", "/api/pages", { spaceId, parentId: null, title, contentJson: content, markdown: text });
  const current = await (await context.request.get(`/api/pages/${id}`)).json();
  await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
  pages.push(id);
  return id;
}
test.afterAll(async ({ browser }) => {
  if (!pages.length) return;
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const page = await context.newPage();
  await signIn(page);
  for (const id of pages.reverse()) {
    const current = await context.request.get(`/api/pages/${id}`);
    if (!current.ok()) continue;
    const body = await current.json();
    const tree = await (await context.request.get(`/api/spaces/${body.spaceId}/tree`)).json();
    await send(context, "post", `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: body.version });
  }
  await context.close();
});

const ALL_ON = { semanticSearch: true, related: true, editorLinks: true, createSuggestions: true, answer: true, assistant: true, editorIntent: false, editorTransform: false, editorOutline: false, publishCheck: false, changeSummary: false, pageSummary: true, tagSuggestions: false, agent: false };
const source = { number: 1, type: "page", pageId: "00000000-0000-0000-0000-000000000001", spaceId: "00000000-0000-0000-0000-000000000002", spaceName: "UX ai", title: "Zdroj", cited: true, changed: false };
const answered = (id: string) => ({ id, conversationId: "c-ux-ai", role: "assistant", state: "answered", sentences: [{ text: "Odpověď ze zdroje.", sources: [1] }], sources: [source], provenance: { location: "company", provider: "gateway", model: "ux" }, createdAt: new Date().toISOString() });
/** The model side of the assistant and the page summary, routed: no model is called. `complete` answers each completion. */
async function routeAi(page: Page, complete: (messageId: string) => { status: number; json: unknown } = id => ({ status: 200, json: answered(id) })) {
  let n = 0;
  await page.route("**/api/ai/capabilities", route => route.fulfill({ json: ALL_ON }));
  const prepared = () => ({ conversationId: "c-ux-ai", messageId: `m-ux-ai-${++n}`, state: "preparing", sources: [source], truncated: false });
  await page.route("**/api/ai/conversations", route => route.request().method() === "POST" ? route.fulfill({ json: prepared() }) : route.fallback());
  await page.route("**/api/ai/conversations/*/messages", route => route.fulfill({ json: prepared() }));
  await page.route("**/api/assistant/summary", route => route.fulfill({ json: prepared() }));
  await page.route("**/api/ai/messages/*/complete", async route => {
    const id = new URL(route.request().url()).pathname.split("/").at(-2)!;
    await new Promise(resolve => setTimeout(resolve, 400));
    const answer = complete(id);
    await route.fulfill({ status: answer.status, json: answer.json });
  });
}
const composer = (page: Page) => page.locator("[data-assistant-panel] [data-assistant-composer] textarea");

test.describe("AI: assistant and page summary", () => {
  test.beforeEach(async ({ page }) => { await routeAi(page); await signIn(page); });

  test("AI4-1 a model that times out (503) leaves the composer and a way to ask again while AI stays available", async ({ page }) => {
    let fail = true;
    await page.unroute("**/api/ai/messages/*/complete");
    await page.route("**/api/ai/messages/*/complete", async route => {
      const id = new URL(route.request().url()).pathname.split("/").at(-2)!;
      if (fail) { fail = false; return route.fulfill({ status: 503, json: { title: "Tahle funkce teď není k dispozici.", status: 503, code: "ai_unavailable" } }); }
      await route.fulfill({ json: answered(id) });
    });
    await page.keyboard.press("Control+j");
    await composer(page).fill("Jak se připojím k VPN?");
    await page.keyboard.press("Enter");
    const answer = page.locator("[data-assistant-panel] [data-ai-answer]").last();
    await expect(answer).toHaveAttribute("data-state", /error|unavailable/);
    // The capability is still on: the panel keeps the composer, and the failed exchange offers its retry.
    await expect(composer(page)).toBeVisible();
    await expect(answer.getByRole("button", { name: "Zkusit znovu" })).toBeVisible();
    await expect(page.locator("body")).not.toBeFocused();
    await answer.getByRole("button", { name: "Zkusit znovu" }).click();
    await expect(answer).toHaveAttribute("data-state", "done");
  });

  test("AI4-2 Ctrl+J puts the caret in the composer, from the library and from a page", async ({ page, context }) => {
    await page.keyboard.press("Control+j");
    await expect(composer(page)).toBeFocused();
    await page.keyboard.press("Escape");
    const id = await createPage(context, `UX-ai Ctrl J ${Date.now().toString(36)}`, "Text stránky pro asistenta.");
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await page.keyboard.press("Control+j");
    await expect(composer(page)).toBeFocused();
  });

  test("AI4-3 Shrnout stránku by keyboard keeps the focus on its control while the summary is written", async ({ page, context }) => {
    const id = await createPage(context, `UX-ai shrnutí ${Date.now().toString(36)}`, "Text stránky ke shrnutí.");
    await page.goto(`/#/pages/${id}`);
    const start = page.locator("[data-page-summary-start]");
    await start.focus();
    await page.keyboard.press("Enter");
    await expect(page.locator("[data-page-summary] [data-ai-answer]")).toHaveAttribute("data-state", /retrieving|preparing/);
    await expect(start).toBeFocused();
    await expect(page.locator("[data-page-summary] [data-ai-answer]")).toHaveAttribute("data-state", "done");
    await expect(start).toBeFocused();
  });
});

test.describe("AI: setup by keyboard", () => {
  // Simulated administration only (the same mock as settings-ai-setup.spec.ts): no provider is contacted.
  async function routeAdmin(page: Page) {
    const mock = createAiAdminMock();
    await page.route("**/api/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      const path = url.pathname;
      if (path === "/api/admin/groups" || path.startsWith("/api/admin/ai")) {
        const body = request.postData() ? JSON.parse(request.postData()!) : undefined;
        const result = mock.handle(request.method(), path.replace(/^\/api/, "") + url.search, body);
        return result.body === undefined ? route.fulfill({ status: result.status }) : route.fulfill({ status: result.status, json: result.body });
      }
      return route.fallback();
    });
    return mock;
  }

  test("AI4-4 saving a setup step lands the focus in the next step, and Vybrat model in the model list", async ({ page }) => {
    await signIn(page);
    await routeAdmin(page);
    await page.goto("/#/settings/general/modules/ai");
    const power = page.locator('[data-ai-setup-step="power"]');
    await power.locator("[data-ai-switch]").focus();
    await page.keyboard.press("Space");
    await power.locator("[data-save-bar-save]").focus();
    await page.keyboard.press("Enter");
    const embeddings = page.locator('[data-ai-setup-step="embeddings"]');
    await expect(embeddings).toBeVisible();
    await expect.poll(() => embeddings.evaluate(step => step.contains(document.activeElement))).toBe(true);
    await embeddings.getByRole("radio", { name: /Firemní LM Studio/ }).click();
    await embeddings.locator("[data-ai-models-load]").focus();
    await page.keyboard.press("Enter");
    await expect(embeddings.getByLabel("Dostupné modely: hledat")).toBeFocused();
  });

  test("AI4-5 the access step without any group leads to creating one", async ({ page }) => {
    await signIn(page);
    const mock = await routeAdmin(page);
    mock.handle("PUT", "/admin/ai/access", { expectedVersion: "a1", groups: [] });
    // A fresh wiki: no group exists yet (registered last, so it wins over the mock's two groups).
    await page.route("**/api/admin/groups", route => route.fulfill({ json: [] }));
    await page.goto("/#/settings/general/modules/ai/access");
    await page.locator("[data-ai-add-group]").first().click();
    await expect(page.locator("[data-ai-no-groups]").getByRole("link", { name: "Nová skupina" })).toBeVisible();
  });
});
