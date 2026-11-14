// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { BoardChange } from "./boardApi";
import { createBoardModel, fromBoardDto, type BoardItem } from "./boardModel";

function note(id: string, extra: Partial<BoardItem> = {}): BoardItem {
  return { id, kind: "note", x: 0, y: 0, width: 200, height: 120, z: 1, text: "", color: "yellow", ...extra };
}
function connector(id: string, from: string, to: string): BoardItem {
  return { id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to };
}
function model(items: BoardItem[] = [note("a"), note("b", { x: 300, z: 2 })], sequence = 10) {
  let next = 0;
  return createBoardModel({ title: "Plán", sequence, items }, { newId: () => `op-${++next}` });
}
const move = (itemId: string, x: number, y = 0): BoardChange => ({ type: "move", itemId, x, y, width: 200, height: 120 });
const ids = (board: ReturnType<typeof model>) => board.items.map(item => item.id);
const itemOf = (board: ReturnType<typeof model>, id: string) => board.items.find(item => item.id === id);

describe("board model: loading", () => {
  it("reads a BoardDto: page targets become target ids, nulls become absent", () => {
    const snapshot = fromBoardDto({ id: "b", spaceId: "s", title: "Plán", sequence: 4, canEdit: true, items: [
      { id: "p", kind: "page", x: 1, y: 2, width: 240, height: 140, z: 3, text: null, color: null, version: 4, target: { resourceId: "page-1", type: "atlas.page", title: "Provoz", available: true } },
    ] });
    expect(snapshot).toEqual({ title: "Plán", sequence: 4, settings: { background: "dots", snapToGrid: false, gridSize: 24 }, items: [{ id: "p", kind: "page", x: 1, y: 2, width: 240, height: 140, z: 3, targetResourceId: "page-1" }] });
  });

  it("shows the confirmed items in z order and knows the confirmed sequence", () => {
    const board = model([note("top", { z: 5 }), note("bottom", { z: -1 }), note("middle", { z: 2 })], 7);
    expect(ids(board)).toEqual(["bottom", "middle", "top"]);
    expect(board.sequence).toBe(7);
    expect(board.title).toBe("Plán");
  });
});

describe("board model: local batches", () => {
  it("applies one gesture as one pending batch at once, leaving the confirmed sequence alone", () => {
    const board = model();
    const batch = board.apply([move("a", 50, 60), { type: "set", itemId: "a", color: "blue" }]);
    expect(batch).toEqual({ operationId: "op-1", changes: [move("a", 50, 60), { type: "set", itemId: "a", color: "blue" }] });
    expect(board.pending).toHaveLength(1);
    expect(itemOf(board, "a")).toMatchObject({ x: 50, y: 60, color: "blue" });
    expect(board.sequence).toBe(10);
  });

  it("moves an acknowledged batch into the confirmed state", () => {
    const board = model();
    const batch = board.apply([move("a", 50)])!;
    expect(board.acknowledge(batch.operationId, { sequence: 11, duplicate: false })).toBe("confirmed");
    expect(board.pending).toEqual([]);
    expect(board.sequence).toBe(11);
    expect(itemOf(board, "a")?.x).toBe(50);
  });

  it("drops a duplicate acknowledgement of a batch the confirmed state already holds", () => {
    const board = model();
    const batch = board.apply([move("a", 50)])!;
    board.receive({ sequence: 11, operationId: batch.operationId, changes: [move("a", 50)] });
    expect(board.acknowledge(batch.operationId, { sequence: 11, duplicate: true })).toBe("confirmed");
    expect(board.sequence).toBe(11);
    expect(board.pending).toEqual([]);
  });

  it("keeps an acknowledgement beyond a gap pending until the missing batches arrive", () => {
    const board = model();
    const batch = board.apply([move("a", 50)])!;
    expect(board.acknowledge(batch.operationId, { sequence: 13, duplicate: false })).toBe("gap");
    expect(board.sequence).toBe(10);
    expect(board.pending).toEqual([{ ...batch, acknowledged: 13 }]);
    board.receive({ sequence: 11, operationId: "x", changes: [move("b", 1)] });
    board.receive({ sequence: 12, operationId: "y", changes: [move("a", 9)] });
    // Ours is later on the server, so it still wins in the view.
    expect(itemOf(board, "a")?.x).toBe(50);
    board.receive({ sequence: 13, operationId: batch.operationId, changes: [move("a", 50)] });
    expect(board.pending).toEqual([]);
    expect(board.sequence).toBe(13);
    expect(itemOf(board, "a")?.x).toBe(50);
  });

  it("drops a rejected batch and reverts its effect in the view", () => {
    const board = model();
    const batch = board.apply([{ type: "delete", itemId: "a" }])!;
    expect(ids(board)).toEqual(["b"]);
    board.reject(batch.operationId);
    expect(board.pending).toEqual([]);
    expect(ids(board)).toEqual(["a", "b"]);
    expect(board.canUndo).toBe(false);
  });

  it("skips a pending change whose item disappeared underneath it, instead of resurrecting the item", () => {
    const board = model();
    board.apply([move("a", 50)]);
    board.receive({ sequence: 11, operationId: "x", changes: [{ type: "delete", itemId: "a" }] });
    expect(ids(board)).toEqual(["b"]);
  });
});

