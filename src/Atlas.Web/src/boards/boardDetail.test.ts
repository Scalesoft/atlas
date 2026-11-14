// @vitest-environment node
import { describe, expect, it } from "vitest";
import { relationsOf } from "./boardDetail";
import type { BoardViewItem } from "./boardSession";

const card = (id: string, target: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "page", x: 0, y: 0, width: 280, height: 140, z: 1, targetResourceId: target, ...extra });
const note = (id: string): BoardViewItem => ({ id, kind: "note", x: 0, y: 0, width: 200, height: 200, z: 1, text: id });
const link = (id: string, from: string, to: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to, ...extra });

describe("object detail on a board (objects spec §6)", () => {
  const items = [card("p", "page-1"), card("q", "page-1"), card("r", "page-2"), note("n"),
    link("out", "p", "n", { text: "závisí na", props: { state: "waiting" } }), link("in", "r", "p"), link("other", "n", "r"), link("gone", "p", "x", { deleted: true })];

  it("lists a card's relations on the board: where they lead, the other end, the label and the state", () => {
    expect(relationsOf(items, "p")).toEqual([
      { id: "out", direction: "out", other: items[3], label: "závisí na", state: "waiting" },
      { id: "in", direction: "in", other: items[2], label: "", state: undefined },
    ]);
    expect(relationsOf(items, "missing")).toEqual([]);
  });

});
