import { expect, test, type Page, type WebSocketRoute } from "@playwright/test";
import { startEditing } from "./editing";

// WP4 V6: password references in the real editor against a mocked API and a mocked SignalR hub (/hubs/markdown).
// No wiki is needed; run against the Vite dev server:
//   pnpm --dir src/Atlas.Web exec vite --port 5361
//   $env:ATLAS_URL='http://127.0.0.1:5361'; npx --prefix tests/browser playwright test secret-references.spec.ts
// The writer inserts a reference through the picker, reveals it (a released value only in the page DOM, never in a Yjs
// update), and a `secretAccessRevoked` event hides it at once.
// SECRET_REFERENCES_SHOTS=<folder> also saves screenshots of the picker and the revealed card.
test.use({ trace: "off", screenshot: "off", video: "off" });
const shots = process.env.SECRET_REFERENCES_SHOTS;
const shoot = async (page: Page, name: string) => { if (shots) await page.screenshot({ path: `${shots}/${name}.png`, animations: "disabled" }); };

const secretId = "d847b3af-4a87-4e97-aab8-a74a4cf48c89";
const VALUE = "CANARY-router-heslo";
const document = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Přístup k routeru v serverovně." }] }] };
const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document });
const livePage = { id: "page", spaceId: "space", parentId: null, title: "Síť", version: 2, isPublished: true, publishedVersion: 2, markdown: "Přístup k routeru v serverovně.", contentJson };
const reference = { id: secretId, title: "Router", path: "Provoz / Síť", vaultId: "vault", vaultName: "Provoz", spaceId: "space", fields: ["title", "path", "userName", "password"], level: "read" };
const RS = "\u001e";

interface Hub { sockets: Set<WebSocketRoute>; updates: string[]; broadcast(target: string, ...args: unknown[]): void }

/** Enough of the SignalR JSON protocol and of the Markdown sync hub for one writer: join as the seeding snapshotter,
 *  accept snapshots, updates and awareness, and push server events to every connection. */
async function mockHub(page: Page): Promise<Hub> {
  const hub: Hub = {
    sockets: new Set(), updates: [],
    broadcast(target, ...args) { for (const socket of hub.sockets) socket.send(JSON.stringify({ type: 1, target, arguments: args }) + RS); },
  };
  let connections = 0;
  await page.route("**/hubs/markdown/negotiate**", route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: `c${++connections}`, connectionToken: `t${connections}`,
    availableTransports: [{ transport: "WebSockets", transferFormats: ["Text", "Binary"] }],
  } }));
  let seq = 0;
  await page.routeWebSocket(/\/hubs\/markdown/, socket => {
    hub.sockets.add(socket);
    socket.onClose(() => hub.sockets.delete(socket));
    socket.onMessage(raw => {
      for (const frame of String(raw).split(RS).filter(Boolean)) {
        const message = JSON.parse(frame);
        if (message.protocol) { socket.send("{}" + RS); continue; }
        if (message.type === 6) continue;
        if (message.type !== 1 || !message.invocationId) continue;
        const reply = (result?: unknown) => socket.send(JSON.stringify({ type: 3, invocationId: message.invocationId, ...(result === undefined ? {} : { result }) }) + RS);
        const [documentId, second, third] = message.arguments;
        switch (message.target) {
          case "JoinDocument":
          case "JoinDocumentWithSchema":
            reply({ snapshot: null, updates: [], seq, seedMarkdown: livePage.markdown, isSnapshotter: true, schema: second ?? null, seedEditorJson: document, canWrite: true });
            break;
          case "SendUpdate":
            hub.updates.push(Buffer.from(String(third), "base64").toString("latin1"));
            reply(++seq);
            break;
          case "PushSnapshot":
            hub.updates.push(Buffer.from(String(third), "base64").toString("latin1"));
            reply();
            break;
          default:
            void documentId;
            reply();
        }
      }
    });
  });
  return hub;
}

async function mockApi(page: Page, options: { canEdit?: boolean; content?: string; reauth?: boolean } = {}) {
  const { canEdit = true, content = contentJson } = options;
  // The "always" mode: every release needs a fresh proof.
  const reauthRequired = options.reauth === true;
  let proofConsumed = false;
  // A revoked grant: the server no longer discloses the reference (404) nor releases its values.
  let revoked = false;
  const calls = { search: [] as string[], release: [] as unknown[], reauth: [] as unknown[], password: [] as unknown[], revoke() { revoked = true; } };
  await page.route("**/api/**", async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    let value: unknown;
    if (path === "/api/setup/status") value = { databaseReady: true, requiresSetup: false };
    else if (path === "/api/auth/me") value = { id: "jana", username: "jana", displayName: "Jana Nováková", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "light" } };
    else if (path === "/api/auth/csrf") value = { token: "fixture-csrf" };
    else if (path === "/api/spaces") value = [{ id: "space", name: "Provoz", icon: "book", color: "blue", role: "Writer" }];
    else if (path === "/api/spaces/space/tree") value = { spaceId: "space", version: 1, items: [{ ...livePage, sortOrder: 1024, canEdit }] };
    else if (path === "/api/pages/page") value = { ...livePage, contentJson: content, canEdit };
    else if (path === "/api/pages/page/collaboration") value = { documentId: "page:0f8e6a1c2b3d4e5f8a9b0c1d2e3f4a5b:1a2b3c4d5e6f47089a0b1c2d3e4f5a6b", generation: "gen", currentVersion: 2 };
    else if (path === "/api/secrets/search") { calls.search.push(url.searchParams.get("q") ?? ""); value = [reference]; }
    else if (revoked && path.startsWith(`/api/secrets/${secretId}/`)) { await route.fulfill({ status: 404, json: {} }); return; }
    else if (path === `/api/secrets/${secretId}/reference`) value = reference;
    else if (path === `/api/secrets/${secretId}/release`) {
      const body = request.postDataJSON();
      calls.release.push(body);
      // A proof is single-use: the server consumes it with the first release that presents it.
      const proofValid = body.proofId === "challenge-1" && !proofConsumed;
      if (reauthRequired && !proofValid) {
        await route.fulfill({ status: 403, json: { title: "Vyžaduje nové přihlášení.", code: "reauth_required", reauth: { mode: "always", modeSource: "secret", supported: true } } });
        return;
      }
      if (body.proofId) proofConsumed = true;
      value = { values: Object.fromEntries((body.fields as string[]).map(field => [field, field === "password" ? VALUE : "admin"])), expiresInSeconds: 60 };
    }
    else if (path === "/api/security/reauth") { calls.reauth.push(request.postDataJSON()); value = { id: "challenge-1", method: "password", expiresAt: "2099-01-01T00:00:00Z", redirectUrl: null }; }
    else if (path === "/api/security/reauth/challenge-1/password") { calls.password.push(request.postDataJSON()); value = { id: "challenge-1", state: "verified" }; }
    else if (/\/personal-state$|\/visit$/.test(path)) value = { isFavorite: false, visitedAt: null };
    else if (path.endsWith("/tags")) value = { pageId: "page", tags: [] };
    else if (path.startsWith("/api/me/library/")) value = { items: [], hasMore: false, nextOffset: null };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json: value, headers: { "cache-control": "no-store" } });
  });
  return calls;
}

