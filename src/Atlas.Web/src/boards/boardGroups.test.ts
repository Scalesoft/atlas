// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { BoardChange } from "./boardApi";
import type { BoardItem } from "./boardModel";
import { arrangeChanges, collapsedOwners, containerAt, dropTargets, dropTargetsOf, escapeSelection, groupChanges, GROUP_PADDING, membersOf, parentOf, ungroupChanges, withMembers } from "./boardGroups";

const item = (id: string, kind: BoardItem["kind"], x: number, y: number, width: number, height: number, extra: Partial<BoardItem> = {}): BoardItem =>
  ({ id, kind, x, y, width, height, z: 1, ...extra });
const link = (id: string, from: string, to: string): BoardItem => ({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to });
const byId = (items: readonly BoardItem[]) => new Map(items.map(entry => [entry.id, entry]));
const moved = (id: string, x: number, y: number, width = 100, height = 100): BoardChange => ({ type: "move", itemId: id, x, y, width, height });

// A frame with a group inside it; the group holds two notes, the frame a third one; one note is free.
const frame = item("f", "frame", 0, 0, 1000, 800);
const group = item("g", "group", 84, 84, 432, 232, { parent: "f", text: "Tým" });
const a = item("a", "note", 100, 100, 200, 200, { parent: "g" });
const b = item("b", "note", 300, 100, 200, 200, { parent: "g" });
const c = item("c", "note", 600, 500, 100, 100, { parent: "f" });
const free = item("free", "note", 2000, 0, 100, 100);
const board = [frame, group, a, b, c, free, link("l", "a", "free")];

describe("membership (objects spec §5.4)", () => {
  it("knows an item's group or frame only while that container is live on the board", () => {
    expect(parentOf(a, byId(board))?.id).toBe("g");
    expect(parentOf(free, byId(board))).toBeUndefined();
    expect(parentOf(a, byId(board.filter(entry => entry.id !== "g")))).toBeUndefined();
    expect(parentOf(a, byId([...board.filter(entry => entry.id !== "g"), { ...group, deleted: true }]))).toBeUndefined();
  });

  it("moves every member of a moving container along, transitively and each once, never a container with its member", () => {
    expect(withMembers(["f"], board)).toEqual(["f", "g", "c", "a", "b"]);
    expect(withMembers(["a", "g"], board)).toEqual(["a", "g", "b"]);
    expect(withMembers(["a"], board)).toEqual(["a"]);
  });
});

describe("grouping (Ctrl+G, Ctrl+Shift+G)", () => {
  it("wraps the selection in a new group inside their common container, members padded, nothing for no item", () => {
    const changes = groupChanges(board, ["c", "g", "a"], "n", 9);
    expect(changes[0]).toEqual({ type: "create", item: {
      id: "n", kind: "group", x: 84 - GROUP_PADDING, y: 84 - GROUP_PADDING, width: 616 + 2 * GROUP_PADDING, height: 516 + 2 * GROUP_PADDING, z: 9, text: "", color: "blue", props: {},
    } });
    // a is inside g, which is selected: only g and c join; the new group joins f, where both were.
    expect(changes.slice(1)).toEqual([
      { type: "parent", itemId: "n", parentId: "f" },
      { type: "parent", itemId: "g", parentId: "n" },
      { type: "parent", itemId: "c", parentId: "n" },
    ]);
    expect(groupChanges(board, ["l"], "n", 9)).toEqual([]);
    // One item is no group: grouping takes at least two (objects spec §5.4, kept simple).
    expect(groupChanges(board, ["free"], "n", 9)).toEqual([]);
    expect(groupChanges(board, ["g", "a"], "n", 9)).toEqual([]);
  });

  it("groups items of different containers at the board's top level", () => {
    const changes = groupChanges(board, ["c", "free"], "n", 9);
    expect(changes.filter(change => change.type === "parent")).toEqual([{ type: "parent", itemId: "c", parentId: "n" }, { type: "parent", itemId: "free", parentId: "n" }]);
  });

  it("refuses a group that would nest something deeper than four levels", () => {
    const chain = [1, 2, 3].map(level => item(`d${level}`, "group", 0, 0, 100, 100, level > 1 ? { parent: `d${level - 1}` } : {}));
    const deep = [item("x", "note", 0, 0, 10, 10, { parent: "d3" }), item("w", "note", 20, 0, 10, 10, { parent: "d3" })];
    expect(groupChanges([...chain, ...deep], ["x", "w"], "n", 1)).toHaveLength(4);
    expect(groupChanges([...chain, ...deep, item("y", "group", 0, 0, 10, 10, { parent: "d3" }), item("z", "note", 0, 0, 5, 5, { parent: "y" })], ["y", "x"], "n", 1)).toEqual([]);
  });

  it("dissolves a selected group: its members go to its own container and the group is deleted", () => {
    expect(ungroupChanges(board, ["g", "c"])).toEqual([
      { type: "parent", itemId: "a", parentId: "f" },
      { type: "parent", itemId: "b", parentId: "f" },
      { type: "delete", itemId: "g" },
    ]);
    expect(ungroupChanges(board, ["a", "f"])).toEqual([]);
  });
});

