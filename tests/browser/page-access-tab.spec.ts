import { expect, test, type Page } from "@playwright/test";

// WP3 S5: the „Přístup“ part of the page access view („Kdo má přístup“, #/pages/<id>/access) for a page manager and for a reader, against a mocked API.
test.use({ trace: "off", screenshot: "off", video: "off" });

const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Postup vydání a kontakty." }] },
] } });
const year = new Date().getFullYear();
const livePage = { id: "page", spaceId: "space", parentId: null, title: "Plán vydání", version: 3, isPublished: true, publishedVersion: 3, markdown: "Postup vydání a kontakty.", contentJson };
type Source = { kind: string; name: string; role: string; groupId?: string; grantId?: string; version?: string; expiresAt?: string | null; effective?: boolean };
const person = (userId: string, displayName: string, role: string | null, sources: Source[], extra: Record<string, unknown> = {}) => ({
  userId, displayName, username: userId, role, isWikiAdmin: false, canReadPublished: !!role, canReadDraft: role !== "Reader" && !!role,
  canEdit: role !== "Reader" && !!role, lastViewedAt: null, lastEditedAt: null, sources, ...extra });
const me = person("eva", "Eva Svobodová", "Admin", [{ kind: "spaceAdmin", name: "", role: "Admin" }], { lastViewedAt: `${year}-10-03T08:00:00Z`, lastEditedAt: `${year}-09-28T08:00:00Z` });
const jan = person("jan", "Jan Novák", "Writer", [{ kind: "page", name: "", role: "Writer", grantId: "g-jan", version: "v-jan", expiresAt: `${year}-12-31T12:00:00Z` }], { lastViewedAt: `${year}-09-30T08:00:00Z` });
const ola = person("ola", "Ola Dvořáková", "Reader", [{ kind: "page", name: "Podpora", role: "Reader", groupId: "support", grantId: "g-support", version: "v-support" }]);
const petr = person("petr", "Petr Malý", "Reader", [{ kind: "space", name: "", role: "Reader" }]);
const wiki = person("wiki", "Správce Wiki", "Admin", [{ kind: "global", name: "", role: "Admin" }], { isWikiAdmin: true });
const support = { groupId: "support", name: "Podpora", memberCount: 4, role: "Reader", sources: [{ kind: "page", name: "Podpora", role: "Reader", groupId: "support", grantId: "g-support", version: "v-support" }] };

function access(mode: "inherit" | "custom", canManage = true) {
  if (!canManage) return { spaceId: "space", spaceName: "Vývoj", isPublished: true, canManage: false, myAccess: petr, items: [], hasMore: false, pageTitle: livePage.title };
  const custom = mode === "custom";
  return {
    spaceId: "space", spaceName: "Vývoj", isPublished: true, canManage: true, myAccess: me, hasMore: false, pageTitle: livePage.title,
    mode, modeVersion: custom ? "m2" : "m1",
    items: custom ? [me, wiki, jan, ola] : [me, wiki, jan, ola, petr], groups: [support],
    counts: { manage: 2, edit: 1, read: custom ? 4 : 11, hidden: custom ? 7 : 0, loseOnReset: 1, downgradeOnReset: 0, defaultGrants: 2, losing: ["Ola Dvořáková"] },
    shareLinks: { active: 1, nextExpiry: `${year}-10-10T10:00:00Z` },
  };
}

// Who reaches the page, as GET /api/access/resources/page/page answers it (the list of ResourceAccess).
const direct = (level: string, extra: Record<string, unknown> = {}) => ({ via: "direct", label: "Přímý přístup", level, version: "v1", effective: true, ...extra });
function resource(mode: "inherit" | "custom") {
  return {
    resource: { kind: "page", id: "page", title: livePage.title, spaceId: "space", spaceName: "Vývoj" },
    canManage: true,
    automatic: [
      { label: "Správci prostoru", detail: "Spravují všechny stránky prostoru Vývoj", level: "manage", count: 1 },
      { label: "Správci wiki", detail: "Ve všech prostorech", level: "manage", count: 1 },
    ],
    entries: [
      { subject: { kind: "group", id: "support", name: "Podpora", memberCount: 4 }, level: "read", sources: [direct("read", { version: "v-support" })] },
      { subject: { kind: "user", id: "jan", name: "Jan Novák", detail: "jan" }, level: "write", sources: [direct("write", { version: "v-jan", expiresAt: `${year}-12-31T12:00:00Z` })] },
      { subject: { kind: "user", id: "ola", name: "Ola Dvořáková", detail: "ola" }, level: "read", sources: [{ via: "group", label: "Podpora", groupId: "support", level: "read", effective: true }] },
      ...(mode === "inherit" ? [{ subject: { kind: "user", id: "petr", name: "Petr Malý", detail: "petr" }, level: "read", sources: [{ via: "derived", label: "Z role v prostoru", level: "read", effective: true }] }] : []),
    ],
    hasMore: false,
  };
}

