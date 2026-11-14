import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import PageTree from "./PageTree.vue";
import type { TreePage } from "../treeMove";

const node = (id: string, parentId: string | null = null): TreePage => ({
  id,
  parentId,
  spaceId: "space",
  title: id,
  version: 1,
  isPublished: true,
  canEdit: true,
});

const stubs = { AtlasIcon: true, PageIcon: true, PageStatusBadge: true };

function tree(pages: TreePage[], props: Record<string, unknown> = {}) {
  return mount(PageTree, { props: { pages, ...props }, global: { stubs } });
}

describe("PageTree tree semantics", () => {
  it("exposes the root list as a tree and nested lists as groups", () => {
    const view = tree([node("parent"), node("child", "parent")]);
    expect(view.get("ul").attributes("role")).toBe("tree");
    const groups = view.findAll('ul[role="group"]');
    expect(groups.length).toBe(1);
  });

  it("makes every page link a treeitem", () => {
    const view = tree([node("a"), node("b"), node("c", "a")]);
    expect(view.findAll('[role="treeitem"]').length).toBe(3);
    // The li is presentational: the treeitem is the link, so the link keeps
    // being a real link (and keeps its aria-current) rather than becoming a div.
    expect(view.get("li").attributes("role")).toBe("none");
  });

  it("positions every item so a screen reader can say where it is", () => {
    const view = tree([node("a"), node("b"), node("c", "a")]);
    const [first, nested] = [
      view.get('[role="treeitem"]'),
      view.findAll('ul[role="group"] [role="treeitem"]')[0]!,
    ];
    expect(first.attributes("aria-level")).toBe("1");
    expect(first.attributes("aria-posinset")).toBe("1");
    expect(first.attributes("aria-setsize")).toBe("2");
    expect(nested.attributes("aria-level")).toBe("2");
    expect(nested.attributes("aria-posinset")).toBe("1");
    expect(nested.attributes("aria-setsize")).toBe("1");
  });

  it("tells assistive technology whether a branch is open", () => {
    const collapsed = tree([node("parent"), node("child", "parent")], {
      collapsedIds: new Set(["parent"]),
    });
    expect(collapsed.get('[role="treeitem"]').attributes("aria-expanded")).toBe("false");
    const open = tree([node("parent"), node("child", "parent")]);
    expect(open.get('[role="treeitem"]').attributes("aria-expanded")).toBe("true");
  });

  it("leaves a leaf without an expanded state", () => {
    const view = tree([node("only")]);
    expect(view.get('[role="treeitem"]').attributes("aria-expanded")).toBeUndefined();
  });

  it("keeps the hooks existing tests select on", () => {
    const view = tree([node("a"), node("b")], { canReorder: true });
    expect(view.findAll(".tree-row").length).toBe(2);
    expect(view.findAll('.tree-row[draggable="true"]').length).toBe(2);
    expect(view.findAll(".tree-page").length).toBe(2);
  });
});

