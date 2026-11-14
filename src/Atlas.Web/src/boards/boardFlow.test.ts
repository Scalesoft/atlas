// @vitest-environment node
import { describe, expect, it } from "vitest";
import { COLLAPSED_GROUP } from "./boardGroups";
import type { BoardItem } from "./boardModel";
import {
  alignChanges, autoSides, blockLayoutOf, spokenText, blockSizeOf, connectChanges, connectToNewNoteChanges, createChange, defaultProps, deleteChanges, distributeChanges,
  duplicateChanges, insertChanges, minSizeOf, moveChanges, noteToPageChanges, nearestSide, reconnectChanges, reorderChanges, reuseUnchanged, styleChanges, textChanges, toFlowEdges, toFlowNodes,
} from "./boardFlow";

const note = (id: string, extra: Partial<BoardItem> = {}): BoardItem => ({ id, kind: "note", x: 0, y: 0, width: 200, height: 200, z: 1, text: "Ahoj", color: "yellow", ...extra });
const link = (id: string, from: string, to: string, extra: Partial<BoardItem> = {}): BoardItem => ({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to, ...extra });
const ui = (extra: Partial<Parameters<typeof toFlowNodes>[1]> = {}) => ({ selected: new Set<string>(), local: new Map(), ...extra });
let counter = 0;
const ids = () => `id-${++counter}`;