async function mockApi(page: Page, mode: "light" | "dark", canManage = true) {
  const calls: { mode: unknown[]; grants: unknown[] } = { mode: [], grants: [] };
  let accessMode: "inherit" | "custom" = "inherit";
  await page.route("**/api/**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    let value: unknown;
    if (path === "/api/setup/status") value = { databaseReady: true, requiresSetup: false };
    else if (path === "/api/auth/me") value = { id: canManage ? "eva" : "petr", username: canManage ? "eva" : "petr", displayName: canManage ? me.displayName : petr.displayName, isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode } };
    else if (path === "/api/spaces") value = [{ id: "space", name: "Vývoj", icon: "book", color: "blue", role: canManage ? "Admin" : "Reader" }];
    else if (path === "/api/spaces/space/tree") value = { spaceId: "space", version: 1, items: [{ ...livePage, sortOrder: 1024, canEdit: canManage }] };
    else if (path === "/api/pages/page") value = { ...livePage, canEdit: canManage };
    else if (path === "/api/pages/page/access") value = access(accessMode, canManage);
    else if (path === "/api/pages/page/access/mode" && request.method() === "PUT") {
      const body = request.postDataJSON(); calls.mode.push(body);
      accessMode = body.ownAccess ? "custom" : "inherit";
      value = { mode: accessMode, version: accessMode === "custom" ? "m2" : "m3" };
    }
    else if (path === "/api/access/resources/page/page") value = resource(accessMode);
    else if (path === "/api/access/resources/page/page/grants" && request.method() === "PUT") { calls.grants.push(request.postDataJSON()); await route.fulfill({ status: 204 }); return; }
    else if (path === "/api/access/candidates") value = url.searchParams.get("kind") === "group"
      ? [{ id: "support", kind: "group", name: "Podpora", memberCount: 4 }]
      : [{ id: "iva", kind: "user", name: "Iva Černá", detail: "iva" }];
    else if (path === "/api/pages/page/access/candidates") value = url.searchParams.get("kind") === "group"
      ? [{ id: "support", kind: "group", name: "Podpora", detail: "4" }]
      : [{ id: "iva", kind: "user", name: "Iva Černá", detail: "iva" }, { id: "petr", kind: "user", name: "Petr Malý", detail: "petr" }];
    else if (path === "/api/pages/page/access/explain") value = { userId: "petr", displayName: "Petr Malý", canView: false, canEdit: false, role: null, sources: [], denial: "ownAccess" };
    else if (path === "/api/spaces/space/external-sharing") value = { enabled: true, version: "policy" };
    else if (path === "/api/pages/page/share-links") value = { items: [], hasMore: false };
    else if (path === "/api/auth/csrf") value = { token: "fixture-csrf" };
    else if (/\/personal-state$|\/visit$/.test(path)) value = { isFavorite: false, visitedAt: null };
    else if (path.endsWith("/tags")) value = { pageId: "page", tags: [] };
    else if (path.startsWith("/api/me/library/")) value = { items: [], hasMore: false, nextOffset: null };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json: value });
  });
  return calls;
}

