import { expect, test, type BrowserContext } from "@playwright/test";
import { PAGE_TEXT } from "./editing";
import {
  checkOf, block, blocksOfContent, createPage, createSpace, finishEditing, kindOf, login, markCatalog, metaOf, missingOf, openPage, partOf, percentOf, publish, readPage, readerPassword, requirementNode,
  requirements, saveDraft, send, skipReason, startEditing, stateOf, stories, summaryNode, summaryOf,
} from "./projector-helpers";

// Story 5: rights. A space Reader sees the published blocks and the summary line but cannot change anything (no editing, nothing to click,
// the server refuses every write with 403); and sees the published revision, not the draft of an editor. A guest with a grant on one page
// reaches that page and nothing else of the space: not the catalog page, not the other deployments, not their numbers.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

async function status(context: BrowserContext, path: string) { return (await context.request.get(path)).status(); }

test("čtenář prostoru vidí publikované bloky a souhrn, nic nezmění; server odmítne zápis s 403", async ({ browser }) => {
  test.setTimeout(240_000);
  const admin = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  const reader = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const a = await login(admin);
    const r = await login(reader, "browser-reader", readerPassword);
    const readerId = (await (await reader.request.get("/api/auth/me")).json()).id;
    const spaceId = await createSpace(admin, "Práva čtenáře");
    expect((await send(admin, "put", `/api/spaces/${spaceId}/access`, { userId: readerId, role: "Reader", expectedVersion: null })).status()).toBe(204);

    const catalog = await createPage(admin, spaceId, "Katalog", requirementNode({ title: "Napojení na NIS", key: "nis" }));
    await markCatalog(admin, catalog.id);
    await publish(admin, catalog.id);
    const kbn = await createPage(admin, spaceId, "KBN — Production", summaryNode(),
      requirementNode({ title: "Napojení na NIS", key: "nis", state: "done", date: "2026-09-10", party: "Dodavatel NIS" }),
      requirementNode({ title: "Notifikace", key: "notifications", requires: ["nis"] }),
      requirementNode({ title: "SSL certifikát", key: "ssl", direction: "inbound", state: "waiting_for_input", party: "IT KBN" }));
    await publish(admin, kbn.id);
    const publishedContent = (await readPage(admin, kbn.id)).contentJson;

    // The editor completes Notifikace in a draft that is not published.
    await startEditing(a, kbn.id);
    await checkOf(block(a, "Notifikace")).click();
    await saveDraft(a);
    expect((await requirements(admin, kbn.id)).summary).toMatchObject({ done: 2, percent: 66, total: 3 });

    // --- what the Reader sees ---
    await r.goto(`/#/pages/${kbn.id}`);
    const article = r.locator(PAGE_TEXT);
    await expect(percentOf(r)).toHaveText(/^33\s*%$/); // published: one of three, not the editor's draft
    await expect(summaryOf(r)).toContainText("1 z 3 hotovo");
    await partOf(r, "waiting").click();
    await expect(summaryOf(r).locator("[data-summary-party]", { hasText: "IT KBN" })).toContainText("SSL certifikát");
    await expect(missingOf(r)).toContainText("Nedodané vstupy 1");
    await expect(stateOf(block(r, "Napojení na NIS"))).toContainText("hotovo");
    await expect(metaOf(block(r, "Napojení na NIS"))).toContainText("10. 9. 2026");
    await expect(kindOf(block(r, "Napojení na NIS"))).toHaveAttribute("aria-label", /úkol, Dodavatel NIS/); // the party is the heading's attribute, shown on its kind chip
    await expect(article).toContainText("Notifikace");
    await expect(article.locator(".rq-check:not([aria-disabled])")).toHaveCount(0);
    await expect(r.locator("[data-edit-lock]")).toHaveCount(0);
    const seen = await requirements(reader, kbn.id);
    expect(seen.working).toBe(false);
    expect(seen.blocks.map(item => [item.key, item.state])).toEqual([["nis", "done"], ["notifications", "planned"], ["ssl", "waiting_for_input"]]);
    expect(seen.summary).toMatchObject({ done: 1, percent: 33, total: 3 });
    expect(seen.blocks.find(item => item.key === "notifications")!.waitingOn).toEqual([]);
    // The catalog of the space is published pages; the Reader may read it.
    const offered = await (await reader.request.get(`/api/projector/spaces/${spaceId}/catalog`)).json();
    expect(offered.definitions.map((item: { key: string }) => item.key)).toEqual(["nis"]);

    // --- what the Reader cannot do: the server says no ---
    const attempt = JSON.parse(publishedContent);
    const forged = JSON.stringify({ ...attempt, document: JSON.parse(JSON.stringify(attempt.document).replace('"state":"planned"', '"state":"done"')) });
    expect((await send(reader, "put", `/api/pages/${kbn.id}`, { title: kbn.title, contentJson: forged, markdown: kbn.title, expectedVersion: (await readPage(admin, kbn.id)).version })).status()).toBe(403);
    expect((await send(reader, "put", `/api/projector/pages/${kbn.id}/catalog`, { isCatalog: true })).status()).toBe(403);
    expect((await send(reader, "put", `/api/projector/pages/${catalog.id}/catalog`, { isCatalog: false })).status()).toBe(403);
    // A Reader may not create pages in the space; the server answers 404 here (not 403).
    expect([403, 404]).toContain((await send(reader, "post", `/api/projector/pages/${kbn.id}/deployments`, { spaceId, title: "Kopie čtenáře" })).status());
    // Nothing changed: the draft and the publication are what the editor left.
    const after = await requirements(admin, kbn.id);
    expect(after.working).toBe(true);
    expect(after.blocks.map(item => item.state)).toEqual(["done", "done", "waiting_for_input"]);
    expect(blocksOfContent(await readPage(admin, kbn.id)).map(item => item.attrs.state)).toEqual(["done", "done", "waiting_for_input"]);
    const space = await (await admin.request.get(`/api/spaces/${spaceId}/tree`)).json();
    expect(space.items.map((item: { title: string }) => item.title).sort()).toEqual(["KBN — Production", "Katalog"]);
    // The editor publishes: now the Reader's numbers follow.
    await finishEditing(a);
    await r.reload();
    await expect(percentOf(r)).toHaveText(/^66\s*%$/);
    await expect(summaryOf(r)).toContainText("2 z 3 hotovo");
  } finally {
    await admin.close();
    await reader.close();
  }
});

