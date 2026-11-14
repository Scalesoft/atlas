// @vitest-environment node
import { describe, expect, it } from "vitest";
import { COLLAPSED_GROUP } from "./boardGroups";
import type { BoardItem } from "./boardModel";
import { planGroupMotion, planMotion } from "./boardMotion";

const at = (x: number, y = 0, width = 200, height = 200) => ({ x, y, width, height });
const shown = (entries: [string, ReturnType<typeof at>][]) => new Map(entries);
const plan = (prev: Parameters<typeof planMotion>[0], next: Parameters<typeof planMotion>[1], extra: Partial<Parameters<typeof planMotion>[2]> = {}) =>
  Object.fromEntries(planMotion(prev, next, { local: new Set(), reduced: false, own: false, ...extra }));

describe("when and how a board item moves (interaction spec §5)", () => {
  it("does not animate the items of a board that has just opened", () => {
    expect(plan(null, shown([["a", at(0)], ["b", at(300)]]))).toEqual({});
  });

  it("lets new items enter and deleted ones leave, and leaves the others alone", () => {
    expect(plan(shown([["a", at(0)], ["gone", at(300)]]), shown([["a", at(0)], ["new", at(600)]]))).toEqual({
      new: { kind: "enter", order: 0 },
      gone: { kind: "leave", order: 0 },
    });
  });

  it("staggers my own new items in order, the first eight one after another and the rest with the eighth", () => {
    const next = shown(Array.from({ length: 10 }, (_, index) => [`n${index}`, at(index * 10)] as [string, ReturnType<typeof at>]));
    const motion = plan(shown([]), next, { own: true });
    expect(Object.values(motion).map(item => item.order)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 7, 7]);
  });

  it("does not stagger someone else's batch of more than eight items", () => {
    const next = shown(Array.from({ length: 9 }, (_, index) => [`n${index}`, at(index * 10)] as [string, ReturnType<typeof at>]));
    expect(new Set(Object.values(plan(shown([]), next)).map(item => item.order))).toEqual(new Set([0]));
    const few = shown(Array.from({ length: 3 }, (_, index) => [`n${index}`, at(index * 10)] as [string, ReturnType<typeof at>]));
    expect(Object.values(plan(shown([]), few)).map(item => item.order)).toEqual([0, 1, 2]);
  });

  it("glides an item someone else moved, or that undo, align or distribute moved, to its new place", () => {
    expect(plan(shown([["a", at(0)]]), shown([["a", at(300, 40)]]))).toEqual({ a: { kind: "glide", order: 0 } });
  });

  it("never animates what follows my own pointer, nor a change of size alone", () => {
    expect(plan(shown([["a", at(0)]]), shown([["a", at(300)]]), { local: new Set(["a"]) })).toEqual({});
    expect(plan(shown([["a", at(0)]]), shown([["a", at(0, 0, 400, 300)]]))).toEqual({});
  });

  it("under reduced motion fades a moved item instead of sliding it, and still lets items enter and leave by a fade", () => {
    expect(plan(shown([["a", at(0)], ["gone", at(0)]]), shown([["a", at(300)], ["new", at(0)]]), { reduced: true })).toEqual({
      a: { kind: "fade", order: 0 }, new: { kind: "enter", order: 0 }, gone: { kind: "leave", order: 0 },
    });
  });
});

