import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import {
  adminPassword, checkOf, depsOf, allBlocks, block, focusTitle, byKey, createPage, createSpace, editorOf, login, markCatalog, openCatalog, outsideBlocks, percentOf, pickFromCatalog, tickInCatalog, publish, requirementNode, requirements,
  blocksOfContent, readPage, saveDraft, skipReason, startEditing, stateOf, stories, summaryNode, summaryOf, titlesOnPage, waitingOf,
} from "./projector-helpers";

// Story 6: validation and odd cases. Each test has a space of its own, so its catalog is its own.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

let context: BrowserContext, page: Page;
test.beforeEach(async ({ browser }) => {
  context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  page = await login(context);
});
test.afterEach(async () => { await context.close(); });

/** A published catalog page of the space with these blocks. */
async function makeCatalog(spaceId: string, ...blocks: Parameters<typeof requirementNode>[0][]) {
  const catalog = await createPage(context, spaceId, "Katalog", ...blocks.map(seed => requirementNode(seed)));
  await markCatalog(context, catalog.id);
  await publish(context, catalog.id);
}

test("cyklická závislost v katalogu: přátelská zpráva s cestou, nic se nevloží", async () => {
  test.setTimeout(120_000);
  const spaceId = await createSpace(context, "Cyklus v katalogu");
  await makeCatalog(spaceId, { title: "Cyklus A", key: "cyc.a", requires: ["cyc.b"] }, { title: "Cyklus B", key: "cyc.b", requires: ["cyc.a"] }, { title: "Volný požadavek", key: "free" });
  const target = await createPage(context, spaceId, "Cílová stránka");
  await startEditing(page, target.id);
  await editorOf(page).focus();
  await page.waitForTimeout(60);
  await page.keyboard.press("Control+End");
  await openCatalog(page);
  await pickFromCatalog(page, "Cyklus A", "Cyklus A");
  const message = page.locator("[data-requirement-catalog] [role=alert]");
  await expect(message).toBeVisible();
  await expect(message).toContainText("kruh");
  await expect(message).toContainText("cyc.a → cyc.b → cyc.a");
  await expect(message).not.toContainText(/exception|stack|500|cyclic_dependency/i);
  await expect(allBlocks(page)).toHaveCount(0);
  // The panel stays usable: another entry of the catalog is inserted, the cycle left nothing behind.
  await pickFromCatalog(page, "Volný", "Volný požadavek");
  await expect(block(page, "Volný požadavek")).toBeVisible();
  await expect.poll(() => titlesOnPage(page)).toEqual(["Volný požadavek"]);
  await saveDraft(page);
  expect((await requirements(context, target.id)).blocks.map(item => item.key)).toEqual(["free"]);
  // The server's own answer for the same request (the token API's resolve; the web resolves locally) is a 400 that names the path.
  const csrf = { "X-CSRF-TOKEN": (await (await context.request.get("/api/auth/csrf")).json()).token };
  const me = await (await context.request.get("/api/auth/me")).json();
  const access = await (await context.request.get(`/api/admin/users/${me.id}/api-access`)).json();
  if (!access.level || access.level === "none") {
    await context.request.put(`/api/admin/users/${me.id}/api-access`, { data: { level: "read", expectedVersion: access.version }, headers: csrf });
  }
  // Issuing a token needs a fresh sign-in (SEC-A-2); verifying the password opens the ten-minute window of this session.
  const challenge = await (await context.request.post("/api/security/reauth", { data: { operation: "security" }, headers: csrf })).json();
  expect((await context.request.post(`/api/security/reauth/${challenge.id}/password`, { data: { password: adminPassword }, headers: csrf })).status()).toBe(200);
  const issued = await (await context.request.post("/api/profile/api-tokens", { data: { name: "Kruh", scopes: ["read"], channels: ["rest"], expiresInDays: 1 }, headers: csrf })).json();
  const response = await context.request.get(`/api/v1/projector/spaces/${spaceId}/resolve?keys=cyc.b`, { headers: { Authorization: `Bearer ${issued.secret}`, Cookie: "" } });
  expect(response.status()).toBe(400);
  expect((await response.json()).path).toEqual(["cyc.b", "cyc.a", "cyc.b"]);
});

/** The catalog of the NIS story: Portál needs NIS and Notifikace; the page already has NIS, marked as not applicable. */
async function pageWithDismissedNis() {
  const spaceId = await createSpace(context, "Netýká se");
  await makeCatalog(spaceId, { title: "Portál: Strukturované požadavky", key: "portal.structured", requires: ["nis", "notifications"] },
    { title: "Napojení na NIS", key: "nis" }, { title: "Notifikace", key: "notifications" });
  const target = await createPage(context, spaceId, "KBN — bez NIS", summaryNode(), requirementNode({ title: "Napojení na NIS", key: "nis", state: "not_applicable" }), { type: "paragraph" });
  await startEditing(page, target.id);
  await editorOf(page).focus();
  await page.waitForTimeout(60);
  await page.keyboard.press("Control+End");
  await openCatalog(page);
  await tickInCatalog(page, "Strukturované", "Portál: Strukturované požadavky");
  return target;
}

