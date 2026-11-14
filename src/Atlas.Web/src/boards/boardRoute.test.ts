// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { Geometry } from "./boardApi";
import { boxesIn, parallelOffsets, pointAtHalf, roundedPath, routeElbow, shiftPort, type Port } from "./boardRoute";

type Point = { x: number; y: number };
/** Whether a segment runs through the inside of a box (touching its outline is not crossing it). */
const crosses = (a: Point, b: Point, box: Geometry) => {
  const [x0, x1] = [Math.min(a.x, b.x), Math.max(a.x, b.x)], [y0, y1] = [Math.min(a.y, b.y), Math.max(a.y, b.y)];
  return x1 > box.x && x0 < box.x + box.width && y1 > box.y && y0 < box.y + box.height;
};
const segments = (points: readonly Point[]) => points.slice(1).map((point, index) => [points[index]!, point] as const);
const orthogonal = (points: readonly Point[]) => segments(points).every(([a, b]) => a.x === b.x || a.y === b.y);

describe("orthogonal connector routes", () => {
  const from: Port = { x: 200, y: 50, side: "right" }, to: Port = { x: 600, y: 250, side: "left" };

  it("leaves and enters at the facing sides, in right angles only, with at most two bends when nothing is in the way", () => {
    const points = routeElbow(from, to, []);
    expect(points[0]).toEqual({ x: 200, y: 50 });
    expect(points.at(-1)).toEqual({ x: 600, y: 250 });
    expect(orthogonal(points)).toBe(true);
    expect(points[1]!.x).toBeGreaterThan(200);
    expect(points[1]!.y).toBe(50);
    expect(points.at(-2)!.x).toBeLessThan(600);
    expect(points.at(-2)!.y).toBe(250);
    expect(points.length - 2).toBeLessThanOrEqual(2);
  });

  it("goes around a card in the way instead of through it", () => {
    const wall: Geometry = { x: 330, y: 0, width: 140, height: 400 };
    const points = routeElbow(from, to, [wall]);
    expect(orthogonal(points)).toBe(true);
    expect(segments(points).some(([a, b]) => crosses(a, b, wall))).toBe(false);
  });

  it("never runs back through its own ends when they face away from each other", () => {
    const source: Geometry = { x: 0, y: 0, width: 200, height: 100 }, target: Geometry = { x: 400, y: 0, width: 200, height: 100 };
    const points = routeElbow({ x: 200, y: 50, side: "right" }, { x: 600, y: 50, side: "right" }, [source, target]);
    expect(orthogonal(points)).toBe(true);
    // The stubs out of the ports are the only part inside the ends' margins.
    expect(segments(points).slice(1, -1).some(([a, b]) => crosses(a, b, source) || crosses(a, b, target))).toBe(false);
  });

  it("keeps clear of every card's margin among many cards, wherever its ends are", () => {
    // Cards on a lattice with gaps wider than two margins, so a way always exists; the ends sit in the gaps.
    let seed = 7;
    const random = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
    const cards: Geometry[] = [];
    for (let column = 0; column < 8; column++) for (let row = 0; row < 6; row++) if (random() < 0.7) cards.push({ x: column * 200 + random() * 20, y: row * 200 + random() * 20, width: 100, height: 80 });
    const sides = ["top", "right", "bottom", "left"] as const;
    const margin = 16, inflated = cards.map(card => ({ x: card.x - margin, y: card.y - margin, width: card.width + 2 * margin, height: card.height + 2 * margin }));
    for (let index = 0; index < 40; index++) {
      const port = (): Port => ({ x: Math.floor(random() * 8) * 200 + 160, y: Math.floor(random() * 6) * 200 + 160, side: sides[Math.floor(random() * 4)]! });
      const points = routeElbow(port(), port(), cards, { margin });
      expect(orthogonal(points)).toBe(true);
      expect(segments(points).some(([a, b]) => inflated.some(box => crosses(a, b, box)))).toBe(false);
    }
  });

  it("falls back to a plain elbow when the cards leave no way through", () => {
    const boxed: Geometry = { x: -10_000, y: -10_000, width: 20_000, height: 20_000 };
    const points = routeElbow(from, to, [boxed]);
    expect(points[0]).toEqual({ x: 200, y: 50 });
    expect(points.at(-1)).toEqual({ x: 600, y: 250 });
    expect(orthogonal(points)).toBe(true);
  });
});

