// UX review cycle 3, area navigator: journeys of a new employee, a team lead
// with boards and a phone reader. Runs against a local build with „Přihlásit jako vývojář“ (ATLAS_URL); every test makes its own pages and
// boards in the space „UX navigator“ (created when missing) and moves them to the trash at the end.
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
const stamp = () => Date.now().toString(36);

async function signIn(page: Page) {
  await page.goto("/");
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  if (!(await developer.waitFor({ timeout: 15_000 }).then(() => true, () => false))) test.skip(true, "Needs a local build with developer sign-in.");
  await developer.click();
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await Promise.race([onboarding.waitFor().then(() => onboarding.click()).catch(() => {}), page.getByRole("heading", { level: 1, name: "Knihovna" }).waitFor()]);
  await expect(page.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
}
async function send(context: BrowserContext, method: "post" | "put", path: string, data?: unknown) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const response = await context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text()) ? response.json() : undefined;
}
async function navigatorSpace(context: BrowserContext): Promise<string> {
  const spaces: { id: string; name: string }[] = await (await context.request.get("/api/spaces")).json();
  return spaces.find(space => space.name === "UX navigator")?.id ?? (await send(context, "post", "/api/spaces", { name: "UX navigator", icon: "code-xml", color: "blue" })).id;
}
const made: string[] = [];
/** A published page with `paragraphs` paragraphs (a long manual) and optional links to other pages. */
async function createPage(context: BrowserContext, spaceId: string, title: string, paragraphs = 1): Promise<string> {
  const body = Array.from({ length: paragraphs }, (_, index) => ({ type: "paragraph", content: [{ type: "text", text: `${title} odstavec ${index + 1}. Dlouhý text příručky, který se čte shora dolů a vrací se k němu.` }] }));
  const content = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: body } });
  const { id } = await send(context, "post", "/api/pages", { spaceId, parentId: null, title, contentJson: content, markdown: title });
  const current = await (await context.request.get(`/api/pages/${id}`)).json();
  await send(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
  made.push(id);
  return id;
}

test.afterAll(async ({ browser }) => {
  if (!made.length) return;
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const page = await context.newPage();
  await signIn(page);
  for (const id of made.reverse()) {
    const csrf = await (await context.request.get("/api/auth/csrf")).json();
    const headers = { "X-CSRF-TOKEN": csrf.token };
    const current = await context.request.get(`/api/pages/${id}`);
    if (!current.ok()) { await context.request.delete(`/api/boards/${id}`, { headers }); continue; }
    const body = await current.json();
    const tree = await (await context.request.get(`/api/spaces/${body.spaceId}/tree`)).json();
    await context.request.post(`/api/pages/${id}/trash`, { data: { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: body.version }, headers });
  }
  await context.close();
});

test.describe("navigator: reading journeys", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("NV3-1 Back to a long manual returns to where the reader was", async ({ page, context }) => {
    const space = await navigatorSpace(context);
    const long = await createPage(context, space, `UX-navigator manuál ${stamp()}`, 160);
    const other = await createPage(context, space, `UX-navigator jiná ${stamp()}`);
    await page.goto(`/#/pages/${long}`);
    await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollHeight)).toBeGreaterThan(8000);
    await page.waitForTimeout(1200);
    await page.evaluate(() => window.scrollTo(0, 2500));
    await expect.poll(() => page.evaluate(() => Math.round(scrollY))).toBe(2500);
    await page.locator(`.tree-page[data-tree-item="${other}"]`).click();
    await expect(page.getByRole("heading", { level: 1, name: /jiná/ })).toBeVisible();
    await page.goBack();
    await expect(page.getByRole("heading", { level: 1, name: /manuál/ })).toBeVisible();
    await expect.poll(() => page.evaluate(() => Math.round(scrollY)), { timeout: 5000 }).toBeGreaterThan(2400);
  });
});
