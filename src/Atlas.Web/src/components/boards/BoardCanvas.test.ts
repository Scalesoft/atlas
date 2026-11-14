import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { useVueFlow, VueFlow } from "@vue-flow/core";
import { NodeResizeControl, NodeResizer } from "@vue-flow/node-resizer";
import { nextTick, toRaw } from "vue";
import { toast } from "vue-sonner";
import type { BoardChange, BoardSettings } from "../../boards/boardApi";
import { boardClipboardType, copyPayload } from "../../boards/boardClipboard";
import { toFlowEdges, toFlowNodes } from "../../boards/boardFlow";
import { registerBoardIntentions } from "../../boards/boardIntentions";
import type { BoardViewItem } from "../../boards/boardSession";
import { editedPage } from "../../boards/pageEditing";
import { liveObjects } from "../../objects/liveObjects";
import BoardCanvas from "./BoardCanvas.vue";
import { loadActionsMenu } from "./canvas/actionsMenuChunk";
import BoardCommandPanel from "./canvas/BoardCommandPanel.vue";
import BoardToolRail from "./canvas/BoardToolRail.vue";
import { textEditorReady } from "./text/textEditorChunk";
import type { TextBinding } from "./text/textBinding";

// The page editor that edits a text in place is its own component with its own tests (components/boards/text); here it is a plain
// field that commits through the same binding, so the tests see what the canvas does with a commit. The renderer is its own chunk
// with its own tests; here a text at rest is a span. Both replace the lazy components BoardText takes from textEditorChunk.
const stubs = await vi.hoisted(async () => {
  const { defineComponent, h } = await import("vue");
  return {
    EditorStub: defineComponent({
      name: "BoardTextEditor",
      props: { binding: { type: Object, required: true }, inline: Boolean, active: { type: Boolean, default: true } },
      emits: ["cancel", "commit", "unsaved"],
      // One editor (BoardText): read-only at rest, an editing flips it in place.
      setup: props => () => h("textarea", { "data-editor-stub": "", readonly: !props.active, value: (props.binding as TextBinding).read(), onChange: (event: Event) => (props.binding as TextBinding).commit((event.target as HTMLTextAreaElement).value) }),
    }),
    MarkdownStub: defineComponent({ name: "BoardMarkdown", props: { text: { type: String, default: "" } }, setup: props => () => h("span", { "data-board-text": "" }, props.text) }),
  };
});
const { EditorStub } = stubs;
// The preheat and the load are no-ops: the real ones load the real editor chunk and raise textEditorReady, which a test sets by hand.
vi.mock("./text/textEditorChunk", async importOriginal => ({ ...await importOriginal<typeof import("./text/textEditorChunk")>(), BoardTextEditor: stubs.EditorStub, BoardMarkdown: stubs.MarkdownStub, preheatTextEditor: () => {}, loadTextEditor: async () => ({}) }));
vi.mock("vue-sonner", () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismiss: vi.fn() } }));
// The derivations as they are, watched: what a selection or a drag frame derives anew.
vi.mock("../../boards/boardFlow", async importOriginal => {
  const original = await importOriginal<typeof import("../../boards/boardFlow")>();
  return { ...original, toFlowNodes: vi.fn(original.toFlowNodes), toFlowEdges: vi.fn(original.toFlowEdges) };
});

const note = (id: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "note", x: 0, y: 0, width: 200, height: 200, z: 1, text: id, color: "yellow", ...extra });
const link = (id: string, from: string, to: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to, ...extra });
const twoNotes = () => [note("a"), note("b", { x: 400, z: 2 })];

let wrapper: VueWrapper | undefined;
function canvas(props: Record<string, unknown> = {}) {
  wrapper = mount(BoardCanvas, { attachTo: document.body, props: { items: twoNotes(), label: "Plán", canEdit: true, ...props } });
  vi.spyOn(wrapper.get("[data-board-surface]").element, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 1200, 800));
  return wrapper;
}
const flow = () => wrapper!.findComponent(VueFlow);
const store = () => useVueFlow(flow().props("id") as string);
const changes = () => (wrapper!.emitted("change") ?? []).map(([batch]) => batch as BoardChange[]);
const node = (id: string) => wrapper!.get(`.vue-flow__node[data-id="${id}"]`);
const selectedIds = () => wrapper!.findAll(".vue-flow__node.selected").map(element => element.attributes("data-id"));
/** The editor's „Co s tím jde dělat“ (P2), in the body. */
const menu = () => document.querySelector("[data-testid=intentions]");
const menuRows = () => [...menu()?.querySelectorAll("[role=option] .t") ?? []].map(row => row.textContent);
async function select(...ids: string[]) {
  flow().vm.$emit("nodesChange", ids.map(id => ({ id, type: "select", selected: true })));
  await nextTick();
}
async function drag(lead: string, ids: string[], to: { x: number; y: number }) {
  flow().vm.$emit("nodeDragStart", { nodes: ids.map(id => ({ id })) });
  flow().vm.$emit("nodesChange", [{ id: lead, type: "position", dragging: true, position: to }]);
  await nextTick();
}
const stop = async () => { flow().vm.$emit("nodeDragStop", {}); await nextTick(); };
function key(target: Element, init: KeyboardEventInit) { target.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, cancelable: true, ...init })); }
function clipboardEvent(type: string, data: Record<string, string>) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, "clipboardData", { value: { getData: (format: string) => data[format] ?? "", setData: (format: string, value: string) => { data[format] = value; } } });
  return event;
}

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
  // The remembered connector style is per browser: no test leaves one for the next.
  localStorage.removeItem("atlas.board.connectorStyle");
});

describe("BoardCanvas: selection", () => {
  it("takes what a marquee took, and of the connectors only those whose both ends it took", async () => {
    canvas({ items: [note("a"), note("b", { x: 400 }), note("x", { x: 900 }), link("ab", "a", "b"), link("ax", "a", "x")] });
    store().userSelectionActive.value = true;
    flow().vm.$emit("nodesChange", [{ id: "a", type: "select", selected: true }, { id: "b", type: "select", selected: true }]);
    flow().vm.$emit("edgesChange", [{ id: "ab", type: "select", selected: true }, { id: "ax", type: "select", selected: true }]);
    store().userSelectionActive.value = false;
    await nextTick();
    expect(selectedIds()).toEqual(["a", "b"]);
    expect(wrapper!.findAll(".vue-flow__edge.selected").map(edge => edge.attributes("data-id"))).toEqual(["ab"]);
  });

  it("lets a finger add separate items to the selection with Vybírat více (review 3.5.1, finding 10)", async () => {
    const board = canvas({ items: [note("a"), note("b", { x: 400 }), note("c", { x: 800 })] });
    expect(board.find("[data-board-multi-select]").exists()).toBe(false);
    board.get("[data-board-canvas]").element.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, pointerType: "touch" }));
    await nextTick();
    const toggle = board.get("[data-board-multi-select]");
    expect(toggle.attributes("aria-pressed")).toBe("false");
    await toggle.trigger("click");
    expect(toggle.attributes("aria-pressed")).toBe("true");
    expect(store().multiSelectionActive.value).toBe(true);
    // Each tap then adds its item, as Shift+click does (Vue Flow's own click handling under multi-selection).
    store().addSelectedNodes([store().findNode("a")!]);
    store().addSelectedNodes([store().findNode("c")!]);
    await nextTick();
    expect(selectedIds()).toEqual(["a", "c"]);
    await toggle.trigger("click");
    expect(store().multiSelectionActive.value).toBe(false);
  });

  it("selects everything with Ctrl+A and lets Esc drop it", async () => {
    const board = canvas();
    key(board.get("[data-board-surface]").element, { key: "a", ctrlKey: true });
    await nextTick();
    expect(selectedIds()).toEqual(["a", "b"]);
    key(board.get("[data-board-surface]").element, { key: "Escape" });
    await nextTick();
    expect(selectedIds()).toEqual([]);
  });
});