describe("drawing a route", () => {
  it("rounds each corner no further than half of the shorter segment beside it", () => {
    expect(roundedPath([{ x: 0, y: 0 }, { x: 100, y: 0 }])).toBe("M0 0L100 0");
    expect(roundedPath([{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }], 12)).toBe("M0 0L88 0Q100 0 100 12L100 100");
    expect(roundedPath([{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 100 }], 12)).toBe("M0 0L5 0Q10 0 10 5L10 100");
  });

  it("never writes NaN for a doubled point, as when the cards are not measured yet", () => {
    expect(roundedPath([{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }], 12)).not.toContain("NaN");
    expect(roundedPath([{ x: 5, y: 5 }, { x: 5, y: 5 }, { x: 5, y: 5 }])).not.toContain("NaN");
  });

  it("puts the label halfway along the line, not at a corner", () => {
    expect(pointAtHalf([{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }])).toEqual({ x: 100, y: 0 });
    expect(pointAtHalf([{ x: 0, y: 0 }, { x: 300, y: 0 }, { x: 300, y: 100 }])).toEqual({ x: 200, y: 0 });
  });
});

describe("parallel connectors", () => {
  it("spreads connectors between the same two items around the middle, in either direction", () => {
    const offsets = parallelOffsets([
      { id: "a", source: "x", target: "y" }, { id: "b", source: "y", target: "x" }, { id: "c", source: "x", target: "y" }, { id: "d", source: "x", target: "z" },
    ], 14);
    expect(offsets.get("a")).toBe(-14);
    expect(offsets.get("b")).toBe(0);
    expect(offsets.get("c")).toBe(14);
    expect(offsets.has("d")).toBe(false);
  });

  it("moves a port along its side", () => {
    expect(shiftPort({ x: 10, y: 20, side: "right" }, 5)).toEqual({ x: 10, y: 25, side: "right" });
    expect(shiftPort({ x: 10, y: 20, side: "top" }, 5)).toEqual({ x: 15, y: 20, side: "top" });
  });
});

describe("boxesIn", () => {
  it("finds the same boxes in the same order as filtering all of them", () => {
    let seed = 7;
    const random = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
    const boxes = Array.from({ length: 400 }, (_, index) => ({ id: `b${index}`, x: Math.round(random() * 6000 - 3000), y: Math.round(random() * 6000 - 3000),
      width: index % 50 ? Math.round(20 + random() * 600) : 4000, height: Math.round(20 + random() * 400) }));
    // Sides exactly on a cell's border (512), negative coordinates and areas of every size.
    boxes.push({ id: "border", x: 512, y: -1024, width: 512, height: 512 });
    type Area = Point & { right: number; bottom: number };
    const overlaps = (area: Area) => (box: Geometry) => box.x < area.right && box.x + box.width > area.x && box.y < area.bottom && box.y + box.height > area.y;
    for (let query = 0; query < 300; query++) {
      const x = random() * 7000 - 3500, y = random() * 7000 - 3500, area = { x, y, right: x + random() * 2000, bottom: y + random() * 2000 };
      expect(boxesIn(boxes, area)).toEqual(boxes.filter(overlaps(area)));
    }
    expect(boxesIn(boxes, { x: 1024, y: -1024, right: 1034, bottom: -1014 })).not.toContainEqual(expect.objectContaining({ id: "border" }));
    expect(boxesIn(boxes, { x: 1014, y: -1024, right: 1024, bottom: -1014 })).toContainEqual(expect.objectContaining({ id: "border" }));
  });
});
