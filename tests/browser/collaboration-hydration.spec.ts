import { test, expect, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { expectSaved, startEditing as editInPlace, PAGE_TEXT, LIVE_EDITOR } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
async function enterSecret(field: Locator, secret: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, value) => {
    const input = element as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }, secret);
}
async function login(context: BrowserContext, password: string) {
  const page = await context.newPage();
  page.on("dialog", (dialog) => dialog.accept());
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill("browser-admin");
  await enterSecret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  return page;
}
async function startEditing(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await editInPlace(page);
}
/** The stored version once the session has settled; hydration must not store anything before someone changes the page. */
async function settledVersion(page: Page, id: string) {
  await page.waitForTimeout(3_000);
  return (await (await page.request.get(`/api/pages/${id}`)).json()).version as number;
}
const text = (value: string) => [{ type: "text", text: value }];
const cell = (type: string, value: string) => ({ type, content: [{ type: "paragraph", content: text(value) }] });

test("a page ending with a table opens for editing without unsaved changes until someone really changed it", { tag: '@smoke' }, async ({ browser }) => {
  test.setTimeout(120_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== "1",
    "Requires an isolated installed acceptance database and explicit ATLAS_E2E_ALLOW_COLLAB=1.");
  const password = process.env.ATLAS_E2E_PASSWORD!;
  expect(password?.length).toBeGreaterThanOrEqual(12);
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL ?? "https://localhost:5220", viewport: { width: 1280, height: 900 } });
  try {
    const page = await login(context, password);
    const spaces = await (await context.request.get("/api/spaces")).json();
    const space = spaces.find((item: { name: string }) => item.name === "Začínáme");
    expect(space).toBeTruthy();
    const csrf = await (await context.request.get("/api/auth/csrf")).json();
    // The stored page carries no chart references, heading ids or trailing paragraph: the seeded shared document will.
    const created = await context.request.post("/api/pages", { headers: { "X-CSRF-TOKEN": csrf.token }, data: {
      spaceId: space.id, title: `Hydratace tabulky ${Date.now()}`, parentId: null,
      contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
        { type: "heading", attrs: { level: 2 }, content: text("Ceník") },
        { type: "paragraph", content: text("Aktuální ceny.") },
        { type: "table", content: [
          { type: "tableRow", content: [cell("tableHeader", "Položka"), cell("tableHeader", "Cena")] },
          { type: "tableRow", content: [cell("tableCell", "Licence"), cell("tableCell", "100")] },
        ] },
      ] } }),
      markdown: "## Ceník\n\nAktuální ceny.\n\n| Položka | Cena |\n| --- | --- |\n| Licence | 100 |\n",
    } });
    expect(created.ok(), await created.text()).toBe(true);
    const { id } = await created.json();

    const initial = await settledVersion(page, id);

    // First open for editing seeds the shared document.
    await startEditing(page, id);
    await expect(page.locator(`${PAGE_TEXT} td`).last()).toHaveText("100");
    expect(await settledVersion(page, id)).toBe(initial);

    // Reopening hydrates the seeded document (with the charts plugin's references) against the stored page.
    await page.reload();
    await startEditing(page, id);
    expect(await settledVersion(page, id)).toBe(initial);

    // A real change is stored and still there after reopening.
    const editor = page.locator(LIVE_EDITOR);
    await page.locator(`${PAGE_TEXT} p`, { hasText: "Aktuální ceny." }).click();
    await editor.press("End");
    await page.keyboard.insertText(" Neuloženo.");
    // The page stores the change by itself before the reload.
    await expectSaved(page);
    await page.reload();
    await startEditing(page, id);
    await expect(page.locator(PAGE_TEXT)).toContainText("Aktuální ceny. Neuloženo.");
    expect(await settledVersion(page, id)).toBeGreaterThan(initial);
  } finally {
    await context.close();
  }
});