describe("board model: remote batches", () => {
  it("follows a merged contact: the server's set of a card's objectId points the card at the target", () => {
    const card: BoardItem = { id: "c", kind: "object", x: 0, y: 0, width: 240, height: 160, z: 1, targetResourceId: "old", props: { objectId: "old", typeId: "atlas.contact", view: "card" } };
    const board = model([card]);
    expect(board.receive({ sequence: 11, operationId: "merge", changes: [{ type: "set", itemId: "c", props: { objectId: "new" } }] })).toBe("applied");
    expect(itemOf(board, "c")).toMatchObject({ targetResourceId: "new", props: { objectId: "new", typeId: "atlas.contact", view: "card" } });
  });

  it("applies the next remote batch and replays the pending ones over it", () => {
    const board = model();
    board.apply([move("a", 50)]);
    expect(board.receive({ sequence: 11, operationId: "x", changes: [move("a", 5), { type: "set", itemId: "a", color: "green" }] })).toBe("applied");
    expect(board.sequence).toBe(11);
    // The remote colour shows; the local move is later and stays on top until it is confirmed.
    expect(itemOf(board, "a")).toMatchObject({ x: 50, color: "green" });
    expect(board.pending).toHaveLength(1);
  });

  it("reports a gap in the sequence instead of applying out of order", () => {
    const board = model();
    expect(board.receive({ sequence: 12, operationId: "x", changes: [move("a", 5)] })).toBe("gap");
    expect(board.sequence).toBe(10);
    expect(itemOf(board, "a")?.x).toBe(0);
  });

  it("ignores a batch it already has", () => {
    const board = model();
    expect(board.receive({ sequence: 10, operationId: "x", changes: [move("a", 5)] })).toBe("stale");
    expect(itemOf(board, "a")?.x).toBe(0);
  });

  it("recognises its own batch echoed by the hub and does not apply it twice", () => {
    const board = model();
    const create: BoardChange = { type: "create", item: { id: "n", kind: "note", x: 0, y: 0, width: 200, height: 120, z: 3, text: "Nová" } };
    const batch = board.apply([create])!;
    expect(board.receive({ sequence: 11, operationId: batch.operationId, changes: [create] })).toBe("applied");
    expect(board.pending).toEqual([]);
    expect(ids(board)).toEqual(["a", "b", "n"]);
    // The HTTP answer that comes after the echo changes nothing.
    expect(board.acknowledge(batch.operationId, { sequence: 11, duplicate: false })).toBe("confirmed");
    expect(board.sequence).toBe(11);
    expect(ids(board)).toEqual(["a", "b", "n"]);
  });

  it("asks for a reload when a remote batch refers to an item it never saw (a restore of an item deleted before loading)", () => {
    const board = model();
    expect(board.receive({ sequence: 11, operationId: "x", changes: [{ type: "restore", itemId: "gone" }] })).toBe("reload");
    expect(board.sequence).toBe(11);
  });

  it("renames the board", () => {
    const board = model();
    board.receive({ sequence: 11, operationId: "x", changes: [{ type: "rename", title: "Nový plán" }] });
    expect(board.title).toBe("Nový plán");
  });
});

