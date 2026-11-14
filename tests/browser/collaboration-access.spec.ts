import { test, expect, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { expectSaved, startEditing, PAGE_TEXT, LIVE_EDITOR } from "./editing";
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
async function edit(page: Page, id: string) {
  await page.goto(`/#/pages/${id}`);
  await expect(page.locator(PAGE_TEXT)).toBeVisible();
  await startEditing(page);
}
function presence(page: Page) {
  return page.getByRole("group", { name: "Spolupracovníci v dokumentu", exact: true });
}
/** A peer avatar is a button named after the collaborator, whether or not their caret can be followed. */
function collaborator(page: Page, name: string) {
  return presence(page).getByRole("button", { name: new RegExp(`^(Přejít na kurzor uživatele ${name}|${name} – kurzor není dostupný)$`) });
}
type RichNode = { type?: string; text?: string; content?: RichNode[] };
/** Text of a page's rich document; the web page API (PageView) no longer returns the Markdown projection. */
function pageText(page: { contentJson: string }) {
  const text = (node: RichNode): string => node.text ?? (node.content ?? []).map(text).join(node.type === "doc" ? String.fromCharCode(10) : "");
  return text(JSON.parse(page.contentJson).document);
}
async function append(page: Page, text: string) {
  const editor = page.locator(LIVE_EDITOR);
  // Focus, not a click: the editor's centre can fall into a gap between blocks, where the block inserter's "+" takes it.
  await editor.focus(); await editor.press("Control+End"); await editor.press("Enter"); await page.keyboard.insertText(text);
}

test("two different users retain acknowledged changes offline and a revoked writer receives no later document updates", async ({ browser }) => {
  test.setTimeout(180_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== "1", "Only an installed isolated acceptance wiki with browser-admin and browser-reader.");
  const password = process.env.ATLAS_E2E_PASSWORD!;
  const admin = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  const writer = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  let spaceId: string | undefined, writerId: string | undefined;
  try {
    const a = await login(admin, "browser-admin", password), b = await login(writer, "browser-reader", process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`);
    const account = await (await writer.request.get("/api/auth/me")).json(); writerId = account.id;
    const administrator = await (await admin.request.get("/api/auth/me")).json();
    expect(account.isWikiAdmin).toBe(false);
    await expect(b.getByRole("button", { name: "Správa wiki", exact: true })).toHaveCount(0);
    const createdSpace = await mutate(admin, "/api/spaces", { name: `Připojení a oprávnění ${Date.now()}`, icon: "code-xml", color: "blue" });
    expect(createdSpace.status()).toBe(200); spaceId = (await createdSpace.json()).id;
    const initialAccess = await (await admin.request.get(`/api/spaces/${spaceId}/access`)).json();
    expect(initialAccess.grants.filter((grant: { groupId: string | null }) => grant.groupId)).toHaveLength(0);
    expect((await mutate(admin, `/api/spaces/${spaceId}/access`, { userId: writerId, role: "Writer", expectedVersion: null }, "put")).status()).toBe(204);
    const seed = "Výchozí společný obsah.";
    const created = await mutate(admin, "/api/pages", { spaceId, parentId: null, title: "Připojení dvou účtů", contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: seed }] }] } }), markdown: seed });
    expect(created.status()).toBe(200); const initial = await created.json(), id = initial.id;
    expect((await mutate(admin, `/api/pages/${id}/publish`, { expectedVersion: initial.version })).status()).toBe(200);
    // Both sessions existed before the API created this new space and grant.
    // Refresh their directory once before joining; later offline/revocation steps never reload.
    await a.reload(); await b.reload();
    await expect(b.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
    let receivedRevocation = false;
    b.on("websocket", socket => socket.on("framereceived", frame => {
      // Record only the access verdict. Document frames and credentials are never logged.
      for (const part of String(frame.payload).split("\u001e")) {
        try { const event = JSON.parse(part); if (event.target === "DocumentAccessChanged" && event.arguments?.[1] === false && event.arguments?.[2] === false) receivedRevocation = true; } catch { /* Non-JSON transport frame. */ }
      }
    }));
    await edit(a, id); await edit(b, id);
    // The wiki header renders presence ("Spolupracovníci v dokumentu"); the editor's own stack is off.
    await expect(presence(a)).toBeVisible();
    const peer = collaborator(a, account.displayName);
    await expect(peer).toBeVisible();
    await peer.focus();
    await peer.press('Shift+Tab');
    await a.keyboard.press('Tab');
    await expect(peer).toBeFocused();
    // The avatar's accessible name carries the collaborator; pointer users get the same text as a tooltip.
    await peer.hover();
    await expect(a.locator('[data-slot="tooltip-content"]')).toContainText(account.displayName);
    const presenceBounds = await peer.boundingBox(), contentBounds = await a.locator(PAGE_TEXT).boundingBox();
    expect(presenceBounds!.y + presenceBounds!.height).toBeLessThanOrEqual(contentBounds!.y);
    await a.screenshot({ path: '../../artifacts/ux-review/after-presence-desktop.png', fullPage: true });
    const confirmed = "Serverem potvrzená poznámka druhého účtu.";
    await append(b, confirmed);
    await expect(a.locator(PAGE_TEXT)).toContainText(confirmed);
    await expectSaved(b);
    // Receiving on the other authenticated peer and Uloženo prove this change reached the server; 3.6 stored it as a checkpoint itself.
    const beforeOffline = await (await admin.request.get(`/api/pages/${id}`)).json();
    expect(beforeOffline.version).toBeGreaterThan(initial.version);
    expect(pageText(beforeOffline)).toContain(confirmed);
    await writer.setOffline(true);
    await expect.poll(() => b.evaluate(() => navigator.onLine)).toBe(false);
    await expect(b.locator(PAGE_TEXT)).toHaveAttribute("contenteditable", "false", { timeout: 60_000 });
    await expect(presence(b)).toHaveCount(0);
    await expect(b.locator(PAGE_TEXT)).toContainText(confirmed);
    const duringOffline = "První autor mezitím pokračoval online.";
    // A's checkpoint waits for the snapshot of B, who wrote last and holds the snapshotter role (3.6-G) until its connection is back or
    // times out; it is saved after B reconnects (below).
    await append(a, duringOffline);
    await writer.setOffline(false);
    await expect(b.locator(LIVE_EDITOR)).toBeVisible({ timeout: 60_000 });
    await expect(b.locator(PAGE_TEXT)).toContainText(confirmed);
    await expect(b.locator(PAGE_TEXT)).toContainText(duringOffline);
    await expect(collaborator(b, administrator.displayName)).toBeVisible();
    await expect(collaborator(a, account.displayName)).toBeVisible();
    const reconnected = "Druhý účet po opětovném připojení.";
    await append(b, reconnected); await expect(a.locator(PAGE_TEXT)).toContainText(reconnected);
    await expectSaved(a);
    const checkpoint = await (await admin.request.get(`/api/pages/${id}`)).json();
    expect(pageText(checkpoint)).toContain(confirmed); expect(pageText(checkpoint)).toContain(duringOffline); expect(pageText(checkpoint)).toContain(reconnected);

    const access = await (await admin.request.get(`/api/spaces/${spaceId}/access`)).json();
    expect(access.grants.filter((grant: { groupId: string | null }) => grant.groupId)).toHaveLength(0);
    const grant = access.grants.find((grant: { userId: string }) => grant.userId === writerId);
    expect(grant.role).toBe("Writer");
    expect((await mutate(admin, `/api/spaces/${spaceId}/access`, { userId: writerId, role: null, expectedVersion: grant.version }, "put")).status()).toBe(204);
    const afterRevoke = "Nový obsah dostupný až po odebrání druhého účtu.";
    await append(a, afterRevoke);
    await expect.poll(() => receivedRevocation, { timeout: 30_000 }).toBe(true);
    await expect(b.locator(PAGE_TEXT)).toHaveAttribute("contenteditable", "false");
    await expect(b.locator(PAGE_TEXT)).not.toContainText(afterRevoke);
    expect((await mutate(writer, `/api/pages/${id}/collaboration`, {})).status()).toBe(404);
    expect((await mutate(writer, `/api/pages/${id}`, { title: checkpoint.title, contentJson: checkpoint.contentJson, markdown: pageText(checkpoint), expectedVersion: checkpoint.version }, "put")).status()).toBe(404);
    expect((await writer.request.get(`/api/pages/${id}`)).status()).toBe(404);
    await expectSaved(a);
    const final = await (await admin.request.get(`/api/pages/${id}`)).json();
    expect(pageText(final)).toContain(afterRevoke);
    await expect(b.locator(PAGE_TEXT)).not.toContainText(afterRevoke);
    const finalAccess = await (await admin.request.get(`/api/spaces/${spaceId}/access`)).json();
    expect(finalAccess.grants.some((grant: { userId: string | null; groupId: string | null }) => grant.userId === writerId || grant.groupId)).toBe(false);
  } finally {
    // If an earlier assertion fails, remove only this test's own direct grant.
    await writer.setOffline(false);
    if (spaceId && writerId) {
      const response = await admin.request.get(`/api/spaces/${spaceId}/access`);
      if (response.ok()) {
        const access = await response.json(), grant = access.grants.find((grant: { userId: string }) => grant.userId === writerId);
        if (grant) await mutate(admin, `/api/spaces/${spaceId}/access`, { userId: writerId, role: null, expectedVersion: grant.version }, "put");
      }
    }
    await admin.close(); await writer.close();
  }
});
