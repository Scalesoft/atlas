import { expect, test } from "@playwright/test";

test.use({ trace: "off", screenshot: "off", video: "off" });

const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
  { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Bezpečné sdílení" }] },
  { type: "paragraph", content: [{ type: "text", text: "Obsah pevné publikované revize." }] },
] } });
const publicWidgetsContentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [
  { id: "contacts", version: 2 }, { id: "math", version: 1 },
], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Obsah pevné publikované revize." }] },
  { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } },
  { type: "mathBlock", attrs: { data: { version: 1, expression: "E = mc^2" } } },
] } });
const livePage = { id: "page", spaceId: "space", parentId: null, title: "Příručka sdílení", version: 7, isPublished: true, publishedVersion: 7, markdown: "Obsah pevné publikované revize.", contentJson };
const person = { userId: "admin", displayName: "Jana Nováková", username: "jana", role: "Admin", isWikiAdmin: false, canReadPublished: true, canReadDraft: true, canEdit: true, lastViewedAt: null, lastEditedAt: null, sources: [{ kind: "spaceAdmin", name: "", role: "Admin" }] };
const link = { id: "link", pageId: "page", pageTitle: livePage.title, name: "Pro klienta", revisionVersion: 7, createdByName: person.displayName, createdAt: "2026-09-13T10:00:00Z", expiresAt: "2026-09-20T10:00:00Z", revokedAt: null, status: "active" };

