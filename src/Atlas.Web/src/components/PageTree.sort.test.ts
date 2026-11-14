// „Řadit abecedně“ in the row menu and „Přesouvat stránky tažením“ (3.6): the server sorts; the tree offers the switch and marks the level.
import { afterEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import PageTree from "./PageTree.vue";
import type { TreePage } from "../treeMove";

const node = (id: string, parentId: string | null = null, extra: Partial<TreePage> = {}): TreePage =>
  ({ id, parentId, spaceId: "space", title: id, version: 1, isPublished: true, canEdit: true, ...extra });
const pages = [node("hub", null, { childrenByTitle: true }), node("alfa", "hub"), node("plain"), node("board", null, { type: "board", version: 0 })];
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
afterEach(() => { document.body.replaceChildren(); });

describe("sorting a level by title", () => {
  it("offers a checkable „Řadit abecedně“ on a page, ticked while its children are sorted, and reports the choice", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    // Reachable from the keyboard as every other row action: Shift+F10 on the row.
    await app.get('[data-tree-item="hub"]').trigger("keydown", { key: "F10", shiftKey: true });
    await tick();
    const item = document.querySelector<HTMLElement>('[data-tree-action="sortByTitle"]')!;
    expect(item.getAttribute("role")).toBe("menuitemcheckbox");
    expect(item.getAttribute("aria-checked")).toBe("true");
    expect(item.textContent).toContain("Řadit abecedně");
    item.click();
    await tick();
    expect(app.emitted("action")).toEqual([["sortByTitle", expect.objectContaining({ id: "hub" })]]);
    app.unmount();
    document.body.replaceChildren();

    const other = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    await other.get('[data-tree-row="plain"] [data-tree-more]').trigger("click");
    await tick();
    expect(document.querySelector('[data-tree-action="sortByTitle"]')!.getAttribute("aria-checked")).toBe("false");
    other.unmount();
  });

  it("offers it to writers on pages only and marks a sorted level quietly for them", async () => {
    const writer = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    expect(writer.get('[data-tree-item="hub"] [data-tree-sorted]').attributes("title")).toBe("Podstránky řazené abecedně");
    expect(writer.find('[data-tree-item="plain"] [data-tree-sorted]').exists()).toBe(false);
    await writer.get('[data-tree-row="board"] [data-tree-more]').trigger("click");
    await tick();
    expect(document.querySelector('[data-tree-action="sortByTitle"]')).toBeNull();
    writer.unmount();
    document.body.replaceChildren();

    const reader = mount(PageTree, { props: { pages: pages.map(page => ({ ...page, canEdit: false })), canReorder: false, menu: true }, attachTo: document.body });
    expect(reader.find("[data-tree-sorted]").exists()).toBe(false);
    await reader.get('[data-tree-row="hub"] [data-tree-more]').trigger("click");
    await tick();
    expect(document.querySelector('[data-tree-action="sortByTitle"]')).toBeNull();
    reader.unmount();
  });

  it("drags no row with dragging turned off, while Alt+arrows still move", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true, drag: false } });
    expect(app.findAll(".tree-row").every(row => row.attributes("draggable") === "false")).toBe(true);
    // The row's link stays natively draggable (and clickable): its drag must not start a move either.
    await new Promise(resolve => setTimeout(resolve, 5));
    const linkDrag = Object.assign(new Event("dragstart", { bubbles: true, cancelable: true }), { dataTransfer: { setData: vi.fn(), effectAllowed: "" } });
    app.get('[data-tree-item="plain"]').element.dispatchEvent(linkDrag);
    expect(linkDrag.defaultPrevented).toBe(true);
    expect(app.emitted("dragStart")).toBeUndefined();
    await app.get('[data-tree-item="plain"]').trigger("keydown", { key: "ArrowUp", altKey: true });
    expect(app.emitted("drop")).toEqual([[{ targetId: "hub", position: "before", sourceId: "plain" }]]);
    await app.setProps({ drag: true });
    expect(app.get('[data-tree-row="plain"]').attributes("draggable")).toBe("true");
    app.unmount();
  });
});