describe("BoardCanvas: moving", () => {
  it("drags the selection locally, shows it to others as a preview, and sends one batch when it is released", async () => {
    const board = canvas();
    await select("a", "b");
    await drag("a", ["a", "b"], { x: 30.4, y: 40 });
    expect(node("b").attributes("style")).toContain("translate(430.4px, 40px)");
    expect(board.emitted("change")).toBeUndefined();
    expect(board.emitted("preview")!.at(-1)![0]).toMatchObject({ dragging: [{ itemId: "a", x: 30, y: 40 }, { itemId: "b", x: 430, y: 40 }] });
    await stop();
    expect(changes()).toEqual([[
      { type: "move", itemId: "a", x: 30, y: 40, width: 200, height: 200 },
      { type: "move", itemId: "b", x: 430, y: 40, width: 200, height: 200 },
    ]]);
    expect(board.emitted("preview")!.at(-1)![0]).toMatchObject({ dragging: [] });
  });

  it("lights up the frame a drag will drop into, and the drop joins it in the same batch (J1)", async () => {
    const frame: BoardViewItem = { id: "f", kind: "frame", x: 600, y: 0, width: 500, height: 400, z: 0, text: "Rám" };
    canvas({ items: [frame, ...twoNotes()] });
    await drag("a", ["a"], { x: 700, y: 50 });
    expect(node("f").find(".board-node").classes()).toContain("is-drop-target");
    expect(node("b").find(".board-node").classes()).not.toContain("is-drop-target");
    await stop();
    expect(changes()).toEqual([[{ type: "move", itemId: "a", x: 700, y: 50, width: 200, height: 200 }, { type: "parent", itemId: "a", parentId: "f" }]]);
    expect(node("f").find(".board-node").classes()).not.toContain("is-drop-target");
  });

  it("walks the items with Tab in reading order and connects the selection from the keyboard with L, an arrow and Enter (J1)", async () => {
    const board = canvas({ items: [note("c", { x: 0, y: 400 }), ...twoNotes()] });
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "Tab" });
    await nextTick();
    expect(selectedIds()).toEqual(["a"]);
    key(surface, { key: "Tab" });
    await nextTick();
    expect(selectedIds()).toEqual(["b"]);
    key(surface, { key: "l" });
    key(surface, { key: "ArrowLeft" });
    await nextTick();
    expect(board.get("[data-board-announcement]").text()).toBe("Spojit z Poznámka: b. Cíl: Poznámka: a. Enter spojí, Esc zruší.");
    expect(node("a").find(".board-node").classes()).toContain("is-drop-target");
    key(surface, { key: "Enter" });
    await nextTick();
    expect(changes().at(-1)).toMatchObject([{ type: "create", item: { kind: "connector", from: "b", to: "a" } }]);
    expect(node("a").find(".board-node").classes()).not.toContain("is-drop-target");
  });

  it("resizes the only selected item from its eight handles as one batch, a note keeping its aspect unless Shift is held", async () => {
    const board = canvas();
    await select("a");
    await nextTick();
    const resizer = board.findComponent(NodeResizer);
    expect(resizer.props("keepAspectRatio")).toBe(true);
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Shift", shiftKey: true }));
    await nextTick();
    expect(resizer.props("keepAspectRatio")).toBe(false);
    window.dispatchEvent(new KeyboardEvent("keyup", { key: "Shift" }));
    resizer.vm.$emit("resize", { params: { x: -10, y: 0, width: 260.4, height: 180, direction: [-1, 0] } });
    await nextTick();
    expect(node("a").attributes("style")).toContain("width: 260.4px");
    expect(board.emitted("change")).toBeUndefined();
    resizer.vm.$emit("resizeEnd", { params: { x: -10, y: 0, width: 260.4, height: 180 } });
    expect(changes()).toEqual([[{ type: "move", itemId: "a", x: -10, y: 0, width: 260, height: 180 }]]);
    await select("b");
    expect(board.findAllComponents(NodeResizer)).toHaveLength(0);
  });

  it("shows every frame of a resize while it runs, not only the first, and sends nothing before release", async () => {
    const board = canvas({ items: [note("a"), { id: "k", kind: "block", x: 400, y: 0, width: 340, height: 104, z: 2, props: { block: { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana" }] } } } } }] });
    await select("a");
    const resizer = board.findComponent(NodeResizer);
    for (const width of [230, 260, 244]) {
      resizer.vm.$emit("resize", { params: { x: 0, y: 0, width, height: 180, direction: [1, 0] } });
      await nextTick();
      expect(node("a").attributes("style")).toContain(`width: ${width}px`);
      expect(node("a").attributes("style")).toContain("height: 180px");
    }
    expect(board.emitted("change")).toBeUndefined();
    // A component that flows takes the width each frame and leaves its height to its content.
    flow().vm.$emit("nodesChange", [{ id: "a", type: "select", selected: false }]);
    await select("k");
    for (const width of [300, 280]) {
      board.findAllComponents(NodeResizeControl)[0]!.vm.$emit("resize", { params: { x: 400, y: 0, width, height: 104, direction: [1, 0] } });
      await nextTick();
      expect(node("k").attributes("style")).toContain(`width: ${width}px`);
      expect(node("k").attributes("style")).not.toContain("height");
    }
  });

  it("moves a frame's members with the frame, in the same batch, and not what only lies over it", async () => {
    canvas({ items: [{ id: "f", kind: "frame", x: 0, y: 0, width: 600, height: 400, z: 1, text: "Sprint" }, note("in", { x: 20, y: 20, z: 2, parent: "f" }),
      note("over", { x: 300, y: 20, z: 3 }), note("out", { x: 700, z: 3 })] });
    await drag("f", ["f"], { x: 100, y: 0 });
    await stop();
    expect(changes()).toEqual([[
      { type: "move", itemId: "f", x: 100, y: 0, width: 600, height: 400 },
      { type: "move", itemId: "in", x: 120, y: 20, width: 200, height: 200 },
    ]]);
  });

  it("snaps to a neighbour's edge within 6 px on screen, and not while Alt is held", async () => {
    const board = canvas({ items: [note("a"), note("b", { x: 400, y: 300 })] });
    await drag("a", ["a"], { x: 404, y: 0 });
    expect(board.findAll("[data-board-guide]").length).toBeGreaterThan(0);
    await stop();
    expect(changes()[0]).toEqual([{ type: "move", itemId: "a", x: 400, y: 0, width: 200, height: 200 }]);
    board.get("[data-board-surface]").element.dispatchEvent(new PointerEvent("pointermove", { bubbles: true, altKey: true }));
    await drag("a", ["a"], { x: 404, y: 0 });
    await stop();
    expect(changes()[1]).toEqual([{ type: "move", itemId: "a", x: 404, y: 0, width: 200, height: 200 }]);
  });

  it("snaps to the board's grid when the board says so, and Ctrl+' switches it", async () => {
    const settings: BoardSettings = { background: "dots", snapToGrid: true, gridSize: 24 };
    const board = canvas({ items: [note("a")], settings });
    await drag("a", ["a"], { x: 37, y: 13 });
    await stop();
    expect(changes()[0]).toEqual([{ type: "move", itemId: "a", x: 48, y: 24, width: 200, height: 200 }]);
    key(board.get("[data-board-surface]").element, { key: "'", ctrlKey: true });
    expect(changes()[1]).toEqual([{ type: "settings", settings: { snapToGrid: false } }]);
  });

  it("nudges the selection by 1 px, 10 px with Shift, and sends a series of presses within 600 ms as one batch", async () => {
    vi.useFakeTimers();
    const board = canvas();
    await select("a");
    const surface = board.get("[data-board-surface]").element;
    for (let press = 0; press < 3; press++) key(surface, { key: "ArrowRight" });
    key(surface, { key: "ArrowDown", shiftKey: true });
    await nextTick();
    expect(board.emitted("change")).toBeUndefined();
    vi.advanceTimersByTime(600);
    expect(changes()).toEqual([[{ type: "move", itemId: "a", x: 3, y: 10, width: 200, height: 200 }]]);
  });

  // Keyboard-first (round 1 review): no mouse needed to size an item; a component that flows takes the width only.
  it("resizes the selected item with Alt and the arrows, a component that flows by its width alone, as one batch", async () => {
    vi.useFakeTimers();
    const table: BoardViewItem = { id: "t", kind: "block", x: 400, y: 0, width: 320, height: 180, z: 3, props: { block: { type: "table" } } };
    const board = canvas({ items: [...twoNotes(), table] });
    await select("a");
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "ArrowRight", altKey: true });
    key(surface, { key: "ArrowDown", altKey: true, shiftKey: true });
    await nextTick();
    expect(board.get("[data-board-announcement]").text()).toBe("Šířka 201, výška 210 px");
    vi.advanceTimersByTime(600);
    expect(changes()).toEqual([[{ type: "move", itemId: "a", x: 0, y: 0, width: 201, height: 210 }]]);
    await select("t");
    flow().vm.$emit("nodesChange", [{ id: "a", type: "select", selected: false }]);
    await nextTick();
    key(surface, { key: "ArrowDown", altKey: true });
    key(surface, { key: "ArrowLeft", altKey: true, shiftKey: true });
    vi.advanceTimersByTime(600);
    expect(changes().at(-1)).toEqual([{ type: "move", itemId: "t", x: 400, y: 0, width: 310, height: 180 }]);
  });

  it("stores the height a component that flows has after Alt narrowed it, not the one it had before its text wrapped", async () => {
    vi.useFakeTimers();
    const table: BoardViewItem = { id: "t", kind: "block", x: 0, y: 0, width: 320, height: 180, z: 3, props: { block: { type: "table" } } };
    const board = canvas({ items: [table] });
    // Opened (its first view), the board draws the component: a preview tile's height is never taken as its own.
    flow().vm.$emit("paneReady", flow().vm);
    await select("t");
    expect(node("t").find("[data-board-block-preview]").exists()).toBe(false);
    key(board.get("[data-board-surface]").element, { key: "ArrowLeft", altKey: true, shiftKey: true });
    flow().vm.$emit("nodesChange", [{ id: "t", type: "dimensions", dimensions: { width: 310, height: 216 } }]);
    vi.advanceTimersByTime(600);
    expect(changes()).toEqual([[{ type: "move", itemId: "t", x: 0, y: 0, width: 310, height: 216 }]]);
  });

  it("goes on from a nudge when Alt follows at once: the resize keeps the move the items have not taken yet", async () => {
    vi.useFakeTimers();
    const board = canvas({ items: twoNotes() });
    await select("a");
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "ArrowRight", shiftKey: true });
    key(surface, { key: "ArrowRight", altKey: true });
    vi.advanceTimersByTime(600);
    expect(changes()).toEqual([
      [{ type: "move", itemId: "a", x: 10, y: 0, width: 200, height: 200 }],
      [{ type: "move", itemId: "a", x: 10, y: 0, width: 201, height: 200 }],
    ]);
  });

  // UX review: a component's own link, disclosure or reveal is reached by Tab from it, before the next item.
  it("tabs from a component into its own controls, then on to the next item", async () => {
    const issue: BoardViewItem = { id: "y", kind: "block", x: 0, y: 300, width: 300, height: 104, z: 3,
      props: { block: { type: "youtrackIssue", attrs: { id: "2-1", key: "ATL-1", title: "Oprava", url: "https://youtrack.example/issue/ATL-1", state: "Open" } } } };
    // The component is a lazy chunk (the page editor's renderer): a cold import takes longer than waitFor's default second.
    await import("./blocks/BoardBlock.vue");
    const board = canvas({ items: [note("a"), issue, note("b", { x: 0, y: 600, z: 4 })] });
    await select("y");
    await vi.waitFor(() => expect(node("y").find("a[href]").exists()).toBe(true));
    const own = node("y").element as HTMLElement, control = node("y").get("a[href]").element as HTMLElement;
    vi.spyOn(control, "getClientRects").mockReturnValue([new DOMRect(0, 0, 40, 16)] as unknown as DOMRectList);
    own.focus();
    key(own, { key: "Tab" });
    expect(document.activeElement).toBe(control);
    key(control, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(own);
    key(own, { key: "Tab" });
    key(control, { key: "Tab" });
    await nextTick();
    expect(selectedIds()).toEqual(["b"]);
    void board;
  });

  it("leaves an overview's level slider its arrows and letters", async () => {
    canvas();
    await select("a");
    const slider = node("a").element.appendChild(document.createElement("span"));
    slider.setAttribute("role", "slider");
    const presses = ["ArrowRight", "s", "m", "l", "x"].map(pressed => new KeyboardEvent("keydown", { key: pressed, bubbles: true, cancelable: true }));
    for (const press of presses) slider.dispatchEvent(press);
    expect(presses.filter(press => press.defaultPrevented)).toEqual([]);
  });

  it("tabs into a deployment card's links and category toggles", async () => {
    canvas();
    await select("a");
    const own = node("a").element as HTMLElement;
    const card = own.appendChild(document.createElement("div"));
    card.setAttribute("data-deployment-card", "product");
    card.innerHTML = `<a href="#m">Modul</a><button type="button">Kategorie</button>`;
    const [module, category] = [...card.children] as HTMLElement[];
    for (const control of [module!, category!]) vi.spyOn(control, "getClientRects").mockReturnValue([new DOMRect(0, 0, 40, 16)] as unknown as DOMRectList);
    own.focus();
    key(own, { key: "Tab" });
    expect(document.activeElement).toBe(module);
    key(module!, { key: "Tab" });
    expect(document.activeElement).toBe(category);
  });
});

// Owner, 4.0: an existing deployment board is put into the architecture picture on command (BoardBlockHost.layout, Projector's).
describe("BoardCanvas: arranging", () => {
  it("offers the host's layout where the selection holds cards it lays out and sends its changes as one batch, one step to undo", async () => {
    const moves: BoardChange[] = [{ type: "move", itemId: "a", x: 500, y: 40, width: 200, height: 200 }, { type: "move", itemId: "b", x: 900, y: 40, width: 200, height: 200 }];
    const layout = { offers: (item: { id: string }) => item.id === "a", changes: vi.fn(async () => moves) };
    canvas({ blockHost: { layout } });
    const toolbar = () => wrapper!.findComponent({ name: "BoardContextToolbar" });
    await select("b");
    expect(toolbar().props("arrangeable")).toBe(false);
    await select("a");
    expect(toolbar().props("arrangeable")).toBe(true);
    toolbar().vm.$emit("layout");
    await flushPromises();
    expect(layout.changes).toHaveBeenCalledWith(expect.any(Array), ["b", "a"]);
    expect(changes()).toEqual([moves]);
  });

  // Review, 2026-11-11: the board changed while the pages were read. Once more over the new board, instead of doing nothing silently.
  it("arranges over the board as it is after a change that came while the host read the pages", async () => {
    const moves: BoardChange[] = [{ type: "move", itemId: "a", x: 500, y: 40, width: 200, height: 200 }];
    const board = canvas();
    const layout = { offers: () => true, changes: vi.fn(async (items: readonly { id: string }[]) => {
      if (layout.changes.mock.calls.length === 1) await board.setProps({ items: twoNotes() });
      return items.length ? moves : [];
    }) };
    await board.setProps({ blockHost: { layout } });
    await select("a", "b");
    wrapper!.findComponent({ name: "BoardContextToolbar" }).vm.$emit("layout");
    await flushPromises();
    expect(layout.changes).toHaveBeenCalledTimes(2);
    expect(changes()).toEqual([moves]);
  });
});

describe("BoardCanvas: connecting", () => {
  it("connects from a handle to another item's handle with both sides, in one batch", async () => {
    canvas();
    flow().vm.$emit("connectStart", { nodeId: "a", handleId: "right", handleType: "source" });
    flow().vm.$emit("connect", { source: "a", target: "b", sourceHandle: "right", targetHandle: "left" });
    flow().vm.$emit("connectEnd", new MouseEvent("mouseup"));
    expect(changes()).toEqual([[{ type: "create", item: expect.objectContaining({ kind: "connector", from: "a", to: "b", props: expect.objectContaining({ fromSide: "right", toSide: "left", route: "elbow", arrowEnd: "arrow" }) }) }]]);
  });

  it("attaches a connector dropped on an item to its nearest side, and one dropped on empty board to a new note", async () => {
    canvas();
    await nextTick();
    document.elementFromPoint ??= () => null;
    vi.spyOn(document, "elementFromPoint").mockReturnValueOnce(node("b").element).mockReturnValueOnce(node("b").element);
    flow().vm.$emit("connectStart", { nodeId: "a", handleId: "bottom", handleType: "source" });
    // Board x 590 of b (400..600) is its right edge.
    flow().vm.$emit("connectEnd", new MouseEvent("mouseup", { clientX: 590, clientY: 100 }));
    expect(changes()[0]).toEqual([{ type: "create", item: expect.objectContaining({ from: "a", to: "b", props: expect.objectContaining({ fromSide: "bottom", toSide: "right" }) }) }]);
    const pane = wrapper!.get(".vue-flow__pane").element;
    vi.spyOn(document, "elementFromPoint").mockReturnValue(pane);
    flow().vm.$emit("connectStart", { nodeId: "a", handleId: "bottom", handleType: "source" });
    flow().vm.$emit("connectEnd", new MouseEvent("mouseup", { clientX: 100, clientY: 600 }));
    const [created, connector] = changes()[1]!;
    expect(created).toMatchObject({ type: "create", item: { kind: "note", x: 0, y: 500 } });
    expect(connector).toMatchObject({ type: "create", item: { kind: "connector", from: "a", to: (created as { item: { id: string } }).item.id } });
  });

  it("reconnects a connector's end to another item by replacing it in one batch, keeping its label and style", async () => {
    canvas({ items: [...twoNotes(), note("d", { y: 500 }), link("c", "a", "b", { text: "vede", props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "dashed" } })] });
    const end = document.createElement("circle");
    end.setAttribute("data-type", "target");
    flow().vm.$emit("edgeUpdateStart", { event: { target: end }, edge: { id: "c" } });
    flow().vm.$emit("edgeUpdate", { edge: { id: "c" }, connection: { source: "a", target: "d", sourceHandle: "bottom", targetHandle: "top" } });
    flow().vm.$emit("edgeUpdateEnd", { event: new MouseEvent("mouseup"), edge: { id: "c" } });
    expect(changes()).toEqual([[
      { type: "delete", itemId: "c" },
      { type: "create", item: expect.objectContaining({ kind: "connector", from: "a", to: "d", text: "vede", props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "dashed", fromSide: "bottom", toSide: "top" } }) },
    ]]);
  });
});

