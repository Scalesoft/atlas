import { expect, test } from "@playwright/test";
import { startEditing, LIVE_EDITOR } from "./editing";

// Regression: a page whose first collaborative seed carries a block with an object attribute (diagram,
// math, OpenAPI) must open for editing. Editor 0.2.0 handed Yjs null-prototype attribute objects, the
// seeding peer threw on every join and the page stayed read-only ("Obnovujeme spojení").
// Creates one page per block type and trashes it afterwards.
test.use({ trace: "off", screenshot: "off", video: "off" });
const username = process.env.ATLAS_E2E_USER ?? "browser-admin", password = process.env.ATLAS_E2E_PASSWORD;
test.skip(!password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1",
  "Requires an isolated installed acceptance database, ATLAS_E2E_PASSWORD and explicit ATLAS_E2E_ALLOW_COLLAB=1.");

const blocks = {
  diagram: { plugin: { id: "diagrams", version: 2 }, node: { type: "diagram", attrs: { data: { version: 1, source: "flowchart LR\n  A --> B", caption: "Tok" } } } },
  math: { plugin: { id: "math", version: 1 }, node: { type: "mathBlock", attrs: { data: { version: 1, expression: "E = mc^2" } } } },
  openapi: { plugin: { id: "openapi", version: 1 }, node: { type: "openapi", attrs: { data: { version: 1, filename: "api.yaml", mode: "reference",
    source: "openapi: 3.1.0\ninfo:\n  title: Block API\n  version: '1.0'\npaths: {}\n" } } } },
};

test("pages with diagram, math and OpenAPI blocks open for collaborative editing", async ({ page, context }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await page.getByLabel("Heslo", { exact: true }).fill(password!);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  const headers = { "X-CSRF-TOKEN": (await (await context.request.get("/api/auth/csrf")).json()).token };
  const spaces: { id: string; role: string }[] = await (await context.request.get("/api/spaces")).json();
  const space = spaces.find(item => item.role === "Admin" || item.role === "Writer");
  expect(space, "The account must write to a space.").toBeTruthy();
  const created: string[] = [];
  try {
    for (const [kind, { plugin, node }] of Object.entries(blocks)) {
      await test.step(kind, async () => {
        const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [plugin], document: { type: "doc", content: [
          { type: "paragraph", content: [{ type: "text", text: `Úvod ${kind}.` }] }, node] } });
        const response = await context.request.post("/api/pages", { headers, data: {
          spaceId: space!.id, parentId: null, title: `Blok ${kind} ${Date.now()}`, contentJson, markdown: `Úvod ${kind}.` } });
        expect(response.ok()).toBe(true);
        const { id } = await response.json();
        created.push(id);
        // A fresh tab per page.
        const tab = await context.newPage();
        await tab.goto(`/#/pages/${id}`);
        await startEditing(tab);
        const editor = tab.locator(LIVE_EDITOR);
        await editor.getByText(`Úvod ${kind}.`).click();
        await tab.keyboard.press("End");
        await tab.keyboard.type(" Upraveno.");
        await expect(editor).toContainText(`Úvod ${kind}. Upraveno.`);
        await expect(tab.getByText("Úpravy jsou pozastavené")).toHaveCount(0);
        await tab.close();
      });
    }
  } finally {
    for (const id of created) {
      const current = await (await context.request.get(`/api/pages/${id}`)).json();
      const tree = await (await context.request.get(`/api/spaces/${space!.id}/tree`)).json();
      await context.request.post(`/api/pages/${id}/trash`, { headers, data: { expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false } });
    }
  }
});
