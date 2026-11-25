// Reference nodes, drops from outside, the row menu and its keys.
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import PageTree from "./PageTree.vue";
import { loadActionsMenu } from "./boards/canvas/actionsMenuChunk";
import { atlasObjectDragType, objectPayload } from "../boards/boardDrop";
import type { TreePage } from "../treeMove";

const contact = "a1b2c3d4-0000-4000-8000-000000000001";
const target = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const node = (id: string, parentId: string | null = null, extra: Partial<TreePage> = {}): TreePage =>
  ({ id, parentId, spaceId: "space", title: id, version: 1, isPublished: true, canEdit: true, ...extra });
const pages = [
  node("a", null, { attachedBoardId: "board-a" }), node("a1", "a"),
  node("ref", "a", { type: "reference", version: 0, title: "Jan Novák", targetId: contact, targetTypeId: "atlas.contact" }),
  node("sec", null, { type: "reference", version: 0, title: "Úvod", targetId: target, targetTypeId: "atlas.page", anchor: "uvod" }),
  node("locked", null, { type: "reference", version: 0, title: "", readable: false, canEdit: true }),
];
const row = (app: ReturnType<typeof mount>, id: string) => app.get(`[data-tree-item="${id}"]`);
// The row menu is the page editor's own (its chunk is in before the tests, as the tree preheats it).
beforeAll(async () => { await loadActionsMenu(); });
const menu = () => document.querySelector<HTMLElement>("[data-testid=intentions]");
const menuRows = () => [...menu()?.querySelectorAll<HTMLElement>("[role=option]") ?? []];
const labels = () => menuRows().map(item => item.querySelector(".t")!.textContent);
const rowBox = (app: ReturnType<typeof mount>, id: string) => {
  const element = row(app, id).element.closest(".tree-row") as HTMLElement;
  element.getBoundingClientRect = () => ({ top: 0, height: 30, bottom: 30, left: 0, right: 100, width: 100, x: 0, y: 0, toJSON: () => ({}) });
  return element;
};
function drag(type: string, clientY: number, data: Record<string, string>) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.assign(event, { clientY, dataTransfer: { types: Object.keys(data), getData: (key: string) => data[key] ?? "", dropEffect: "" } });
  return event;
}
afterEach(() => { document.body.replaceChildren(); });