describe("PageTree keyboard model", () => {
  const pages = [node("a"), node("a1", "a"), node("b")];

  it("keeps exactly one tab stop for the whole tree", () => {
    const view = tree(pages);
    const items = view.findAll('[role="treeitem"]');
    expect(items.filter(i => i.attributes("tabindex") === "0")).toHaveLength(1);
    expect(items.filter(i => i.attributes("tabindex") === "-1")).toHaveLength(2);
  });

  it("moves the tab stop down and up through the visible order", async () => {
    const view = tree(pages);
    const at = () => view.findAll('[role="treeitem"]')
      .findIndex(i => i.attributes("tabindex") === "0");
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowDown" });
    expect(at()).toBe(1);
    await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowUp" });
    expect(at()).toBe(0);
  });

  it("returns to the last focused page after pointer or programmatic focus", async () => {
    const view = tree(pages);
    const items = view.findAll('[role="treeitem"]');
    await items[2]!.trigger("focus");
    expect(items[2]!.attributes("tabindex")).toBe("0");
    expect(items[0]!.attributes("tabindex")).toBe("-1");
    view.unmount();
  });

  it("does not add a separate tab stop for each branch toggle", () => {
    const view = tree(pages);
    const stops = view.findAll<HTMLButtonElement | HTMLAnchorElement>('button, a')
      .filter(item => item.element.tabIndex >= 0);
    expect(stops).toHaveLength(1);
    expect(stops[0]!.attributes("role")).toBe("treeitem");
    view.unmount();
  });

  it("does not wrap past either end", async () => {
    const view = tree(pages);
    const items = () => view.findAll('[role="treeitem"]');
    const at = () => items().findIndex(i => i.attributes("tabindex") === "0");
    await items()[0]!.trigger("keydown", { key: "ArrowUp" });
    expect(at()).toBe(0);
    await items()[0]!.trigger("keydown", { key: "End" });
    expect(at()).toBe(2);
    await items()[2]!.trigger("keydown", { key: "ArrowDown" });
    expect(at()).toBe(2);
    await items()[2]!.trigger("keydown", { key: "Home" });
    expect(at()).toBe(0);
  });

  it("moves ten rows with PageDown and PageUp, stopping at the ends (UX SE5-3)", async () => {
    const view = tree(Array.from({ length: 25 }, (_, index) => node(`p${index}`)));
    const items = () => view.findAll('[role="treeitem"]');
    const at = () => items().findIndex(i => i.attributes("tabindex") === "0");
    await items()[0]!.trigger("keydown", { key: "PageDown" });
    expect(at()).toBe(10);
    await items()[10]!.trigger("keydown", { key: "PageDown" });
    await items()[20]!.trigger("keydown", { key: "PageDown" });
    expect(at()).toBe(24);
    await items()[24]!.trigger("keydown", { key: "PageUp" });
    expect(at()).toBe(14);
    await items()[14]!.trigger("keydown", { key: "PageUp" });
    await items()[4]!.trigger("keydown", { key: "PageUp" });
    expect(at()).toBe(0);
  });

  it("collapses an open branch with ArrowLeft using the existing emit", async () => {
    const view = tree(pages);
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowLeft" });
    expect(view.emitted("toggleCollapsed")).toEqual([["a"]]);
  });

  it("expands a collapsed branch with ArrowRight", async () => {
    const view = tree(pages, { collapsedIds: new Set(["a"]) });
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowRight" });
    expect(view.emitted("toggleCollapsed")).toEqual([["a"]]);
  });

  it("ArrowLeft climbs to the parent when the branch is already closed", async () => {
    const view = tree(pages);
    const items = view.findAll('[role="treeitem"]');
    await items[1]!.trigger("keydown", { key: "ArrowLeft" });
    expect(view.emitted("toggleCollapsed")).toBeUndefined();
    expect(view.findAll('[role="treeitem"]').findIndex(i => i.attributes("tabindex") === "0")).toBe(0);
  });
});