describe("model → Vue Flow nodes", () => {
  it("makes a node of every item but connectors, sized and placed as the item, frames below everything", () => {
    const frame: BoardItem = { id: "f", kind: "frame", x: -50, y: -50, width: 800, height: 600, z: 9, text: "Sprint" };
    const nodes = toFlowNodes([note("a", { z: 1 }), link("c", "a", "b"), note("b", { x: 300, z: 2 }), frame], ui({ selected: new Set(["b"]) }));
    expect(nodes.map(node => [node.id, node.zIndex])).toEqual([["f", 0], ["a", 2], ["b", 3]]);
    expect(nodes[2]).toMatchObject({ type: "board", position: { x: 300, y: 0 }, width: 200, height: 200, selected: true, draggable: undefined, connectable: undefined });
    expect(nodes[2]!.data!.item.id).toBe("b");
    expect(nodes[2]!.domAttributes).toMatchObject({ "data-board-item": "b", "data-kind": "note" });
    expect(nodes[1]!.ariaLabel).toBe("Poznámka: Ahoj");
    expect(nodes[0]!.ariaLabel).toBe("Rám: Sprint");
  });

  // Owner, 28. 10.: an editor component lies on the board without a card, filling its box or as tall as its content.
  it("gives a component that flows the height of its content and keeps the box of one that fills it", () => {
    const block = (id: string, type: string): BoardItem => ({ id, kind: "block", x: 0, y: 0, width: 320, height: 200, z: 1, props: { block: { type } } });
    const nodes = Object.fromEntries(toFlowNodes([block("t", "table"), block("c", "chart"), block("r", "requirement"), note("n")], ui()).map(node => [node.id, node]));
    expect(nodes.t).toMatchObject({ width: 320, height: undefined });
    expect(nodes.c).toMatchObject({ width: 320, height: 200 });
    expect(nodes.r).toMatchObject({ width: 320, height: 200 });
    expect(nodes.n).toMatchObject({ width: 200, height: 200 });
    expect([block("a", "table"), block("b", "mathBlock"), block("c", "requirement"), note("d")].map(blockLayoutOf)).toEqual(["flow", "fill", undefined, undefined]);
    expect(blockSizeOf("chart")).toEqual({ width: 480, height: 320 });
    expect(blockSizeOf("kanban")).toEqual({ width: 320, height: 200 });
  });

  it("keeps a contact card readable: its own minimum size, the general one for the rest", () => {
    const object = (typeId: string): BoardItem => ({ id: "o", kind: "object", x: 0, y: 0, width: 300, height: 168, z: 1, props: { objectId: "c1", typeId } });
    const block = (type: string): BoardItem => ({ id: "b", kind: "block", x: 0, y: 0, width: 320, height: 200, z: 1, props: { block: { type } } });
    expect(minSizeOf(object("atlas.contact"))).toEqual({ width: 160, height: 56 });
    expect(minSizeOf(block("contactCard"))).toEqual({ width: 160, height: 56 });
    expect(minSizeOf(object("atlas.requirement"))).toEqual({ width: 24, height: 24 });
    expect(minSizeOf(block("table"))).toEqual({ width: 120, height: 24 });
    expect(minSizeOf(note("n"))).toEqual({ width: 24, height: 24 });
  });

  it("draws a frame I drag above the other frames, still below the items (J1)", () => {
    const frame = (id: string, z: number): BoardItem => ({ id, kind: "frame", x: 0, y: 0, width: 400, height: 300, z, text: id });
    const items = [frame("low", 1), frame("high", 2), note("n", { z: 3 })];
    expect(toFlowNodes(items, ui()).map(node => [node.id, node.zIndex])).toEqual([["low", 0], ["high", 1], ["n", 3]]);
    const dragging = toFlowNodes(items, ui({ local: new Map([["low", { x: 500, y: 0, width: 400, height: 300 }]]) }));
    expect(dragging.map(node => [node.id, node.zIndex])).toEqual([["high", 0], ["low", 1], ["n", 3]]);
  });

  // U3-11: a focus stop read „Komponenta: requirement“, the editor's node name instead of what the component is.
  it("names a component by what it is and its own text, not by the editor's node name", () => {
    const block = (id: string, node: object): BoardItem => ({ id, kind: "block", x: 0, y: 0, width: 300, height: 120, z: 1, props: { block: node as never } });
    const labels = Object.fromEntries(toFlowNodes([
      block("r", { type: "requirement", attrs: { key: "ssl" }, content: [{ type: "paragraph", content: [{ type: "text", text: "SSL " }, { type: "text", text: "certifikát", marks: [{ type: "bold" }] }] }] }),
      block("e", { type: "requirement", attrs: { key: "" }, content: [{ type: "paragraph" }] }),
      block("c", { type: "contactCard", attrs: {} }),
      block("k", { type: "kanban" }),
    ], ui()).map(node => [node.id, node.ariaLabel]));
    expect(labels).toEqual({ r: "Požadavek: SSL certifikát", e: "Požadavek: bez názvu", c: "Kontakt", k: "Komponenta" });
  });

  it("shows my gesture's geometry instead of the item's until the gesture lands", () => {
    const [node] = toFlowNodes([note("a")], ui({ local: new Map([["a", { x: 40, y: 50, width: 260, height: 260 }]]) }));
    expect(node).toMatchObject({ position: { x: 40, y: 50 }, width: 260, height: 260 });
  });

  it("lets nothing be dragged or connected read-only, nor the item whose text is being edited", () => {
    expect(toFlowNodes([note("a")], ui({ editing: "a" }))[0]).toMatchObject({ draggable: false });
  });

  it("hands the planned motion to the node, and a leaving item takes no input any more", () => {
    const motion = new Map([["a", { kind: "leave" as const, order: 0, key: 1 }], ["b", { kind: "enter" as const, order: 2, key: 2 }]]);
    const [a, b] = toFlowNodes([note("a"), note("b", { z: 2 })], ui({ motion }));
    expect(a).toMatchObject({ draggable: false, selectable: false, connectable: false, data: { motion: { kind: "leave" } } });
    expect(b).toMatchObject({ draggable: undefined, data: { motion: { kind: "enter", order: 2 } } });
  });

  it("keeps a node's data and attributes while its item and motion stay, so that only what changed draws again", () => {
    const a = note("a"), b = note("b", { x: 400 });
    const before = toFlowNodes([a, b], ui());
    const after = toFlowNodes([a, { ...b, x: 500 }], ui({ local: new Map([["a", { x: 9, y: 9, width: 200, height: 200 }]]) }));
    expect(after[0]!.data).toBe(before[0]!.data);
    expect(after[0]!.domAttributes).toBe(before[0]!.domAttributes);
    expect(after[1]!.data).not.toBe(before[1]!.data);
  });

  it("lets the pointer through a frame's area to the board, so a marquee can start inside it", () => {
    const [frame] = toFlowNodes([{ id: "f", kind: "frame", x: 0, y: 0, width: 600, height: 400, z: 1 }], ui());
    expect(frame!.style).toEqual({ pointerEvents: "none" });
  });

  it("draws a kind it does not know as a plain card instead of failing", () => {
    const [node] = toFlowNodes([{ ...note("w"), kind: "widget" as never }], ui());
    expect(node).toMatchObject({ id: "w", type: "board", ariaLabel: "Prvek tabule" });
  });
});