test("předpoklad, který už je na stránce jako „netýká se“, se nepřidá podruhé", async () => {
  test.setTimeout(120_000);
  const target = await pageWithDismissedNis();
  const also = page.locator("[data-catalog-also]");
  await expect(also).toContainText("Notifikace");
  await expect(also).not.toContainText("Napojení na NIS"); // not offered again
  await page.locator("[data-catalog-query]").press("Enter");
  await expect.poll(() => titlesOnPage(page)).toEqual(["Napojení na NIS", "Notifikace", "Portál: Strukturované požadavky"]);
  await expect(stateOf(block(page, "Napojení na NIS"))).toContainText("netýká se");
  // The dismissed block counts for nothing, so Portál waits for Notifikace only.
  await expect(depsOf(block(page, "Portál: Strukturované požadavky"))).toContainText("Notifikace");
  await expect(waitingOf(block(page, "Portál: Strukturované požadavky"))).toHaveText(/čeká na 1/);
  await saveDraft(page);
  const data = await requirements(context, target.id);
  expect(data.blocks.map(item => item.key)).toEqual(["nis", "notifications", "portal.structured"]);
  expect(byKey(data, "nis").state).toBe("not_applicable");
  expect(byKey(data, "portal.structured").waitingOn).toEqual(["notifications"]);
  // What does not apply counts for nothing: the web says "0 z 2", the server's percent leaves it out too (its `total` counts all blocks).
  await expect(summaryOf(page)).toContainText("0 z 2 hotovo");
  expect(data.summary).toMatchObject({ done: 0, percent: 0 });
  expect(data.summary.states.not_applicable).toBe(1);
});

// Spec §4: the resolver reports a key "marked not applicable" to what needs it, and the catalog panel shows it in its confirmation,
// so the user learns why Napojení na NIS is not added again.
test("předpoklad „netýká se“ je uživateli oznámen (varování resolveru v potvrzení)", async () => {
  test.setTimeout(120_000);
  await pageWithDismissedNis();
  await expect(page.locator("[data-requirement-catalog]")).toContainText(/nerelevantní|netýká se/);
});

test("duplikace bloku: nový klíč a nové id, původní zůstává", async () => {
  test.setTimeout(120_000);
  const spaceId = await createSpace(context, "Duplikace");
  const seeded = await createPage(context, spaceId, "Duplikace bloku", summaryNode(),
    requirementNode({ title: "SSL certifikát", key: "ssl", direction: "inbound", state: "waiting_for_input", party: "IT KBN", notes: ["Pro portal.kbn.example."] }),
    requirementNode({ title: "Notifikace", key: "notifications", requires: ["ssl"] }));
  await startEditing(page, seeded.id);
  await focusTitle(page, block(page, "SSL certifikát"));
  await page.keyboard.press("Alt+Shift+ArrowDown"); // the block menu's Duplikovat (the 3.5 toolbar button is gone)
  await expect.poll(() => titlesOnPage(page)).toEqual(["SSL certifikát", "SSL certifikát", "Notifikace"]);
  await saveDraft(page);
  const data = await requirements(context, seeded.id);
  // The copy gets a new heading id (a uuid in 3.6; the id is the key), the original keeps its own.
  expect(data.blocks.map(item => item.key)).toEqual(["ssl", data.blocks[1]!.key, "notifications"]);
  expect(data.blocks[1]!.key).not.toBe("ssl");
  expect(new Set(data.blocks.map(item => item.blockId)).size).toBe(3);
  // The section is copied whole, its note too, and the original keeps its own.
  expect(blocksOfContent(await readPage(context, seeded.id)).map(item => item.notes)).toEqual([["Pro portal.kbn.example."], ["Pro portal.kbn.example."], []]);
  expect(data.blocks[1]).toMatchObject({ title: "SSL certifikát", direction: "inbound", party: "IT KBN" });
  // Dependencies point at keys, so they still mean the original.
  expect(byKey(data, "notifications").requires).toEqual(["ssl"]);
  expect(byKey(data, "notifications").waitingOn).toEqual(["ssl"]);
  await expect(depsOf(block(page, "Notifikace"))).toContainText("SSL certifikát");
});