test("host s právem na jednu stránku nevidí katalog ani jiná nasazení", async ({ browser }) => {
  test.setTimeout(180_000);
  const admin = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  const guest = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    await login(admin);
    const g = await login(guest, "browser-reader", readerPassword);
    const guestId = (await (await guest.request.get("/api/auth/me")).json()).id;
    const spaceId = await createSpace(admin, "Host jedné stránky");
    const catalog = await createPage(admin, spaceId, "Interní katalog", requirementNode({ title: "Tajný katalogový požadavek", key: "secret.catalog" }));
    await markCatalog(admin, catalog.id);
    await publish(admin, catalog.id);
    const mine = await createPage(admin, spaceId, "Nasazení pro hosta", summaryNode(),
      requirementNode({ title: "Požadavek hosta", key: "guest.item", direction: "inbound", state: "waiting_for_input", party: "Host" }));
    const other = await createPage(admin, spaceId, "Cizí nasazení", summaryNode(),
      requirementNode({ title: "Cizí tajný blok", key: "other.item", direction: "inbound", state: "waiting_for_input", party: "Jiná strana" }));
    await publish(admin, mine.id);
    await publish(admin, other.id);
    expect((await send(admin, "put", `/api/pages/${mine.id}/access/grants`, { userId: guestId, role: "Reader", expectedVersion: null })).status()).toBe(204);

    // The granted page: readable, with its blocks.
    await g.goto(`/#/pages/${mine.id}`);
    await expect(g.locator(PAGE_TEXT)).toContainText("Požadavek hosta");
    await expect(kindOf(block(g, "Požadavek hosta"))).toHaveAttribute("aria-label", /vstup, od Host/);
    expect((await requirements(guest, mine.id)).blocks.map(item => item.key)).toEqual(["guest.item"]);

    // Everything else of the space answers as if it did not exist.
    expect(await status(guest, `/api/projector/pages/${other.id}/requirements`)).toBe(404);
    expect(await status(guest, `/api/projector/pages/${catalog.id}/requirements`)).toBe(404);
    for (const path of [`/api/projector/spaces/${spaceId}/catalog`, `/api/projector/spaces/${spaceId}/overview?filter=all`, `/api/projector/spaces/${spaceId}/waiting`]) {
      const response = await guest.request.get(path);
      const text = await response.text();
      expect(response.status() === 404 || response.status() === 200, `${path} → ${response.status()}`).toBe(true);
      for (const secret of ["Tajný katalogový požadavek", "secret.catalog", "Cizí tajný blok", "other.item", "Cizí nasazení", "Interní katalog", "Jiná strana"]) {
        expect(text, `${path} must not mention ${secret}`).not.toContain(secret);
      }
    }
    expect(await status(guest, `/api/pages/${other.id}`)).toBe(404);
    expect(await status(guest, `/api/pages/${catalog.id}`)).toBe(404);
    expect((await send(guest, "put", `/api/projector/pages/${catalog.id}/catalog`, { isCatalog: false })).status()).toBeGreaterThanOrEqual(403);
    expect((await send(guest, "post", `/api/projector/pages/${other.id}/deployments`, { spaceId, title: "Kopie hosta" })).status()).toBeGreaterThanOrEqual(403);

    // In the browser the other pages are not there either.
    await openPage(g, mine.id);
    await g.goto(`/#/pages/${other.id}`);
    await expect(g.getByText("Cizí tajný blok")).toHaveCount(0);
    await expect(g.getByText("Interní katalog")).toHaveCount(0);
    await g.goto(`/#/pages/${catalog.id}`);
    await expect(g.getByText("Tajný katalogový požadavek")).toHaveCount(0);
  } finally {
    await admin.close();
    await guest.close();
  }
});