describe("board model: full reload", () => {
  it("takes the new snapshot, keeps unacknowledged batches on top and drops the ones it already contains", () => {
    const board = model();
    const acknowledged = board.apply([move("a", 50)])!;
    board.acknowledge(acknowledged.operationId, { sequence: 12, duplicate: false });
    const unsent = board.apply([{ type: "set", itemId: "b", text: "Ahoj" }])!;
    board.reset({ title: "Plán", sequence: 12, items: [note("a", { x: 50 }), note("b", { x: 300, z: 2 }), note("c", { z: 3 })] });
    expect(board.sequence).toBe(12);
    expect(board.pending).toEqual([unsent]);
    expect(ids(board)).toEqual(["a", "b", "c"]);
    expect(itemOf(board, "b")?.text).toBe("Ahoj");
  });
});

describe("board model: connectors", () => {
  it("hides a connector while either end is deleted and shows it again when the end is restored, as the server reads it", () => {
    const board = model([note("a"), note("b", { z: 2 }), connector("c", "a", "b")]);
    expect(ids(board)).toEqual(["c", "a", "b"]);
    board.receive({ sequence: 11, operationId: "x", changes: [{ type: "delete", itemId: "b" }] });
    expect(ids(board)).toEqual(["a"]);
    board.receive({ sequence: 12, operationId: "y", changes: [{ type: "restore", itemId: "b" }] });
    expect(ids(board)).toEqual(["c", "a", "b"]);
  });

  it("hides a connector whose end it does not know", () => {
    expect(ids(model([note("a"), connector("c", "a", "missing")]))).toEqual(["a"]);
  });
});