describe("BoardCanvas: surface label", () => {
  // UX3-1: a given label wins, and the editing wording never reaches a read-only board.
  it("uses the given aria-label, and the read-only wording without edit rights", () => {
    canvas({ canEdit: false, ariaLabel: "Graf vazeb" });
    expect(wrapper!.get("[data-board-surface]").attributes("aria-label")).toBe("Graf vazeb");
    wrapper!.unmount();
    canvas({ canEdit: false });
    expect(wrapper!.get("[data-board-surface]").attributes("aria-label")).toContain("jen ke čtení");
  });
});

describe("BoardCanvas: editing", () => {
  it("edits a note's text in place on a double-click and saves it clean as one change", async () => {
    canvas();
    flow().vm.$emit("nodeDoubleClick", { node: { id: "a" } });
    await nextTick();
    const field = node("a").get("[data-editor-stub]");
    expect(field.attributes("readonly")).toBeUndefined();
    await field.setValue("Nový **text**\t!");
    expect(changes()).toEqual([[{ type: "set", itemId: "a", text: "Nový **text**    !" }]]);
    // The editing ended in the same editor, read-only again (one editor, no swap).
    await nextTick();
    expect(node("a").get("[data-editor-stub]").element).toBe(field.element);
    expect(field.attributes("readonly")).toBeDefined();
  });

  // MOT2-1: the editor is a chunk; until it is in, the note keeps showing its text (the stand-in) instead of going blank.
  it("keeps a note's text drawn while its editor chunk is on the way, and hands over once it is in", async () => {
    textEditorReady.value = false;
    canvas();
    flow().vm.$emit("nodeDoubleClick", { node: { id: "a" } });
    await vi.waitFor(() => expect(node("a").find("span[data-board-text]").exists()).toBe(true));
    textEditorReady.value = true;
    await nextTick();
    expect(node("a").find("span[data-board-text]").exists()).toBe(false);
    expect(node("a").find("textarea[data-board-text]").exists()).toBe(true);
    textEditorReady.value = false;
  });

  it("places the chosen tool's item on top of a card instead of only selecting the card", async () => {
    const board = canvas();
    key(board.get("[data-board-surface]").element, { key: "n" });
    flow().vm.$emit("nodeClick", { node: { id: "a" }, event: new MouseEvent("click", { clientX: 300, clientY: 200 }) });
    expect(changes()[0]).toEqual([{ type: "create", item: expect.objectContaining({ kind: "note" }) }]);
  });

  // J2.6: a finger held still on an item opens its actions, as the right button does (iOS sends no menu event of its own).
  it("opens an item's actions on a long press of a finger, not when the finger moves", async () => {
    canvas();
    await vi.waitFor(() => node("a"));
    vi.useFakeTimers();
    try {
      const target = node("a").element, menus = vi.fn();
      target.addEventListener("contextmenu", menus);
      const press = (type: string, x = 10) => target.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerType: "touch", clientX: x, clientY: 10, button: 0 }));
      press("pointerdown");
      vi.advanceTimersByTime(600);
      expect(menus).toHaveBeenCalledOnce();
      press("pointerdown");
      press("pointermove", 40);
      vi.advanceTimersByTime(600);
      expect(menus).toHaveBeenCalledOnce();
    } finally { vi.useRealTimers(); }
  });

  // D12: what is typed after a new note is placed and before its editor has the focus goes into it, never to the tools.
  it("keeps what is typed before a new note's editor has the focus for that editor, without choosing tools", async () => {
    const board = canvas();
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "n" });
    flow().vm.$emit("paneClick", new MouseEvent("click", { clientX: 600, clientY: 400 }));
    await nextTick();
    for (const letter of "hvezda") key(surface, { key: letter });
    expect(board.get('[data-board-tool="select"]').attributes("aria-pressed")).toBe("true");
    // The editor takes the focus: the text goes in through its own input.
    const editor = document.createElement("div");
    editor.setAttribute("contenteditable", "true");
    const insertText = vi.fn(() => ({}));
    const view = { isDestroyed: false, state: { selection: { from: 1, to: 1 }, tr: { insertText } }, someProp: vi.fn(() => true), dispatch: vi.fn() };
    Object.assign(editor, { editor: { view } });
    const created = (changes()[0]![0] as { item: BoardViewItem }).item;
    await board.setProps({ items: [...twoNotes(), created] });
    await vi.waitFor(() => node(created.id));
    node(created.id).element.appendChild(editor);
    editor.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    await new Promise(resolve => setTimeout(resolve));
    expect(view.someProp).toHaveBeenCalledWith("handleTextInput", expect.any(Function));
  });

  it("saves what was typed for a new note whose editor never took the keys when Esc ends the editing first", async () => {
    const board = canvas();
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "n" });
    flow().vm.$emit("paneClick", new MouseEvent("click", { clientX: 600, clientY: 400 }));
    const created = (changes()[0]![0] as { item: BoardViewItem }).item;
    await board.setProps({ items: [...twoNotes(), created] });
    for (const letter of "R4") key(surface, { key: letter });
    key(surface, { key: "Escape" });
    expect(changes().at(-1)).toEqual([{ type: "set", itemId: created.id, text: "R4" }]);
  });

  // U3-1: the text being edited reaches the board while the editing goes on, and the board knows while it holds unsaved text.
  it("saves a text while it is edited, keeps the editing, and says while an editor holds unsaved text", async () => {
    canvas();
    flow().vm.$emit("nodeDoubleClick", { node: { id: "a" } });
    await nextTick();
    const editor = wrapper!.findComponent(EditorStub);
    editor.vm.$emit("unsaved", true);
    expect(wrapper!.emitted("unsaved")).toEqual([[true]]);
    (editor.props("binding") as TextBinding).save!("Rozepsaný text");
    editor.vm.$emit("unsaved", false);
    await nextTick();
    expect(changes()).toEqual([[{ type: "set", itemId: "a", text: "Rozepsaný text" }]]);
    expect(wrapper!.emitted("unsaved")).toEqual([[true], [false]]);
    expect(node("a").find("[data-editor-stub]").exists()).toBe(true);
  });

  it("puts a note where the board was double-clicked, edited at once", async () => {
    const board = canvas({ items: [] });
    board.get(".vue-flow__pane").element.dispatchEvent(new MouseEvent("dblclick", { bubbles: true, clientX: 300, clientY: 300 }));
    expect(changes()[0]).toEqual([{ type: "create", item: expect.objectContaining({ kind: "note", x: 200, y: 200, width: 200, height: 200 }) }]);
  });

  it("acts from the context toolbar: order, a connector's quick styles, delete; the next connector takes the style", async () => {
    const board = canvas({ items: [...twoNotes(), link("c", "a", "b"), link("d", "b", "a")] });
    await select("a");
    await board.get("[data-board-front]").trigger("click");
    expect(changes().at(-1)).toEqual([{ type: "reorder", itemId: "a", z: 3 }]);
    flow().vm.$emit("nodesChange", [{ id: "a", type: "select", selected: false }]);
    // Both connectors at once: the quick styles apply to each.
    flow().vm.$emit("edgesChange", [{ id: "c", type: "select", selected: true }, { id: "d", type: "select", selected: true }]);
    await nextTick();
    await board.get("[data-board-dash]").trigger("click");
    expect(changes().at(-1)).toEqual([{ type: "set", itemId: "c", props: { dash: "dashed" } }, { type: "set", itemId: "d", props: { dash: "dashed" } }]);
    await board.get("[data-board-animated]").trigger("click");
    expect(changes().at(-1)).toEqual([{ type: "set", itemId: "c", props: { animated: true } }, { type: "set", itemId: "d", props: { animated: true } }]);
    flow().vm.$emit("edgesChange", [{ id: "d", type: "select", selected: false }]);
    await nextTick();
    await board.get("[data-board-delete]").trigger("click");
    expect(changes().at(-1)).toEqual([{ type: "delete", itemId: "c" }]);
    flow().vm.$emit("connectStart", { nodeId: "a", handleId: "right", handleType: "source" });
    flow().vm.$emit("connect", { source: "a", target: "b", sourceHandle: "right", targetHandle: "left" });
    expect(changes().at(-1)).toEqual([expect.objectContaining({ type: "create", item: expect.objectContaining({ props: expect.objectContaining({ dash: "dashed", animated: true }) }) })]);
  });

  it("selects a frame by a click into its empty inside, its members by Ctrl+A, and goes back up with Esc", async () => {
    const frame: BoardViewItem = { id: "f", kind: "frame", x: -100, y: -100, width: 900, height: 600, z: 0, text: "Rám" };
    const board = canvas({ items: [frame, note("a", { parent: "f" }), note("b", { x: 400, z: 2, parent: "f" }), note("out", { x: 2000 })] });
    flow().vm.$emit("paneClick", new MouseEvent("click", { clientX: 300, clientY: 450 }));
    await nextTick();
    expect(selectedIds()).toEqual(["f"]);
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "a", ctrlKey: true });
    await nextTick();
    expect(selectedIds().sort()).toEqual(["a", "b"]);
    key(surface, { key: "Escape" });
    await nextTick();
    expect(selectedIds()).toEqual(["f"]);
    key(surface, { key: "Escape" });
    await nextTick();
    expect(selectedIds()).toEqual([]);
    // Outside every frame a click selects nothing.
    flow().vm.$emit("paneClick", new MouseEvent("click", { clientX: 1100, clientY: 750 }));
    await nextTick();
    expect(selectedIds()).toEqual([]);
  });

  it("draws the context toolbar as the page editor's, whose arrows walk its buttons instead of moving the selection", async () => {
    const board = canvas();
    await select("a");
    const toolbar = board.get("[data-board-context-toolbar]");
    expect(toolbar.classes()).toContain("md-context-toolbar");
    const buttons = toolbar.findAll<HTMLButtonElement>("button.bb");
    expect(buttons.length).toBeGreaterThan(3);
    buttons[0]!.element.focus();
    const before = changes().length;
    await buttons[0]!.trigger("keydown", { key: "ArrowRight" });
    expect(document.activeElement).toBe(buttons[1]!.element);
    await buttons[1]!.trigger("keydown", { key: "End" });
    expect(document.activeElement).toBe(buttons.at(-1)!.element);
    await buttons.at(-1)!.trigger("keydown", { key: "ArrowRight" });
    expect(document.activeElement).toBe(buttons[0]!.element);
    expect(changes()).toHaveLength(before);
  });

  it("asks for undo and redo from the keyboard, but leaves a text field its own keys", async () => {
    const board = canvas();
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "z", ctrlKey: true });
    key(surface, { key: "z", ctrlKey: true, shiftKey: true });
    expect(board.emitted("undo")).toHaveLength(1);
    expect(board.emitted("redo")).toHaveLength(1);
    const field = document.createElement("input");
    surface.append(field);
    key(field, { key: "z", ctrlKey: true });
    key(field, { key: "Delete" });
    expect(board.emitted("undo")).toHaveLength(1);
    expect(board.emitted("change")).toBeUndefined();
  });
});