test("a writer inserts a reference through the picker, reveals it and loses it on revocation", async ({ page }) => {
  const hub = await mockHub(page);
  const calls = await mockApi(page);
  await page.goto("/#/pages/page");
  await expect(page.locator(".document-body")).toContainText("Přístup k routeru");
  await startEditing(page);

  await page.locator(".document-body .ProseMirror:not(.editor-hydrating *) p").first().click();
  await page.getByRole("button", { name: "Další akce stránky" }).click();
  await page.getByRole("menuitem", { name: /Heslo z trezoru/ }).click();
  const picker = page.getByRole("dialog", { name: "Vložit heslo" });
  await expect(picker).toBeVisible();
  await picker.getByRole("searchbox").fill("rou");
  await expect.poll(() => calls.search).toContain("rou");
  await expect(picker.locator(`[role="radio"][data-value="${secretId}"]`)).toBeVisible();
  await shoot(page, "secret-picker");
  await picker.locator(`[role="radio"][data-value="${secretId}"]`).click();
  await expect(picker).toBeHidden();

  const card = page.locator(".document-body .md-secret-card").first();
  await expect(card).toBeVisible();
  // Only the reference travels in the shared document, never a value.
  await expect.poll(() => hub.updates.some(update => update.includes(secretId))).toBe(true);

  await card.locator(".md-concealed").first().hover();
  await card.getByRole("button", { name: "Odkrýt" }).click();
  await expect(card).toContainText(VALUE);
  await shoot(page, "secret-revealed");
  expect(calls.release).toEqual([{ fields: ["password"], operation: "reveal" }]);
  expect(hub.updates.some(update => update.includes(VALUE))).toBe(false);

  // The server revokes the grant and then tells the writer's connections; discovery runs again and hides the reference.
  calls.revoke();
  hub.broadcast("secretAccessRevoked", { secretId });
  await expect(page.locator(".document-body")).not.toContainText(VALUE);
  await expect(page.locator(".document-body .md-secret-card")).toHaveCount(0);
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }))).not.toContain(VALUE);
});

test("a reader confirms one fresh sign-in for a reveal of several fields and the value never reaches storage", async ({ page }) => {
  await mockHub(page);
  const withReference = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "secrets", version: 4 }], document: { type: "doc", content: [
    ...document.content,
    { type: "secretReference", attrs: { secretId, field: "password", fields: ["username", "password"], showCopy: true, showDetails: false, autoHideSeconds: 60 } },
  ] } });
  const calls = await mockApi(page, { canEdit: false, content: withReference, reauth: true });
  await page.goto("/#/pages/page");
  const card = page.locator(".document-body .md-secret-card").first();
  await expect(card).toBeVisible();
  await card.locator(".md-concealed").first().hover();
  await card.getByRole("button", { name: "Odkrýt" }).click();

  const dialog = page.getByRole("dialog", { name: "Ověření před zobrazením hesla" });
  await expect(dialog).toBeVisible();
  expect(calls.reauth).toEqual([expect.objectContaining({ operation: "reveal", secretId, target: { secretId } })]);
  await dialog.getByLabel("Heslo k vašemu účtu").fill("moje-heslo");
  await shoot(page, "secret-reauth");
  await dialog.getByRole("button", { name: "Zobrazit heslo" }).click();
  await expect(dialog).toBeHidden();
  await expect(card).toContainText(VALUE);
  expect(calls.password).toEqual([{ password: "moje-heslo" }]);
  await expect(card).toContainText("admin");
  // One request for both fields: the single-use proof is presented once, and one sign-in covers the whole reveal.
  expect(calls.reauth).toHaveLength(1);
  expect(calls.release).toEqual([
    { fields: expect.arrayContaining(["userName", "password"]), operation: "reveal" },
    { fields: expect.arrayContaining(["userName", "password"]), operation: "reveal", proofId: "challenge-1" },
  ]);
  expect((calls.release as { fields: string[] }[]).every(call => call.fields.length === 2)).toBe(true);
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }))).not.toContain(VALUE);
});
