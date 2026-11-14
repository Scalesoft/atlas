// @vitest-environment node
import { describe, expect, it } from "vitest";
import { layerOf, layoutLayers, type LayerNode } from "./layoutLayers";

const node = (id: string, width = 100, height = 40): LayerNode => ({ id, width, height });

describe("layerOf", () => {
  it("puts a node one layer after the last node that leads into it", () => {
    const layers = layerOf(["a", "b", "c", "d"].map(id => node(id)), [{ from: "a", to: "b" }, { from: "b", to: "c" }, { from: "a", to: "c" }]);
    expect([...layers]).toEqual([["a", 0], ["b", 1], ["c", 2], ["d", 0]]);
  });

  it("survives a cycle, a self loop and an edge to an unknown node", () => {
    const layers = layerOf(["a", "b", "c"].map(id => node(id)), [{ from: "a", to: "b" }, { from: "b", to: "c" }, { from: "c", to: "a" }, { from: "a", to: "a" }, { from: "a", to: "zzz" }]);
    expect([layers.get("a"), layers.get("b"), layers.get("c")]).toEqual([0, 1, 2]);
  });
});

describe("layoutLayers", () => {
  it("places layers left to right with a gap and centres a short layer against the tallest", () => {
    const { positions, width, height, layers } = layoutLayers([node("a"), node("b"), node("c")], [{ from: "a", to: "b" }, { from: "a", to: "c" }], { gapLayer: 50, gapNode: 20 });
    expect(layers).toEqual([["a"], ["b", "c"]]);
    expect(positions.get("a")).toEqual({ x: 0, y: 30 });
    expect(positions.get("b")).toEqual({ x: 150, y: 0 });
    expect(positions.get("c")).toEqual({ x: 150, y: 60 });
    expect([width, height]).toEqual([250, 100]);
  });

  it("wraps a wide layer into columns so that it stays a block", () => {
    const leaves = Array.from({ length: 10 }, (_, index) => node(`n${index}`));
    const { positions, layers } = layoutLayers([node("hub"), ...leaves], leaves.map(leaf => ({ from: "hub", to: leaf.id })), { wrap: 4, gapLayer: 60, gapNode: 20 });
    expect(layers[1]).toHaveLength(10);
    expect(new Set(leaves.map(leaf => positions.get(leaf.id)!.x)).size).toBe(3);
  });

  it("keeps the order of the layer before: the neighbour of the upper node stays above", () => {
    const { positions } = layoutLayers([node("a"), node("b"), node("x"), node("y")], [{ from: "a", to: "y" }, { from: "b", to: "x" }]);
    expect(positions.get("y")!.y).toBeLessThan(positions.get("x")!.y);
  });

  it("is empty for no nodes", () => {
    expect(layoutLayers([], [])).toMatchObject({ width: 0, height: 0, layers: [] });
  });
});
