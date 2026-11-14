// UX review cycle 5, area admin: settings, trash, AI defaults, the MCP
// approval page and rights from the UI. Runs against a local build with „Přihlásit jako vývojář“ (ATLAS_URL, ATLAS_UX_DEV_LOGIN=1);
// its pages live in the space „UX admin“ (created when missing) and go to the trash at the end.
// The rights tests create accounts and change access, so they run only with ATLAS_UX_ADMIN_USERS=1, against a COPY of a wiki
// (never the shared real-data instance).
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { login, send, stamp } from "./projector-helpers";

process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
const usersAllowed = process.env.ATLAS_UX_ADMIN_USERS === "1";

async function signIn(page: Page) {
  await page.goto("/");
  const developer = page.getByRole("button", { name: "Přihlásit jako vývojář" });
  if (!(await developer.isVisible({ timeout: 15_000 }).catch(() => false))) test.skip(true, "Needs a local build with developer sign-in.");
  await developer.click();
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" });
  await Promise.race([onboarding.waitFor().then(() => onboarding.click()).catch(() => {}), page.getByRole("heading", { level: 1, name: "Knihovna" }).waitFor()]);
  await expect(page.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
}
async function ok<T = any>(context: BrowserContext, method: "post" | "put" | "delete", path: string, data?: unknown): Promise<T> {
  const response = await send(context, method, path, data);
  expect(response.status(), `${path}: ${await response.text()}`).toBeLessThan(300);
  return (await response.text()) ? response.json() : (undefined as T);
}
async function adminSpace(context: BrowserContext): Promise<string> {
  const spaces: { id: string; name: string }[] = await (await context.request.get("/api/spaces")).json();
  return spaces.find(space => space.name === "UX admin")?.id ?? (await ok(context, "post", "/api/spaces", { name: "UX admin", icon: "settings", color: "blue" })).id;
}
const made: string[] = [];
const doc = (text: string, ...marks: { text: string; href: string }[]) => JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text }, ...marks.map(link => ({ type: "text", text: link.text, marks: [{ type: "link", attrs: { href: link.href } }] }))] }] } });
async function createPage(context: BrowserContext, spaceId: string, title: string, parentId: string | null = null, content = doc(`${title} text.`)): Promise<string> {
  const { id } = await ok(context, "post", "/api/pages", { spaceId, parentId, title, contentJson: content, markdown: title });
  const current = await (await context.request.get(`/api/pages/${id}`)).json();
  await ok(context, "post", `/api/pages/${id}/publish`, { expectedVersion: current.version });
  made.push(id);
  return id;
}
async function trashPage(context: BrowserContext, id: string) {
  const page = await (await context.request.get(`/api/pages/${id}`)).json();
  const tree = await (await context.request.get(`/api/spaces/${page.spaceId}/tree`)).json();
  await ok(context, "post", `/api/pages/${id}/trash`, { includeSubtree: true, expectedTreeVersion: tree.version, expectedVersion: page.version });
}
const focused = (page: Page) => page.evaluate(() => {
  const active = document.activeElement;
  return active && active !== document.body ? `${active.tagName.toLowerCase()} ${(active.getAttribute("aria-label") || active.textContent || "").trim().replace(/\s+/g, " ").slice(0, 60)}` : "body";
});

test.afterAll(async ({ browser }) => {
  if (!made.length) return;
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  await signIn(await context.newPage());
  for (const id of made.reverse()) {
    const current = await context.request.get(`/api/pages/${id}`);
    if (current.ok()) await trashPage(context, id).catch(() => {});
  }
  await context.close();
});