describe("reference nodes in the tree", () => {
  it("shows a reference with its target's icon and address, and an unreadable one as a lock without a title", () => {
    const app = mount(PageTree, { props: { pages, collapsedIds: new Set<string>() } });
    expect(row(app, "sec").attributes("href")).toContain(`#/pages/${target}?heading=uvod`);
    expect(row(app, "sec").find("[data-tree-reference-icon]").exists()).toBe(true);
    // The icons are the object types' own, the ones Obsah draws.
    expect(row(app, "sec").find('[data-tree-reference-icon] [data-icon="file-text"]').exists()).toBe(true);
    expect(row(app, "ref").find('[data-tree-reference-icon] [data-icon="user"]').exists()).toBe(true);
    expect(row(app, "ref").attributes("href")).toBeUndefined();
    expect(row(app, "locked").get(".tree-page-title").text()).toBe("");
    expect(row(app, "locked").text()).toContain("Odkaz, k cíli nemáte přístup");
    // A leaf, in the ARIA of its level.
    expect(row(app, "ref").attributes()).toMatchObject({ role: "treeitem", "aria-level": "2", "aria-posinset": "2", "aria-setsize": "2" });
    expect(row(app, "ref").attributes("aria-expanded")).toBeUndefined();
    app.unmount();
  });

  it("lets a reader drag a readable reference out as a copy, without moving anything in the tree", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: false } });
    await new Promise(resolve => setTimeout(resolve, 5));
    expect(rowBox(app, "sec").getAttribute("draggable")).toBe("true");
    expect(rowBox(app, "locked").getAttribute("draggable")).toBe("false");
    const event = new Event("dragstart", { bubbles: true, cancelable: true });
    const dataTransfer = { setData: vi.fn(), effectAllowed: "" };
    Object.assign(event, { dataTransfer });
    rowBox(app, "sec").dispatchEvent(event);
    expect(app.emitted("dragStart")).toBeUndefined();
    expect(dataTransfer.setData).toHaveBeenCalledWith(atlasObjectDragType, expect.stringContaining(target));
    expect(dataTransfer.effectAllowed).toBe("copy");
    app.unmount();
  });

  it("takes an object dropped from outside at the row's place by the thirds rule, never inside a leaf", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true } });
    const data = { [atlasObjectDragType]: objectPayload({ id: contact, typeId: "atlas.contact", title: "Jan" }) };
    rowBox(app, "a").dispatchEvent(drag("drop", 15, data));
    rowBox(app, "sec").dispatchEvent(drag("drop", 15, data));
    expect(app.emitted("insert")).toEqual([
      [{ targetId: "a", position: "inside" }, { targetId: contact, typeId: "atlas.contact", title: "Jan" }],
      [{ targetId: "sec", position: "after" }, { targetId: contact, typeId: "atlas.contact", title: "Jan" }],
    ]);
    expect(app.emitted("drop")).toBeUndefined();
    // Without the right to place, nothing is offered.
    const reader = mount(PageTree, { props: { pages, canReorder: false } });
    const over = drag("dragover", 15, data);
    rowBox(reader, "a").dispatchEvent(over);
    expect(over.defaultPrevented).toBe(false);
    reader.unmount(); app.unmount();
  });

  it("opens the page editor's menu from a row's „⋯“ with grouped actions, filters it by typing and reports the chosen one", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true, boards: true }, attachTo: document.body });
    await app.get('[data-tree-row="a"] [data-tree-more]').trigger("click");
    expect(menu()!.getAttribute("aria-label")).toBe("Akce: a");
    expect(labels()).toEqual(["Otevřít", "Otevřít připojenou tabuli", "Kopírovat odkaz", "Nová podstránka", "Nová tabule", "Přejmenovat", "Přesunout…", "Řadit abecedně", "Přesunout do koše…"]);
    expect(menu()!.querySelectorAll(".sep")).toHaveLength(3);
    expect(menuRows().at(-1)!.classList).toContain("danger");
    expect(menuRows()[5]!.querySelector("kbd")!.textContent).toBe("F2");
    expect(app.get('[data-tree-row="a"]').classes()).toContain("tree-row-menu-open");
    for (const letter of "přesunout") document.body.dispatchEvent(new KeyboardEvent("keydown", { key: letter, bubbles: true, cancelable: true }));
    expect(labels()).toEqual(["Přesunout…", "Přesunout do koše…"]);
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(menu()).toBeNull();
    expect(app.emitted("action")).toEqual([["move", expect.objectContaining({ id: "a" })]]);
    // The focus went back to the row before the action (a dialog then takes it).
    expect(document.activeElement).toBe(row(app, "a").element);
    await app.vm.$nextTick();
    expect(app.get('[data-tree-row="a"]').classes()).not.toContain("tree-row-menu-open");
    app.unmount();
  });

  it("answers Delete, F2 and Ctrl+Enter on a row", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    await row(app, "ref").trigger("keydown", { key: "Delete" });
    await row(app, "a").trigger("keydown", { key: "Enter", ctrlKey: true });
    expect(app.emitted("action")).toEqual([["remove", expect.objectContaining({ id: "ref" })], ["openAttached", expect.objectContaining({ id: "a" })]]);
    await row(app, "a1").trigger("keydown", { key: "F2" });
    const field = app.get('[data-tree-rename="a1"]');
    await field.setValue("Nový název");
    await field.trigger("keydown", { key: "Enter" });
    expect(app.emitted("rename")).toEqual([[expect.objectContaining({ id: "a1" }), "Nový název"]]);
    // A reference keeps its target's title: no rename.
    await row(app, "ref").trigger("keydown", { key: "F2" });
    expect(app.find('[data-tree-rename="ref"]').exists()).toBe(false);
    app.unmount();
  });

  it("opens the same actions menu with every key of „Co s tím jde dělat“, and not with a plain Space or Enter", async () => {
    for (const init of [{ key: " ", code: "Space", ctrlKey: true }, { key: "Enter", altKey: true }, { key: ".", ctrlKey: true }, { key: "F10", shiftKey: true }, { key: "ContextMenu" }]) {
      const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
      await row(app, "a1").trigger("keydown", init);
      expect(menu(), JSON.stringify(init)).not.toBeNull();
      app.unmount();
      document.body.replaceChildren();
    }
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    for (const init of [{ key: " ", code: "Space" }, { key: "Enter" }, { key: "Enter", ctrlKey: true, altKey: true }]) await row(app, "a1").trigger("keydown", init);
    expect(menu()).toBeNull();
    app.unmount();
  });

  it("opens a page on the first click and renames it in place on the double click (a row is a link)", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    await row(app, "a1").trigger("click");
    expect(app.emitted("select")).toEqual([[expect.objectContaining({ id: "a1" })]]);
    expect(app.find("[data-tree-rename]").exists()).toBe(false);
    await row(app, "a1").trigger("dblclick");
    const field = app.get<HTMLInputElement>('[data-tree-rename="a1"]');
    expect(field.element.value).toBe("a1");
    await field.setValue("Nový název");
    await field.trigger("keydown", { key: "Enter" });
    expect(app.emitted("rename")).toEqual([[expect.objectContaining({ id: "a1" }), "Nový název"]]);
    // A reference keeps its target's title, the „⋯“ is a button of its own, and a tree without the menu renames nothing.
    await row(app, "ref").trigger("dblclick");
    await app.get('[data-tree-row="a"] [data-tree-more]').trigger("dblclick");
    expect(app.find("[data-tree-rename]").exists()).toBe(false);
    const plain = mount(PageTree, { props: { pages }, attachTo: document.body });
    await row(plain, "a1").trigger("dblclick");
    expect(plain.find("[data-tree-rename]").exists()).toBe(false);
    plain.unmount(); app.unmount();
  });

  it("opens the page once on a double click: its second click does not fetch it again", async () => {
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    for (const detail of [1, 2]) row(app, "a1").element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, detail }));
    expect(app.emitted("select")).toHaveLength(1);
    app.unmount();
  });

  // P4: Tab or a click out of the field saves the name and leaves the keyboard where the reader went; only Enter, Esc or a blur to nowhere hand it back to the row.
  it("saves a name left by a click or Tab without pulling the keyboard back to the row", async () => {
    const other = document.createElement("input");
    document.body.append(other);
    const app = mount(PageTree, { props: { pages, canReorder: true, menu: true }, attachTo: document.body });
    await row(app, "a1").trigger("keydown", { key: "F2" });
    const field = app.get<HTMLInputElement>('[data-tree-rename="a1"]');
    field.element.focus();
    await field.setValue("Nový název");
    other.focus();
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(app.emitted("rename")).toEqual([[expect.objectContaining({ id: "a1" }), "Nový název"]]);
    expect(document.activeElement).toBe(other);
    await row(app, "a1").trigger("keydown", { key: "F2" });
    app.get<HTMLInputElement>('[data-tree-rename="a1"]').element.focus();
    (document.activeElement as HTMLElement).blur();
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(document.activeElement).toBe(row(app, "a1").element);
    app.unmount();
  });

  it("opens the page's own board from its quiet mark", async () => {
    const open = vi.fn();
    const app = mount(PageTree, { props: { pages, menu: true, onAction: open } });
    await app.get('[data-tree-row="a"] [data-tree-attached]').trigger("click");
    expect(open).toHaveBeenCalledWith("openAttached", expect.objectContaining({ id: "a" }));
    expect(app.find('[data-tree-row="a1"] [data-tree-attached]').exists()).toBe(false);
    app.unmount();
  });
});
