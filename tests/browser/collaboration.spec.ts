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

test("two writers → convergence → reload before save → publish → restored generation fences old writer", async ({
  browser,
}) => {
  test.setTimeout(120_000);
  test.skip(
    process.env.ATLAS_E2E_ALLOW_COLLAB !== "1",
    "Requires an isolated installed acceptance database and explicit ATLAS_E2E_ALLOW_COLLAB=1.",
  );
  const password = process.env.ATLAS_E2E_PASSWORD!;
  expect(password?.length).toBeGreaterThanOrEqual(12);
  const contextA = await browser.newContext({
    baseURL: process.env.ATLAS_URL ?? "https://localhost:5220",
    viewport: { width: 1280, height: 900 },
  });
  const contextB = await browser.newContext({
    baseURL: process.env.ATLAS_URL ?? "https://localhost:5220",
    viewport: { width: 1280, height: 900 },
  });
  try {
    const status = await (
      await contextA.request.get("/api/setup/status")
    ).json();
    expect(
      status.requiresSetup,
      "Run the first-install acceptance once before this separate collaboration test.",
    ).toBe(false);
    const a = await login(contextA, password),
      b = await login(contextB, password);
    const spaces = await (await contextA.request.get("/api/spaces")).json();
    const space = spaces.find(
      (item: { name: string }) => item.name === "Začínáme",
    );
    expect(space).toBeTruthy();
    const seed = "Výchozí týmový dokument.";
    const title = `Společné úpravy ${Date.now()}`;
    const created = await mutate(contextA, "/api/pages", {
      spaceId: space.id,
      title,
      parentId: null,
      contentJson: JSON.stringify({
        format: "atlas-rich",
        version: 1,
        document: {
          type: "doc",
          content: [
            {
              type: "paragraph",
              content: [
                { type: "text", text: seed, marks: [{ type: "bold" }] },
              ],
            },
          ],
        },
      }),
      markdown: `**${seed}**`,
    });
    expect(created.status()).toBe(200);
    const initial = await created.json();
    const id = initial.id;
    const leaseA = await startEditing(a, id),
      leaseB = await startEditing(b, id);
    expect(leaseA.generation).toBe(leaseB.generation);
    await expect(a.locator(`${PAGE_TEXT} strong`).filter({ hasText: seed })).toContainText(seed);
    await expect(b.locator(`${PAGE_TEXT} strong`).filter({ hasText: seed })).toContainText(seed);
    const alpha = "Poznámka od prvního autora.",
      beta = "Doplnění od druhého autora.";
    await Promise.all([append(a, alpha), append(b, beta)]);
    for (const page of [a, b]) {
      await expect(page.locator(PAGE_TEXT)).toContainText(alpha);
      await expect(page.locator(PAGE_TEXT)).toContainText(beta);
      await expect(page.locator('.collaboration-carets__label').filter({ hasText: 'Test správce' }).first()).toBeVisible();
    }
    // 3.6 saves by itself: the checkpoint holds both authors' text; the reconnect below still hydrates from the shared session.
    await expectSaved(a);
    const checkpointBefore = await (
      await contextA.request.get(`/api/pages/${id}`)
    ).json();
    expect(checkpointBefore.version).toBeGreaterThan(initial.version);
    expect(pageText(checkpointBefore)).toContain(alpha);
    await a.reload();
    await editInPlace(a);
    await expect(a.locator(PAGE_TEXT)).toContainText(alpha);
    await expect(a.locator(PAGE_TEXT)).toContainText(beta);
    await expect(a.locator(`${PAGE_TEXT} strong`).filter({ hasText: seed })).toContainText(seed);
    // Nothing changed since the checkpoint above, so the reloaded page shows no save state; Publikovat checkpoints first anyway.
    await a.locator(".document-primary-actions .publish-main").click();
    await expect(
      a.getByText("Stránka byla publikována.", { exact: true }),
    ).toBeVisible();
    const published = await (
      await contextA.request.get(`/api/pages/${id}`)
    ).json();
    // The web page API (PageView) no longer returns the Markdown projection; the rich
    // document below is the published content itself.
    type RichNode = { type: string; text?: string; marks?: { type: string }[]; content?: RichNode[] };
    const nodes: RichNode[] = [];
    function collect(node: RichNode) { nodes.push(node); node.content?.forEach(collect); }
    collect(JSON.parse(published.contentJson).document);
    const richText = nodes.filter(node => node.type === 'text').map(node => node.text).join('');
    expect(richText).toContain(alpha);
    expect(richText).toContain(beta);
    expect(nodes.some(node => node.text?.includes(seed) && node.marks?.some(mark => mark.type === 'bold'))).toBe(true);
    expect(published.publishedVersion).toBe(published.version);
    await a.screenshot({
      path: "../../artifacts/atlas-accepted-collaboration.png",
      fullPage: true,
    });
    // Restore through the real history UI while B still holds the old generation.
    await a.getByRole("button", { name: "Další akce stránky", exact: true }).click();
    await a.getByRole("menuitem", { name: "Historie", exact: true }).click();
    const history = a.getByRole("dialog", { name: "Historie stránky", exact: true });
    const versions = history.getByRole("navigation", { name: "Uložené verze stránky", exact: true });
    const first = versions.getByRole("button", { name: /\bv1\b/ }), steps = versions.getByRole("button", { name: /^Kroky verze/ });
    // 3.6-C groups one author's saves into a version: v1 may be a step of the oldest one.
    await expect(first.or(steps).first()).toBeVisible();
    if (!(await first.isVisible())) await steps.last().click();
    await first.click();
    await history.getByRole("button", { name: "Celá verze", exact: true }).click();
    await expect(history.locator(".md-renderer")).toContainText(seed);
    await history
      .getByRole("button", { name: "Obnovit tuto verzi…", exact: true })
      .click();
    await history
      .getByRole("button", { name: "Obnovit jako koncept", exact: true })
      .click();
    await expect(history).not.toBeVisible();
    await expect(a.locator(LIVE_EDITOR)).toBeVisible();
    await expect(a.locator(PAGE_TEXT)).toContainText(seed);
    await expect(a.locator(PAGE_TEXT)).not.toContainText(alpha);
    const restored = await (
      await contextA.request.get(`/api/pages/${id}`)
    ).json();
    expect(restored.version).toBeGreaterThan(published.version);
    expect(restored.publishedVersion).toBe(published.publishedVersion);
    // Use the latest business version so a rejection proves the generation fence,
    // rather than being explained by an ordinary stale expectedVersion.
    const oldSave = await mutate(
      contextB,
      `/api/pages/${id}`,
      {
        title: published.title,
        contentJson: published.contentJson,
        markdown: pageText(published),
        expectedVersion: restored.version,
        collaborationGeneration: leaseB.generation,
      },
      "put",
    );
    expect(oldSave.status()).toBe(409);
    // The fence reaches B asynchronously and may turn its editor read-only at any
    // moment; type through the keyboard so a lost race is a no-op, not a timeout
    // waiting for a contenteditable="true" that no longer exists.
    await b.locator(PAGE_TEXT).evaluate((element) => {
      (element as HTMLElement).focus();
      const selection = window.getSelection();
      selection?.selectAllChildren(element);
      selection?.collapseToEnd();
    });
    await b.keyboard.press("Enter");
    await b.keyboard.insertText("STARÁ RELACE NESMÍ ZAPSAT.");
    await expect(b.locator(PAGE_TEXT)).toHaveAttribute(
      "contenteditable",
      "false",
    );
    const afterDenied = await (
      await contextA.request.get(`/api/pages/${id}`)
    ).json();
    expect(afterDenied.version).toBe(restored.version);
    expect(pageText(afterDenied)).not.toContain("STARÁ RELACE");
    // Only the shown status counts: the closed discard dialogs ("Neuložené změny" is their title) stay in the DOM.
    await expect(a.getByText('Neuložené změny', { exact: true }).filter({ visible: true })).toHaveCount(0);
    await expect(a.locator(PAGE_TEXT)).not.toContainText("STARÁ RELACE");
    await a.screenshot({
      path: "../../artifacts/atlas-accepted-restored-generation.png",
      fullPage: true,
    });
  } finally {
    await contextA.close();
    await contextB.close();
  }
});


