// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { Geometry } from "./boardApi";
import { snapMove, snapResize } from "./boardSnap";

const box = (x: number, y: number, width = 100, height = 60): Geometry => ({ x, y, width, height });

describe("alignment guides and snapping (interaction spec §3.2)", () => {
  it("snaps an edge to the same edge of a neighbour within the threshold and shows the guide through both", () => {
    const result = snapMove(box(304, 200), [box(300, 0, 300)], 6);
    expect(result).toMatchObject({ dx: -4, dy: 0 });
    expect(result.guides).toEqual([{ axis: "x", at: 300, from: 0, to: 260, kind: "align" }]);
  });

  it("snaps centres and the opposite edges too, and each axis on its own", () => {
    // Centre x 355 against 350; bottom 263 against top 265 of the item below.
    const result = snapMove(box(305, 203), [box(250, 0, 200), box(600, 265)], 6);
    expect(result).toMatchObject({ dx: -5, dy: 2 });
    expect(result.guides.map(guide => [guide.axis, guide.at])).toEqual([["x", 350], ["y", 265]]);
  });

  it("does nothing beyond the threshold", () => {
    expect(snapMove(box(307, 207), [box(300, 0), box(600, 0, 100, 200)], 6)).toEqual({ dx: 0, dy: 0, guides: [] });
  });

  it("takes the nearest of several candidates", () => {
    expect(snapMove(box(103, 500), [box(100, 0), box(105, 900)], 6).dx).toBe(2);
  });

  it("spaces an item evenly between its two neighbours on a row", () => {
    // Left neighbour ends at 100, right one starts at 400: a 100 wide item sits evenly at 200 (gaps of 100).
    const result = snapMove(box(203, 10), [box(0, 0), box(400, 20)], 6);
    expect(result.dx).toBe(-3);
    expect(result.guides.filter(guide => guide.kind === "spacing")).toEqual([
      { axis: "x", at: 40, from: 100, to: 200, kind: "spacing" },
      { axis: "x", at: 40, from: 300, to: 400, kind: "spacing" },
    ]);
  });

  it("continues a row with the gap it already has", () => {
    // Two items 50 apart (100..150); the third snaps 50 after the second.
    const result = snapMove(box(302, 0), [box(0, 0), box(150, 0)], 6);
    expect(result.dx).toBe(-2);
    expect(result.guides.filter(guide => guide.kind === "spacing").map(guide => [guide.from, guide.to])).toEqual([[100, 150], [250, 300]]);
  });

  it("spaces in columns as in rows, and ignores items that share no row or column with the moved one", () => {
    expect(snapMove(box(0, 203), [box(0, 0), box(0, 400)], 6).dy).toBe(-3);
    // Far off to the side: no common column, no spacing, no snap.
    expect(snapMove(box(0, 203), [box(900, 0), box(900, 400)], 6)).toEqual({ dx: 0, dy: 0, guides: [] });
  });
});

describe("snapping to the grid (interaction spec §3.7)", () => {
  it("puts the top-left corner of a moved box on the grid", () => {
    expect(snapMove(box(13, 29), [], 6, { step: 24 })).toMatchObject({ dx: 11, dy: -5 });
  });

  it("lets a guide to a neighbour win on its axis, the grid takes the other", () => {
    expect(snapMove(box(303, 29), [box(300, 500, 300)], 6, { step: 24 })).toMatchObject({ dx: -3, dy: -5 });
  });

  it("snaps to the divisions of the timeline on x when it has them", () => {
    expect(snapMove(box(37, 0), [], 6, { step: 24, xTicks: [0, 31, 59] })).toMatchObject({ dx: -6, dy: 0 });
  });

  it("puts a resized box's corner and size on the grid, at least one step large", () => {
    expect(snapResize(box(13, 29, 101, 50), 24)).toEqual({ x: 24, y: 24, width: 96, height: 48 });
    expect(snapResize(box(0, 0, 5, 5), 24)).toEqual({ x: 0, y: 0, width: 24, height: 24 });
  });
});