for (const mode of ["light", "dark"] as const) {
  test(`access and links preserve the live document across desktop and mobile (${mode})`, async ({ page, context }) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    let links: typeof link[] = [], issued = false;
    await page.route("**/api/**", async route => {
      const request = route.request(), url = new URL(request.url()), path = url.pathname;
      let value: unknown;
      if (path === "/api/setup/status") value = { databaseReady: true, requiresSetup: false };
      else if (path === "/api/auth/me") value = { id: "admin", username: "jana", displayName: person.displayName, isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode } };
      else if (path === "/api/spaces") value = [{ id: "space", name: "Vývoj", icon: "book", color: "blue", role: "Admin" }];
      else if (path === "/api/spaces/space/tree") value = { spaceId: "space", version: 1, items: [{ ...livePage, sortOrder: 1024, canEdit: true }] };
      else if (path === "/api/pages/page") value = livePage;
      else if (path === "/api/pages/page/access") value = { spaceId: "space", spaceName: "Vývoj", isPublished: true, canManage: true, myAccess: person, items: [person], hasMore: false,
        pageTitle: livePage.title, mode: "inherit", modeVersion: "m1", groups: [], shareLinks: { active: links.length, nextExpiry: links[0]?.expiresAt ?? null },
        counts: { manage: 1, edit: 0, read: 0, hidden: 0, loseOnReset: 0, downgradeOnReset: 0, defaultGrants: 0, losing: [] } };
      else if (path === "/api/access/resources/page/page") value = { resource: { kind: "page", id: "page", title: livePage.title, spaceId: "space", spaceName: "Vývoj" }, canManage: true,
        automatic: [{ label: "Správci prostoru", detail: "Spravují všechny stránky prostoru Vývoj", level: "manage", count: 1 }], entries: [], hasMore: false };
      else if (path === "/api/spaces/space/external-sharing") value = { enabled: true, version: "policy" };
      else if (path === "/api/pages/page/share-links" && request.method() === "GET") value = { items: links, hasMore: false };
      else if (path === "/api/pages/page/share-links" && request.method() === "POST") { issued = true; links = [link]; value = { link, url: "/s/sas_browser_secret" }; }
      else if (path === "/api/pages/page/share-links/link" && request.method() === "DELETE") { links = []; await route.fulfill({ status: 204 }); return; }
      else if (path === "/api/auth/csrf") value = { token: "fixture-csrf" };
      else if (/\/personal-state$|\/visit$/.test(path)) value = { isFavorite: false, visitedAt: null };
      else if (path.endsWith("/tags")) value = { pageId: "page", tags: [] };
      else if (path.startsWith("/api/me/library/")) value = { items: [], hasMore: false, nextOffset: null };
      else { await route.fulfill({ status: 404, json: {} }); return; }
      await route.fulfill({ json: value });
    });
    await page.goto("/#/pages/page");
    const article = page.locator(".document-body .ProseMirror");
    await expect(article).toContainText("pevné publikované");
    // „Kdo má přístup“ is a view of the page with linked parts, not a dialog over the document.
    await page.getByRole("button", { name: "Kdo má přístup", exact: true }).first().click();
    await expect(page).toHaveURL(/#\/pages\/page\/access$/);
    const view = page.locator("[data-page-sharing]");
    await expect(view.locator('[data-resource-access="page"] [data-access-entry="automatic:0"]')).toContainText("Správci prostoru");
    await view.getByRole("link", { name: /Anonymní odkazy/ }).click();
    await expect(page).toHaveURL(/#\/pages\/page\/access\/links$/);
    await expect(view).toContainText("kdo odkaz má, bez přihlášení");
    await view.getByRole("button", { name: "Nový odkaz", exact: true }).click();
    await view.getByLabel("Název odkazu").fill("Pro klienta");
    await view.getByRole("button", { name: "Vytvořit odkaz", exact: true }).click();
    const secret = view.locator("[data-issued-link]");
    await secret.getByRole("button", { name: "Zobrazit", exact: true }).click();
    await expect(secret.locator("[data-one-time-value]")).toHaveText(/\/s\/sas_browser_secret$/);
    await secret.getByRole("button", { name: "Kopírovat", exact: true }).click();
    await expect(secret.getByRole("button", { name: "Zkopírováno", exact: true })).toBeVisible();
    await secret.getByLabel("Odkaz mám uložený").check();
    await secret.getByRole("button", { name: "Pokračovat", exact: true }).click();
    await expect(secret).toHaveCount(0);
    await expect(view).not.toContainText("sas_browser_secret");
    await view.getByRole("button", { name: "Zneplatnit odkaz Pro klienta", exact: true }).click();
    const confirm = page.getByRole("dialog", { name: "Zneplatnit odkaz", exact: true });
    await expect(confirm).toContainText("přestane platit hned");
    await confirm.getByRole("button", { name: "Zneplatnit odkaz", exact: true }).click();
    await expect(view).toContainText("zatím nejsou žádné anonymní odkazy");
    expect(issued).toBe(true);
    await page.screenshot({ path: `artifacts/access-sharing-${mode}-desktop.png`, animations: "disabled" });
    await view.getByRole("link", { name: "Zpět na stránku", exact: true }).click();
    await expect(page).toHaveURL(/#\/pages\/page$/);
    await expect(article).toContainText("pevné publikované");
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole("button", { name: "Další akce stránky", exact: true }).click();
    await page.getByRole("menuitem", { name: "Kdo má přístup", exact: true }).click();
    await expect(view).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/access-sharing-${mode}-mobile.png`, animations: "disabled" });
  });
}

test("public link retries uniformly and performs no private requests", async ({ page }) => {
  const requests: string[] = []; let attempt = 0;
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/api/**", async route => {
    const path = new URL(route.request().url()).pathname; requests.push(path);
    if (path !== "/api/shared/sas_public") { await route.fulfill({ status: 500, json: {} }); return; }
    if (!attempt++) { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json: { title: "Veřejná příručka", contentJson: publicWidgetsContentJson, revisionVersion: 7, publishedAt: "2026-09-13T10:00:00Z", expiresAt: "2026-09-20T10:00:00Z" } });
  });
  await page.goto("/s/sas_public");
  await expect(page.getByRole("alert")).toContainText("Odkaz není dostupný");
  await page.getByRole("button", { name: "Zkusit znovu", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Veřejná příručka" })).toBeVisible();
  await expect(page.getByRole("document", { name: "Sdílený dokument pouze pro čtení" })).toContainText("pevné publikované");
  await expect(page.locator(".md-contact-card")).toContainText("Jana Nováková");
  await expect(page.locator("math")).toBeVisible();
  await expect(page.locator("[contenteditable=true], [data-testid=presence-stack]")).toHaveCount(0);
  expect(requests).toEqual(["/api/shared/sas_public", "/api/shared/sas_public"]);
  await expect(page.locator(".public-share-card")).toHaveCSS("animation-name", "none");
  const logo = await page.locator(".public-share-brand .atlas-brand-logo").boundingBox();
  expect(logo?.width).toBeLessThanOrEqual(32);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: "artifacts/access-sharing-public-mobile.png", animations: "disabled" });
});

test("space settings manages anonymous sharing from the keyboard on mobile", async ({ page }) => {
  let enabled = true;
  const policyUpdates: boolean[] = [];
  let spaceLinks = [link];
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route("**/api/**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    let value: unknown;
    if (path === "/api/setup/status") value = { databaseReady: true, requiresSetup: false };
    else if (path === "/api/auth/me") value = { id: "admin", username: "jana", displayName: person.displayName, isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "light" } };
    else if (path === "/api/spaces") value = [{ id: "space", name: "Vývoj", icon: "book", color: "blue", role: "Admin" }];
    else if (path === "/api/spaces/space/tree") value = { spaceId: "space", version: 1, items: [{ ...livePage, sortOrder: 1024, canEdit: true }] };
    else if (path === "/api/spaces/space/settings") value = { id: "space", name: "Vývoj", icon: "book", color: "blue", description: "", version: "settings" };
    else if (path === "/api/spaces/space/external-sharing" && request.method() === "GET") value = { enabled, version: `policy-${policyUpdates.length}` };
    else if (path === "/api/spaces/space/external-sharing" && request.method() === "PUT") {
      enabled = (await request.postDataJSON()).enabled;
      policyUpdates.push(enabled);
      if (!enabled) spaceLinks = [];
      value = { enabled, version: `policy-${policyUpdates.length}` };
    }
    else if (path === "/api/spaces/space/share-links") value = { items: spaceLinks, hasMore: false };
    else if (path === "/api/auth/csrf") value = { token: "fixture-csrf" };
    else if (path.startsWith("/api/me/library/")) value = { items: [], hasMore: false, nextOffset: null };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json: value });
  });

  await page.goto("/#/spaces/space");
  await page.getByRole("button", { name: "Akce prostoru: Vývoj", exact: true }).click();
  await page.getByRole("menuitem", { name: "Nastavení prostoru", exact: true }).click();
  // Nastavení prostoru is a page; on a phone its sections are in the "Sekce" sheet, as real links.
  await expect(page).toHaveURL(/#\/spaces\/space\/settings\/general$/);
  const dialog = page.locator("[data-settings-host]");
  await dialog.getByRole("button", { name: /Sekce/ }).click();
  const sharing = page.getByRole("dialog", { name: "Sekce" }).getByRole("link", { name: "Sdílení", exact: true });
  await sharing.focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/spaces\/space\/settings\/sharing$/);
  await expect(dialog.getByRole("heading", { level: 2, name: "Sdílení" })).toBeVisible();
  await expect(dialog).toContainText("Příručka sdílení");
  await expect(dialog).toContainText("Pro klienta");
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);

  const toggle = dialog.getByRole("switch", { name: /Povolit anonymní odkazy/ });
  await expect(toggle).toBeChecked();
  await toggle.click();
  // Turning links off is destructive: a confirmation dialog, nothing sent before it.
  const confirm = page.getByRole("dialog", { name: "Vypnout anonymní odkazy" });
  await expect(confirm).toContainText("Vypnutím trvale zrušíte všechny aktivní anonymní odkazy");
  expect(policyUpdates).toEqual([]);
  await confirm.getByRole("button", { name: "Vypnout a zrušit odkazy", exact: true }).click();
  await expect(toggle).not.toBeChecked();
  await expect(dialog).toContainText("Dříve zrušené odkazy se po zapnutí neobnoví");
  await toggle.click();
  await expect(toggle).toBeChecked();
  expect(policyUpdates).toEqual([false, true]);
});
