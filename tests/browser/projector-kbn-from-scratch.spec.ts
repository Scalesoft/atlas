import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import {
  checkOf, allBlocks, block, byKey, createPage, createSpace, czechDate, editorOf, finishEditing, hasNode, heading, insertSummary, login, openCatalog, pickFromCatalog, tickInCatalog,
  depsOf, kindOf, metaOf, missingListOf, missingOf, outsideBlocks, partOf, percentOf, pickState, pragueToday, readPage, requirementNode, requirements, saveDraft, skipReason, startEditing, stateOf, stories, summaryNode, summaryOf,
  titlesOnPage, waitingOf, openPage,
} from "./projector-helpers";

// Story 1 (Projector v1, spec 2026-10-27): KBN from scratch. A catalog page is written in the editor (Portál: Strukturované požadavky needs
// Napojení na NIS and Notifikace; SSL certifikát and SMS brána are inputs), marked as catalog and published. A new page "KBN — Production"
// takes the packages from the catalog, gets notes under the blocks and is completed one block after another to 100 %.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

const PORTAL = "Portál: Strukturované požadavky", NIS = "Napojení na NIS", NOTIFICATIONS = "Notifikace", SSL = "SSL certifikát", SMS = "SMS brána";

test.describe.serial("KBN od nuly", () => {
  let context: BrowserContext, page: Page, spaceId: string, deploymentId: string;

  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
    page = await login(context);
    spaceId = await createSpace(context, "KBN projektor");
  });
  test.afterAll(async () => { await context?.close(); });

  test("katalog: balíček Portál se závislostmi, vstupy SSL a SMS; označení jako katalog a publikace", async () => {
    test.setTimeout(240_000);
    // Seeded as the old blocks with their keys, kinds and prerequisites; the page opens as headings (repair on open) and is saved so.
    const catalog = await createPage(context, spaceId, "Katalog požadavků KBN", heading("Portál KBN"), summaryNode(),
      requirementNode({ title: PORTAL, key: "portal.structured", requires: ["nis", "notifications"] }),
      requirementNode({ title: NIS, key: "nis" }), requirementNode({ title: NOTIFICATIONS, key: "notifications" }),
      requirementNode({ title: SSL, key: "ssl", direction: "inbound" }), requirementNode({ title: SMS, key: "sms", direction: "inbound" }));
    await startEditing(page, catalog.id);
    for (const title of [PORTAL, NIS, NOTIFICATIONS, SSL, SMS]) await expect(block(page, title)).toBeVisible();
    await expect(depsOf(block(page, PORTAL))).toContainText(NIS);
    await expect(depsOf(block(page, PORTAL))).toContainText(NOTIFICATIONS);

    await summaryOf(page).locator("[data-summary-menu]").click();
    await page.locator('[data-summary-action="catalog"]').click();
    await expect(page.getByText("Stránka je katalog požadavků.", { exact: true })).toBeVisible();
    // Marking the page changes no text, so there is nothing to save (no badge): publishing is the next step.
    await finishEditing(page);

    // The catalog the space now offers is the published page (spec §4).
    const offered = await (await context.request.get(`/api/projector/spaces/${spaceId}/catalog`)).json();
    expect(offered.definitions.map((item: { key: string }) => item.key).sort()).toEqual(["nis", "notifications", "portal.structured", "sms", "ssl"]);
    const portal = offered.definitions.find((item: { key: string }) => item.key === "portal.structured");
    expect(portal).toMatchObject({ title: PORTAL, requires: ["nis", "notifications"], bundle: "Portál KBN", direction: "internal" });
    expect(offered.definitions.find((item: { key: string }) => item.key === "sms").direction).toBe("inbound");
  });

  test("nasazení: Strukturované požadavky z katalogu přidají oba předpoklady v pořadí závislostí", async () => {
    test.setTimeout(240_000);
    const deployment = await createPage(context, spaceId, "KBN — Production");
    deploymentId = deployment.id;
    await startEditing(page, deploymentId);
    const editor = editorOf(page);
    await editor.focus();
    await page.waitForTimeout(60);
    await editor.press("Control+End");
    await page.keyboard.type("Nasazení portálu KBN do produkce.");
    await page.keyboard.press("Enter");
    await insertSummary(page);
    await expect(percentOf(page)).toHaveText(/^0\s*%$/);
    await expect(summaryOf(page)).toContainText("Zatím žádné požadavky");

    await outsideBlocks(page);
    await openCatalog(page);
    // Typing the name and Space ticks the first, preselected item; what comes along shows live, Enter inserts it all (J2).
    await tickInCatalog(page, "Strukturované", PORTAL);
    const also = page.locator("[data-catalog-also]");
    await expect(also).toContainText("Přidá se také");
    await expect(also).toContainText(NIS);
    await expect(also).toContainText(NOTIFICATIONS);
    await expect(also).toContainText(`potřebuje ho: ${PORTAL}`);
    await page.locator("[data-catalog-query]").press("Enter");

    await expect.poll(() => titlesOnPage(page)).toEqual([NIS, NOTIFICATIONS, PORTAL]);
    await expect(depsOf(block(page, PORTAL))).toContainText(NIS);
    await expect(depsOf(block(page, PORTAL))).toContainText(NOTIFICATIONS);
    await expect(waitingOf(block(page, NIS))).toHaveCount(0);

    // A single input has no dependencies to confirm: the first match is inserted at once.
    for (const [title, query] of [[SSL, "SSL"], [SMS, "SMS"]]) {
      await outsideBlocks(page);
      await openCatalog(page);
      await pickFromCatalog(page, query!, title!);
      await expect(block(page, title!)).toBeVisible();
    }
    // What is on the page is no longer offered again.
    await outsideBlocks(page);
    await openCatalog(page);
    await page.locator("[data-catalog-query]").fill("SSL");
    await expect(page.getByTestId("requirement-catalog-list").getByRole("option", { name: /Už je na stránce/ })).toBeVisible();
    await page.keyboard.press("Escape");

    await expect.poll(() => titlesOnPage(page)).toEqual([NIS, NOTIFICATIONS, PORTAL, SSL, SMS]);
    await saveDraft(page);
    const data = await requirements(context, deploymentId);
    expect(data.blocks.map(item => item.key)).toEqual(["nis", "notifications", "portal.structured", "ssl", "sms"]);
    expect(byKey(data, "portal.structured").requires).toEqual(["nis", "notifications"]);
    expect(byKey(data, "portal.structured").waitingOn).toEqual(["nis", "notifications"]);
    expect(data.blocks.every(item => item.state === "planned" && item.date === "" && item.party === "")).toBe(true);
    expect(data.summary).toMatchObject({ total: 5, done: 0, percent: 0 });
    expect(data.summary.inboundMissing.map(item => item.key)).toEqual(["ssl", "sms"]);
    // The copies are the deployment's own blocks, not the catalog's.
    const ids = new Set(data.blocks.map(item => item.blockId));
    expect(ids.size).toBe(5);
  });

  test("splnění jeden po druhém: klíče, „čeká na“ a souhrn až do 100 %", async () => {
    test.setTimeout(240_000);
    const percent = (value: number) => expect(percentOf(page)).toHaveText(new RegExp(`^${value}\\s*%$`));
    const check = (title: string) => checkOf(block(page, title));
    const today = pragueToday();

    // An input that has not come: asked of a party, listed in the summary once under that party; every input not delivered is counted under „Nedodané vstupy“.
    await pickState(page, block(page, SSL), "waiting_for_input", "IT KBN");
    await expect(stateOf(block(page, SSL))).toContainText("čeká na vstup");
    await expect(kindOf(block(page, SSL))).toContainText("od IT KBN");
    await partOf(page, "waiting").click();
    await expect(summaryOf(page).locator("[data-summary-party]", { hasText: "IT KBN" })).toContainText(SSL);
    await expect(summaryOf(page).locator("[data-summary-waiting]").getByRole("button", { name: SSL })).toHaveCount(1);
    await expect(missingOf(page)).toContainText("Nedodané vstupy 2");
    await missingOf(page).click();
    await expect(missingListOf(page)).toContainText(SSL);
    await expect(missingListOf(page)).toContainText(SMS);
    await percent(0);

    // Completing dates the facts line beside the deadline (3.5's „hotovo“); the round check undoes it.
    await check(SSL).click();
    await expect(stateOf(block(page, SSL))).toContainText("hotovo");
    await expect(check(SSL)).toBeChecked();
    await expect(metaOf(block(page, SSL))).toContainText(`hotovo ${czechDate(today)}`);
    // 3.6: the party is the heading's own attribute and stays when the input arrives (who delivered it); only the state „čeká na vstup“ puts it into the waiting list.
    await expect(kindOf(block(page, SSL))).toContainText("od IT KBN");
    await percent(20);
    await expect(partOf(page, "waiting")).toHaveCount(0); // nothing else waits for input
    await expect(missingListOf(page)).not.toContainText(SSL);

    await check(NIS).click();
    await percent(40);
    // Portál still waits for Notifikace only.
    await expect(waitingOf(block(page, PORTAL))).toHaveText(/čeká na 1/);
    await expect(depsOf(block(page, PORTAL))).toContainText(NOTIFICATIONS);

    await check(NOTIFICATIONS).click();
    await percent(60);
    await expect(waitingOf(block(page, PORTAL))).toHaveCount(0);

    await check(PORTAL).click();
    await percent(80);
    // The round check undoes what it did, a second toggle done again.
    await check(PORTAL).click();
    await expect(stateOf(block(page, PORTAL))).toContainText("neřešeno");
    expect((await metaOf(block(page, PORTAL)).allTextContents()).join()).not.toContain(czechDate(today)); // no facts line at all is as good
    await percent(60);
    await check(PORTAL).click();
    await percent(80);

    await pickState(page, block(page, SMS), "in_progress");
    await expect(stateOf(block(page, SMS))).toContainText("rozpracováno");
    await expect(missingListOf(page)).toContainText(SMS);
    await expect(summaryOf(page).locator("[data-summary-bar]")).toBeVisible();
    await expect(summaryOf(page).locator('[data-summary-bar] [data-tone="in_progress"]')).toHaveCount(1); // the bar is split by state
    await expect(summaryOf(page)).not.toHaveClass(/is-complete/);

    await check(SMS).click();
    await percent(100);
    // The celebration: the summary is marked complete, the lists of what is missing are gone.
    await expect(summaryOf(page)).toHaveClass(/is-complete/);
    await expect(page.locator("[data-requirement-summary-view] [data-summary-percent]")).toHaveAttribute("aria-valuenow", "100");
    await expect(summaryOf(page).locator("[data-summary-waiting]")).toHaveCount(0);
    await expect(missingOf(page)).toHaveCount(0);
    await expect(allBlocks(page).locator('[data-rq-chip="ready"]')).toHaveCount(0);

    await saveDraft(page);
    const data = await requirements(context, deploymentId);
    expect(data.summary).toMatchObject({ total: 5, done: 5, percent: 100, waiting: 0 });
    expect(data.summary.inboundMissing).toEqual([]);
    expect(data.blocks.every(item => item.state === "done" && item.completed === today && item.waitingOn.length === 0)).toBe(true);
    expect(byKey(data, "ssl").party).toBe("IT KBN"); // kept when the block left waiting for input (3.6: an attribute of the heading)
    const content = await readPage(context, deploymentId);
    expect(hasNode(content, "overview")).toBe(true);

    // Published and opened again: the summary card, the states and the party read as the editor shows them.
    await finishEditing(page);
    await openPage(page, deploymentId);
    await percent(100);
    await expect(summaryOf(page)).toContainText("Vše hotovo · 5 z 5");
    await expect(summaryOf(page)).toHaveClass(/is-complete/);
    await expect(summaryOf(page).locator("[data-summary-waiting]")).toHaveCount(0);
    await expect(stateOf(block(page, SSL))).toContainText("hotovo");
    await expect(kindOf(block(page, SSL))).toHaveAttribute("aria-label", /vstup, od IT KBN/); // its direction, and the party that delivered it
    expect((await readPage(context, deploymentId)).publishedVersion).not.toBeNull();
  });
});
