import {
  test,
  expect,
  type BrowserContext,
  type Locator,
  type Page,
} from "@playwright/test";
import { expectSaved, publishPage, startEditing as editInPlace, sessionLease, PAGE_TEXT, LIVE_EDITOR } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({
  trace: "off",
  screenshot: "off",
  video: "off",
  actionTimeout: 15_000,
});
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
  await page
    .getByLabel("Uživatelské jméno", { exact: true })
    .fill("browser-admin");
  await enterSecret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(
    page.getByRole("button", { name: /^Hledat ve wiki/ }),
  ).toBeVisible();
  return page;
}
async function mutate(
  context: BrowserContext,
  path: string,
  data: unknown,
  method: "post" | "put" = "post",
) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  return context.request[method](path, {
    data,
    headers: { "X-CSRF-TOKEN": csrf.token },
  });
}
async function startEditing(page: Page, id: string) {
  // The writable page is the shared editor from the first frame, built from the session state bundled with the page.
  const joined = sessionLease(page, id);
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  const lease = await joined;
  await editInPlace(page);
  return lease;
}
async function append(page: Page, text: string) {
  const editor = page.locator(LIVE_EDITOR);
  // The caret goes to the end through the DOM: the editor's middle may be the hover band of the block inserter below the last
  // block, and a click's selection reaches ProseMirror only after a frame (keys pressed at once went to the start).
  await editor.evaluate((element) => {
    (element as HTMLElement).focus();
    const selection = window.getSelection();
    selection?.selectAllChildren(element);
    selection?.collapseToEnd();
  });
  await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done))));
  await page.keyboard.press("Enter");
  await page.keyboard.insertText(text);
}
type TextNode = { type?: string; text?: string; content?: TextNode[] };
/** Text of a page's rich document; the web page API (PageView) no longer returns the Markdown projection. */
function pageText(page: { contentJson: string }) {
  const text = (node: TextNode): string =>
    node.text ?? (node.content ?? []).map(text).join(node.type === "doc" ? String.fromCharCode(10) : "");
  return text(JSON.parse(page.contentJson).document);
}

test("one writer saves and publishes → the other writer keeps editing and can save", async ({ browser }) => {
  test.setTimeout(120_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== "1", "Requires an isolated installed acceptance database and explicit ATLAS_E2E_ALLOW_COLLAB=1.");
  const password = process.env.ATLAS_E2E_PASSWORD!;
  const options = { baseURL: process.env.ATLAS_URL ?? "https://localhost:5220", viewport: { width: 1280, height: 900 } };
  const contextA = await browser.newContext(options), contextB = await browser.newContext(options);
  try {
    const a = await login(contextA, password), b = await login(contextB, password);
    const problems: string[] = [];
    b.on("pageerror", error => problems.push(`pageerror: ${error.message}\n${error.stack}`));
    b.on("console", message => { if (message.type() === "error" || message.type() === "warning") problems.push(`console.${message.type()}: ${message.text()}`); });
    b.on("response", response => { if (response.status() >= 400) problems.push(`http ${response.status()} ${response.request().method()} ${response.url()}`); });
    const spaces = await (await contextA.request.get("/api/spaces")).json();
    const space = spaces.find((item: { name: string }) => item.name === "Začínáme");
    const seed = "Výchozí dokument.";
    const created = await mutate(contextA, "/api/pages", {
      spaceId: space.id, title: `Publikace při společných úpravách ${Date.now()}`, parentId: null,
      contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: seed }] }] } }),
      markdown: seed,
    });
    expect(created.status()).toBe(200);
    const id = (await created.json()).id;
    await startEditing(a, id);
    await startEditing(b, id);
    const alpha = "Text prvního autora.", beta = "Text druhého autora.";
    await append(a, alpha);
    await append(b, beta);
    for (const page of [a, b]) await expect(page.locator(PAGE_TEXT)).toContainText(beta);
    await publishPage(a);
    await b.waitForTimeout(3_000);
    await b.screenshot({ path: "../../artifacts/collab-publish-b-after.png", fullPage: true });
    console.log("B after publish:\n" + problems.join("\n"));
    await expect(b.locator(LIVE_EDITOR)).toBeVisible();
    const gamma = "Pokračování druhého autora po publikaci.";
    await append(b, gamma);
    await expectSaved(b);
    await b.waitForTimeout(3_000);
    await b.screenshot({ path: "../../artifacts/collab-publish-b-saved.png", fullPage: true });
    console.log("B after save:\n" + problems.join("\n"));
    const saved = await (await contextB.request.get(`/api/pages/${id}`)).json();
    expect(pageText(saved)).toContain(gamma);
  } finally {
    await contextA.close();
    await contextB.close();
  }
});