describe("BoardCanvas: components", () => {
  const offered = async (props: Record<string, unknown>) => {
    const board = canvas(props);
    key(board.get("[data-board-surface]").element, { key: "k" });
    // The panel and the editor's registry of blocks are chunks that load on the first K.
    const ids = () => (board.findComponent(BoardCommandPanel).props("commands") as readonly { id: string }[]).map(entry => entry.id);
    await vi.waitFor(() => expect(ids().some(id => id.startsWith("component:"))).toBe(true), { timeout: 10000 });
    return ids();
  };
  it("offers the board's own contact and issue pickers instead of the plugins' entries for the same, never both", async () => {
    const ids = await offered({ pickIssues: async () => null, pickContacts: async () => null });
    expect(ids).toEqual(expect.arrayContaining(["pick:youtrack", "pick:contact", "component:charts.insert", "component:callout"]));
    expect(ids).not.toContain("component:youtrack.search");
    expect(ids).not.toContain("component:contacts.insert");
    wrapper!.unmount();
    wrapper = undefined;
    expect(await offered({})).toEqual(expect.arrayContaining(["component:youtrack.search", "component:contacts.insert"]));
  });

  // A catalog pick is requirement headings with their lines, which a board would split into items: no „Moduly produktu“ on a board
  // until the decision on requirements on boards; the plain „Požadavek“ stays.
  it("offers no requirement catalog on a board, even with the space's projector host", async () => {
    const projector = { spaceId: () => "s1", pageId: () => undefined, api: {} as never, open: () => {} };
    const ids = await offered({ blockHost: { youTrack: { search: async () => [], resolve: async () => null }, projector } });
    expect(ids).not.toContain("component:requirements.catalog");
  });

  // P15: the lists derived from the relations are components of a board that has a space; „Kde se používá“ asks at once what to follow.
  it("inserts a list derived from the relations from the components, and opens what it follows by „Otevřít“", async () => {
    const links = { spaceId: () => "s1", api: { relations: { contents: async () => ({ items: [], summary: { types: {}, relations: { unused: 0, noIncoming: 0, broken: 0, attached: 0 } }, nextCursor: null }), relations: async () => null },
      usage: { usage: async () => null, count: async () => null }, waiting: { waiting: async () => ({ parties: [] }), contact: async () => undefined } } };
    const ids = await offered({ blockHost: { youTrack: { search: async () => [], resolve: async () => null }, links } });
    expect(ids).toEqual(expect.arrayContaining(["links:people", "links:usage"]));
    // Without Projector there is nothing to wait for.
    expect(ids).not.toContain("links:waiting");
    wrapper!.findComponent(BoardCommandPanel).vm.$emit("run", "links:usage");
    await nextTick();
    const created = changes().flat().find(change => change.type === "create") as Extract<BoardChange, { type: "create" }>;
    expect(created.item).toMatchObject({ kind: "links", props: { show: "usage" } });
    expect(created.item.text).toBeUndefined();
    wrapper!.unmount();

    liveObjects.seed({ id: "page-9", typeId: "atlas.page", version: 1, title: "Plán", canEdit: true, available: true, data: null });
    const board = canvas({ items: [{ id: "l", kind: "links", x: 0, y: 0, width: 340, height: 320, z: 1, props: { show: "usage", objectId: "page-9" } }], blockHost: { youTrack: { search: async () => [], resolve: async () => null }, links } });
    await select("l");
    key(board.get("[data-board-surface]").element, { key: "Enter", ctrlKey: true });
    expect(board.emitted("openPage")).toEqual([["page-9"]]);
  }, 20_000);

  it("U12: K and the rail's Komponenta open the components as a search beside the rail: only components, the first preselected, Enter inserts", async () => {
    const ids = await offered({});
    expect(ids.length).toBeGreaterThan(3);
    expect(ids.every(id => id.startsWith("component:") || id.startsWith("pick:"))).toBe(true);
    const query = wrapper!.get<HTMLInputElement>("[data-board-command-query]");
    expect(document.activeElement).toBe(query.element);
    // Nothing is highlighted before something is typed (Enter would take none); the typed text preselects the first match (P8).
    expect(query.attributes("aria-activedescendant")).toBeUndefined();
    await query.setValue("informační");
    expect(query.attributes("aria-activedescendant")).toBeTruthy();
    await query.trigger("keydown", { key: "Enter" });
    await vi.waitFor(() => expect(changes().flat().some(change => change.type === "create" && change.item.kind === "block")).toBe(true));
    expect(wrapper!.findComponent(BoardCommandPanel).exists()).toBe(false);
    wrapper!.findComponent(BoardToolRail).vm.$emit("components");
    await nextTick();
    expect(wrapper!.findComponent(BoardCommandPanel).props("commands").every((entry: { section: string }) => entry.section === "component")).toBe(true);
  });

  it("puts a component inserted from the menu where it hides nothing, as a pasted or picked one", async () => {
    await offered({});
    const run = () => wrapper!.findComponent(BoardCommandPanel).vm.$emit("run", "component:callout");
    run();
    await vi.waitFor(() => expect(changes()).toHaveLength(1));
    await wrapper!.setProps({ items: changes().flatMap(batch => batch.flatMap(change => (change.type === "create" ? [change.item as BoardViewItem] : []))) });
    key(wrapper!.get("[data-board-surface]").element, { key: "k" });
    await nextTick();
    run();
    await vi.waitFor(() => expect(changes()).toHaveLength(2));
    const placed = changes().flatMap(batch => batch.flatMap(change => (change.type === "create" ? [[change.item.x, change.item.y]] : [])));
    expect(placed).toHaveLength(2);
    expect(placed[1]).not.toEqual(placed[0]);
  });
});