test("smazání bloku, na kterém jiné závisí: žádné „čeká na“ ze zbytku, Zpět blok vrátí i s čekáním", async () => {
  test.setTimeout(120_000);
  const spaceId = await createSpace(context, "Smazání");
  const seeded = await createPage(context, spaceId, "Smazání závislosti", summaryNode(),
    requirementNode({ title: "Základ", key: "base" }),
    requirementNode({ title: "Nástavba", key: "top", requires: ["base"] }));
  await startEditing(page, seeded.id);
  await expect(depsOf(block(page, "Nástavba"))).toContainText("Základ");
  await percentOf(page).waitFor();
  await focusTitle(page, block(page, "Základ"));
  await page.keyboard.press("Control+Shift+Backspace"); // the block menu's Smazat (the 3.5 toolbar button is gone)
  await expect.poll(() => titlesOnPage(page)).toEqual(["Nástavba"]);
  await expect(waitingOf(block(page, "Nástavba"))).toHaveCount(0);
  await expect(page.locator(".atlas-requirement-ghost")).toHaveCount(0); // the folding copy has gone
  await expect(summaryOf(page)).toContainText("0 z 1 hotovo");
  await saveDraft(page);
  const data = await requirements(context, seeded.id);
  expect(data.blocks.map(item => item.key)).toEqual(["top"]);
  expect(data.blocks[0]!.waitingOn).toEqual([]); // no ghost of the removed block

  // Zpět: the block unfolds and the dependent waits for it again.
  await editorOf(page).focus();
  await page.keyboard.press("Control+z");
  await expect.poll(() => titlesOnPage(page)).toEqual(["Základ", "Nástavba"]);
  await expect(depsOf(block(page, "Nástavba"))).toContainText("Základ");
});

test("omezený pohyb: splnění, souhrn i oslava fungují, animace nepřesáhnou 160 ms", async ({ browser }) => {
  test.setTimeout(180_000);
  const spaceId = await createSpace(context, "Omezený pohyb");
  const seeded = await createPage(context, spaceId, "Omezený pohyb", summaryNode(), requirementNode({ title: "První", key: "first" }),
    requirementNode({ title: "Druhý", key: "second", requires: ["first"] }));

  const run = async (motion: "reduce" | "no-preference") => {
    const tab = await context.newPage();
    await tab.emulateMedia({ reducedMotion: motion });
    // Every programmatic animation of the page, with its duration.
    await tab.addInitScript(() => {
      const durations: number[] = [];
      (window as unknown as { __durations: number[] }).__durations = durations;
      const animate = Element.prototype.animate;
      Element.prototype.animate = function (this: Element, keyframes: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) {
        durations.push(typeof options === "number" ? options : Number(options?.duration ?? 0));
        return animate.call(this, keyframes, options);
      };
    });
    await startEditing(tab, seeded.id);
    // Both from planned: the second waits for the first.
    for (const title of ["První", "Druhý"]) {
      const check = checkOf(block(tab, title));
      if ((await check.getAttribute("aria-checked")) === "true") await check.click();
    }
    await expect(percentOf(tab)).toHaveText(/^0\s*%$/);
    await expect(depsOf(block(tab, "Druhý"))).toContainText("První");
    await checkOf(block(tab, "První")).click();
    await expect(stateOf(block(tab, "První"))).toContainText("hotovo");
    await expect(percentOf(tab)).toHaveText(/^50\s*%$/);
    await expect(waitingOf(block(tab, "Druhý"))).toHaveCount(0);
    await checkOf(block(tab, "Druhý")).click();
    await expect(percentOf(tab)).toHaveText(/^100\s*%$/);
    await expect(summaryOf(tab)).toHaveClass(/is-complete/);
    // The bar has settled: its one segment (everything is done) has grown to the bar's full length (a width transition, at once when motion is reduced).
    const bar = summaryOf(tab).locator("[data-summary-bar]");
    await expect.poll(() => bar.evaluate(element => element.firstElementChild!.getBoundingClientRect().width / element.getBoundingClientRect().width)).toBeCloseTo(1, 1);
    const chip = await stateOf(block(tab, "Druhý")).evaluate(element => getComputedStyle(element).transitionDuration);
    const chipMs = Math.max(...chip.split(",").map(value => parseFloat(value) * (value.trim().endsWith("ms") ? 1 : 1000)));
    const durations = await tab.evaluate(() => (window as unknown as { __durations: number[] }).__durations);
    // Back to planned for the next run.
    await checkOf(block(tab, "Druhý")).click();
    await checkOf(block(tab, "První")).click();
    await expect(percentOf(tab)).toHaveText(/^0\s*%$/);
    await saveDraft(tab);
    await tab.close();
    return { chipMs, longest: Math.max(0, ...durations), count: durations.length };
  };

  const reduced = await run("reduce");
  expect(reduced.chipMs).toBeLessThanOrEqual(160);
  expect(reduced.longest).toBeLessThanOrEqual(160);
  const full = await run("no-preference");
  test.info().annotations.push({ type: "motion", description: `reduced ${JSON.stringify(reduced)}; full ${JSON.stringify(full)}` });
  // Sanity: the comparison is meaningful only when normal motion is really longer.
  expect(full.count).toBeGreaterThan(0);
  expect(full.longest).toBeGreaterThan(160);
  const data = await requirements(context, seeded.id);
  expect(data.blocks.map(item => item.state)).toEqual(["planned", "planned"]);
});
