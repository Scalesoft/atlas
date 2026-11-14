import { expect, test } from "@playwright/test";
import {
  checkOf, depsOf, block, blocksOfContent, editorOf, kindOf, byKey, createPage, createSpace, ensureEditing, finishEditing, heading, login, openPage, percentOf, publish, readPage, requirementNode, requirements, saveDraft, skipReason,
  startEditing, stateOf, stories, summaryNode, summaryOf, titlesOnPage, waitingOf,
} from "./projector-helpers";

// Story 2: a finished-looking deployment (KBN) becomes the template of the next one (Kostelec) from the summary's menu. Text and blocks are
// copied; state, date, party and the place of storage start again; keys, direction, dependencies and notes stay; the original is untouched.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

test("kopie nasazení KBN → Kostelec: průběh se vynuluje, klíče, závislosti a poznámky zůstanou, původní stránka se nezmění", async ({ browser }) => {
  test.setTimeout(240_000);
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const page = await login(context);
    const spaceId = await createSpace(context, "Kopie nasazení");
    const kbn = await createPage(context, spaceId, "KBN — Production",
      heading("Portál"), summaryNode(),
      requirementNode({ title: "Napojení na NIS", key: "nis", state: "done", date: "2026-09-10", party: "Dodavatel NIS", where: "Keepass › KBN", notes: ["Kontakt: Jan Novák, nis@kbn.example"] }),
      requirementNode({ title: "Notifikace", key: "notifications", state: "in_progress", party: "Tým portálu", notes: ["Šablony schvaluje klient."] }),
      requirementNode({ title: "Portál: Strukturované požadavky", key: "portal.structured", state: "blocked", requires: ["nis", "notifications"] }),
      requirementNode({ title: "SSL certifikát", key: "ssl", direction: "inbound", state: "waiting_for_input", date: "2026-10-01", party: "IT KBN", where: "Trezor Atlasu › KBN",
        notes: ["Certifikát pro portal.kbn.example."], children: [
          { title: "Test řetězce certifikátů", key: "ssl.chain", state: "done", date: "2026-10-02", requires: ["ssl"] },
        ] }),
      requirementNode({ title: "SMS brána", key: "sms", direction: "inbound", state: "done", date: "2026-09-20", party: "IT KBN" }));
    await publish(context, kbn.id);

    const before = await requirements(context, kbn.id);
    expect(before.summary).toMatchObject({ total: 6, done: 3, percent: 50 });
    const originalVersion = (await readPage(context, kbn.id)).version;

    // --- the menu on the summary of the finished deployment ---
    await startEditing(page, kbn.id);
    await expect(percentOf(page)).toHaveText(/^50\s*%$/);
    await expect(kindOf(block(page, "SSL certifikát"))).toContainText("od IT KBN");
    await summaryOf(page).locator("[data-summary-menu]").click();
    await page.locator('[data-summary-action="deployment"]').click();
    await expect(page.getByText("Zkopíruje se text i bloky", { exact: false })).toBeVisible();
    await page.getByRole("textbox", { name: "Název nového nasazení" }).fill("Kostelec — Production");
    await page.getByRole("button", { name: "Založit", exact: true }).click();
    await expect(page.getByText("Nasazení „Kostelec — Production“ je založené.", { exact: true })).toBeVisible();
    // The copy is offered, not forced: the toast opens it.
    await page.getByRole("button", { name: "Otevřít", exact: true }).click();
    await expect(page).toHaveURL(/#\/pages\/[0-9a-f-]{36}$/);
    const copyId = page.url().split("/").pop()!;
    expect(copyId).not.toBe(kbn.id);

    // --- what the user sees on the copy (a new page opens for its author; editing shows the blocks live) ---
    await ensureEditing(page);
    await expect(page.getByRole("textbox", { name: "Název stránky" })).toHaveValue("Kostelec — Production");
    await expect.poll(() => titlesOnPage(page)).toEqual(["Napojení na NIS", "Notifikace", "Portál: Strukturované požadavky", "SSL certifikát", "Test řetězce certifikátů", "SMS brána"]);
    await expect(percentOf(page)).toHaveText(/^0\s*%$/);
    for (const title of await titlesOnPage(page)) {
      const item = block(page, title);
      await expect(stateOf(item)).toContainText("neřešeno");
      // The date sits in the block's head and the party and date chips in its title, so the whole block is read, not just its facts line.
      await expect(item).not.toContainText(/\d{1,2}\. \d{1,2}\. \d{4}/); // no date
      await expect(item).not.toContainText("IT KBN");
      await expect(item).not.toContainText("Keepass");
    }
    await expect(block(page, "SSL certifikát")).toContainText("vstup"); // direction stays
    await expect(depsOf(block(page, "Portál: Strukturované požadavky"))).toContainText("Napojení na NIS");
    await expect(depsOf(block(page, "Portál: Strukturované požadavky"))).toContainText("Notifikace");
    // The notes are paragraphs under their heading (3.6), not part of it.
    await expect(editorOf(page)).toContainText("Kontakt: Jan Novák, nis@kbn.example");
    await expect(editorOf(page)).toContainText("Certifikát pro portal.kbn.example.");
    await expect(block(page, "Test řetězce certifikátů")).toBeVisible();

    // --- the saved state of the copy ---
    const copy = await requirements(context, copyId);
    expect(copy.blocks.map(item => item.key)).toEqual(before.blocks.map(item => item.key));
    for (const item of copy.blocks) {
      const origin = byKey(before, item.key);
      expect(item, item.key).toMatchObject({ title: origin.title, direction: origin.direction, requires: origin.requires, state: "planned", date: "", party: "", where: "" });
    }
    expect(copy.summary).toMatchObject({ total: 6, done: 0, percent: 0, waiting: 0 });
    expect(byKey(copy, "portal.structured").waitingOn).toEqual(["nis", "notifications"]);
    const copyPage = await readPage(context, copyId);
    expect(copyPage.title).toBe("Kostelec — Production");
    expect(copyPage.spaceId).toBe(spaceId);
    expect(blocksOfContent(copyPage).find(item => item.attrs.key === "ssl")!.notes).toEqual(["Certifikát pro portal.kbn.example."]);

    // --- the original is untouched, also after the copy has been worked on ---
    await checkOf(block(page, "Napojení na NIS")).click();
    await expect(percentOf(page)).toHaveText(/^16\s*%$/);
    await saveDraft(page);
    expect((await requirements(context, copyId)).summary).toMatchObject({ done: 1, percent: 16 });

    const after = await requirements(context, kbn.id);
    expect(after.blocks).toEqual(before.blocks);
    expect(after.summary).toEqual(before.summary);
    const original = await readPage(context, kbn.id);
    expect(original.version).toBe(originalVersion);
    expect(original.title).toBe("KBN — Production");
    expect(blocksOfContent(original).map(item => item.attrs.state)).toEqual(["done", "in_progress", "blocked", "waiting_for_input", "done", "done"]);
    await finishEditing(page);
    await openPage(page, kbn.id);
    await expect(percentOf(page)).toHaveText(/^50\s*%$/);
    await expect(summaryOf(page)).toContainText("3 z 6 hotovo");
    await expect(kindOf(block(page, "SSL certifikát"))).toContainText("od IT KBN");
  } finally {
    await context.close();
  }
});
