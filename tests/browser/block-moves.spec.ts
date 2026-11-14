import {
  test,
  expect,
  type BrowserContext,
  type Locator,
  type Page,
} from "@playwright/test";
import { expectSaved, startEditing as editInPlace, sessionLease, PAGE_TEXT, LIVE_EDITOR } from "./editing";
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

// Blocks are ordinary top-level ProseMirror paragraph nodes; the block-move
// drag handle is a single floating overlay, not a per-block wrapper.
function blocksOf(page: Page) {
  return page.locator(`${LIVE_EDITOR} > p`);
}

/** Remote carets are non-document widgets; textContent also includes their opacity-hidden user labels. */
async function expectBlockText(blocks: Locator, expected: readonly string[]) {
  await expect.poll(() => blocks.evaluateAll(elements => elements.map(element => {
    const clone = element.cloneNode(true) as HTMLElement;
    clone.querySelectorAll('.collaboration-carets__caret.ProseMirror-widget').forEach(caret => caret.remove());
    return clone.textContent ?? '';
  }))).toEqual(expected);
}

test("page author reorders blocks live, by keyboard and by drag, and the order survives save + reload", async ({
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
      "Run the first-install acceptance once before this separate block-move test.",
    ).toBe(false);
    const a = await login(contextA, password);
    const spaces = await (await contextA.request.get("/api/spaces")).json();
    const space = spaces.find(
      (item: { name: string }) => item.name === "Začínáme",
    );
    expect(space).toBeTruthy();
    const title = `Přesun bloků ${Date.now()}`;
    const created = await mutate(contextA, "/api/pages", {
      spaceId: space.id,
      title,
      parentId: null,
      contentJson: JSON.stringify({
        format: "atlas-rich",
        version: 1,
        document: { type: "doc", content: [{ type: "paragraph" }] },
      }),
      markdown: "",
    });
    expect(created.status()).toBe(200);
    const initial = await created.json();
    const id = initial.id;

    const leaseA = await startEditing(a, id);
    const editorA = a.locator(LIVE_EDITOR);
    const blocksA = blocksOf(a);

    // Type three separate paragraph blocks as a real author would.
    await editorA.click();
    await editorA.press("ControlOrMeta+A");
    await a.keyboard.type("Blok A");
    await a.keyboard.press("Enter");
    await a.keyboard.type("Blok B");
    await a.keyboard.press("Enter");
    await a.keyboard.type("Blok C");
    await expectBlockText(blocksA, ["Blok A", "Blok B", "Blok C"]);

    // A second window joins the same live collaborative session while the
    // first is still editing — this is the acceptance-critical scenario.
    const b = await login(contextB, password);
    const leaseB = await startEditing(b, id);
    expect(leaseB.generation).toBe(leaseA.generation);
    const blocksB = blocksOf(b);
    await expectBlockText(blocksB, ["Blok A", "Blok B", "Blok C"]);

    // Keyboard move: focus the grip over the middle block and use Shift+arrows.
    await blocksA.nth(1).hover();
    const grip = a.getByTestId("block-drag-btn");
    await expect(grip).toBeVisible();
    // The grip must promise the move, not explain that it is unavailable —
    // block moves are ordinary edits under the shared Yjs binding now.
    await expect(grip).toHaveAccessibleName(/Shift\+šipky přesunou/);
    await expect(grip).not.toHaveAccessibleName(/dočasně nedostupné/);
    await grip.focus();
    await a.keyboard.press("Shift+ArrowUp");
    await expectBlockText(blocksA, ["Blok B", "Blok A", "Blok C"]);
    await expectBlockText(blocksB, ["Blok B", "Blok A", "Blok C"]);
    await a.keyboard.press("Shift+ArrowDown");
    await expectBlockText(blocksA, ["Blok A", "Blok B", "Blok C"]);
    await expectBlockText(blocksB, ["Blok A", "Blok B", "Blok C"]);

    // Mouse drag: drag the middle block's grip past the last block.
    await blocksA.nth(1).hover();
    const source = (await grip.boundingBox())!;
    const destination = (await blocksA.last().boundingBox())!;
    await a.mouse.move(
      source.x + source.width / 2,
      source.y + source.height / 2,
    );
    await a.mouse.down();
    await a.mouse.move(destination.x + 20, destination.y + destination.height, {
      steps: 12,
    });
    await a.mouse.up();
    await expectBlockText(blocksA, ["Blok A", "Blok C", "Blok B"]);
    await expectBlockText(blocksB, ["Blok A", "Blok C", "Blok B"]);

    // Save the page and confirm the reordered content is durable, not just
    // an in-memory Yjs artifact.
    await expectSaved(a);
    const saved = await (await contextA.request.get(`/api/pages/${id}`)).json();
    expect(saved.version).toBeGreaterThan(initial.version);
    type RichNode = {
      type: string;
      text?: string;
      content?: RichNode[];
    };
    const savedDocument = JSON.parse(saved.contentJson).document as RichNode;
    const savedParagraphs = (savedDocument.content ?? [])
      .filter((node) => node.type === "paragraph")
      .map((node) => (node.content ?? []).map((child) => child.text ?? "").join(""));
    expect(savedParagraphs).toEqual(["Blok A", "Blok C", "Blok B"]);

    await a.reload();
    await editInPlace(a);
    await expectBlockText(blocksA, ["Blok A", "Blok C", "Blok B"]);
  } finally {
    await contextA.close();
    await contextB.close();
  }
});