describe("board model: undo and redo", () => {
  it("undoes a gesture as one inverse batch against the current view and redoes it", () => {
    const board = model();
    board.apply([move("a", 50), move("b", 70)]);
    const undo = board.undo()!;
    expect(undo.changes).toEqual([move("b", 300), move("a", 0)]);
    expect(itemOf(board, "a")?.x).toBe(0);
    expect(board.canRedo).toBe(true);
    expect(board.redo()!.changes).toEqual([move("a", 50), move("b", 70)]);
    expect(itemOf(board, "a")?.x).toBe(50);
    expect(board.canRedo).toBe(false);
  });

  it("holds a card's object edit as one step among the board's own, never in a batch, and skips it once the object changed since (P6)", () => {
    let name: unknown = "Jana Novák";
    const board = createBoardModel({ title: "Plán", sequence: 1, items: [note("a"), { ...note("o"), kind: "object", targetResourceId: "c1" }] },
      { readObject: (id, key) => (id === "c1" && key === "name" ? name : null) });
    board.apply([move("a", 50)]);
    // Two saves of one editing of the card: one step, none of it for the server.
    expect(board.apply([{ type: "object", itemId: "o", objectId: "c1", before: { name: "Jana" }, after: { name: "Jana N." } }], "o#1")).toBeNull();
    expect(board.apply([{ type: "object", itemId: "o", objectId: "c1", before: { name: "Jana N." }, after: { name: "Jana Novák" } }], "o#1")).toBeNull();
    expect(board.pending).toHaveLength(1);
    const undo = board.undo()!;
    expect(undo.changes).toEqual([{ type: "object", itemId: "o", objectId: "c1", before: { name: "Jana Novák" }, after: { name: "Jana" } }]);
    expect(board.pending).toHaveLength(1);
    name = "Jana";
    expect(board.redo()!.changes).toEqual([{ type: "object", itemId: "o", objectId: "c1", before: { name: "Jana" }, after: { name: "Jana Novák" } }]);
    // A colleague renamed the contact meanwhile: the undo leaves it and takes the board's step before.
    name = "Jana Dvořáková";
    expect(board.undo()!.changes).toEqual([move("a", 0)]);
    expect(board.canUndo).toBe(false);
  });

  it("keeps the saves of one editing (each pause in typing) as one undo step, a new editing as the next (W4-4)", () => {
    const board = model([note("a", { text: "Před" })]);
    board.apply([{ type: "set", itemId: "a", text: "Po" }], "a#1");
    board.apply([{ type: "set", itemId: "a", text: "Potom" }], "a#1");
    board.apply([{ type: "set", itemId: "a", text: "Potom ještě" }], "a#1");
    board.apply([{ type: "set", itemId: "a", text: "Jindy" }], "a#2");
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", text: "Potom ještě" }]);
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", text: "Před" }]);
    expect(board.canUndo).toBe(false);
  });

  it("merges unsent batches into one, in order and within the server's size, and keeps their undo steps (W4-3)", () => {
    const board = model();
    board.apply([move("a", 10)]);
    board.apply([move("b", 20)]);
    board.apply([move("a", 30)]);
    const [first, ...rest] = board.pending.map(batch => batch.operationId);
    const merged = board.coalesce(rest, 200);
    expect(merged.map(batch => batch.changes)).toEqual([[move("b", 20), move("a", 30)]]);
    expect(board.pending.map(batch => batch.operationId)).toEqual([first, merged[0]!.operationId]);
    expect(board.coalesce(board.pending.map(batch => batch.operationId), 1)).toHaveLength(2);
    board.reject(merged[0]!.operationId);
    expect(board.undo()!.changes).toEqual([move("a", 0)]);
    expect(board.canUndo).toBe(false);
  });

  it("undoes a creation by deleting and redoes it by restoring", () => {
    const board = model();
    board.apply([{ type: "create", item: { id: "n", kind: "note", x: 0, y: 0, width: 200, height: 120, z: 3 } }]);
    expect(board.undo()!.changes).toEqual([{ type: "delete", itemId: "n" }]);
    expect(ids(board)).toEqual(["a", "b"]);
    expect(board.redo()!.changes).toEqual([{ type: "restore", itemId: "n" }]);
    expect(ids(board)).toEqual(["a", "b", "n"]);
  });

  it("undoes a deletion by restoring", () => {
    const board = model();
    board.apply([{ type: "delete", itemId: "a" }]);
    expect(board.undo()!.changes).toEqual([{ type: "restore", itemId: "a" }]);
    expect(ids(board)).toEqual(["a", "b"]);
  });

  it("undoes text and colour separately, back to what they were", () => {
    const board = model([note("a", { text: "Před", color: "yellow" })]);
    board.apply([{ type: "set", itemId: "a", text: "Po" }]);
    board.apply([{ type: "set", itemId: "a", color: "pink" }]);
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", color: "yellow" }]);
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", text: "Před" }]);
  });

  it("undoes the first colour of an item by removing it (an empty colour), and a removal by giving it back", () => {
    const board = model([note("a", { color: undefined })]);
    board.apply([move("a", 50), { type: "set", itemId: "a", color: "pink" }]);
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", color: "" }, move("a", 0)]);
    expect(itemOf(board, "a")!.color).toBeUndefined();
    expect(board.redo()!.changes).toEqual([move("a", 50), { type: "set", itemId: "a", color: "pink" }]);
    board.apply([{ type: "set", itemId: "a", color: "" }]);
    expect(itemOf(board, "a")!.color).toBeUndefined();
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", color: "pink" }]);
  });

  it("undoes a rename", () => {
    const board = model();
    board.apply([{ type: "rename", title: "Jiný" }]);
    expect(board.undo()!.changes).toEqual([{ type: "rename", title: "Plán" }]);
    expect(board.title).toBe("Plán");
  });

  it("skips, without an error, the move of an item someone else deleted and undoes the gesture before it", () => {
    const board = model();
    board.apply([{ type: "set", itemId: "b", text: "Moje" }]);
    board.apply([move("a", 50)]);
    board.receive({ sequence: 11, operationId: "x", changes: [{ type: "delete", itemId: "a" }] });
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "b", text: "" }]);
    expect(board.undo()).toBeNull();
    expect(board.canUndo).toBe(false);
  });

  it("leaves a property alone that someone else changed after the gesture", () => {
    const board = model();
    const batch = board.apply([move("a", 50), { type: "set", itemId: "a", color: "blue" }])!;
    board.acknowledge(batch.operationId, { sequence: 11, duplicate: false });
    board.receive({ sequence: 12, operationId: "x", changes: [move("a", 999)] });
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "a", color: "yellow" }]);
    expect(itemOf(board, "a")).toMatchObject({ x: 999, color: "yellow" });
  });

  it("forgets the redo history on a new gesture", () => {
    const board = model();
    board.apply([move("a", 50)]);
    board.undo();
    board.apply([move("b", 1)]);
    expect(board.canRedo).toBe(false);
    expect(board.redo()).toBeNull();
  });

  it("forgets the history entry of a rejected undo", () => {
    const board = model();
    board.apply([move("a", 50)]);
    const undo = board.undo()!;
    board.reject(undo.operationId);
    expect(board.canRedo).toBe(false);
    expect(board.canUndo).toBe(false);
  });

  it("records nothing for an empty gesture", () => {
    const board = model();
    expect(board.apply([])).toBeNull();
    expect(board.canUndo).toBe(false);
  });
});