async function openAccess(page: Page) {
  await page.goto("/#/pages/page");
  await expect(page.locator(".document-body .ProseMirror")).toContainText("Postup vydání");
  await page.getByRole("button", { name: "Kdo má přístup", exact: true }).first().click();
  await expect(page).toHaveURL(/#\/pages\/page\/access$/);
  return page.locator("[data-page-sharing]");
}

for (const mode of ["light", "dark"] as const) {
  test(`page manager switches to own access, adds a grant and views the page as a person (${mode})`, async ({ page }) => {
    const calls = await mockApi(page, mode);
    const view = await openAccess(page);
    await expect(view.getByRole("heading", { level: 2 })).toHaveText("Přístup ke stránce");
    await expect(view).toContainText("„Plán vydání“ v prostoru Vývoj.");
    const tab = view.locator("[data-page-access]");
    await expect(tab).toContainText("14 lidí stránku vidí · spravovat: 2 · upravovat: 1 · jen číst: 11");
    const list = tab.locator('[data-resource-access="page"]');
    await expect(list.locator('[data-access-entry="user:jan"]')).toContainText("Přímý přístup");
    await expect(list.locator('[data-access-entry="user:jan"]')).toContainText("do 31. 12.");
    await expect(list.locator('[data-access-entry="user:petr"]')).toContainText("Z role v prostoru");
    await expect(list.locator('[data-access-entry="group:support"]')).toContainText("Skupina · 4 členové");
    await expect(list.locator('[data-access-entry="automatic:0"]')).toContainText("Správci prostoru");
    // The rows keep the inset of their card: the hover band stays inside it (it ran 9 px past it on both sides), the icon sits
    // where the card's header icon sits and the level badges end on the inset the header's button ends on.
    const inset = await list.evaluate((root) => {
      const card = root.querySelector(".settings-section")!.getBoundingClientRect();
      const header = root.querySelector(".settings-section-header")!;
      const iconLeft = header.querySelector(".st-icon-tile")!.getBoundingClientRect().left;
      const rowRects = [...root.querySelectorAll(".access-entry")].map((row) => ({
        row: row.getBoundingClientRect(),
        icon: row.querySelector(".st-icon-tile, .subject-avatar")!.getBoundingClientRect().left,
        level: row.querySelector(".access-entry-level")!.getBoundingClientRect().right,
      }));
      return { card: { left: card.left, right: card.right }, iconLeft, rowRects: rowRects.map(({ row, icon, level }) => ({ left: row.left, right: row.right, icon, level })) };
    });
    expect(inset.rowRects.length).toBeGreaterThan(3);
    for (const row of inset.rowRects) {
      expect(row.left).toBeGreaterThan(inset.card.left);
      expect(row.right).toBeLessThan(inset.card.right);
      expect(Math.abs(row.icon - inset.iconLeft)).toBeLessThanOrEqual(1);
      expect(inset.card.right - row.level).toBeGreaterThanOrEqual(17);
      expect(inset.card.right - row.level).toBeLessThanOrEqual(20);
    }
    await expect(tab).toContainText("Čte → Upravuje → Spravuje: každá úroveň zahrnuje nižší.");
    await expect(tab).toContainText("Podstránky přebírají tento přístup při vytvoření.");
    await expect(tab.locator("[data-share-summary]")).toContainText("Anonymní odkazy: 1 aktivní");
    await page.screenshot({ path: `artifacts/page-access-tab-inherit-${mode}.png`, animations: "disabled" });

    await tab.getByRole("radio", { name: /Vlastní přístup/ }).click();
    const confirm = page.getByRole("dialog", { name: "Zapnout vlastní přístup", exact: true });
    await expect(confirm).toContainText("2 výchozích přiřazení prostoru se zkopíruje");
    expect(calls.mode).toEqual([]);
    await confirm.getByRole("button", { name: "Zapnout vlastní přístup", exact: true }).click();
    await expect(tab.getByRole("radio", { name: /Vlastní přístup/ })).toHaveAttribute("aria-checked", "true");
    expect(calls.mode).toEqual([{ ownAccess: true, expectedVersion: "m1" }]);
    const hidden = tab.locator("[data-hidden-members]");
    await expect(hidden.locator("s")).toHaveText("Ostatní členové prostoru (7)");
    await expect(hidden).toContainText("Nevidí: stránka má vlastní přístup");
    await expect(hidden.locator("s")).toHaveCSS("text-decoration-line", "line-through");

    const viewAs = tab.locator("[data-view-as]");
    await viewAs.getByRole("button", { name: /Zobrazit jako/ }).click();
    await viewAs.getByRole("searchbox").fill("pet");
    await viewAs.locator('[data-value="petr"]').click();
    await expect(viewAs.locator("[data-view-as-result]")).toHaveText("Petr Malý nevidí, nemůže upravovat — stránka má vlastní přístup");
    await page.screenshot({ path: `artifacts/page-access-tab-custom-${mode}.png`, animations: "disabled" });

    // A new grant unfolds in place of the list (GrantFlow), no dialog; people come from the server as they are typed.
    await tab.locator("[data-add-access]").click();
    const flow = tab.locator("[data-resource-grant-flow]");
    await flow.getByRole("radio", { name: /Uživatel/ }).click();
    await flow.getByRole("searchbox").fill("iv");
    await flow.locator('[data-value="iva"]').click();
    await expect(flow.locator("[data-access-level-picker] .level-step-title")).toHaveText(["Čte", "Upravuje", "Spravuje"]);
    await flow.locator('[data-access-level-picker] [data-value="write"]').click();
    await flow.getByRole("button", { name: "Uložit přístup", exact: true }).click();
    await expect(tab.getByRole("status").filter({ hasText: "Iva Černá má přístup Upravuje · stránka Plán vydání." })).toBeVisible();
    expect(calls.grants).toEqual([{ subjectKind: "user", subjectId: "iva", level: "write", startAt: null, expiresAt: null, expectedVersion: null }]);

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(view).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/page-access-tab-custom-${mode}-mobile.png`, animations: "disabled" });
    await view.getByRole("link", { name: "Zpět na stránku", exact: true }).click();
    await expect(page).toHaveURL(/#\/pages\/page$/);
    await expect(view).toHaveCount(0);
  });
}

test("a reader sees only their own access and no management", { tag: '@smoke' }, async ({ page }) => {
  await mockApi(page, "light", false);
  const view = await openAccess(page);
  await expect(view.locator("[data-own-access]")).toContainText("Petr Malý má úroveň Čte");
  await expect(view.locator("[data-page-access]")).toHaveCount(0);
  await expect(view.locator("[data-resource-access]")).toHaveCount(0);
  await expect(view.getByRole("button", { name: "Přidat přístup", exact: true })).toHaveCount(0);
  await expect(view.getByRole("radio", { name: /Vlastní přístup/ })).toHaveCount(0);
  await expect(view.getByRole("link", { name: /Anonymní odkazy/ })).toHaveCount(0);
  await page.screenshot({ path: "artifacts/page-access-tab-reader-light.png", animations: "disabled" });
});