describe("which animation shows a group change (interaction spec §5)", () => {
  const note = (id: string, x: number, extra: Partial<BoardItem> = {}): BoardItem => ({ id, kind: "note", x, y: 100, width: 100, height: 100, z: 1, ...extra });
  const group = (extra: Partial<BoardItem> = {}): BoardItem => ({ id: "g", kind: "group", x: 84, y: 84, width: 332, height: 132, z: 0, ...extra });
  const members = [note("a", 100, { parent: "g" }), note("b", 300, { parent: "g" })];
  const free = [note("a", 100), note("b", 300)];
  const byId = (items: BoardItem[]) => new Map(items.map(item => [item.id, item]));
  const groupPlan = (prev: BoardItem[], next: BoardItem[]) => Object.fromEntries(planGroupMotion(byId(prev), byId(next)));

  it("draws a new group's outline in from its members' common bounds and pulls the members gently together", () => {
    expect(groupPlan(free, [group(), ...members])).toEqual({
      g: { kind: "enter", order: 0, variant: "draw", from: { x: 100, y: 100, width: 300, height: 100 } },
      a: { kind: "gather", order: 0 }, b: { kind: "gather", order: 0 },
    });
    // A new group without members (pasted, created by someone else empty) just enters.
    expect(groupPlan([], [group()])).toEqual({});
  });

  it("dissolves an ungrouped group outward and lets its members, free again, light up", () => {
    expect(groupPlan([group(), ...members], free)).toEqual({
      g: { kind: "leave", order: 0, variant: "dissolve" }, a: { kind: "freed", order: 0 }, b: { kind: "freed", order: 0 },
    });
    // A group deleted with its members only leaves.
    expect(groupPlan([group(), ...members], [])).toEqual({});
  });

  it("glides the members into the card of a collapsing group, and out to their places one after another when it expands", () => {
    const card = { x: 84 + COLLAPSED_GROUP.width / 2, y: 84 + COLLAPSED_GROUP.height / 2 };
    const toCard = (item: BoardItem) => ({ x: card.x - (item.x + 50), y: card.y - (item.y + 50) });
    const open = [group(), ...members, link("ab", "a", "b"), link("ao", "a", "o"), note("o", 600)];
    const closed = [group({ props: { collapsed: true } }), ...open.slice(1)];
    expect(groupPlan(open, closed)).toEqual({ a: { kind: "tuck", order: 0, toward: toCard(members[0]!) }, b: { kind: "tuck", order: 0, toward: toCard(members[1]!) } });
    // Expanding staggers the members and draws their connectors again (merged edges split).
    expect(groupPlan(closed, open)).toEqual({
      a: { kind: "untuck", order: 0, toward: toCard(members[0]!) }, b: { kind: "untuck", order: 1, toward: toCard(members[1]!) },
      ab: { kind: "enter", order: 1 }, ao: { kind: "enter", order: 0 },
    });
  });

  it("refits the outline smoothly from where it was when an item joins or leaves, and not when the group is only moved", () => {
    const before = [group(), ...members, note("c", 600)];
    const joined = [group({ width: 632 }), ...members, note("c", 600, { parent: "g" })];
    expect(groupPlan(before, joined)).toEqual({ g: { kind: "refit", order: 0, from: { x: 84, y: 84, width: 332, height: 132 } } });
    expect(groupPlan(joined, before)).toEqual({ g: { kind: "refit", order: 0, from: { x: 84, y: 84, width: 632, height: 132 } } });
    const moved = [group({ x: 184 }), note("a", 200, { parent: "g" }), note("b", 400, { parent: "g" }), note("c", 600)];
    expect(groupPlan(before, moved)).toEqual({});
  });

  it("refits a frame that grew around a member or took one in or let one go, but not one I resize myself (J1)", () => {
    const frame = (extra: Partial<BoardItem> = {}): BoardItem => ({ id: "f", kind: "frame", x: 0, y: 0, width: 400, height: 300, z: 0, ...extra });
    const inside = [frame(), note("a", 100, { parent: "f" })];
    const grown = [frame({ width: 516 }), note("a", 400, { parent: "f" })];
    expect(groupPlan(inside, grown)).toEqual({ f: { kind: "refit", order: 0, from: { x: 0, y: 0, width: 400, height: 300 } } });
    // The member left: the frame keeps its size and lights up.
    expect(groupPlan(inside, [frame(), note("a", 500)])).toEqual({ f: { kind: "refit", order: 0, from: { x: 0, y: 0, width: 400, height: 300 } } });
    // My own resize follows the pointer: nothing replays it.
    expect(Object.fromEntries(planGroupMotion(byId(inside), byId([frame({ width: 600 }), inside[1]!]), new Set(["f"])))).toEqual({});
    expect(groupPlan(inside, [frame({ x: 50 }), note("a", 150, { parent: "f" })])).toEqual({});
  });

  it("stays out of the way on a board that has just opened", () => {
    expect(planGroupMotion(null, byId([group(), ...members])).size).toBe(0);
  });
});

function link(id: string, from: string, to: string): BoardItem {
  return { id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to };
}