describe("BoardCanvas: clipboard and drop", () => {
  it("copies the selection for boards and as text, and pastes it as new items", async () => {
    const board = canvas();
    await select("a");
    const data: Record<string, string> = {};
    board.get("[data-board-surface]").element.dispatchEvent(clipboardEvent("copy", data));
    expect(data["text/plain"]).toBe("a");
    board.get("[data-board-surface]").element.dispatchEvent(clipboardEvent("paste", data));
    await flushPromises();
    expect(changes()[0]).toEqual([{ type: "create", item: expect.objectContaining({ kind: "note", text: "a", z: 3 }) }]);
    expect((changes()[0]![0] as { item: { id: string } }).item.id).not.toBe("a");
  });

  it("pastes text from other applications as notes, a line each", async () => {
    const board = canvas({ items: [], pasteContext: { origin: "https://atlas.example" } });
    board.get("[data-board-surface]").element.dispatchEvent(clipboardEvent("paste", { "text/plain": "První nápad\nDruhý nápad" }));
    await flushPromises();
    expect(changes()[0]!.map(change => change.type === "create" && [change.item.kind, change.item.text])).toEqual([["note", "První nápad"], ["note", "Druhý nápad"]]);
  });

  it("places a page dropped from the tree where it was dropped", async () => {
    const board = canvas({ items: [], pasteContext: { origin: "https://atlas.example" } });
    const id = "6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b";
    const drop = new Event("drop", { bubbles: true, cancelable: true });
    Object.defineProperties(drop, { clientX: { value: 500 }, clientY: { value: 400 }, dataTransfer: { value: { types: ["application/x-atlas-page"], getData: (type: string) => (type === "application/x-atlas-page" ? id : ""), dropEffect: "none" } } });
    board.get("[data-board-surface]").element.dispatchEvent(drop);
    await flushPromises();
    expect(changes()[0]).toEqual([{ type: "create", item: expect.objectContaining({ kind: "page", targetResourceId: id, x: 360, y: 330, props: { display: "card" } }) }]);
  });

  it("reads its own clipboard format only from its own payload", () => {
    expect(copyPayload([note("a")], ["a"])!.json).toContain("atlas.board");
    expect(boardClipboardType).toBe("application/x-atlas-board+json");
  });
});

describe("BoardCanvas: motion (spec §5)", () => {
  const motionOf = (id: string) => node(id).get(".board-node").attributes("data-motion");

  it("lets a new item enter and a deleted one fade out, without holding up the items", async () => {
    const board = canvas();
    await nextTick();
    expect(motionOf("a")).toBeUndefined();
    await board.setProps({ items: [...twoNotes(), note("n", { x: 800, z: 3 })] });
    expect(motionOf("n")).toBe("enter");
    await board.setProps({ items: [note("a")] });
    await flushPromises();
    await nextTick();
    expect(node("b").get(".board-node").classes()).toContain("is-leaving");
    expect(motionOf("b")).toBe("leave");
    await new Promise(resolve => setTimeout(resolve, 400));
    await nextTick();
    expect(board.find('.vue-flow__node[data-id="b"]').exists()).toBe(false);
  });

  it("glides an item someone else moved, fades it under reduced motion, and never animates my own drag", async () => {
    const board = canvas();
    await board.setProps({ items: [note("a", { x: 50 }), note("b", { x: 400, z: 2 })] });
    expect(motionOf("a")).toBe("glide");
    board.get("[data-board-canvas]").element.setAttribute("data-atlas-reduced-motion", "");
    await board.setProps({ items: [note("a", { x: 90 }), note("b", { x: 400, z: 2 })] });
    expect(motionOf("a")).toBe("fade");
    board.get("[data-board-canvas]").element.removeAttribute("data-atlas-reduced-motion");
    await drag("b", ["b"], { x: 450, y: 0 });
    await stop();
    await board.setProps({ items: [note("a", { x: 90 }), note("b", { x: 450, z: 2 })] });
    expect(motionOf("b")).toBeUndefined();
  });

  it("glides a colleague's move from where the card stood to its new place, not from the old place to the old place (COL2-2)", async () => {
    const played: { el: Element; frames: Keyframe[] }[] = [];
    Object.defineProperty(Element.prototype, "animate", {
      configurable: true,
      value(this: Element, frames: Keyframe[]) { played.push({ el: this, frames }); return { cancel() {}, finished: Promise.resolve(), playState: "running" }; },
    });
    try {
      const board = canvas();
      await nextTick();
      await board.setProps({ items: [note("a", { x: 240, y: 30 }), note("b", { x: 400, z: 2 })] });
      await flushPromises();
      const wrapperOf = node("a").element;
      const glide = played.find(entry => entry.el === wrapperOf);
      expect(glide?.frames.map(frame => frame.transform)).toEqual(["translate(0px,0px)", "translate(240px,30px)"]);
    } finally {
      delete (Element.prototype as Partial<Element>).animate;
    }
  });

  it("hands Vue Flow only the node a colleague's batch changed, the others untouched (COL2-1)", async () => {
    const items = [note("a"), note("b", { x: 400, z: 2 }), note("c", { x: 800, z: 3 })];
    const board = canvas({ items });
    await nextTick();
    const graph = store().nodes.value, bBefore = toRaw(store().findNode("b")!.position);
    // The same objects for b and c, as the board session hands them on.
    const moved = [{ ...items[0]!, x: 120 }, items[1]!, items[2]!];
    await board.setProps({ items: moved });
    await flushPromises();
    expect(store().nodes.value).toBe(graph);
    expect(store().findNode("a")!.position).toEqual({ x: 120, y: 0 });
    expect(toRaw(store().findNode("b")!.position)).toBe(bBefore);
    // A new item is added to them, the others not taken again.
    await board.setProps({ items: [...moved, note("d", { x: 1200, z: 4 })] });
    await flushPromises();
    expect(store().nodes.value.map(entry => entry.id)).toEqual(["a", "b", "c", "d"]);
    expect(store().nodes.value).toBe(graph);
  });
});

