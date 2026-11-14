// Boards in the space tree (interaction spec §6, catalog §2): the same rows, keys and drags as pages, told apart only by
// a quiet board icon; a board is a leaf, opens at its own address and carries no page payload when dragged.
import { describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import PageTree from "./PageTree.vue";
import type { TreePage } from "../treeMove";

const node = (id: string, parentId: string | null = null, type: "page" | "board" = "page"): TreePage =>
  ({ id, parentId, spaceId: "space", title: id, version: type === "board" ? 0 : 1, isPublished: true, canEdit: true, type });
const pages = [node("a"), node("plan", "a", "board"), node("a1", "a"), node("map", null, "board"), node("c")];

function row(app: ReturnType<typeof mount>, id: string) {
  return app.get(`[data-tree-item="${id}"]`);
}
function dragEvent(type: string, clientY = 0) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.assign(event, { clientY, dataTransfer: { setData: vi.fn(), effectAllowed: "", dropEffect: "" } });
  return event;
}

describe("a board in the page tree", () => {
  it("renders as a tree row with the board icon and its own address, and opens through the same select", async () => {
    const app = mount(PageTree, { props: { pages, activeId: "map" } });
    const board = row(app, "map");
    expect(board.attributes("role")).toBe("treeitem");
    expect(board.attributes("href")).toContain("#/boards/map");
    expect(board.attributes("aria-current")).toBe("page");
    expect(board.find('[data-tree-board-icon] [data-icon="layout-dashboard"], [data-tree-board-icon]').exists()).toBe(true);
    // Only the icon tells it apart: the title is the same span as a page's.
    expect(board.get(".tree-page-title").text()).toBe("map");
    expect(row(app, "c").find("[data-tree-board-icon]").exists()).toBe(false);
    // A leaf: no chevron, no aria-expanded.
    expect(board.attributes("aria-expanded")).toBeUndefined();
    board.element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    expect(app.emitted("select")).toEqual([[expect.objectContaining({ id: "map", type: "board" })]]);
    app.unmount();
  });

  it("walks boards with the arrow keys and reorders them with Alt+Arrow like pages", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true }, attachTo: document.body });
    await row(app, "map").trigger("keydown", { key: "ArrowUp", altKey: true });
    expect(app.emitted("drop")).toEqual([[{ targetId: "a", position: "before", sourceId: "map" }]]);
    await row(app, "plan").trigger("keydown", { key: "ArrowLeft" });
    await new Promise(resolve => requestAnimationFrame(resolve));
    expect(document.activeElement).toBe(row(app, "a").element);
    app.unmount();
  });

  it("drags a board to reorder without the page payload a board canvas would turn into a card", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true } });
    await new Promise(resolve => setTimeout(resolve, 5));
    const event = dragEvent("dragstart");
    row(app, "map").element.closest(".tree-row")!.dispatchEvent(event);
    expect(app.emitted("dragStart")?.[0]?.[0]).toMatchObject({ id: "map" });
    const transfer = (event as unknown as { dataTransfer: { setData: ReturnType<typeof vi.fn>; effectAllowed: string } }).dataTransfer;
    expect(transfer.setData).not.toHaveBeenCalledWith("application/x-atlas-page", "map");
    // Another space's tree places a reference to it (the object payload, which a board canvas does not read).
    expect(transfer.setData).toHaveBeenCalledWith("application/x-atlas-object", JSON.stringify({ id: "map", typeId: "atlas.board", title: "map" }));
    expect(transfer.effectAllowed).toBe("copyMove");
    app.unmount();
  });

  it("offers before and after on a board row, never inside it", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, draggedId: "a1" } });
    const boardRow = row(app, "map").element.closest(".tree-row") as HTMLElement;
    boardRow.getBoundingClientRect = () => ({ top: 0, height: 30, bottom: 30, left: 0, right: 100, width: 100, x: 0, y: 0, toJSON: () => ({}) });
    // The middle of a page row is „inside“; on a board it falls to the nearer edge.
    boardRow.dispatchEvent(dragEvent("dragover", 14));
    await app.vm.$nextTick();
    expect(boardRow.classList.contains("drop-before")).toBe(true);
    boardRow.dispatchEvent(dragEvent("drop", 16));
    expect(app.emitted("drop")).toEqual([[{ targetId: "map", position: "after" }]]);
    app.unmount();
  });
});