describe("board model: props (interaction spec §4)", () => {
  const arrow = (id: string, extra: Partial<BoardItem> = {}): BoardItem => ({ ...connector(id, "a", "b"), props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" }, ...extra });

  it("reads props from a BoardDto, and tolerates a kind and a prop it does not know", () => {
    const snapshot = fromBoardDto({ id: "b", spaceId: "s", title: "Plán", sequence: 4, canEdit: true, items: [
      { id: "y", kind: "block", x: 0, y: 0, width: 260, height: 120, z: 1, version: 4, props: { block: { type: "youtrack", attrs: { issueId: "ATL-1" } } } },
      { id: "w", kind: "widget" as never, x: 0, y: 0, width: 10, height: 10, z: 2, version: 4, props: { mystery: 1 } as never },
    ] });
    expect(snapshot.items).toEqual([
      { id: "y", kind: "block", x: 0, y: 0, width: 260, height: 120, z: 1, props: { block: { type: "youtrack", attrs: { issueId: "ATL-1" } } } },
      { id: "w", kind: "widget", x: 0, y: 0, width: 10, height: 10, z: 2, props: { mystery: 1 } },
    ]);
  });

  it("sets only the props it names and removes a key set to null", () => {
    const board = model([note("a"), note("b", { z: 2 }), arrow("c")]);
    board.apply([{ type: "set", itemId: "c", props: { route: "curved", arrowEnd: null } }]);
    expect(itemOf(board, "c")?.props).toEqual({ route: "curved", arrowStart: "none", dash: "solid" });
  });

  it("undoes and redoes props key by key, removing a key the item did not have", () => {
    const board = model([note("a"), note("b", { z: 2 }), arrow("c")]);
    board.apply([{ type: "set", itemId: "c", props: { route: "straight", fromSide: "right" } }]);
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "c", props: { fromSide: null } }, { type: "set", itemId: "c", props: { route: "elbow" } }]);
    expect(itemOf(board, "c")?.props).toEqual({ route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" });
    expect(board.redo()!.changes).toEqual([{ type: "set", itemId: "c", props: { route: "straight" } }, { type: "set", itemId: "c", props: { fromSide: "right" } }]);
    expect(itemOf(board, "c")?.props).toMatchObject({ route: "straight", fromSide: "right" });
  });

  it("undoes one gesture across kinds and properties as one step: a label, a shape type and a z order", () => {
    const shape: BoardItem = { id: "s", kind: "shape", x: 0, y: 0, width: 160, height: 100, z: 3, text: "", color: "blue", props: { shape: "rect" } };
    const board = model([note("a"), note("b", { z: 2 }), arrow("c", { text: "" }), shape]);
    board.apply([
      { type: "set", itemId: "c", text: "vede k" },
      { type: "set", itemId: "s", props: { shape: "diamond" }, color: "teal" },
      { type: "reorder", itemId: "a", z: 9 },
    ]);
    board.undo();
    expect(itemOf(board, "c")?.text).toBe("");
    expect(itemOf(board, "s")).toMatchObject({ color: "blue", props: { shape: "rect" } });
    expect(itemOf(board, "a")?.z).toBe(1);
    expect(board.canUndo).toBe(false);
  });

  it("leaves a prop alone that someone else changed after the gesture, and undoes the others", () => {
    const board = model([note("a"), note("b", { z: 2 }), arrow("c")]);
    const batch = board.apply([{ type: "set", itemId: "c", props: { route: "curved", dash: "dashed" } }])!;
    board.acknowledge(batch.operationId, { sequence: 11, duplicate: false });
    board.receive({ sequence: 12, operationId: "x", changes: [{ type: "set", itemId: "c", props: { route: "straight" } }] });
    expect(board.undo()!.changes).toEqual([{ type: "set", itemId: "c", props: { dash: "solid" } }]);
    expect(itemOf(board, "c")?.props).toMatchObject({ route: "straight", dash: "solid" });
  });

});

describe("board model: settings (interaction spec §3.7, §4)", () => {
  it("reads the board's settings over the defaults, also from a server that sends none", () => {
    const base = { id: "b", spaceId: "s", title: "Plán", sequence: 1, canEdit: true, items: [] };
    expect(fromBoardDto(base).settings).toEqual({ background: "dots", snapToGrid: false, gridSize: 24 });
    expect(fromBoardDto({ ...base, settings: { background: "timeline", snapToGrid: true, gridSize: 16, timeline: { origin: "2026-01-01", pxPerDay: 4 } } }).settings)
      .toEqual({ background: "timeline", snapToGrid: true, gridSize: 16, timeline: { origin: "2026-01-01", pxPerDay: 4 } });
  });

  it("changes only the settings it names, and undoes them key by key", () => {
    const board = createBoardModel({ title: "Plán", sequence: 1, items: [], settings: { background: "dots", snapToGrid: false, gridSize: 24 } }, { newId: () => "op" });
    board.apply([{ type: "settings", settings: { snapToGrid: true, background: "lines" } }]);
    expect(board.settings).toEqual({ background: "lines", snapToGrid: true, gridSize: 24 });
    expect(board.undo()!.changes).toEqual([{ type: "settings", settings: { background: "dots" } }, { type: "settings", settings: { snapToGrid: false } }]);
    expect(board.settings).toEqual({ background: "dots", snapToGrid: false, gridSize: 24 });
  });

  it("takes someone else's settings from the hub", () => {
    const board = createBoardModel({ title: "Plán", sequence: 1, items: [] });
    board.receive({ sequence: 2, operationId: "x", changes: [{ type: "settings", settings: { gridSize: 48 } }] });
    expect(board.settings.gridSize).toBe(48);
  });
});

describe("board model: membership (objects spec §5.4)", () => {
  it("reads each item's group or frame from a BoardDto", () => {
    const snapshot = fromBoardDto({ id: "b", spaceId: "s", title: "Plán", sequence: 1, canEdit: true, items: [
      { id: "g", kind: "group", x: 0, y: 0, width: 400, height: 300, z: 0, version: 1, text: "Tým" },
      { id: "a", kind: "note", x: 20, y: 20, width: 200, height: 120, z: 1, version: 1, parent: "g" },
    ] });
    expect(snapshot.items.map(item => item.parent)).toEqual([undefined, "g"]);
  });

  it("puts an item into a group and takes it out, a parent change without a parent clearing it", () => {
    const board = model([note("g", { kind: "group" }), note("a", { z: 2 })]);
    board.apply([{ type: "parent", itemId: "a", parentId: "g" }]);
    expect(itemOf(board, "a")?.parent).toBe("g");
    board.apply([{ type: "parent", itemId: "a" }]);
    expect(itemOf(board, "a")).not.toHaveProperty("parent");
  });

  it("undoes a change of membership back to the previous group, and to none", () => {
    const board = model([note("g", { kind: "group" }), note("h", { kind: "group", z: 3 }), note("a", { z: 2, parent: "g" })]);
    board.apply([{ type: "parent", itemId: "a", parentId: "h" }]);
    expect(board.undo()!.changes).toEqual([{ type: "parent", itemId: "a", parentId: "g" }]);
    board.apply([{ type: "parent", itemId: "a" }]);
    board.apply([{ type: "parent", itemId: "a", parentId: "h" }]);
    expect(board.undo()!.changes).toEqual([{ type: "parent", itemId: "a" }]);
    expect(itemOf(board, "a")).not.toHaveProperty("parent");
  });
});
