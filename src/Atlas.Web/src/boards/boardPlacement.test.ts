// @vitest-environment node
import { describe, expect, it } from "vitest";
import { freeSpot, nearestInDirection, readingOrder } from "./boardPlacement";

const size = { width: 200, height: 140 };
const at = (x: number, y: number) => ({ x, y, ...size });
const visible = { x: 0, y: 0, width: 1000, height: 600 };

describe("free spot for a new item", () => {
  it("is the wanted spot when nothing sits there", () => {
    expect(freeSpot(at(400, 230), [at(0, 0), at(700, 400)], visible)).toEqual(at(400, 230));
  });

  it("moves down and to the right by a step for each item already on the spot, until one is free", () => {
    const others = [at(400, 230), at(424, 254), at(448, 278)];
    expect(freeSpot(at(400, 230), others, visible)).toEqual(at(472, 302));
    expect(freeSpot(at(400, 230), others.slice(0, 1), visible)).toEqual(at(424, 254));
  });

  it("counts an item that is a few pixels off as being on the spot, and one a step away as not", () => {
    expect(freeSpot(at(400, 230), [at(405, 226)], visible)).toEqual(at(424, 254));
    expect(freeSpot(at(400, 230), [at(424, 230)], visible)).toEqual(at(400, 230));
  });

  it("keeps the item inside the visible area, and falls back to the wanted spot when the diagonal is full", () => {
    const wanted = at(700, 400);
    // The bottom edge (600) allows 400 + k * 24 + 140 <= 600, so two steps; the right edge would allow more.
    const others = [at(700, 400), at(724, 424)];
    expect(freeSpot(wanted, others, visible)).toEqual(at(748, 448));
    expect(freeSpot(wanted, [...others, at(748, 448)], visible)).toEqual(wanted);
  });

  it("follows a visible area that is not at the origin", () => {
    const moved = { x: -500, y: -300, width: 1000, height: 600 };
    expect(freeSpot(at(-100, -70), [at(-100, -70)], moved)).toEqual(at(-76, -46));
  });
});

describe("keyboard order and direction (J1)", () => {
  const box = (id: string, x: number, y: number, width = 100, height = 100) => ({ id, x, y, width, height });

  it("reads items row by row, left to right, a row taking what starts within half its height", () => {
    const items = [box("c", 400, 10), box("a", 0, 0), box("b", 200, 30), box("d", 0, 200), box("e", 220, 180), box("frame", -40, -80, 800, 60)];
    expect(readingOrder(items)).toEqual(["frame", "a", "b", "c", "d", "e"]);
  });

  it("finds the nearest item in a direction, preferring the one in line over a closer diagonal one", () => {
    const from = box("s", 0, 0);
    const items = [from, box("right", 300, 0), box("diagonal", 160, 220), box("far", 900, 0), box("up", 0, -300)];
    expect(nearestInDirection(from, items, "right")).toBe("right");
    expect(nearestInDirection(from, items, "up")).toBe("up");
    expect(nearestInDirection(from, items, "down")).toBe("diagonal");
    expect(nearestInDirection(from, items, "left")).toBeUndefined();
  });
});