test.describe("admin area", () => {
  test.beforeEach(async ({ page }) => { await signIn(page); });

  test("AD5-1 a settings flow opened by a button starts in its first field, and Esc is its Zrušit", async ({ page, context }) => {
    const space = await adminSpace(context);
    await page.goto(`/#/spaces/${space}/settings/access`);
    const add = page.getByRole("link", { name: "Přidat přístup" });
    await add.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toMatch(/^button Uživatel/);
    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(/\/settings\/access$/);
    await expect(page.getByRole("radio", { name: /Uživatel/ })).toHaveCount(0);

    await page.goto("/#/settings/people/accounts");
    await page.locator("a, button").filter({ hasText: "Pozvat člověka" }).first().focus();
    await page.keyboard.press("Enter");
    await expect(page.locator("[data-invite-username]")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator("[data-invite-username]")).toHaveCount(0);
  });

  test("AD5-4 the trash confirmation counts the pages in Czech", async ({ page, context }) => {
    const space = await adminSpace(context);
    const parent = await createPage(context, space, `UX-admin Koš ${stamp()}`);
    await createPage(context, space, `UX-admin Koš dítě ${stamp()}`, parent);
    await page.goto(`/#/pages/${parent}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await page.getByRole("button", { name: "Další akce stránky" }).click();
    await page.getByRole("menuitem", { name: "Přesunout do koše" }).click();
    const dialog = page.locator("dialog[open]");
    await expect(dialog).toContainText("Přesunout také všechny podstránky (1). Celkem 2 stránky.");
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
  });

  test("AD5-5 the space trash starts on its first item, and picking one leads on to where it goes back", async ({ page, context }) => {
    const space = await adminSpace(context);
    const title = `UX-admin V koši ${stamp()}`;
    const id = await createPage(context, space, title);
    await trashPage(context, id);
    await page.goto(`/#/spaces/${space}`);
    await page.getByRole("button", { name: /^Akce prostoru/ }).click();
    await page.getByRole("menuitem", { name: "Koš", exact: true }).click();
    // The shared trash list (TrashList): focus starts on the first row's Obnovit, which opens the restore form.
    const first = page.locator("dialog[open] [data-trash-restore]").first();
    await expect(first).toBeVisible();
    await expect(first).toBeFocused();
    await page.locator("dialog[open]").getByRole("button", { name: `Obnovit: ${title}`, exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toBe("input Hledat: Umístění po obnovení");
    await page.keyboard.press("Escape");
  });

  test("AD5-8 a dialog whose content loads lands on its first control, not on Zavřít (Automatický export PDF)", async ({ page, context }) => {
    const id = await createPage(context, await adminSpace(context), `UX-admin PDF ${stamp()}`);
    await page.goto(`/#/pages/${id}`);
    await expect(page.locator(".document-header .document-title")).toBeVisible();
    await page.getByRole("button", { name: "Další akce stránky" }).click();
    await page.getByRole("menuitem", { name: "Automatický export PDF…" }).click();
    await expect(page.locator("dialog[open]")).toContainText("Automatický export");
    await expect.poll(() => focused(page)).not.toMatch(/^button Zavřít/);
    expect(await page.evaluate(() => !!document.activeElement?.closest("dialog[open] .app-dialog-header"))).toBe(false);
    await page.keyboard.press("Escape");
  });

  test("AD5-6 the command palette describes the profile as it is (no notifications there)", async ({ page }) => {
    await page.keyboard.press("Control+Shift+P");
    await page.keyboard.type("profil");
    const option = page.locator('[role="option"]').filter({ hasText: "Profil a vzhled" });
    await expect(option).toContainText("Účet, vzhled, zabezpečení");
    await page.keyboard.press("Escape");
  });

});

// --- rights from the UI: own accounts, only on a copy -------------------------------------------------------------------------
test.describe("admin area: rights", () => {
  test.skip(!usersAllowed, "Creates accounts and changes access: only with ATLAS_UX_ADMIN_USERS=1 against a copy of a wiki.");
  const password = `Ux-admin-${Date.now()}-heslo!`;
  async function account(context: BrowserContext, role: "Reader" | "Writer" | null, space: string) {
    const username = `ux-admin-${(role ?? "none").toLowerCase()}-${stamp()}`;
    const created = await ok(context, "post", "/api/admin/users", { username, displayName: `UX admin ${role ?? "bez přístupu"}`, isWikiAdmin: false });
    if (role) await ok(context, "put", `/api/spaces/${space}/access`, { userId: created.user.id, role, expectedVersion: null });
    return { username, id: created.user.id as string, token: created.token as string };
  }
  async function activate(browser: import("@playwright/test").Browser, token: string) {
    const fresh = await browser.newContext({ baseURL: process.env.ATLAS_URL });
    await ok(fresh, "post", "/api/auth/activate", { token, newPassword: password });
    return fresh;
  }

  test("AD5-2 a settings link a reader may not open says why", async ({ browser, page, context }) => {
    await signIn(page);
    const space = await adminSpace(context);
    const reader = await account(context, "Reader", space);
    const readerContext = await activate(browser, reader.token);
    try {
      const tab = await login(readerContext, reader.username, password);
      await tab.goto(`/#/spaces/${space}/settings/access`);
      await expect(tab.getByRole("heading", { level: 1, name: "Knihovna" })).toBeVisible();
      await expect(tab.getByText("Nastavení prostoru může otevřít jen jeho správce.")).toBeVisible();
      await tab.goto("/#/settings/people/accounts");
      await expect(tab.getByText("Správu wiki může otevřít jen správce wiki.")).toBeVisible();
    } finally { await readerContext.close(); }
  });

  test("AD5-9 Změnit on a grant lands on its level, Esc closes it and gives the focus back to Změnit", async ({ page, context }) => {
    await signIn(page);
    const space = await adminSpace(context);
    const reader = await account(context, "Reader", space);
    await page.goto(`/#/spaces/${space}/settings/access`);
    const change = page.locator(`[data-access-entry]`).filter({ hasText: reader.username }).getByRole("button", { name: /^Změnit přístup/ });
    await change.focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toMatch(/^button Čte/);
    await page.keyboard.press("Escape");
    await expect(page.locator("[data-access-editor]")).toHaveCount(0);
    await expect(change).toBeFocused();
  });

  test("AD5-R a reader sees no admin control and never the title of a page with own access", async ({ browser, page, context }) => {
    await signIn(page);
    const space = await adminSpace(context);
    const secretTitle = `UX-admin Tajná ${stamp()}`;
    const secret = await createPage(context, space, secretTitle);
    const open = await createPage(context, space, `UX-admin Veřejná ${stamp()}`, null, doc("Odkaz: ", { text: "tajná", href: `#/pages/${secret}` }));
    const reader = await account(context, "Reader", space);
    // Own access copies the space's grants onto the page; the reader's copy is then taken away.
    const access = await (await context.request.get(`/api/pages/${secret}/access`)).json();
    await ok(context, "put", `/api/pages/${secret}/access/mode`, { ownAccess: true, expectedVersion: access.modeVersion ?? null });
    const own = await (await context.request.get(`/api/pages/${secret}/access`)).json();
    const grant = own.items.find((item: { userId: string }) => item.userId === reader.id)?.sources.find((source: { kind: string }) => source.kind === "page");
    if (grant) await ok(context, "put", `/api/pages/${secret}/access/grants`, { userId: reader.id, role: null, expectedVersion: grant.version });
    const readerContext = await activate(browser, reader.token);
    try {
      const tab = await login(readerContext, reader.username, password);
      const seen: string[] = [];
      tab.on("response", async response => { if (response.url().includes("/api/") && (await response.text().catch(() => "")).includes(secretTitle)) seen.push(new URL(response.url()).pathname); });
      await tab.goto(`/#/pages/${open}`);
      await expect(tab.locator(".document-header .document-title")).toBeVisible();
      await tab.waitForLoadState("networkidle");
      await expect(tab.locator("[data-edit-lock]")).toHaveCount(0);
      await expect(tab.getByRole("button", { name: /^Akce prostoru/ })).toHaveCount(0);
      await tab.getByRole("button", { name: "Další akce stránky" }).click();
      const items = await tab.locator('[role="menu"] [role="menuitem"]').allTextContents();
      expect(items.join(" | ")).not.toMatch(/koše|Přesunout|Historie|Nová stránka|šablon/);
      await tab.keyboard.press("Escape");
      await tab.goto(`/#/spaces/${space}`);
      await tab.waitForLoadState("networkidle");
      await tab.keyboard.press("Control+k");
      await tab.keyboard.type(secretTitle);
      await tab.waitForLoadState("networkidle");
      await expect(tab.locator('[role="option"]').filter({ hasText: secretTitle })).toHaveCount(0);
      await tab.keyboard.press("Escape");
      await tab.goto(`/#/pages/${secret}`);
      await expect(tab.getByText("Stránka není dostupná")).toBeVisible();
      expect(await tab.evaluate(title => document.body.innerHTML.includes(title), secretTitle)).toBe(false);
      expect(seen).toEqual([]);
    } finally { await readerContext.close(); }
  });
});