describe("model → Vue Flow edges", () => {
  const items = [note("a"), note("b", { x: 400 }), note("d", { y: 500 })];

  it("makes an edge of a connector with the spec's defaults when the item has no props yet", () => {
    const [edge] = toFlowEdges([...items, link("c", "a", "b", { text: "vede k" })], ui());
    expect(edge).toMatchObject({ id: "c", type: "board", source: "a", target: "b", sourceHandle: "right", targetHandle: "left", label: "vede k", selectable: true });
    expect(edge!.data).toMatchObject({ route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" });
  });

  it("uses the sides the connector names and works the others out from where the ends are, also mid-drag", () => {
    const [named, auto] = toFlowEdges([...items, link("c", "a", "b", { props: { fromSide: "top", toSide: "top", route: "curved", arrowStart: "arrow", arrowEnd: "none", dash: "dashed" } }), link("e", "a", "d")], ui());
    expect(named).toMatchObject({ sourceHandle: "top", targetHandle: "top", data: { route: "curved", arrowStart: "arrow", arrowEnd: "none", dash: "dashed" } });
    expect(auto).toMatchObject({ sourceHandle: "bottom", targetHandle: "top" });
    const [moved] = toFlowEdges([...items, link("e", "a", "d")], ui({ local: new Map([["d", { x: -900, y: 0, width: 200, height: 200 }]]) }));
    expect(moved).toMatchObject({ sourceHandle: "left", targetHandle: "right" });
  });

  it("puts connectors above frames and below the other items, and skips one whose end is not drawn", () => {
    const frame: BoardItem = { id: "f", kind: "frame", x: 0, y: 0, width: 800, height: 600, z: 1 };
    expect(toFlowEdges([frame, ...items, link("c", "a", "b"), link("x", "a", "gone")], ui()).map(edge => [edge.id, edge.zIndex])).toEqual([["c", 1]]);
  });

  it("spreads connectors between the same two items apart and carries the quick styles", () => {
    const edges = toFlowEdges([...items, link("c", "a", "b"), link("e", "b", "a", { props: { route: "elbow", arrowStart: "arrow", arrowEnd: "arrow", dash: "solid", animated: true, thickness: "l" } }), link("g", "a", "d")], ui());
    expect(edges.map(edge => [edge.id, edge.data!.offset])).toEqual([["c", -7], ["e", 7], ["g", undefined]]);
    expect(edges[1]!.data).toMatchObject({ animated: true, thickness: "l" });
  });

  it("keeps an edge's data object while only the selection changes, so its route is not worked out again (UX SB4-3)", () => {
    const all = [...items, link("c", "a", "b"), link("e", "b", "a"), link("g", "a", "d")];
    const before = toFlowEdges(all, ui()), after = toFlowEdges(all, ui({ selected: new Set(["a", "b", "c", "e", "g"]) }));
    expect(after.map(edge => (edge as { selected?: boolean }).selected)).toEqual([true, true, true]);
    after.forEach((edge, index) => expect(edge.data).toBe(before[index]!.data));
    // A changed item (or offset) gets new data.
    const moved = toFlowEdges(all.map(item => (item.id === "g" ? { ...item, text: "nový" } : item)), ui());
    expect(moved[2]!.data).not.toBe(before[2]!.data);
    expect(moved[0]!.data).toBe(before[0]!.data);
    // End handles to move only on the selected, hovered or keyboard-focused connectors.
    expect(after.map(edge => edge.updatable)).toEqual([true, true, true]);
    expect(before.map(edge => edge.updatable)).toEqual([false, false, false]);
    expect(toFlowEdges(all, ui({ hoveredEdge: "e", focusedEdge: "g" })).map(edge => edge.updatable)).toEqual([false, true, true]);
  });

  it("names a connector after its ends and label, again when one of them changes", () => {
    const all = [...items, link("c", "a", "b", { text: "vede k" })];
    const [first] = toFlowEdges(all, ui());
    expect(first!.ariaLabel).toBe(toFlowEdges(all, ui())[0]!.ariaLabel);
    const renamed = toFlowEdges(all.map(item => (item.id === "b" ? { ...item, text: "Konec" } : item)), ui())[0]!.ariaLabel;
    expect(renamed).not.toBe(first!.ariaLabel);
    expect(renamed).toContain("Konec");
    expect(toFlowEdges(all.map(item => (item.id === "c" ? { ...item, text: "blokuje" } : item)), ui())[0]!.ariaLabel).toContain("blokuje");
  });

  it("raises what my gesture drags above the other cards", () => {
    const nodes = toFlowNodes(items, ui({ local: new Map([["a", { x: 10, y: 10, width: 200, height: 200 }]]) }));
    expect(nodes.find(node => node.id === "a")!.zIndex).toBeGreaterThan(Math.max(...nodes.filter(node => node.id !== "a").map(node => node.zIndex!)));
  });
});

describe("connector sides", () => {
  const a = { x: 0, y: 0, width: 100, height: 100 };
  it("faces the two ends at each other along the axis they are further apart on", () => {
    expect(autoSides(a, { x: 300, y: 50, width: 100, height: 100 })).toEqual(["right", "left"]);
    expect(autoSides(a, { x: -300, y: 50, width: 100, height: 100 })).toEqual(["left", "right"]);
    expect(autoSides(a, { x: 50, y: 300, width: 100, height: 100 })).toEqual(["bottom", "top"]);
    expect(autoSides(a, { x: 50, y: -300, width: 100, height: 100 })).toEqual(["top", "bottom"]);
  });
  it("takes the side nearest to where a connector was dropped on an item", () => {
    expect(nearestSide(a, { x: 50, y: 5 })).toBe("top");
    expect(nearestSide(a, { x: 98, y: 40 })).toBe("right");
    expect(nearestSide(a, { x: 50, y: 140 })).toBe("bottom");
    expect(nearestSide(a, { x: -30, y: 50 })).toBe("left");
  });
});

describe("gestures → one batch each", () => {
  it("moves every dragged item that ended elsewhere, rounded and kept within the board's limits, in one batch", () => {
    const items = [note("a"), note("b", { x: 300 }), note("c", { x: 600 })];
    const next = new Map([["a", { x: 10.4, y: 20.6, width: 200, height: 200 }], ["b", { x: 300, y: 0, width: 200, height: 200 }], ["c", { x: 2e6, y: 0, width: 4, height: 200 }]]);
    expect(moveChanges(items, next)).toEqual([
      { type: "move", itemId: "a", x: 10, y: 21, width: 200, height: 200 },
      { type: "move", itemId: "c", x: 1_000_000, y: 0, width: 8, height: 200 },
    ]);
  });

  it("creates a square note centred where it was put, with the props of its kind", () => {
    expect(createChange("note", { x: 100, y: 100 }, { id: "n", z: 4 })).toEqual({ type: "create", item: { id: "n", kind: "note", x: 0, y: 0, width: 200, height: 200, z: 4, text: "", color: "yellow", props: { fontSize: "auto" } } });
    expect(defaultProps("connector")).toEqual({ route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" });
    expect(createChange("shape", { x: 0, y: 0 }, { id: "s", z: 1 })).toMatchObject({ item: { kind: "shape", props: { shape: "rect", align: "center" } } });
  });

  it("U1: sizes a requirement block to its one line and its facts, not the box of a chart", () => {
    expect(createChange("block", { x: 0, y: 0 }, { id: "r", z: 1, props: { block: { type: "requirement" } } })).toMatchObject({ item: { width: 320, height: 88 } });
    expect(createChange("block", { x: 0, y: 0 }, { id: "c", z: 1, props: { block: { type: "chart" } } })).toMatchObject({ item: { width: 480, height: 320 } });
    expect(createChange("block", { x: 0, y: 0 }, { id: "k", z: 1, props: { block: { type: "kanban" } } })).toMatchObject({ item: { width: 320, height: 200 } });
  });

  it("connects two items with the sides it was dragged between, and refuses a loop or a connector end", () => {
    const items = [note("a"), note("b"), link("c", "a", "b")];
    expect(connectChanges(items, { id: "k", from: "a", to: "b", fromSide: "right", toSide: "left" })).toEqual([{ type: "create", item: {
      id: "k", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "a", to: "b", props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid", fromSide: "right", toSide: "left" } } }]);
    expect(connectChanges(items, { id: "k", from: "a", to: "a" })).toEqual([]);
    expect(connectChanges(items, { id: "k", from: "a", to: "c" })).toEqual([]);
  });

  it("drops a connector on empty board as a new note at the end, both in one batch", () => {
    const changes = connectToNewNoteChanges([note("a")], { noteId: "n", connectorId: "k", from: "a", fromSide: "bottom", at: { x: 100, y: 500 }, z: 3 });
    expect(changes).toHaveLength(2);
    expect(changes[0]).toMatchObject({ type: "create", item: { id: "n", kind: "note", x: 0, y: 400, z: 3 } });
    expect(changes[1]).toMatchObject({ type: "create", item: { id: "k", kind: "connector", from: "a", to: "n", props: { fromSide: "bottom" } } });
  });

  it("reconnects to another side of the same item by props, and to another item by replacing the connector", () => {
    const items = [note("a"), note("b"), note("d"), link("c", "a", "b", { text: "ano", color: "red", props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "solid", toSide: "left" } })];
    const connector = items[3]!;
    expect(reconnectChanges(connector, { from: "a", to: "b", fromSide: "bottom", toSide: "left" }, "new")).toEqual([{ type: "set", itemId: "c", props: { fromSide: "bottom" } }]);
    expect(reconnectChanges(connector, { from: "a", to: "d", toSide: "top" }, "new")).toEqual([
      { type: "delete", itemId: "c" },
      { type: "create", item: { id: "new", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "a", to: "d", text: "ano", color: "red",
        props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "solid", toSide: "top" } } },
    ]);
    expect(reconnectChanges(connector, { from: "a", to: "b", toSide: "left" }, "new")).toEqual([]);
  });

  it("deletes the selection", () => {
    expect(deleteChanges(["a", "c"])).toEqual([{ type: "delete", itemId: "a" }, { type: "delete", itemId: "c" }]);
  });

  it("duplicates the selection beside itself on top, with the connectors between the copies", () => {
    const items = [note("a", { z: 1 }), note("b", { x: 300, z: 5 }), note("o", { x: 900, z: 2 }), link("c", "a", "b", { text: "x" }), link("d", "a", "o")];
    const { changes, ids: copies } = duplicateChanges(items, ["a", "b", "c", "d"], ids);
    expect(copies).toHaveLength(3);
    expect(changes.map(change => change.type === "create" && [change.item.kind, change.item.x, change.item.y, change.item.z, change.item.from, change.item.to])).toEqual([
      ["note", 24, 24, 6, undefined, undefined],
      ["note", 324, 24, 7, undefined, undefined],
      ["connector", 0, 0, 0, copies[0], copies[1]],
    ]);
  });

  it("brings the selection to the front or sends it to the back, keeping its own order", () => {
    const items = [note("a", { z: 1 }), note("b", { z: 2 }), note("c", { z: 3 }), note("d", { z: 4 })];
    expect(reorderChanges(items, ["b", "a"], "front")).toEqual([{ type: "reorder", itemId: "a", z: 5 }, { type: "reorder", itemId: "b", z: 6 }]);
    expect(reorderChanges(items, ["c", "d"], "back")).toEqual([{ type: "reorder", itemId: "c", z: -1 }, { type: "reorder", itemId: "d", z: 0 }]);
    expect(reorderChanges(items, ["d"], "front")).toEqual([]);
  });

  it("paints and styles only what is different", () => {
    const items = [note("a", { color: "yellow" }), note("b", { color: "blue" }), link("c", "a", "b", { props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" } })];
    expect(styleChanges(items, ["a", "b"], { color: "blue" })).toEqual([{ type: "set", itemId: "a", color: "blue" }]);
    expect(styleChanges(items, ["c"], { props: { route: "elbow", dash: "dashed" } })).toEqual([{ type: "set", itemId: "c", props: { dash: "dashed" } }]);
    // No colour (a requirement card back to its state's): an empty one, only for what has one.
    expect(styleChanges(items, ["a", "c"], { color: null })).toEqual([{ type: "set", itemId: "a", color: "" }]);
  });

  it("gives a new connector the last style used, never its sides or state", () => {
    const [change] = connectChanges([note("a"), note("b")], { id: "k", from: "a", to: "b", toSide: "left", style: { route: "curved", arrowStart: "arrow", animated: true, thickness: "l", color: "red" } });
    expect(change).toEqual({ type: "create", item: { id: "k", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "a", to: "b", color: "red",
      props: { route: "curved", arrowStart: "arrow", arrowEnd: "arrow", dash: "solid", animated: true, thickness: "l", toSide: "left" } } });
  });

  it("aligns the selection on an edge or centre of its bounds and distributes it evenly", () => {
    const items = [note("a", { x: 0, y: 0, width: 100, height: 100 }), note("b", { x: 250, y: 40, width: 100, height: 60 }), note("c", { x: 600, y: 10, width: 200, height: 100 })];
    expect(alignChanges(items, ["a", "b", "c"], "top")).toEqual([
      { type: "move", itemId: "b", x: 250, y: 0, width: 100, height: 60 },
      { type: "move", itemId: "c", x: 600, y: 0, width: 200, height: 100 },
    ]);
    expect(alignChanges(items, ["a", "b"], "right").map(change => change.type === "move" && change.x)).toEqual([250]);
    expect(alignChanges(items, ["a", "c"], "middle").map(change => change.type === "move" && [change.itemId, change.y])).toEqual([["a", 5], ["c", 5]]);
    // Bounds 0..800 hold 400 of items: three items leave two gaps of 200.
    expect(distributeChanges(items, ["c", "a", "b"], "horizontal")).toEqual([{ type: "move", itemId: "b", x: 300, y: 40, width: 100, height: 60 }]);
  });

  it("saves edited text clean, a connector label on one line, and nothing for no change", () => {
    expect(textChanges(note("a"), "Nový\ttext\u202E")).toEqual([{ type: "set", itemId: "a", text: "Nový    text" }]);
    expect(textChanges(link("c", "a", "b"), "dva\nřádky")).toEqual([{ type: "set", itemId: "c", text: "dva řádky" }]);
    expect(textChanges(note("a"), "Ahoj")).toEqual([]);
  });
});

describe("inserting several items as a grid of cards (spec §3.5)", () => {
  it("lays pasted pages, editor blocks and notes out in a square grid of equal cells centred where they go, one batch", () => {
    counter = 0;
    const block = { type: "contact", attrs: { name: "Jana" } };
    const { changes, ids: made } = insertChanges([{ kind: "page", resourceId: "r1", anchor: "uvod" }, { kind: "block", block }, { kind: "note", text: "Nápad" }, { kind: "note", text: "Druhý" }], { x: 0, y: 0 }, ids, 7);
    expect(made).toEqual(["id-1", "id-2", "id-3", "id-4"]);
    expect(changes.map(change => change.type === "create" && [change.item.kind, change.item.x, change.item.y, change.item.z])).toEqual([
      ["page", -312, -182, 7], ["block", 12, -212, 8], ["note", -272, 12, 9], ["note", 72, 12, 10],
    ]);
    expect(changes[0]).toMatchObject({ item: { targetResourceId: "r1", props: { display: "card", anchor: "uvod" } } });
    expect(changes[1]).toMatchObject({ item: { props: { block } } });
    expect(changes[2]).toMatchObject({ item: { text: "Nápad", color: "yellow" } });
  });

  it("sizes the cells by a component's own size: a chart and a note do not overlap", () => {
    const { changes } = insertChanges([{ kind: "block", block: { type: "chart" } }, { kind: "note", text: "x" }], { x: 0, y: 0 }, ids, 1);
    const [chart, note] = changes.map(change => (change.type === "create" ? change.item : undefined));
    expect(chart).toMatchObject({ width: 480, height: 320 });
    expect(note!.x).toBeGreaterThanOrEqual(chart!.x + chart!.width);
  });

  it("puts a single item centred on the point", () => {
    const { changes } = insertChanges([{ kind: "note", text: "x" }], { x: 100, y: 100 }, ids, 1);
    expect(changes[0]).toMatchObject({ item: { x: 0, y: 0 } });
  });
});

describe("groups on the canvas (objects spec §5.4)", () => {
  const group = (extra: Partial<BoardItem> = {}): BoardItem => ({ id: "g", kind: "group", x: 0, y: 0, width: 500, height: 300, z: 5, text: "Tým", color: "blue", ...extra });
  const members = [note("a", { parent: "g", x: 16, y: 16 }), note("b", { parent: "g", x: 260, y: 16 }), note("o", { x: 900 })];
  const collapsed = [group({ props: { collapsed: true } }), ...members, link("ab", "a", "b"), link("ao", "a", "o"), link("bo", "b", "o"), link("oa", "o", "a")];

  it("draws an open group below the items, above a frame, and lets the pointer through its area like a frame", () => {
    const frame: BoardItem = { id: "f", kind: "frame", x: -50, y: -50, width: 800, height: 600, z: 9 };
    const nodes = toFlowNodes([note("a", { parent: "g" }), group({ parent: "f" }), frame], ui());
    expect(nodes.map(node => node.id)).toEqual(["f", "g", "a"]);
    expect(nodes[1]).toMatchObject({ class: "board-group-node", style: { pointerEvents: "none" } });
    expect(nodes[1]!.ariaLabel).toBe("Skupina: Tým");
  });

  it("draws a collapsed group as one compact card counting its members, which fade away and take no input", () => {
    const nodes = toFlowNodes(collapsed, ui());
    const byId = new Map(nodes.map(node => [node.id, node]));
    expect(byId.get("g")).toMatchObject({ width: COLLAPSED_GROUP.width, height: COLLAPSED_GROUP.height, data: { members: 2 } });
    expect(byId.get("g")!.class).toBe("");
    expect(byId.get("a")).toMatchObject({ class: "is-collapsed-away", selectable: false, draggable: false, connectable: false, focusable: false });
    expect(byId.get("o")!.class).toBe("");
  });

  it("merges the connectors of hidden members into one edge per direction with their count, and drops those inside the group", () => {
    const edges = toFlowEdges(collapsed, ui());
    expect(edges.map(edge => [edge.id, edge.source, edge.target, edge.data!.merged])).toEqual([["merged:g:o", "g", "o", 2], ["merged:o:g", "o", "g", 1]]);
    expect(edges[0]).toMatchObject({ selectable: false, updatable: false, ariaLabel: "2 sloučené spojnice z Skupina: Tým do Poznámka: Ahoj" });
    expect(toFlowEdges(collapsed.map(item => (item.id === "g" ? group() : item)), ui()).map(edge => edge.id)).toEqual(["ab", "ao", "bo", "oa"]);
  });

  it("creates a group with its size and colour and keeps its name on one line", () => {
    expect(createChange("group", { x: 0, y: 0 }, { id: "n", z: 1 })).toMatchObject({ item: { kind: "group", width: 400, height: 300, color: "blue", text: "" } });
    expect(textChanges(group(), "Tým\nA")).toEqual([{ type: "set", itemId: "g", text: "Tým A" }]);
  });

  it("duplicates a group with its members, the copies inside the copied group", () => {
    let next = 0;
    const { changes } = duplicateChanges([group(), ...members], ["g", "a", "b"], () => `c${++next}`);
    // The copies are made bottom to top: the notes (c1, c2) before their group (c3).
    expect(changes.filter(change => change.type === "parent")).toEqual([{ type: "parent", itemId: "c1", parentId: "c3" }, { type: "parent", itemId: "c2", parentId: "c3" }]);
    expect(changes.every(change => change.type !== "create" || !("parent" in change.item))).toBe(true);
  });
});

describe("a note becomes a page (objects spec §5.2)", () => {
  it("is replaced by a card of the new page in its place and container, its connectors moved to the card, in one batch", () => {
    let next = 0;
    const items = [note("n", { x: 10, y: 20, z: 3, parent: "g" }), note("o", { x: 400 }), link("in", "o", "n"), link("out", "n", "o")];
    const changes = noteToPageChanges(items, "n", "page-1", () => `c${++next}`);
    expect(changes.slice(0, 3)).toEqual([
      { type: "delete", itemId: "n" },
      { type: "create", item: { id: "c1", kind: "page", x: 10, y: 20, width: 200, height: 200, z: 3, targetResourceId: "page-1", props: { display: "card" } } },
      { type: "parent", itemId: "c1", parentId: "g" },
    ]);
    expect(changes.slice(3).filter(change => change.type === "create").map(change => change.type === "create" && [change.item.from, change.item.to])).toEqual([["o", "c1"], ["c1", "o"]]);
    expect(noteToPageChanges(items, "o2", "page-1", () => "x")).toEqual([]);
  });
});

describe("object cards (objects spec §5.1)", () => {
  it("places an object as a card targeting it, with no text of its own", () => {
    const { changes } = insertChanges([{ kind: "object", objectId: "c1", typeId: "atlas.contact" }], { x: 0, y: 0 }, () => "n1", 3);
    expect(changes).toEqual([{ type: "create", item: { id: "n1", kind: "object", x: -150, y: -84, width: 300, height: 168, z: 3, targetResourceId: "c1",
      props: { objectId: "c1", typeId: "atlas.contact", view: "card" } } }]);
  });
});

describe("nodes derived again (COL2-1)", () => {
  it("keeps the node of every item that did not change, also once Vue Flow wrote its size into the node's style, and takes the changed ones", () => {
    const a = note("a"), b = note("b", { x: 300, z: 2 });
    const first = toFlowNodes([a, b], ui());
    // NodeWrapper's getStyle writes the node's width and height into the style object it was given.
    Object.assign(first[0]!.style as object, { width: "200px", height: "200px" });
    const moved = { ...b, x: 340 };
    const next = reuseUnchanged(first, toFlowNodes([a, moved], ui()));
    expect(next[0]).toBe(first[0]);
    expect(next[1]).not.toBe(first[1]);
    expect(next[1]!.position).toEqual({ x: 340, y: 0 });
    // A selection, a new item and a style the earlier node did not have are changes too.
    const selected = reuseUnchanged(next, toFlowNodes([a, moved, note("c", { z: 3 })], ui({ selected: new Set(["a"]) })));
    expect(selected[0]).not.toBe(next[0]);
    expect(selected[1]).toBe(next[1]);
    expect(selected).toHaveLength(3);
    const styled = reuseUnchanged(first, first.map(node => ({ ...node, style: { pointerEvents: "none" } })));
    expect(styled[0]).not.toBe(first[0]);
  });
});
describe("spokenText", () => {
  it("says a board text's Markdown as words: link texts, no marks, no block markup, escapes resolved", () => {
    expect(spokenText("Plán na **pátek**: ověřit *dodávku* a [web](https://example.cz)")).toBe("Plán na pátek: ověřit dodávku a web");
    expect(spokenText("# Nadpis\n- [ ] úkol\n> citace `kód`")).toBe("Nadpis úkol citace kód");
    expect(spokenText("cena 5 \\* 3 a snake_case")).toBe("cena 5 * 3 a snake_case");
  });
});
