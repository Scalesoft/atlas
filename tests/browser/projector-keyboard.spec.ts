import { expect, test, type Page } from "@playwright/test";
import { expectSaved } from "./editing";
import {
  block, blocksOfContent, byKey, createPage, createSpace, editorOf, login, markCatalog, openPage, percentOf, publish, readPage, requirementNode, requirements,
  stateOf, stories, skipReason, summaryNode, summaryOf, titlesOnPage, waitingOf,
} from "./projector-helpers";

// Story 7: keyboard only. From a page with an empty line to a completed deployment without touching the mouse: Enter in the page's text, the slash menu, the
// catalog panel, the confirmation of the added prerequisites, the round check, the state menu and the page saving itself.
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
test.skip(!stories, skipReason);

const focusedLabel = (page: Page) => page.evaluate(() => document.activeElement?.getAttribute("aria-label") ?? "");
/** The caret's text and offset in it. */
const caret = (page: Page) => page.evaluate(() => { const selection = getSelection(); return `${selection?.anchorNode?.textContent ?? ""}@${selection?.anchorOffset ?? -1}`; });

test("jen klávesnicí: vložení z katalogu s předpoklady a splnění všech bloků", async ({ browser }) => {
  test.setTimeout(240_000);
  const context = await browser.newContext({ baseURL: process.env.ATLAS_URL, viewport: { width: 1280, height: 900 } });
  try {
    const page = await login(context);
    const spaceId = await createSpace(context, "Klávesnice");
    const catalog = await createPage(context, spaceId, "Katalog",
      requirementNode({ title: "Portál: Strukturované požadavky", key: "portal.structured", requires: ["nis", "notifications"] }),
      requirementNode({ title: "Napojení na NIS", key: "nis" }), requirementNode({ title: "Notifikace", key: "notifications" }));
    await markCatalog(context, catalog.id);
    await publish(context, catalog.id);
    const target = await createPage(context, spaceId, "Nasazení bez myši", summaryNode(), { type: "paragraph" });

    await openPage(page, target.id);
    await page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().focus();
    await page.keyboard.press("Enter");
    await expect(editorOf(page)).toBeVisible();
    await editorOf(page).focus();
    await page.waitForTimeout(80); // ProseMirror re-applies its own selection right after the editor gains focus
    await page.keyboard.press("Control+End");
    await page.keyboard.type("/Moduly");
    await expect(page.getByTestId("slash-menu").getByRole("option", { name: /^Moduly produktu/ })).toBeVisible();
    await page.keyboard.press("Enter");

    // The catalog panel takes the typing at once; Space ticks the first match and says live what comes along; Enter inserts (J2).
    await expect(page.locator("[data-catalog-query]")).toBeFocused();
    await page.keyboard.type("Strukturované");
    await expect(page.getByTestId("requirement-catalog-list").locator("[data-active]")).toContainText("Portál"); // the preselected match (a category row may stand above)
    await page.keyboard.press(" ");
    await expect(page.locator("[data-catalog-also]")).toContainText("Napojení na NIS");
    await expect(page.locator("[data-catalog-query]")).toBeFocused();
    await page.keyboard.press("Enter");
    await expect.poll(() => titlesOnPage(page)).toEqual(["Napojení na NIS", "Notifikace", "Portál: Strukturované požadavky"]);
    await expect(editorOf(page)).toBeFocused(); // the panel gave the focus back

    // The arrow keys take the caret from the summary card down into the first title; Alt+F10, then Tab, walk the controls of that heading, its
    // round check first (keymap „Požadavky“); Space completes it.
    const check = (title: string) => page.getByRole("checkbox", { name: `Hotovo: ${title}`, exact: true });
    await page.keyboard.press("Control+Home");
    // The caret's heading: a caret the browser moved onto a heading line beside its check sits in the heading element itself, not in its text.
    const inTitle = () => page.evaluate(() => {
      const anchor = window.getSelection()?.anchorNode;
      return (anchor instanceof Element ? anchor : anchor?.parentElement)?.closest<HTMLElement>("[data-heading-id]")?.dataset.headingId;
    });
    for (let step = 0; step < 12 && (await inTitle()) !== "nis"; step++) await page.keyboard.press("ArrowDown");
    expect(await inTitle()).toBe("nis");
    await page.keyboard.press("Alt+F10");
    await page.keyboard.press("Tab");
    await expect(check("Napojení na NIS")).toBeFocused();
    await page.keyboard.press("Space");
    await expect(stateOf(block(page, "Napojení na NIS"))).toContainText("hotovo");
    await expect(percentOf(page)).toHaveText(/^33\s*%$/);
    await expect(waitingOf(block(page, "Portál: Strukturované požadavky"))).toHaveText(/čeká na 1/);

    // Tab walks check, the kind chip and the state pill (the labels first, the state at the line's end) of that heading only.
    await page.keyboard.press("Tab");
    expect(await focusedLabel(page)).toBe("Druh: úkol");
    await page.keyboard.press("Tab");
    expect(await focusedLabel(page)).toBe("Stav: hotovo");
    // Esc gives the caret back to the title; the next heading's controls are reached the same way (arrow down, Alt+F10, Tab); Space completes it.
    const toCheck = async (id: string, title: string, fromEnd = false) => {
      await page.keyboard.press("Escape");
      await page.keyboard.press(fromEnd ? "Control+End" : "Control+Home"); // out of the summary card, which takes the arrows
      for (let step = 0; step < 12 && (await inTitle()) !== id; step++) await page.keyboard.press(fromEnd ? "ArrowUp" : "ArrowDown");
      expect(await inTitle()).toBe(id);
      await page.keyboard.press("Alt+F10");
      await page.keyboard.press("Tab");
      await expect(check(title)).toBeFocused();
    };
    await toCheck("notifications", "Notifikace");
    await page.keyboard.press("Space");
    await expect(percentOf(page)).toHaveText(/^66\s*%$/);
    await expect(waitingOf(block(page, "Portál: Strukturované požadavky"))).toHaveCount(0);

    // The state menu of the last block: arrows and Enter pick "rozpracováno".
    // (the way to its check is the one above: a writer's chips are out of the Tab order, so only Alt+F10, then Tab walks check, kind, state)
    await toCheck("portal.structured", "Portál: Strukturované požadavky", true);
    await page.keyboard.press("Tab"); // the kind chip
    await page.keyboard.press("Tab"); // the state pill
    expect(await focusedLabel(page)).toBe("Stav: neřešeno");
    await page.keyboard.press("Enter");
    await expect(page.locator("[data-state-option]").first()).toBeVisible();
    const option = () => page.evaluate(() => document.activeElement?.getAttribute("data-state-option"));
    for (let step = 0; step < 6 && (await option()) !== "in_progress"; step++) await page.keyboard.press("ArrowDown");
    expect(await option()).toBe("in_progress");
    await page.keyboard.press("Enter");
    await expect(stateOf(block(page, "Portál: Strukturované požadavky"))).toContainText("rozpracováno");

    // Back over its kind chip to the check of Portál (the first control of its row) and the last completion.
    // (the focus is back in the text once the menu has closed, so it is put on the check again)
    await check("Portál: Strukturované požadavky").focus();
    await expect(check("Portál: Strukturované požadavky")).toBeFocused();
    await page.keyboard.press("Space");
    await expect(percentOf(page)).toHaveText(/^100\s*%$/);
    await expect(summaryOf(page)).toHaveClass(/is-complete/);

    // The page stores itself, no save step.
    await expectSaved(page);
    const data = await requirements(context, target.id);
    expect(data.blocks.map(item => [item.key, item.state])).toEqual([["nis", "done"], ["notifications", "done"], ["portal.structured", "done"]]);
    expect(data.summary).toMatchObject({ done: 3, percent: 100 });
    expect(blocksOfContent(await readPage(context, target.id)).map(item => item.attrs.key)).toEqual(["nis", "notifications", "portal.structured"]);
    expect(byKey(data, "portal.structured").waitingOn).toEqual([]);
  } finally {
    await context.close();
  }
});

