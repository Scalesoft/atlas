import { randomBytes } from "node:crypto";
import { startEditing as editInPlace, LIVE_EDITOR } from "./editing";
import {
  expect,
  test,
  type BrowserContext,
  type Locator,
  type Page,
} from "@playwright/test";

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

async function send(
  context: BrowserContext,
  method: "POST" | "PUT",
  path: string,
  data: unknown,
) {
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  return context.request.fetch(path, {
    method,
    data,
    headers: { "X-CSRF-TOKEN": csrf.token },
  });
}

async function login(context: BrowserContext, password: string) {
  const page = await context.newPage();
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill("presence-admin");
  await enterSecret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
  return page;
}

async function startEditing(page: Page, pageId: string) {
  await page.goto(`/#/pages/${pageId}`);
  await editInPlace(page);
}

async function placeCaret(page: Page, paragraph: number, offset = 2) {
  await page.locator(`${LIVE_EDITOR} p`).nth(paragraph).evaluate((node, caretOffset) => {
    const text = node.firstChild;
    if (!text) throw new Error("Fixture paragraph has no text node.");
    const selection = getSelection();
    const range = document.createRange();
    range.setStart(text, Math.min(caretOffset, text.textContent?.length ?? 0));
    range.collapse(true);
    selection?.removeAllRanges();
    selection?.addRange(range);
    (node.parentElement as HTMLElement).focus();
    document.dispatchEvent(new Event("selectionchange"));
  }, offset);
}

async function cursorPoint(cursor: Locator) {
  return cursor.evaluate(element => {
    const rect = element.getBoundingClientRect();
    return { x: rect.x, y: rect.y };
  });
}

async function selectionSignature(page: Page) {
  return page.locator(LIVE_EDITOR).evaluate(editor => {
    const selection = getSelection();
    const path = (node: Node | null) => {
      const indexes: number[] = [];
      while (node && node !== editor) {
        const parent = node.parentNode;
        if (!parent) break;
        indexes.unshift(Array.prototype.indexOf.call(parent.childNodes, node));
        node = parent;
      }
      return indexes;
    };
    return {
      anchor: path(selection?.anchorNode ?? null),
      anchorOffset: selection?.anchorOffset,
      focus: path(selection?.focusNode ?? null),
      focusOffset: selection?.focusOffset,
    };
  });
}

