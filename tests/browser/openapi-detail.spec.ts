import { expect, test, type Page } from "@playwright/test";
import { startEditing } from "./editing";

// The OpenAPI block's detail (3.6): the block is a card of its own height in the page, and its documentation and YAML open
// in a full-height dialog over the page. Only the dialog's content scrolls (the Scalar frame, the source editor); the page
// behind stays where it was, also when a wheel runs past the end of the documentation. Esc leaves the source editor, then
// closes, and the block is selected again. Creates one page and trashes it afterwards.
test.use({ trace: "off", screenshot: "off", video: "off" });
const username = process.env.ATLAS_E2E_USER ?? "browser-admin", password = process.env.ATLAS_E2E_PASSWORD;
test.skip(!password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1",
  "Requires an isolated installed acceptance database, ATLAS_E2E_PASSWORD and explicit ATLAS_E2E_ALLOW_COLLAB=1.");

const apiTitle = "Detail Billing API";
const paths = Array.from({ length: 30 }, (_, index) => `  /invoices/${index}:\n    get:\n      summary: Faktura ${index}\n      responses:\n        '200':\n          description: OK\n`).join("");
const apiSource = `openapi: 3.1.0\ninfo:\n  title: ${apiTitle}\n  version: '1.0'\npaths:\n${paths}`;
const filler = Array.from({ length: 40 }, (_, index) => ({ type: "paragraph", content: [{ type: "text", text: `Odstavec ${index + 1} před a za blokem API.` }] }));
const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }], document: { type: "doc", content: [
  ...filler.slice(0, 20), { type: "openapi", attrs: { data: { version: 1, filename: "billing.yaml", source: apiSource, mode: "reference" } } }, ...filler.slice(20),
] } });

/** Elements inside the open dialog that scroll on their own (the Scalar frame scrolls its own document, so it is not one). */
const nestedScrollers = (page: Page) => page.evaluate(() => [...document.querySelectorAll(".openapi-detail-dialog[open], .openapi-detail-dialog[open] *")]
  .filter(element => /(auto|scroll)/.test(getComputedStyle(element).overflowY) && element.scrollHeight > element.clientHeight + 1 && !element.closest(".monaco-editor"))
  .map(element => element.className));
const pageTop = (page: Page) => page.evaluate(() => document.scrollingElement!.scrollTop);

test("the OpenAPI detail opens in a dialog with one scrolling area and gives the keyboard back to the block", async ({ page, context }) => {
  test.setTimeout(150_000);
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await page.getByLabel("Heslo", { exact: true }).fill(password!);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  const headers = { "X-CSRF-TOKEN": (await (await context.request.get("/api/auth/csrf")).json()).token };
  const spaces: { id: string; role: string }[] = await (await context.request.get("/api/spaces")).json();
  const space = spaces.find(item => item.role === "Admin" || item.role === "Writer");
  expect(space, "The account must write to a space.").toBeTruthy();
  const response = await context.request.post("/api/pages", { headers, data: {
    spaceId: space!.id, parentId: null, title: `API detail ${Date.now()}`, contentJson, markdown: apiTitle } });
  expect(response.ok()).toBe(true);
  const { id } = await response.json();
  try {
    await page.goto(`/#/pages/${id}`);
    await startEditing(page);
    const card = page.locator(".md-openapi-card");
    await card.scrollIntoViewIfNeeded();
    // The card has its natural height and no scrolling area of its own.
    expect(await card.evaluate(element => [element, ...element.querySelectorAll("*")].some(item => /(auto|scroll)/.test(getComputedStyle(item).overflowY)))).toBe(false);
    const before = await pageTop(page);

    await card.locator(".md-btn-primary").click();
    const dialog = page.getByRole("dialog", { name: apiTitle });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("tab")).toHaveText(["Dokumentace", "YAML"]);
    const frame = dialog.getByTestId("openapi-detail-reference").locator("iframe");
    await expect(frame.contentFrame().locator("body")).toContainText(apiTitle, { timeout: 30_000 });
    // Full height of the window; nothing in it but the frame scrolls, and the page behind never moves.
    expect((await dialog.boundingBox())!.height).toBeGreaterThanOrEqual(page.viewportSize()!.height - 1);
    expect(await nestedScrollers(page)).toEqual([]);
    const box = (await frame.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, 100_000);
    await page.mouse.move(8, box.y + box.height / 2);
    await page.mouse.wheel(0, 2_000);
    await expect.poll(() => pageTop(page)).toBe(before);

    await dialog.getByRole("tab", { name: "YAML", exact: true }).click();
    const monaco = dialog.getByTestId("openapi-detail-source").locator(".monaco-editor");
    await expect(monaco.locator(".view-lines")).toContainText("openapi", { timeout: 30_000 });
    expect(await nestedScrollers(page)).toEqual([]);
    await monaco.locator(".view-lines").click();
    // Esc leaves the source editor for its tab, the next Esc closes the dialog and selects the block again.
    await page.keyboard.press("Escape");
    await expect(dialog.getByRole("tab", { name: "YAML", exact: true })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(page.locator(".ProseMirror-selectednode .md-openapi-card")).toHaveCount(1);
    await expect(page.locator(".ProseMirror").first()).toBeFocused();
    expect(await pageTop(page)).toBe(before);

    // Enter on the selected block opens its source (the editor's way into a block) in the same dialog.
    await page.keyboard.press("Enter");
    await expect(dialog.getByRole("tab", { name: "YAML", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.keyboard.press("Escape");
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();

    // A phone: the dialog takes the whole screen.
    await page.setViewportSize({ width: 390, height: 844 });
    await card.locator(".md-btn-primary").click();
    await expect(dialog).toBeVisible();
    const phone = (await dialog.boundingBox())!;
    expect(Math.round(phone.width)).toBe(390);
    expect(Math.round(phone.height)).toBe(844);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
  } finally {
    const current = await (await context.request.get(`/api/pages/${id}`)).json();
    const tree = await (await context.request.get(`/api/spaces/${space!.id}/tree`)).json();
    await context.request.post(`/api/pages/${id}/trash`, { headers, data: { expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false } });
  }
});