describe("arranging a batch: membership by position and group bounds", () => {
  it("lets an item dragged out of a group leave it, and the group shrink around the rest", () => {
    const changes = arrangeChanges(board, [moved("b", 700, 100)], ["b"]);
    expect(changes).toEqual([
      moved("b", 700, 100),
      { type: "parent", itemId: "b", parentId: "f" },
      { type: "move", itemId: "g", x: 100 - GROUP_PADDING, y: 100 - GROUP_PADDING, width: 200 + 2 * GROUP_PADDING, height: 200 + 2 * GROUP_PADDING },
    ]);
  });

  it("lets an item dropped into a group join it, the innermost container under its centre winning, and the group grow", () => {
    const changes = arrangeChanges(board, [moved("free", 450, 250)], ["free"]);
    expect(changes.slice(1)).toEqual([
      { type: "parent", itemId: "free", parentId: "g" },
      { type: "move", itemId: "g", x: 84, y: 84, width: 450 + 100 + GROUP_PADDING - 84, height: 250 + 100 + GROUP_PADDING - 84 },
    ]);
  });

  it("keeps a member whose centre stays in its group, which refits, and moves nothing else when a container moves with its members", () => {
    expect(arrangeChanges(board, [moved("a", 110, 110, 200, 200)], ["a"]).slice(1)).toEqual([{ type: "move", itemId: "g", x: 94, y: 84, width: 422, height: 242 }]);
    const shift = [frame, group, a, b, c].map(entry => moved(entry.id, entry.x + 50, entry.y, entry.width, entry.height));
    expect(arrangeChanges(board, shift, ["f", "g", "a", "b", "c"])).toEqual(shift);
  });

  it("puts a new item into the container under its centre, unless its batch already says where it belongs", () => {
    const create: BoardChange = { type: "create", item: { id: "n", kind: "note", x: 620, y: 300, width: 100, height: 100, z: 5 } };
    expect(arrangeChanges(board, [create]).slice(1)).toEqual([{ type: "parent", itemId: "n", parentId: "f" }]);
    const placed: BoardChange[] = [create, { type: "parent", itemId: "n", parentId: "g" }];
    expect(arrangeChanges(board, placed).slice(0, 2)).toEqual(placed);
    expect(arrangeChanges(board, [{ type: "create", item: { id: "n", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "a", to: "b" } }])).toHaveLength(1);
  });

  it("never drops into a collapsed group nor into something deeper than four levels", () => {
    const collapsed = [frame, { ...group, props: { collapsed: true } }, a, b, c, free];
    expect(arrangeChanges(collapsed, [moved("free", 450, 250)], ["free"]).slice(1)).toEqual([{ type: "parent", itemId: "free", parentId: "f" }]);
  });
});

describe("frames keep their members inside (J1)", () => {
  it("grows a frame around a member that stays in it (its centre inside) but sticks out, never shrinking it", () => {
    // c's centre (980, 550) is in f, its right edge 1030 past the frame's 1000: the frame grows to wrap it with the padding.
    const changes = arrangeChanges(board, [moved("c", 930, 500)], ["c"]);
    expect(changes.slice(1)).toEqual([{ type: "move", itemId: "f", x: 0, y: 0, width: 1030 + GROUP_PADDING, height: 800 }]);
    // Well inside: the frame keeps its size, it does not shrink to its members as a group does.
    expect(arrangeChanges(board, [moved("c", 300, 500)], ["c"])).toEqual([moved("c", 300, 500)]);
  });

  it("detaches a member dragged more than half out of its frame, keeping its place, and the frame stays as it is", () => {
    expect(arrangeChanges(board, [moved("c", 960, 500)], ["c"])).toEqual([moved("c", 960, 500), { type: "parent", itemId: "c" }]);
  });

  it("grows nested frames from the inside out", () => {
    const inner = item("i", "frame", 600, 450, 300, 300, { parent: "f" });
    const m = item("m", "note", 620, 470, 100, 100, { parent: "i" });
    const changes = arrangeChanges([frame, inner, m], [moved("m", 840, 690)], ["m"]);
    // m's centre (890, 740) is in i; i grows to its right and bottom edge (956, 806), past f's bottom (800), so f grows too.
    expect(changes.slice(1)).toEqual([
      { type: "move", itemId: "i", x: 600, y: 450, width: 940 + GROUP_PADDING - 600, height: 790 + GROUP_PADDING - 450 },
      { type: "move", itemId: "f", x: 0, y: 0, width: 1000, height: 806 + GROUP_PADDING },
    ]);
  });

  it("lets the members a frame no longer covers after it was resized leave it, and does not grow it back", () => {
    // f shrinks to 500 wide: c's centre (650, 550) falls outside and c goes to the top level; the group (centre 300, 200) stays.
    const resize = moved("f", 0, 0, 500, 800);
    expect(arrangeChanges(board, [resize], ["f"])).toEqual([resize, { type: "parent", itemId: "c" }]);
  });
});