describe("BoardCanvas: navigation and read-only", () => {
  // T9: d3 interpolates a travel over the pane's own extent; a pane without a size (not laid out, hidden; jsdom) got a NaN view
  // whenever a frame of the travel ran, so every node vanished (the flaky motion, group and neighbour tests).
  it("changes the view at once instead of travelling when the pane has no size of its own", async () => {
    const board = canvas();
    // As a press of the zoom button does: the person touched the board, so it is no longer fitted as it opens.
    board.get("[data-board-surface]").element.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    (board.vm as unknown as { zoomTo(zoom: number): void }).zoomTo(2);
    await new Promise(resolve => setTimeout(resolve, 60));
    expect(store().viewport.value.zoom).toBe(2);
    expect(board.findAll(".vue-flow__node")).toHaveLength(2);
  });

  it("pans with the wheel and zooms at the cursor with Ctrl and the wheel", async () => {
    const board = canvas();
    const surface = board.get("[data-board-surface]").element;
    const transform = () => (board.get(".vue-flow__transformationpane").element as HTMLElement).style.transform;
    surface.dispatchEvent(new WheelEvent("wheel", { bubbles: true, cancelable: true, deltaX: 10, deltaY: 30 }));
    await nextTick();
    expect(transform()).toContain("translate(-10px, -30px) scale(1)");
    surface.dispatchEvent(new WheelEvent("wheel", { bubbles: true, cancelable: true, deltaY: -100, ctrlKey: true, clientX: 0, clientY: 0 }));
    await nextTick();
    expect(transform()).toMatch(/scale\(1\.14/);
    expect(board.get("[data-board-zoom]").text()).toBe("115 %");
  });

  it("fits a board whose pane gets its size after opening, also when resized, until the person first touches it", async () => {
    const observed: [Element, ResizeObserverCallback][] = [];
    vi.stubGlobal("ResizeObserver", class {
      constructor(private callback: ResizeObserverCallback) {}
      observe(target: Element) { observed.push([target, this.callback]); }
      unobserve() {}
      disconnect() {}
    });
    const board = canvas({ items: [note("a", { x: 1000, y: 1000 })] });
    const surface = board.get("[data-board-surface]").element;
    const transform = () => (board.get(".vue-flow__transformationpane").element as HTMLElement).style.transform;
    async function resize(width: number, height: number) {
      vi.spyOn(surface, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, width, height));
      for (const [target, callback] of observed) if (target === surface) callback([{ contentRect: { width, height } } as ResizeObserverEntry], {} as ResizeObserver);
      await flushPromises();
    }
    await resize(1200, 800);
    expect(transform()).toContain("translate(-500px, -700px) scale(1)");
    await resize(1000, 600);
    expect(transform()).toContain("translate(-600px, -800px) scale(1)");
    surface.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    await resize(1200, 800);
    expect(transform()).toContain("translate(-600px, -800px) scale(1)");
    vi.unstubAllGlobals();
  });

  // U2-10: on a phone the rail covered a quarter of the width and most of the height, and fitting put notes under it.
  it("lays the rail out as a bar along the bottom of a narrow board and fits the content above it", async () => {
    const observed: [Element, ResizeObserverCallback][] = [];
    vi.stubGlobal("ResizeObserver", class {
      constructor(private callback: ResizeObserverCallback) {}
      observe(target: Element) { observed.push([target, this.callback]); }
      unobserve() {}
      disconnect() {}
    });
    const board = canvas({ items: [note("a", { x: 1000, y: 1000 })] });
    const surface = board.get("[data-board-surface]").element;
    vi.spyOn(surface, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 375, 700));
    for (const [target, callback] of observed) if (target === surface) callback([{ contentRect: { width: 375, height: 700 } } as ResizeObserverEntry], {} as ResizeObserver);
    await flushPromises();
    expect(board.get("[data-board-rail]").attributes("aria-orientation")).toBe("horizontal");
    expect((board.get(".vue-flow__transformationpane").element as HTMLElement).style.transform).toContain("translate(-912.5px, -782px) scale(1)");
    vi.unstubAllGlobals();
  });

  it("keeps a read-only board explorable and selectable, but offers no editing and sends nothing", async () => {
    const board = canvas({ canEdit: false });
    expect(board.findAll("[data-board-tool]").map(tool => tool.attributes("data-board-tool"))).toEqual(["select", "hand"]);
    expect(board.findAll(".vue-flow__handle")).toHaveLength(0);
    await select("a");
    expect(board.find("[data-board-delete]").exists()).toBe(false);
    key(board.get("[data-board-surface]").element, { key: "Delete" });
    flow().vm.$emit("nodeDoubleClick", { node: { id: "a" } });
    await nextTick();
    // A reader's texts are the editor read-only; nothing makes one writable.
    expect(board.findAll("[data-editor-stub]").every(editor => editor.attributes("readonly") !== undefined)).toBe(true);
    expect(board.emitted("change")).toBeUndefined();
  });

  it("keeps the reader's selection when the items change but the preselected card is still there (a group opened)", async () => {
    const board = canvas({ canEdit: false, items: [note("a"), note("b", { x: 400 })], preselect: "a" });
    await flushPromises();
    key(board.get("[data-board-surface]").element, { key: "ArrowRight" });
    await flushPromises();
    expect(selectedIds()).toEqual(["b"]);
    await board.setProps({ items: [note("a"), note("b", { x: 400 }), note("c", { y: 400 })] });
    await flushPromises();
    expect(selectedIds()).toEqual(["b"]);
  });

  it("walks a read-only board with the arrows to the nearest card in that direction, reports the selection and takes a preselected card", async () => {
    const board = canvas({ canEdit: false, items: [note("a"), note("b", { x: 400 }), note("c", { y: 400 })], preselect: "a" });
    await flushPromises();
    expect(selectedIds()).toEqual(["a"]);
    expect(board.emitted("selection")?.at(-1)).toEqual([["a"]]);
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "ArrowRight" });
    await flushPromises();
    expect(selectedIds()).toEqual(["b"]);
    // Nothing is right of b: the selection stays (a nudge would have moved the card).
    key(surface, { key: "ArrowRight" });
    await flushPromises();
    expect(selectedIds()).toEqual(["b"]);
    key(surface, { key: "ArrowDown" });
    await flushPromises();
    expect(selectedIds()).toEqual(["c"]);
    key(surface, { key: "ArrowUp" });
    await flushPromises();
    expect(selectedIds()).toEqual(["a"]);
    expect(board.emitted("change")).toBeUndefined();
  });

  it("shows other people's pointers and drags in the server's colour, the one of their caret in a page", () => {
    const board = canvas({ remote: [{ userId: "u2", displayName: "Jana", color: "#2a9d8f", cursor: { x: 10, y: 20 }, dragging: [{ itemId: "a", x: 5, y: 6, width: 200, height: 200 }] }] });
    expect(board.get("[data-board-cursor]").text()).toBe("Jana");
    expect(board.get("[data-board-cursor]").attributes("style")).toContain("--collaboration-color: #2a9d8f");
    expect(board.findAll("[data-board-ghost]")).toHaveLength(1);
  });
});

describe("BoardCanvas: groups (objects spec §5.4)", () => {
  it("groups the selection with Ctrl+G and dissolves the group with Ctrl+Shift+G, one batch each", async () => {
    const board = canvas();
    await select("a", "b");
    key(board.get("[data-board-surface]").element, { key: "g", ctrlKey: true });
    await nextTick();
    const [grouping] = changes();
    const created = grouping![0] as Extract<BoardChange, { type: "create" }>;
    expect(created.item).toMatchObject({ kind: "group", x: -16, y: -16, width: 632, height: 232 });
    expect(grouping!.slice(1)).toEqual([{ type: "parent", itemId: "a", parentId: created.item.id }, { type: "parent", itemId: "b", parentId: created.item.id }]);

    await wrapper!.setProps({ items: [{ ...created.item }, note("a", { parent: created.item.id }), note("b", { x: 400, z: 2, parent: created.item.id })] });
    // What happened shows: the outline draws in around the members, which gather (boardMotion.planGroupMotion).
    const motionOf = (id: string) => node(id).get(".board-node").attributes("data-motion");
    expect([motionOf(created.item.id), motionOf("a"), motionOf("b")]).toEqual(["enter", "gather", "gather"]);
    await select(created.item.id);
    key(board.get("[data-board-surface]").element, { key: "G", ctrlKey: true, shiftKey: true });
    await nextTick();
    expect(changes()[1]).toEqual([{ type: "parent", itemId: "a" }, { type: "parent", itemId: "b" }, { type: "delete", itemId: created.item.id }]);
    await wrapper!.setProps({ items: [note("a"), note("b", { x: 400, z: 2 })] });
    expect([motionOf(created.item.id), motionOf("a"), motionOf("b")]).toEqual(["leave", "freed", "freed"]);
  });

  it("lets a dragged item join the frame it is dropped into", async () => {
    canvas({ items: [{ id: "f", kind: "frame", x: 600, y: 0, width: 600, height: 400, z: 0 }, note("a")] });
    await drag("a", ["a"], { x: 700, y: 50 });
    await stop();
    expect(changes()).toEqual([[{ type: "move", itemId: "a", x: 700, y: 50, width: 200, height: 200 }, { type: "parent", itemId: "a", parentId: "f" }]]);
  });

  it("draws a collapsed group as its card with the count, and expands it from the card", async () => {
    const board = canvas({ items: [{ id: "g", kind: "group", x: -16, y: -16, width: 632, height: 232, z: 0, text: "Tým", props: { collapsed: true } },
      note("a", { parent: "g" }), note("b", { x: 400, parent: "g" })] });
    await flushPromises();
    expect(board.get("[data-board-group-count]").text()).toBe("2 prvky");
    expect(node("a").classes()).toContain("is-collapsed-away");
    await board.get("[data-board-group-expand]").trigger("click");
    expect(changes()).toEqual([[{ type: "set", itemId: "g", props: { collapsed: null } }]]);
  });
});

describe("BoardCanvas: command panel (objects spec §5.2)", () => {
  it("opens with / over the board, finds a command as it is typed and runs it at the pointer", async () => {
    const board = canvas();
    board.get("[data-board-surface]").element.dispatchEvent(new PointerEvent("pointermove", { bubbles: true, clientX: 300, clientY: 500 }));
    key(board.get("[data-board-surface]").element, { key: "/" });
    await flushPromises();
    const query = board.get<HTMLInputElement>("[data-board-command-query]");
    expect(document.activeElement).toBe(query.element);
    await query.setValue("rám");
    await query.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(board.find("[data-board-command-panel]").exists()).toBe(false);
    expect(changes()[0]![0]).toMatchObject({ type: "create", item: { kind: "frame", x: 300 - 320, y: 500 - 200 } });
  });

  it("opens on a short tap of Space over empty board, and Ctrl+K without a selection stays the application's", async () => {
    const board = canvas();
    const surface = board.get("[data-board-surface]").element;
    key(surface, { key: "k", ctrlKey: true });
    await flushPromises();
    expect(board.find("[data-board-command-panel]").exists()).toBe(false);
    key(surface, { key: " " });
    surface.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true, key: " " }));
    await flushPromises();
    expect(board.find("[data-board-command-panel]").exists()).toBe(true);
  });

  it("leaves Ctrl+K to the application's search over a selection too (3.6 review F8)", async () => {
    const board = canvas();
    await select("a", "b");
    const ctrlK = new KeyboardEvent("keydown", { bubbles: true, cancelable: true, key: "k", ctrlKey: true });
    board.get("[data-board-surface]").element.dispatchEvent(ctrlK);
    await flushPromises();
    expect(board.find("[data-board-command-panel]").exists()).toBe(false);
    expect(ctrlK.defaultPrevented).toBe(false);
  });

  it("finds an existing connector by name and selects it with the keyboard (review 3.5.1, finding 9)", async () => {
    const board = canvas({ items: [...twoNotes(), link("ab", "a", "b", { text: "nasazuje" })] });
    key(board.get("[data-board-surface]").element, { key: "/" });
    await flushPromises();
    const query = board.get<HTMLInputElement>("[data-board-command-query]");
    await query.setValue("popisek nasazuje");
    await query.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(wrapper!.findAll(".vue-flow__edge.selected").map(edge => edge.attributes("data-id"))).toEqual(["ab"]);
    await new Promise(resolve => requestAnimationFrame(resolve));
    expect(document.activeElement).toBe(wrapper!.get('.vue-flow__edge[data-id="ab"]').element);
  });

  it("opens over a selection with /, its actions first", async () => {
    const board = canvas();
    await select("a", "b");
    key(board.get("[data-board-surface]").element, { key: "/" });
    await flushPromises();
    expect(document.querySelector("[data-testid=board-command-list] [role=option]")?.textContent).toBe("SeskupitCtrl+G");
  });
});

