import { test, expect, type BrowserContext, type Locator } from "@playwright/test";
import { startEditing, PAGE_TEXT, LIVE_EDITOR } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
async function secret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((el, password) => { (el as HTMLInputElement).value = password; el.dispatchEvent(new Event("input", { bubbles: true })); }, value);
}
async function login(context: BrowserContext, username: string, password: string) {
  const page = await context.newPage(); page.on("dialog", dialog => dialog.accept());
  await page.goto("/"); await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await secret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  return page;
}
async function mutate(context: BrowserContext, path: string, data: unknown, method: "post" | "put" = "post") {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  return context.request[method](path, { data, headers: { "X-CSRF-TOKEN": csrf.token } });
}

// 3.6-I Živě (O12): a reader of a live page follows the writers' typing without a reload, also across a new generation of the live
// document, and cannot type into it.
test("a reader of a live page sees a writer's typing within seconds and cannot type", async ({ browser }) => {
  test.setTimeout(120_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== "1", "Only an installed isolated acceptance wiki with browser-admin and browser-reader.");
  const password = process.env.ATLAS_E2E_PASSWORD!;
  const admin = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  const reader = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const a = await login(admin, "browser-admin", password);
    const r = await login(reader, "browser-reader", process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`);
    const readerId = (await (await reader.request.get("/api/auth/me")).json()).id;
    const space = await mutate(admin, "/api/spaces", { name: `Živě ${Date.now()}`, icon: "code-xml", color: "blue" });
    expect(space.status()).toBe(200); const spaceId = (await space.json()).id;
    expect((await mutate(admin, `/api/spaces/${spaceId}/access`, { userId: readerId, role: "Reader", expectedVersion: null }, "put")).status()).toBe(204);
    const seed = "Výchozí text živé stránky.";
    const created = await mutate(admin, "/api/pages", { spaceId, parentId: null, title: "Živá stránka", contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: seed }] }] } }), markdown: seed });
    expect(created.status()).toBe(200); const page = await created.json(), id = page.id;
    expect((await mutate(admin, `/api/pages/${id}/publish`, { expectedVersion: page.version })).status()).toBe(200);
    expect((await mutate(admin, `/api/pages/${id}/publication`, { mode: "Live" }, "put")).status()).toBe(200);
    await a.reload(); await r.reload();

    await r.goto(`/#/pages/${id}`);
    const readerText = r.locator(PAGE_TEXT);
    await expect(readerText).toContainText(seed);
    await r.waitForTimeout(1_500);
    // A save outside the live session (as a restore or a stale live state reset on open) starts a new generation under the open reader.
    const doc = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: seed + " Upraveno." }] }] } });
    expect((await mutate(admin, `/api/pages/${id}`, { title: "Živá stránka", contentJson: doc, markdown: seed + " Upraveno.", expectedVersion: 1 }, "put")).status()).toBe(200);
    await a.goto(`/#/pages/${id}`);
    await expect(a.locator(PAGE_TEXT)).toContainText(seed);
    await startEditing(a);
    // The reader's editor is never writable.
    await expect(r.locator(LIVE_EDITOR)).toHaveCount(0);

    // The hub ends the reader's session of the old generation; the reader joins the writer's at once, then the writer types.
    await r.waitForTimeout(1_500);
    const editor = a.locator(LIVE_EDITOR);
    await editor.focus(); await editor.press("Control+End"); await editor.press("Enter");
    const typed = `Živý dodatek ${Date.now()}`;
    await a.keyboard.insertText(typed);
    await expect(readerText).toContainText(typed, { timeout: 2_500 });

    // Typing on the reader's side changes nothing, here or for the writer.
    await readerText.click();
    await r.keyboard.type("čtenář píše");
    await expect(readerText).not.toContainText("čtenář píše");
    await a.waitForTimeout(500);
    await expect(a.locator(LIVE_EDITOR)).not.toContainText("čtenář píše");
  } finally {
    await reader.close(); await admin.close();
  }
});
