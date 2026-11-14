// @vitest-environment node
import { describe, expect, it } from "vitest";
import { cubicBezier, fitViewport, visibleObjects, wheelViewport, zoomAt, zoomStep } from "./boardViewport";

const wheel = (extra: Partial<Parameters<typeof wheelViewport>[1]> = {}) => ({ deltaX: 0, deltaY: 0, deltaMode: 0, ctrlKey: false, metaKey: false, shiftKey: false, ...extra });
const origin = { x: 0, y: 0, zoom: 1 };
/** The board point under a screen point. */
const under = (v: { x: number; y: number; zoom: number }, p: { x: number; y: number }) => ({ x: (p.x - v.x) / v.zoom, y: (p.y - v.y) / v.zoom });

describe("board navigation (interaction spec §2)", () => {
  it("pans with the wheel and two fingers, a line at a time as 16 px", () => {
    expect(wheelViewport(origin, wheel({ deltaX: 10, deltaY: 30 }), { x: 0, y: 0 })).toEqual({ x: -10, y: -30, zoom: 1 });
    expect(wheelViewport(origin, wheel({ deltaY: 3, deltaMode: 1 }), { x: 0, y: 0 })).toEqual({ x: 0, y: -48, zoom: 1 });
    // Shift turns a mouse wheel sideways.
    expect(wheelViewport(origin, wheel({ deltaY: 40, shiftKey: true }), { x: 0, y: 0 })).toEqual({ x: -40, y: 0, zoom: 1 });
  });

  it("zooms with Ctrl or ⌘ and the wheel, and with a pinch, keeping the point under the cursor in place", () => {
    const cursor = { x: 300, y: 200 };
    const start = { x: 40, y: -20, zoom: 1 };
    for (const event of [wheel({ deltaY: -100, ctrlKey: true }), wheel({ deltaY: -100, metaKey: true }), wheel({ deltaY: -4.5, ctrlKey: true })]) {
      const next = wheelViewport(start, event, cursor);
      expect(next.zoom).toBeGreaterThan(1);
      expect(under(next, cursor).x).toBeCloseTo(under(start, cursor).x);
      expect(under(next, cursor).y).toBeCloseTo(under(start, cursor).y);
    }
    // A mouse notch is a comfortable step, a pinch is no slower than the fingers.
    expect(wheelViewport(start, wheel({ deltaY: -100, ctrlKey: true }), cursor).zoom).toBeCloseTo(2 ** 0.2, 2);
    expect(wheelViewport(start, wheel({ deltaY: 100, ctrlKey: true }), cursor).zoom).toBeLessThan(1);
  });

  it("stays between 5 and 400 %", () => {
    expect(zoomAt(origin, 10, { x: 0, y: 0 }).zoom).toBe(4);
    expect(zoomAt(origin, 0.001, { x: 0, y: 0 }).zoom).toBe(0.05);
  });

  it("steps through round zoom levels with the buttons", () => {
    expect(zoomStep(1, 1)).toBe(1.25);
    expect(zoomStep(1, -1)).toBe(0.75);
    expect(zoomStep(0.8, -1)).toBe(0.75);
    expect(zoomStep(4, 1)).toBe(4);
    expect(zoomStep(0.05, -1)).toBe(0.05);
  });

  it("fits the content with a margin and opens a board at most at 100 %", () => {
    const fitted = fitViewport({ x: 0, y: 0, width: 2000, height: 1000 }, { width: 1096, height: 600 });
    expect(fitted.zoom).toBeCloseTo(0.5);
    expect(fitted).toMatchObject({ x: 48, y: 50 });
    expect(fitViewport({ x: 0, y: 0, width: 4000, height: 1000 }, { width: 1000, height: 600 }, { minZoom: 0.5 }).zoom).toBe(0.5);
    expect(fitViewport({ x: 100, y: 100, width: 200, height: 100 }, { width: 1000, height: 600 })).toEqual({ x: 300, y: 150, zoom: 1 });
    expect(fitViewport({ x: 0, y: 0, width: 200, height: 100 }, { width: 1000, height: 600 }, { maxZoom: 4 }).zoom).toBeGreaterThan(1);
    expect(fitViewport(null, { width: 1000, height: 600 })).toEqual({ x: 500, y: 300, zoom: 1 });
    expect(fitViewport({ x: 0, y: 0, width: 1e6, height: 1e6 }, { width: 1000, height: 600 }).zoom).toBe(0.05);
  });
});

describe("the travel curve of programmatic view changes (spec §5)", () => {
  it("follows cubic-bezier(.3, .2, .1, 1): starts at rest, ends at rest, monotonic", () => {
    const ease = cubicBezier(0.3, 0.2, 0.1, 1);
    expect(ease(0)).toBe(0);
    expect(ease(1)).toBe(1);
    expect(ease(0.05)).toBeCloseTo(0.0433, 3);
    expect(ease(0.5)).toBeGreaterThan(0.6);
    const samples = Array.from({ length: 21 }, (_, index) => ease(index / 20));
    expect(samples).toEqual([...samples].sort((a, b) => a - b));
  });
});

describe("visible objects", () => {
  const card = (id: string, x: number, target?: string, extra = {}) => ({ id, kind: "page" as const, x, y: 0, width: 100, height: 100, z: 1, targetResourceId: target, ...extra });
  it("names the objects of the items that overlap the visible area, each once", () => {
    const items = [card("a", 0, "P1"), card("b", 150, "p1"), card("c", 900, "p2"), card("d", 50), card("e", 50, "p3", { deleted: true })];
    expect(visibleObjects(items, { x: 80, y: 0, width: 400, height: 300 })).toEqual(["p1"]);
    expect(visibleObjects(items, { x: 0, y: 0, width: 2000, height: 300 })).toEqual(["p1", "p2"]);
    expect(visibleObjects(items, { x: 0, y: 500, width: 2000, height: 300 })).toEqual([]);
  });
});
