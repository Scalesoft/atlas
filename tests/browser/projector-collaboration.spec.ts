import { expect, test, type Page } from "@playwright/test";
import { startEditing as editInPlace } from "./editing";
import {
  checkOf, block, depsOf, editorOf, blocksOfContent, byKey, createPage, createSpace, login, percentOf, pragueToday, readPage, requirementNode, requirements, saveDraft, skipReason, startEditing,
  stateOf, stories, summaryNode, summaryOf, titlesOnPage, waitingOf,
} from "./projector-helpers";

// Story 4: two people edit one deployment page at the same time. One completes a requirement while the other completes another. Both windows converge on the same blocks and the same summary, and nothing is lost after saving and opening again.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

const check = (page: Page, title: string) => checkOf(block(page, title));
const percent = (page: Page, value: number) => expect(percentOf(page)).toHaveText(new RegExp(`^${value}\\s*%$`));
/** What the summary says, whitespace and the counting animation settled. */
const summaryText = (page: Page) => summaryOf(page).evaluate(element => (element as HTMLElement).innerText.replace(/\s+/g, " ").trim());

test("dva spolupracovníci: splnění naráz, obě okna se shodnou a nic se neztratí", async ({ browser }) => {
  test.setTimeout(240_000);
  const contextA = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  const contextB = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const a = await login(contextA), b = await login(contextB);
    const spaceId = await createSpace(contextA, "Společné nasazení");
    // An intro line first: both writers' carets start there, so a remote caret's name is never inside the heading a story looks up by its text.
    const seeded = await createPage(contextA, spaceId, "Souběžné nasazení", { type: "paragraph", content: [{ type: "text", text: "Úvod" }] }, summaryNode(),
      requirementNode({ title: "Alfa", key: "alfa", notes: ["Poznámka k Alfě."] }),
      requirementNode({ title: "Beta", key: "beta" }),
      requirementNode({ title: "Gama", key: "gama", requires: ["alfa"] }),
      requirementNode({ title: "Delta", key: "delta", direction: "inbound", party: "IT KBN" }));
    await startEditing(a, seeded.id);
    await startEditing(b, seeded.id);
    for (const page of [a, b]) {
      await expect.poll(() => titlesOnPage(page)).toEqual(["Alfa", "Beta", "Gama", "Delta"]);
      await expect(depsOf(block(page, "Gama"))).toContainText("Alfa");
      await expect(waitingOf(block(page, "Gama"))).toHaveText(/čeká na 1/);
      await percent(page, 0);
    }

    // A completes Alfa while B completes Delta.
    await Promise.all([check(a, "Alfa").click(), check(b, "Delta").click()]);

    for (const [name, page] of [["A", a], ["B", b]] as const) {
      await expect(stateOf(block(page, "Alfa")), name).toContainText("hotovo");
      await expect(stateOf(block(page, "Delta")), name).toContainText("hotovo");
      await expect(waitingOf(block(page, "Gama")), `${name}: Alfa is done, Gama waits for nothing`).toHaveCount(0);
      await percent(page, 50);
    }
    await expect.poll(() => summaryText(b)).toBe(await summaryText(a));

    // And again: A completes Gama.
    await check(a, "Gama").click();
    for (const [name, page] of [["A", a], ["B", b]] as const) {
      await percent(page, 75);
      await expect(stateOf(block(page, "Gama")), name).toContainText("hotovo");
    }
    await expect.poll(() => summaryText(b)).toBe(await summaryText(a));
    expect(await summaryText(a)).toContain("75 %");
    await expect(summaryOf(a)).not.toHaveClass(/is-complete/);

    // Saved from either window: the server holds the same requirements in the same places.
    await saveDraft(b);
    const data = await requirements(contextA, seeded.id);
    expect(data.blocks.map(item => item.key)).toEqual(["alfa", "beta", "gama", "delta"]);
    expect(data.blocks.map(item => item.state)).toEqual(["done", "planned", "done", "done"]);
    expect(data.blocks.filter(item => item.state === "done").every(item => item.completed === pragueToday())).toBe(true);
    expect(data.summary).toMatchObject({ total: 4, done: 3, percent: 75 });
    expect(new Set(data.blocks.map(item => item.blockId)).size).toBe(4);
    const content = blocksOfContent(await readPage(contextA, seeded.id));
    expect(content.map(item => item.attrs.key)).toEqual(["alfa", "beta", "gama", "delta"]);
    expect(content[0]!.notes).toEqual(["Poznámka k Alfě."]);

    // Opened again, both windows read the draft back: the same headings, the same numbers.
    for (const page of [a, b]) {
      await page.reload();
      await editInPlace(page);
      await expect.poll(() => titlesOnPage(page)).toEqual(["Alfa", "Beta", "Gama", "Delta"]);
      await percent(page, 75);
      await expect(editorOf(page)).toContainText("Poznámka k Alfě.");
    }
    await expect.poll(() => summaryText(b)).toBe(await summaryText(a));
  } finally {
    await contextA.close();
    await contextB.close();
  }
});