describe("BoardCanvas: neighbours and detail (objects spec §5.3, §6)", () => {
  it("marks the hovered node's relations and dims the rest, and nothing for a node without any", async () => {
    canvas({ items: [note("a"), note("b", { x: 250 }), note("c", { x: 500 }), note("lone", { x: 750 }), link("ab", "a", "b"), link("bc", "b", "c")] });
    await flushPromises();
    flow().vm.$emit("nodeMouseEnter", { node: { id: "b" } });
    await nextTick();
    // A pointer passing over a card highlights nothing; one resting on it does.
    expect(document.querySelector(".board-canvas")?.hasAttribute("data-highlighting")).toBe(false);
    await new Promise(resolve => setTimeout(resolve, 150));
    await nextTick();
    const marks = () => ["a", "b", "c", "lone"].map(id => node(id).get(".board-node").attributes("data-highlight"));
    expect(marks()).toEqual(["up", "self", "down", undefined]);
    // The rest dims by one mark on the canvas, not one on each of its items.
    expect(document.querySelector(".board-canvas")?.hasAttribute("data-highlighting")).toBe(true);
    flow().vm.$emit("nodeMouseLeave", { node: { id: "b" } });
    flow().vm.$emit("nodeMouseEnter", { node: { id: "lone" } });
    await nextTick();
    expect(marks()).toEqual([undefined, undefined, undefined, undefined]);
    await new Promise(resolve => setTimeout(resolve, 150));
    await nextTick();
    expect(marks()).toEqual([undefined, undefined, undefined, undefined]);
  });

  // J1.3, J4.2, K13: on a board read only (a reader, the lock) a card's edit gestures read it: Enter and a double-click open its page.
  it("opens a page card for a reader on Enter and a double-click, and never its page over the board for editing", async () => {
    canvas({ canEdit: false, items: [{ id: "p", kind: "page", x: 0, y: 0, width: 280, height: 140, z: 1, targetResourceId: "page-1", target: { title: "Příručka", available: true, canEdit: true } }] });
    flow().vm.$emit("nodeDoubleClick", { node: { id: "p" } });
    await select("p");
    key(node("p").element, { key: "Enter" });
    key(node("p").element, { key: "F2" });
    expect(editedPage.value).toBeNull();
    expect(wrapper!.emitted("openPage")).toEqual([["page-1", undefined], ["page-1", undefined]]);
  });

  it("edits a page card's page on a double-click and F2 (D1); its detail and the page open from the menu and Ctrl+Enter, never Enter", async () => {
    canvas({ items: [{ id: "p", kind: "page", x: 0, y: 0, width: 280, height: 140, z: 1, targetResourceId: "page-1", target: { title: "Příručka", available: true, canEdit: true } }] });
    flow().vm.$emit("nodeDoubleClick", { node: { id: "p" } });
    expect(editedPage.value).toEqual({ pageId: "page-1", title: "Příručka" });
    editedPage.value = null;
    await select("p");
    key(node("p").element, { key: "F2" });
    expect(editedPage.value).toMatchObject({ pageId: "page-1" });
    editedPage.value = null;
    key(node("p").element, { key: "Enter", ctrlKey: true });
    expect(wrapper!.emitted("openPage")).toEqual([["page-1", undefined]]);
    await loadActionsMenu();
    key(node("p").element, { key: " ", code: "Space", ctrlKey: true });
    expect(menuRows()).toEqual(["Upravit", "Otevřít stránku", "Otevřít detail", "Kopírovat odkaz", "Duplikovat", "Odebrat z tabule"]);
    for (const letter of "detail") key(node("p").element, { key: letter });
    key(node("p").element, { key: "Enter" });
    expect(wrapper!.emitted("openDetail")).toEqual([["p"]]);
    expect(editedPage.value).toBeNull();
  });

  // 3.6 (owner, 2026-11-06): a page's preview has no „Upravit“; its text is edited in place, as a note's, never in the panel over the board.
  it("edits a page preview in place: the keys wait for its editor, Esc gives the entering up", async () => {
    canvas({ items: [{ id: "p", kind: "page", x: 0, y: 0, width: 320, height: 220, z: 1, targetResourceId: "page-1", props: { display: "preview" },
      target: { title: "Příručka", available: true, canEdit: true } }] });
    await select("p");
    flow().vm.$emit("nodeDoubleClick", { node: { id: "p" } });
    expect(editedPage.value).toBeNull();
    // Until the page's editor has the keys they are not the board's: Delete would remove the card being entered.
    key(node("p").element, { key: "Delete" });
    expect(wrapper!.emitted("change")).toBeUndefined();
    key(node("p").element, { key: "Escape" });
    key(node("p").element, { key: "Delete" });
    expect(changes()).toHaveLength(1);
  });

  // The letters typed while the page's live editor still loads go in at its caret once it takes the focus; an Esc before then drops them.
  it("keeps the letters typed while a page preview's editor loads for that editor, and drops them with an Esc before it came", async () => {
    canvas({ items: [{ id: "p", kind: "page", x: 0, y: 0, width: 320, height: 220, z: 1, targetResourceId: "page-1", props: { display: "preview" },
      target: { title: "Příručka", available: true, canEdit: true } }] });
    await select("p");
    flow().vm.$emit("nodeDoubleClick", { node: { id: "p" } });
    for (const letter of "Ahoj") key(node("p").element, { key: letter });
    const editor = document.createElement("div");
    editor.setAttribute("contenteditable", "true");
    const insertText = vi.fn(() => ({}));
    const view = { isDestroyed: false, state: { selection: { from: 3, to: 3 }, tr: { insertText } }, someProp: vi.fn(() => false), dispatch: vi.fn() };
    Object.assign(editor, { editor: { view } });
    node("p").element.appendChild(editor);
    editor.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
    await new Promise(resolve => setTimeout(resolve));
    expect(insertText).toHaveBeenCalledWith("Ahoj", 3, 3);
    expect(view.dispatch).toHaveBeenCalledOnce();
    editor.remove();

    flow().vm.$emit("nodeDoubleClick", { node: { id: "p" } });
    for (const letter of "xy") key(node("p").element, { key: letter });
    key(node("p").element, { key: "Escape" });
    // No text of the card's own: the board changed nothing.
    expect(wrapper!.emitted("change")).toBeUndefined();
  });
});