test("real collaboration presence follows, navigates, respects motion preference, and clears on exit", async ({ browser }) => {
  test.skip(process.env.ATLAS_PRESENCE_LIVE !== "1", "Requires a disposable collaboration-presence host.");
  test.setTimeout(120_000);

  const baseURL = process.env.ATLAS_URL ?? "http://127.0.0.1:5219";
  const bootstrapToken = process.env.ATLAS_BOOTSTRAP_TOKEN;
  expect(bootstrapToken, "ATLAS_BOOTSTRAP_TOKEN must contain the disposable host token.").toBeTruthy();
  const password = randomBytes(24).toString("hex");
  const contextA = await browser.newContext({ baseURL, viewport: { width: 1280, height: 720 } });
  const contextB = await browser.newContext({ baseURL, viewport: { width: 1280, height: 720 } });
  let contextBClosed = false;

  try {
    const status = await (await contextA.request.get("/api/setup/status")).json();
    expect(status.databaseReady).toBe(true);
    expect(status.requiresSetup, "Use an empty isolated database for this acceptance test.").toBe(true);
    const installed = await send(contextA, "POST", "/api/setup/complete", {
      bootstrapToken,
      workspaceName: "Presence acceptance",
      username: "presence-admin",
      displayName: "Správce přítomnosti",
      password,
    });
    expect(installed.status()).toBe(200);

    const a = await contextA.newPage();
    await a.goto("/");
    for (let step = 0; step < 3; step++)
      await a.getByRole("button", { name: "Další možnost", exact: true }).click();
    await a.locator("footer").getByRole("button", { name: "Otevřít wiki", exact: true }).click();
    const b = await login(contextB, password);

    const spaces = await (await contextA.request.get("/api/spaces")).json();
    const paragraphs = Array.from({ length: 30 }, (_, index) => ({
      type: "paragraph",
      content: [{ type: "text", text: `Odstavec ${String(index + 1).padStart(2, "0")} — dostatečně dlouhý obsah pro skutečný posun dokumentu.` }],
    }));
    const created = await send(contextA, "POST", "/api/pages", {
      spaceId: spaces[0].id,
      parentId: null,
      title: "Přítomnost ve dlouhém dokumentu",
      contentJson: JSON.stringify({
        format: "atlas-rich",
        version: 1,
        document: { type: "doc", content: paragraphs },
      }),
      markdown: paragraphs.map(item => item.content[0].text).join("\n\n"),
    });
    expect(created.status()).toBe(200);
    const document = await created.json();

    await Promise.all([startEditing(a, document.id), startEditing(b, document.id)]);
    await placeCaret(b, 1);

    const presence = a.locator(".collaboration-presence");
    await expect(presence).toBeVisible();
    const remoteCursor = presence.locator('.collaboration-presence__cursor[data-client-id]');
    await expect(remoteCursor).toHaveCount(1);
    await expect(remoteCursor.locator(".collaboration-presence__label")).toHaveText("Správce přítomnosti");
    const clientId = await remoteCursor.getAttribute("data-client-id");
    expect(clientId).toMatch(/^\d+$/);
    await expect(remoteCursor.locator(".collaboration-presence__label")).not.toHaveCSS("background-color", "rgba(0, 0, 0, 0)");

    const avatar = a.locator(".document-actions").getByRole("button", {
      name: "Přejít na kurzor uživatele Správce přítomnosti",
      exact: true,
    }).and(a.locator(`.collaborator-avatar[data-client-id="${clientId}"]`));
    await expect(avatar).toHaveCount(1);
    await expect(avatar).not.toHaveAttribute("aria-disabled", "true");
    await a.screenshot({
      path: "../../artifacts/collaboration-presence/normal.png",
      fullPage: false,
      animations: "disabled",
    });

    const start = await cursorPoint(remoteCursor);
    expect(await remoteCursor.evaluate(element => getComputedStyle(element).transitionDuration)).toBe("0.12s");
    const motionSamples = remoteCursor.evaluate(element => new Promise<number[]>(resolve => {
      const samples: number[] = [];
      const began = performance.now();
      const sample = () => {
        samples.push(element.getBoundingClientRect().y);
        if (performance.now() - began < 1_000) requestAnimationFrame(sample);
        else resolve(samples);
      };
      sample();
    }));
    await placeCaret(b, 28);
    const samples = await motionSamples;
    const settled = { x: start.x, y: samples.at(-1)! };
    expect(Math.abs(settled.y - start.y)).toBeGreaterThan(100);
    expect(samples.some(y =>
      y > Math.min(start.y, settled.y) + 1
      && y < Math.max(start.y, settled.y) - 1,
    )).toBe(true);

    await placeCaret(a, 2, 5);
    const localSelection = await selectionSignature(a);
    await avatar.click();
    await expect.poll(() => a.evaluate(() => scrollY)).toBeGreaterThan(200);
    expect(await selectionSignature(a)).toEqual(localSelection);
    await expect(remoteCursor).toBeInViewport();

    await a.emulateMedia({ reducedMotion: "reduce" });
    await expect(presence).toHaveClass(/is-reduced-motion/);
    const reducedStart = await cursorPoint(remoteCursor);
    await placeCaret(b, 4);
    await expect.poll(async () => (await cursorPoint(remoteCursor)).y).not.toBe(reducedStart.y);
    expect(await remoteCursor.evaluate(element => getComputedStyle(element).transitionDuration)).toBe("0s");
    await avatar.click();
    await expect.poll(async () => {
      const cursor = await remoteCursor.boundingBox();
      const bar = await a.locator(".workspace-bar").boundingBox();
      return !!cursor && !!bar && cursor.y > bar.y + bar.height + 4;
    }).toBe(true);
    expect(await selectionSignature(a)).toEqual(localSelection);

    const darkAppearance = await send(contextA, "PUT", "/api/profile/appearance", {
      layout: "connected",
      accent: "blue",
      mode: "dark",
    });
    expect(darkAppearance.status()).toBe(200);
    await a.reload();
    await startEditing(a, document.id);
    await expect(a.locator("html")).toHaveAttribute("data-mode", "dark");
    await expect(remoteCursor).toHaveCount(1);
    await a.screenshot({
      path: "../../artifacts/collaboration-presence/dark.png",
      fullPage: false,
      animations: "disabled",
    });
    await a.setViewportSize({ width: 390, height: 844 });
    await expect(avatar).toBeVisible();
    await expect(a.locator("[data-edit-lock], [data-page-mobile-lock]").first()).toBeVisible();
    expect(await a.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);

    await b.keyboard.press("Escape");
    await expect(b.locator(LIVE_EDITOR)).toHaveCount(0);
    await expect(avatar).toHaveCount(0);
    await expect(remoteCursor).toHaveCount(0);

    await startEditing(b, document.id);
    await placeCaret(b, 8);
    await expect(remoteCursor).toHaveCount(1);
    const abruptClientId = await remoteCursor.getAttribute("data-client-id");
    expect(abruptClientId).toMatch(/^\d+$/);
    const abruptAvatar = a.locator(`.collaborator-avatar[data-client-id="${abruptClientId}"]`);
    await expect(abruptAvatar).toHaveCount(1);
    await contextB.close();
    contextBClosed = true;
    // Abrupt transport loss uses the editor provider's 30s awareness timeout and
    // 5s sweep interval; allow that documented upper bound before calling it stale.
    await expect(abruptAvatar).toHaveCount(0, { timeout: 40_000 });
    await expect(remoteCursor).toHaveCount(0, { timeout: 40_000 });
  } finally {
    if (!contextBClosed) await contextB.close().catch(() => {});
    await contextA.close().catch(() => {});
  }
});