describe("PageTree keyboard reordering", () => {
  it("reorders with Alt+Arrow through the existing drop emit and payload", async () => {
    const view = tree([node("a"), node("b")], { canReorder: true });
    await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowUp", altKey: true });
    // The mouse drop payload plus the source: no drag has named it for a keyboard move (SH2-2).
    expect(view.emitted("drop")).toEqual([[{ targetId: "a", position: "before", sourceId: "b" }]]);
  });

  it("moves a page down past its next sibling", async () => {
    const view = tree([node("a"), node("b")], { canReorder: true });
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowDown", altKey: true });
    expect(view.emitted("drop")).toEqual([[{ targetId: "b", position: "after", sourceId: "a" }]]);
  });

  it("nests a page under the sibling above with Alt+ArrowRight and lifts it out below its parent with Alt+ArrowLeft", async () => {
    const view = tree([node("a"), node("b")], { canReorder: true });
    await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowRight", altKey: true });
    expect(view.emitted("drop")).toEqual([[{ targetId: "a", position: "inside", sourceId: "b" }]]);
    const nested = tree([node("a"), node("b", "a")], { canReorder: true });
    await nested.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowLeft", altKey: true });
    expect(nested.emitted("drop")).toEqual([[{ targetId: "a", position: "after", sourceId: "b" }]]);
    // The first sibling has nothing above to nest under; a root page has no parent to leave.
    const first = tree([node("a"), node("b")], { canReorder: true });
    await first.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowRight", altKey: true });
    await first.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowLeft", altKey: true });
    expect(first.emitted("drop")).toBeUndefined();
  });

  it("announces that a page cannot nest where Alt+ArrowRight finds nothing to nest under, a leaf or no sibling above", async () => {
    const view = tree([{ ...node("board"), type: "board" }, node("b")], { canReorder: true });
    expect(view.get('[role="status"]').text()).toBe("");
    await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowRight", altKey: true });
    expect(view.emitted("drop")).toBeUndefined();
    expect(view.get('[role="status"]').text()).toBe("Sem nejde zanořit");
    const first = tree([node("a"), node("b")], { canReorder: true });
    await first.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowRight", altKey: true });
    expect(first.get('[role="status"]').text()).toBe("Sem nejde zanořit");
    // A reader moves nothing, so there is nothing to refuse.
    const reader = tree([node("a"), node("b")]);
    await reader.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowRight", altKey: true });
    expect(reader.get('[role="status"]').text()).toBe("");
  });

  it("puts the menu popup on the „⋯“ button only and speaks and sorts the plain words of a title", async () => {
    const view = tree([{ ...node("a"), title: "**Úvod** do `kódu`" }], { menu: true });
    expect(view.get('[role="treeitem"]').attributes("aria-haspopup")).toBeUndefined();
    expect(view.get("[data-tree-more]").attributes("aria-haspopup")).toBe("menu");
    expect(view.get(".tree-page-title").text()).toBe("Úvod do kódu");
    expect(view.get("[data-tree-more]").attributes("aria-label")).toBe("Akce: Úvod do kódu");
    // Renaming edits the Markdown itself.
    await view.get('[role="treeitem"]').trigger("keydown", { key: "F2" });
    expect(view.get<HTMLInputElement>(".tree-rename").element.value).toBe("**Úvod** do `kódu`");
  });

  it("refuses a move resolveTreeMove rejects", async () => {
    const view = tree([{ ...node("locked"), canEdit: false }, node("other")], { canReorder: true });
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowDown", altKey: true });
    expect(view.emitted("drop")).toBeUndefined();
  });

  it("does nothing while reordering is off or the tree is busy", async () => {
    for (const props of [{ canReorder: false }, { canReorder: true, busy: true }]) {
      const view = tree([node("a"), node("b")], props);
      await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "ArrowUp", altKey: true });
      expect(view.emitted("drop")).toBeUndefined();
    }
  });
});

describe("PageTree tab stop survives the tree changing under it", () => {
  it("keeps exactly one tab stop after the focused row's ancestor is collapsed", async () => {
    // Arrow onto a nested child, then collapse its parent with the mouse. The
    // focused row stops rendering; without a fallback the remembered id matches
    // nothing and the whole tree drops out of the tab order.
    const pages = [node("parent"), node("child", "parent"), node("other")];
    const view = mount(PageTree, {
      props: { pages, collapsedIds: new Set<string>() },
      global: { stubs },
    });
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowDown" });
    expect(view.findAll('[role="treeitem"]').filter(i => i.attributes("tabindex") === "0")).toHaveLength(1);

    await view.setProps({ collapsedIds: new Set(["parent"]) });
    const stops = view.findAll('[role="treeitem"]').filter(i => i.attributes("tabindex") === "0");
    expect(stops).toHaveLength(1);
  });

  it("keeps exactly one tab stop when the focused page disappears from the list", async () => {
    const view = mount(PageTree, {
      props: { pages: [node("a"), node("b")] },
      global: { stubs },
    });
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "ArrowDown" });
    await view.setProps({ pages: [node("a")] });
    expect(view.findAll('[role="treeitem"]').filter(i => i.attributes("tabindex") === "0")).toHaveLength(1);
  });
});

describe("PageTree type-ahead (keyboard pass)", () => {
  const titled = (id: string, title: string, parentId: string | null = null) => ({ ...node(id, parentId), title });
  it("jumps to the next visible page whose title starts with the typed letter, diacritics aside", async () => {
    const view = tree([titled("a", "Architektura"), titled("b", "Čtení"), titled("c", "Cesty"), titled("d", "Bezpečnost")]);
    const at = () => view.findAll('[role="treeitem"]').findIndex(i => i.attributes("tabindex") === "0");
    await view.findAll('[role="treeitem"]')[0]!.trigger("keydown", { key: "c" });
    expect(at()).toBe(1);
    await new Promise(resolve => setTimeout(resolve, 750));
    await view.findAll('[role="treeitem"]')[1]!.trigger("keydown", { key: "c" });
    expect(at()).toBe(2);
    await new Promise(resolve => setTimeout(resolve, 750));
    await view.findAll('[role="treeitem"]')[2]!.trigger("keydown", { key: "b" });
    expect(at()).toBe(3);
    view.unmount();
  });
});