describe("drop targets while dragging (J1)", () => {
  it("names the container each dragged item would belong to if dropped now, none for one going out onto the board", () => {
    expect([...dropTargets(board, new Map([["free", { x: 450, y: 250, width: 100, height: 100 }]]))]).toEqual(["g"]);
    expect([...dropTargets(board, new Map([["c", { x: 960, y: 500, width: 100, height: 100 }]]))]).toEqual([]);
    // A container dragged with its members: they move along and target nothing, the container targets what is under it.
    const drag = new Map([["g", { x: 2000, y: 2000, width: 432, height: 232 }], ["a", { x: 2016, y: 2016, width: 200, height: 200 }]]);
    expect([...dropTargets(board, drag)]).toEqual([]);
  });

  it("are prepared once per gesture and decide every frame as arrangeChanges would", () => {
    for (const ids of [["free"], ["g", "a", "b"], ["c"]]) {
      const targets = dropTargetsOf(board, ids), root = board.find(entry => entry.id === ids[0])!;
      for (let dx = -400; dx <= 1200; dx += 200) for (let dy = -400; dy <= 1000; dy += 200) {
        const local = new Map(ids.map(id => { const at = board.find(entry => entry.id === id)!; return [id, { x: at.x + dx, y: at.y + dy, width: at.width, height: at.height }] as const; }));
        const arranged = arrangeChanges(board, [...local].map(([id, g]) => moved(id, g.x, g.y, g.width, g.height)), ids);
        const join = arranged.find((change): change is Extract<BoardChange, { type: "parent" }> => change.type === "parent" && change.itemId === root.id);
        const expected = join ? join.parentId : root.parent;
        expect([...targets(local)]).toEqual(expected ? [expected] : []);
      }
    }
  });
});

describe("collapsed groups", () => {
  it("hide their members, transitively, behind the outermost collapsed group", () => {
    const owners = collapsedOwners([frame, { ...group, props: { collapsed: true } }, a, b, c, free]);
    expect([...owners]).toEqual([["a", "g"], ["b", "g"]]);
    const nested = collapsedOwners([{ ...frame, kind: "group", props: { collapsed: true } }, { ...group, props: { collapsed: true } }, a, b, c]);
    expect(new Map(nested)).toEqual(new Map([["g", "f"], ["a", "f"], ["b", "f"], ["c", "f"]]));
    expect(collapsedOwners(board).size).toBe(0);
  });
});

describe("frame selection (owner feedback 27. 10.)", () => {
  it("names the direct members of a container: what „Vybrat obsah“ and Ctrl+A on a selected frame select", () => {
    expect(membersOf("f", board)).toEqual(["g", "c"]);
    expect(membersOf("g", board)).toEqual(["a", "b"]);
    expect(membersOf("a", board)).toEqual([]);
  });

  it("goes with Esc from the selected items to the container they share, and from a top-level selection to none", () => {
    expect(escapeSelection(["a", "b"], board)).toEqual(["g"]);
    expect(escapeSelection(["g"], board)).toEqual(["f"]);
    expect(escapeSelection(["f"], board)).toEqual([]);
    expect(escapeSelection(["a", "c"], board)).toEqual([]);
    expect(escapeSelection([], board)).toEqual([]);
  });

  it("finds the innermost open container under a point of empty board, its outline taking a few units of slack", () => {
    expect(containerAt(board, { x: 700, y: 100 })).toBe("f");
    expect(containerAt(board, { x: 90, y: 90 })).toBe("g");
    expect(containerAt(board, { x: 1003, y: 400 }, 4)).toBe("f");
    expect(containerAt(board, { x: 1010, y: 400 }, 4)).toBeUndefined();
    expect(containerAt([frame, { ...group, props: { collapsed: true } }], { x: 90, y: 90 })).toBe("f");
  });

  it("lets a new frame drawn over loose items take them in, but not items that belong to another container", () => {
    const created: BoardChange = { type: "create", item: { id: "n", kind: "frame", x: 1900, y: -50, width: 400, height: 300, z: 9, props: {} } };
    expect(arrangeChanges(board, [created]).slice(1)).toEqual([{ type: "parent", itemId: "free", parentId: "n" }]);
    // Inside f over c (a member of f, as the new frame is): c goes in; a and b over it would stay in their group.
    const inside: BoardChange = { type: "create", item: { id: "n", kind: "frame", x: 550, y: 450, width: 300, height: 250, z: 9, props: {} } };
    expect(arrangeChanges(board, [inside]).slice(1)).toEqual([{ type: "parent", itemId: "n", parentId: "f" }, { type: "parent", itemId: "c", parentId: "n" }]);
    const overMembers: BoardChange = { type: "create", item: { id: "n", kind: "frame", x: 560, y: 150, width: 400, height: 140, z: 9, props: {} } };
    expect(arrangeChanges([...board, item("d", "note", 150, 150, 20, 20, { parent: "g" })], [overMembers]).slice(1)).toEqual([{ type: "parent", itemId: "n", parentId: "f" }]);
  });
});
