import { expect, test } from "@playwright/test";
import {
  checkOf, depsOf, block, createPage, createSpace, login, kindOf, metaOf, missingListOf, missingOf, openPage, partOf, pickState, publish, requirementNode, requirements, saveDraft, skipReason, startEditing,
  stateOf, stories, summaryNode, summaryOf, waitingOf,
} from "./projector-helpers";

// Story 3: PX2 Test is in progress and waits for the SMS gateway from "IT PX2". The page says so (chip, party, "čeká na" of what depends on
// it, the summary's „Čekáme na“ row) and so do the space-wide queries: the overview filter "Co nám nebylo dodáno" (inbound-missing) and the
// waiting list grouped by party. The web has no screen for those two yet; the browser reads them from /api/projector like the MCP tools do.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

interface Overview { keys: string[]; pages: { pageId: string; title: string; cells: { key: string; state: string }[] }[] }
interface Waiting { parties: { party: string; items: { pageId: string; pageTitle: string; key: string; title: string; date: string }[] }[] }

test("PX2 Test čeká na SMS bránu od IT PX2: stránka, přehled „Co nám nebylo dodáno“ a seznam „Na co čekáme“ podle strany", async ({ browser }) => {
  test.setTimeout(240_000);
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const page = await login(context);
    const spaceId = await createSpace(context, "Čekání na vstupy");
    // Another deployment of the space, already waiting on a different party for longer: the space-wide lists must hold both.
    const kbn = await createPage(context, spaceId, "KBN — Production", summaryNode(),
      requirementNode({ title: "SSL certifikát", key: "ssl", direction: "inbound", state: "waiting_for_input", date: "2026-10-01", party: "IT KBN" }),
      requirementNode({ title: "Přístup do NIS", key: "nis.access", direction: "inbound", state: "waiting_for_input", date: "2026-09-01", party: "IT KBN" }),
      requirementNode({ title: "Logo klienta", key: "logo", direction: "inbound", state: "done", date: "2026-09-05", party: "Marketing KBN" }));
    await publish(context, kbn.id);
    const px2 = await createPage(context, spaceId, "PX2 — Test", summaryNode(),
      requirementNode({ title: "SMS brána", key: "sms", direction: "inbound" }),
      requirementNode({ title: "Notifikace", key: "notifications", requires: ["sms"] }),
      requirementNode({ title: "Napojení na NIS", key: "nis", state: "in_progress", party: "Tým portálu" }),
      requirementNode({ title: "Certifikát PX2", key: "ssl", direction: "inbound" }));
    await publish(context, px2.id);

    await startEditing(page, px2.id);
    // Nothing waits for input yet, so there is no „Čekáme na“ row; the inputs not delivered are counted under „Nedodané vstupy“, whose list opens on a click.
    const waiting = summaryOf(page).locator("[data-summary-waiting]");
    await expect(waiting).toHaveCount(0);
    await expect(partOf(page, "waiting")).toHaveCount(0);
    await expect(missingOf(page)).toContainText("Nedodané vstupy 2");
    await missingOf(page).click();
    await expect(missingListOf(page).getByRole("button", { name: /SMS brána/ })).toHaveCount(1);
    await expect(missingListOf(page)).toContainText("Certifikát PX2");
    await expect(depsOf(block(page, "Notifikace"))).toContainText("SMS brána");

    await pickState(page, block(page, "SMS brána"), "waiting_for_input", "IT PX2");
    await expect(stateOf(block(page, "SMS brána"))).toContainText("čeká na vstup");
    await expect(kindOf(block(page, "SMS brána"))).toContainText("od IT PX2");
    // One „Čekáme na“ row by party for what waits for input; the other input is still among the not delivered ones. The card opens one
    // part at a time (the card stays short), „Zobrazit vše“ every one.
    await expect(partOf(page, "waiting")).toContainText("Čekáme na 1");
    await partOf(page, "waiting").click();
    await expect(missingListOf(page)).toHaveCount(0);
    await expect(waiting.locator("[data-summary-party]", { hasText: "IT PX2" })).toContainText("SMS brána");
    await expect(waiting).not.toContainText("Certifikát PX2");
    await summaryOf(page).locator("[data-summary-all]").click();
    await expect(missingListOf(page)).toContainText("Certifikát PX2");
    await expect(waiting.getByRole("button", { name: /SMS brána/ })).toHaveCount(1);
    await expect(depsOf(block(page, "Notifikace"))).toContainText("SMS brána");
    await expect(summaryOf(page).locator("[data-summary-counts]")).toContainText("čeká na vstup 1");

    // The summary's entry takes the eye to its requirement.
    await waiting.getByRole("button", { name: /SMS brána/ }).click();
    await expect(block(page, "SMS brána")).toBeInViewport();

    await saveDraft(page);
    const saved = await requirements(context, px2.id);
    expect(saved.blocks.find(item => item.key === "sms")).toMatchObject({ state: "waiting_for_input", party: "IT PX2", direction: "inbound" });
    expect(saved.blocks.find(item => item.key === "notifications")!.waitingOn).toEqual(["sms"]);
    expect(saved.summary).toMatchObject({ total: 4, done: 0, waiting: 1 });
    expect(saved.summary.inboundMissing.map(item => item.key)).toEqual(["sms", "ssl"]);

    // "Co nám nebylo dodáno": inbound blocks of every deployment of the space that are neither done nor not applicable.
    const overview: Overview = await (await context.request.get(`/api/projector/spaces/${spaceId}/overview?filter=inbound-missing`)).json();
    const cells = (title: string) => overview.pages.find(item => item.title === title)?.cells.map(cell => cell.key).sort();
    expect(cells("PX2 — Test")).toEqual(["sms", "ssl"]);
    expect(cells("KBN — Production")).toEqual(["nis.access", "ssl"]); // the delivered logo is not missing
    expect(overview.keys).toEqual(expect.arrayContaining(["sms", "ssl", "nis.access"]));
    expect(overview.keys).not.toContain("logo");
    expect(overview.keys).not.toContain("notifications"); // internal work is not an input
    const everything: Overview = await (await context.request.get(`/api/projector/spaces/${spaceId}/overview?filter=missing`)).json();
    expect(everything.pages.find(item => item.title === "PX2 — Test")!.cells.map(cell => cell.key).sort()).toEqual(["nis", "notifications", "sms", "ssl"]);
    expect((await context.request.get(`/api/projector/spaces/${spaceId}/overview?filter=nesmysl`)).status()).toBe(400);

    // "Na co čekáme": grouped by party; within a party the longest waiting (oldest date) first.
    const list: Waiting = await (await context.request.get(`/api/projector/spaces/${spaceId}/waiting`)).json();
    expect(list.parties.map(item => item.party).sort()).toEqual(["IT KBN", "IT PX2"]);
    expect(list.parties.find(item => item.party === "IT KBN")!.items.map(item => [item.key, item.date])).toEqual([["nis.access", "2026-09-01"], ["ssl", "2026-10-01"]]);
    expect(list.parties.find(item => item.party === "IT PX2")!.items).toMatchObject([{ key: "sms", title: "SMS brána", pageTitle: "PX2 — Test", date: "" }]);

    // The SMS gateway arrives: the dependent block stops waiting, the lists shrink, the space-wide queries follow.
    await checkOf(block(page, "SMS brána")).click();
    await expect(waitingOf(block(page, "Notifikace"))).toHaveCount(0);
    await expect(waiting).toHaveCount(0);
    await expect(missingOf(page)).toContainText("Nedodané vstupy 1");
    await expect(missingListOf(page)).not.toContainText("SMS brána");
    await expect(missingListOf(page)).toContainText("Certifikát PX2");
    await saveDraft(page);
    const arrived = await (await context.request.get(`/api/projector/spaces/${spaceId}/waiting`)).json() as Waiting;
    expect(arrived.parties.map(item => item.party)).toEqual(["IT KBN"]);
    const overviewAfter: Overview = await (await context.request.get(`/api/projector/spaces/${spaceId}/overview?filter=inbound-missing`)).json();
    expect(overviewAfter.pages.find(item => item.title === "PX2 — Test")!.cells.map(cell => cell.key)).toEqual(["ssl"]);
    expect((await requirements(context, px2.id)).blocks.find(item => item.key === "notifications")!.waitingOn).toEqual([]);

    // The other deployment was not touched by any of this.
    await openPage(page, kbn.id);
    const delivering = block(page, "SSL certifikát");
    await expect(stateOf(delivering)).toContainText("čeká na vstup");
    await expect(metaOf(delivering)).toContainText("1. 10. 2026"); // the deadline line under the heading
    await expect(kindOf(delivering)).toHaveAttribute("aria-label", /vstup, od IT KBN/);
  } finally {
    await context.close();
  }
});