describe("BoardCanvas: „Co s tím jde dělat“ (P2)", () => {
  it("opens the editor's menu on the right button over an item, selects it, runs the first row with Enter and closes on Esc", async () => {
    await loadActionsMenu();
    const board = canvas();
    await flushPromises();
    node("a").element.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2 }));
    await nextTick();
    expect(selectedIds()).toEqual(["a"]);
    expect(document.querySelector("[data-testid=intentions]")?.getAttribute("aria-label")).toBe("Co s tím jde dělat");
    expect(menuRows()).toEqual(["Upravit", "Duplikovat", "Smazat"]);
    expect(document.querySelector("[data-testid=intentions] [aria-selected=true] .t")?.textContent).toBe("Upravit");
    // Esc closes the menu first; the selection stays.
    key(node("a").element, { key: "Escape" });
    expect(menu()).toBeNull();
    expect(selectedIds()).toEqual(["a"]);
    key(node("a").element, { key: "Enter", altKey: true });
    expect(menuRows()[0]).toBe("Upravit");
    key(node("a").element, { key: "Enter" });
    await nextTick();
    expect(menu()).toBeNull();
    expect(board.find('[data-id="a"] [data-editor-stub]').exists()).toBe(true);
    // Shift with the right button keeps the browser's menu.
    const shifted = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2, shiftKey: true });
    node("b").element.dispatchEvent(shifted);
    expect(shifted.defaultPrevented).toBe(false);
  });

  it("takes rows a module adds (where AI puts its own while it can answer) and passes them the selection", async () => {
    await loadActionsMenu();
    const run = vi.fn();
    const provider = vi.fn(() => [{ id: "ai.summary", label: "Shrnout", group: "ai", run }]);
    const unregister = registerBoardIntentions(provider);
    try {
      canvas();
      await select("a", "b");
      key(node("a").element, { key: " ", code: "Space", ctrlKey: true });
      expect(provider).toHaveBeenCalledWith(expect.objectContaining({ canEdit: true, items: [expect.objectContaining({ id: "a" }), expect.objectContaining({ id: "b" })] }));
      expect(menuRows()).toEqual(["Seskupit", ...["vlevo", "na střed", "vpravo", "nahoru", "doprostřed", "dolů"].map(how => `Zarovnat ${how}`), "Duplikovat", "Smazat", "Shrnout"]);
      for (const letter of "shr") key(node("a").element, { key: letter });
      key(node("a").element, { key: "Enter" });
      expect(run).toHaveBeenCalledOnce();
      // Alt+Enter puts AI's rows first, as in the page editor.
      key(node("a").element, { key: "Enter", altKey: true });
      expect(menuRows()[0]).toBe("Shrnout");
    } finally { unregister(); }
  });

  it("keeps the browser's menu where the board offers nothing (a reader's note) and on a link inside an item", async () => {
    await loadActionsMenu();
    canvas({ canEdit: false });
    await flushPromises();
    const reader = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2 });
    node("a").element.dispatchEvent(reader);
    expect(reader.defaultPrevented).toBe(false);
    expect(menu()).toBeNull();
    wrapper!.unmount();
    canvas();
    await flushPromises();
    const anchor = node("a").element.appendChild(document.createElement("a"));
    anchor.href = "https://example.com/";
    const onLink = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2 });
    anchor.dispatchEvent(onLink);
    expect(onLink.defaultPrevented).toBe(false);
    expect(menu()).toBeNull();
  });

  it("takes the keys before the host while open, and the menu key's own contextmenu keeps it (one menu)", async () => {
    await loadActionsMenu();
    canvas();
    await select("a");
    const host = vi.fn();
    key(node("a").element, { key: "ContextMenu" });
    document.body.addEventListener("keydown", host, true);
    try {
      for (const letter of "dup") key(node("a").element, { key: letter });
      const open = menu();
      expect(menuRows()).toEqual(["Duplikovat"]);
      // Windows sends the menu key's contextmenu on its release, at the focused element or the surface.
      const release = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 0 });
      wrapper!.get("[data-board-surface]").element.dispatchEvent(release);
      expect(release.defaultPrevented).toBe(true);
      expect(menu()).toBe(open);
      expect(document.querySelectorAll("[data-testid=intentions]")).toHaveLength(1);
      // The relations graph listens in the capture phase for its own Esc: the menu's Esc never reaches it.
      key(node("a").element, { key: "Escape" });
      expect(menu()).toBeNull();
      expect(host).not.toHaveBeenCalled();
    } finally { document.body.removeEventListener("keydown", host, true); }
  });

  it("offers no opening or link for the relations graph's „Nedostupné“, and a module's object opens without a link", async () => {
    await loadActionsMenu();
    const card = (id: string, typeId: string): BoardViewItem => ({ id, kind: "page", x: id === "u" ? 0 : 400, y: 0, width: 248, height: 96, z: 1, targetResourceId: id, target: { typeId, title: id, available: true } });
    canvas({ canEdit: false, items: [card("u", "unavailable"), card("m", "example.module")] });
    await select("u");
    key(node("u").element, { key: " ", code: "Space", ctrlKey: true });
    expect(menuRows()).not.toContain("Kopírovat odkaz");
    expect(menuRows().filter(row => row?.startsWith("Otevřít") && row !== "Otevřít detail")).toEqual([]);
    key(node("u").element, { key: "Escape" });
    flow().vm.$emit("nodesChange", [{ id: "u", type: "select", selected: false }, { id: "m", type: "select", selected: true }]);
    await nextTick();
    key(node("m").element, { key: " ", code: "Space", ctrlKey: true });
    expect(menuRows()).toContain("Otevřít");
    expect(menuRows()).not.toContain("Kopírovat odkaz");
  });

  it("gives „Zpět“ of a toast only to its own gesture: not after an undo; a card is „Odebráno z tabule“ (P6, P12)", async () => {
    canvas({ items: [note("a"), { id: "p", kind: "page", x: 400, y: 0, width: 280, height: 140, z: 2, targetResourceId: "page-1", target: { title: "Příručka", available: true } }] });
    await select("a");
    key(node("a").element, { key: "Delete" });
    expect(toast.success).toHaveBeenLastCalledWith("Prvek smazán", expect.anything());
    const back = (vi.mocked(toast.success).mock.calls.at(-1)![1]!.action as { onClick: () => void }).onClick;
    key(wrapper!.get("[data-board-surface]").element, { key: "z", ctrlKey: true });
    expect(wrapper!.emitted("undo")).toHaveLength(1);
    back();
    expect(wrapper!.emitted("undo")).toHaveLength(1);
    await select("p");
    key(node("p").element, { key: "Delete" });
    expect(toast.success).toHaveBeenLastCalledWith("Odebráno z tabule", expect.anything());
  });

  it("keeps the keys on the board when Vue Flow blurs a connector that Shift+click took out of the selection (P7)", async () => {
    canvas({ items: [...twoNotes(), link("c", "a", "b")] });
    const edge = wrapper!.get<SVGGElement>('.vue-flow__edge[data-id="c"]').element;
    edge.focus();
    expect(document.activeElement).toBe(edge);
    flow().vm.$emit("edgeClick", { edge: { id: "c" }, event: new MouseEvent("click", { shiftKey: true }) });
    edge.blur();
    await new Promise(resolve => requestAnimationFrame(resolve));
    expect(document.activeElement).toBe(wrapper!.get("[data-board-surface]").element);
  });

  it("makes a contact card's field edit one step of the board's undo, never a batch for the server (P6)", async () => {
    canvas({ items: [{ id: "o", kind: "object", x: 0, y: 0, width: 340, height: 120, z: 1, targetResourceId: "c1", props: { typeId: "atlas.contact", objectId: "c1" } }] });
    flow().vm.$emit("nodeDoubleClick", { node: { id: "o" } });
    await vi.waitFor(() => expect(wrapper!.findComponent({ name: "ObjectCard" }).exists()).toBe(true));
    const edit = { objectId: "c1", before: { name: "Jana" }, after: { name: "Jana Nováková" } };
    wrapper!.findComponent({ name: "ObjectCard" }).vm.$emit("edited", edit);
    wrapper!.findComponent({ name: "ObjectCard" }).vm.$emit("edited", { ...edit, before: { name: "Jana Nováková" }, after: { name: "Jana Novák" } });
    const sent = wrapper!.emitted("change")!;
    expect(sent.map(([batch]) => batch)).toEqual([[{ type: "object", itemId: "o", ...edit }], [{ type: "object", itemId: "o", objectId: "c1", before: { name: "Jana Nováková" }, after: { name: "Jana Novák" } }]]);
    // One editing, one step.
    expect(new Set(sent.map(([, coalesce]) => coalesce)).size).toBe(1);
    expect(sent[0]![1]).toBeTruthy();
  });
});

describe("BoardCanvas: large boards", () => {
  /** A row of `count` notes, each above the one before it. */
  const row = (count: number) => Array.from({ length: count }, (_, index) => note(`n${index}`, { x: index * 300, z: index + 1 }));

  it("opens a large board slice by slice, adding each slice's nodes and connectors to the ones before, connectors in their order", async () => {
    // The first connector joins the last cards, which come in the last slice: it still lies first among the connectors (their z).
    const items = [...row(60), link("late", "n59", "n58"), link("early", "n0", "n1"), link("middle", "n30", "n31")];
    canvas({ items });
    await vi.waitFor(() => expect(store().findEdge("early")).toBeDefined());
    const nodesBefore = toRaw(store().nodes.value), early = toRaw(store().findEdge("early")!);
    await vi.waitFor(() => expect(store().nodes.value).toHaveLength(60));
    await vi.waitFor(() => expect(store().edges.value.map(edge => edge.id)).toEqual(["late", "early", "middle"]));
    // Nothing fed before is taken again: the nodes' list grew, the first connector is the same object.
    expect(toRaw(store().nodes.value)).toBe(nodesBefore);
    expect(toRaw(store().findEdge("early")!)).toBe(early);
    expect(store().nodes.value.map(entry => entry.id)).toEqual(items.slice(0, 60).map(item => item.id));
  });

  it("takes a new order of the connectors (one brought to the front) without drawing them again", async () => {
    const [a, b] = twoNotes(), lower = link("lower", "a", "b"), upper = link("upper", "b", "a");
    const board = canvas({ items: [a!, b!, lower, upper] });
    await flushPromises();
    const drawn = toRaw(store().findEdge("lower")!);
    await board.setProps({ items: [a!, b!, upper, lower] });
    await flushPromises();
    expect(store().edges.value.map(edge => edge.id)).toEqual(["upper", "lower"]);
    expect(toRaw(store().findEdge("lower")!)).toBe(drawn);
    expect(wrapper!.findAll(".vue-flow__edge").map(edge => edge.attributes("data-id"))).toEqual(["upper", "lower"]);
  });

  it("derives no node or connector anew for a change of the selection, and redraws only the items whose selection changed", async () => {
    const updated: string[] = [];
    wrapper = mount(BoardCanvas, {
      attachTo: document.body, props: { items: [...row(12), link("c", "n0", "n1"), link("d", "n4", "n5")], label: "Plán", canEdit: true },
      global: { mixins: [{ updated(this: { $options: { name?: string; __name?: string }; $props: { id?: string } }) { if (/^Board(Node|Edge)$/.test(this.$options.name ?? this.$options.__name ?? "")) updated.push(this.$props.id!); } }] },
    });
    await flushPromises();
    await new Promise(resolve => requestAnimationFrame(resolve));
    vi.mocked(toFlowNodes).mockClear();
    vi.mocked(toFlowEdges).mockClear();
    updated.length = 0;
    await select("n3", "n7");
    expect(selectedIds()).toEqual(["n3", "n7"]);
    expect(toFlowNodes).not.toHaveBeenCalled();
    expect(toFlowEdges).not.toHaveBeenCalled();
    expect(new Set(updated)).toEqual(new Set(["n3", "n7"]));
    updated.length = 0;
    flow().vm.$emit("edgesChange", [{ id: "d", type: "select", selected: true }]);
    await nextTick();
    expect(wrapper.findAll(".vue-flow__edge.selected").map(edge => edge.attributes("data-id"))).toEqual(["d"]);
    expect(new Set(updated)).toEqual(new Set(["d"]));
    // A connector under the pointer gets its end handles, as the selected one has them: only it is drawn again.
    updated.length = 0;
    flow().vm.$emit("edgeMouseEnter", { edge: { id: "c" } });
    await nextTick();
    expect(store().edges.value.filter(edge => edge.updatable).map(edge => edge.id)).toEqual(["c", "d"]);
    expect(new Set(updated)).toEqual(new Set(["c"]));
    flow().vm.$emit("edgeMouseLeave", { edge: { id: "c" } });
    await nextTick();
    expect(store().findEdge("c")!.updatable).toBe(false);
    expect(toFlowEdges).not.toHaveBeenCalled();
  });

  it("derives on each frame of a drag only the connectors attached to what moves, a group's members with it", async () => {
    const group: BoardViewItem = { id: "g", kind: "group", x: -24, y: -48, width: 548, height: 272, z: 0, text: "Skupina" };
    const items = [group, note("a", { parent: "g" }), note("b", { x: 300, z: 2, parent: "g" }), note("x", { x: 900, z: 3 }), note("y", { x: 900, y: 600, z: 4 }),
      link("ax", "a", "x"), link("bx", "b", "x"), link("xy", "x", "y")];
    canvas({ items });
    await flushPromises();
    expect(store().findEdge("ax")!.sourceHandle).toBe("right");
    await select("g");
    flow().vm.$emit("nodeDragStart", { nodes: [{ id: "g" }] });
    await nextTick();
    vi.mocked(toFlowEdges).mockClear();
    for (const y of [652, 1552]) {
      flow().vm.$emit("nodesChange", [{ id: "g", type: "position", dragging: true, position: { x: -24, y } }]);
      await nextTick();
    }
    const frames = vi.mocked(toFlowEdges).mock.calls;
    expect(frames).toHaveLength(2);
    for (const [derived] of frames) expect(derived.filter(item => item.kind === "connector").map(item => item.id)).toEqual(["ax", "bx"]);
    // The member moved far below x: its connector leaves from its top now.
    expect(store().findEdge("ax")!.sourceHandle).toBe("top");
    await stop();
    expect(changes()).toHaveLength(1);
  });
});
