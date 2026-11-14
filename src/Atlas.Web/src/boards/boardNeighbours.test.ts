// @vitest-environment node
import { describe, expect, it } from "vitest";
import { linkOf, neighbourhood, pathBetween, type GraphLink } from "./boardNeighbours";

// a → b → c → d, e → b, and an unrelated x → y.
const links: GraphLink[] = [
  { id: "ab", from: "a", to: "b" }, { id: "bc", from: "b", to: "c" }, { id: "cd", from: "c", to: "d" },
  { id: "eb", from: "e", to: "b" }, { id: "xy", from: "x", to: "y" },
];
const entries = <K, V>(map: ReadonlyMap<K, V>) => Object.fromEntries(map as Map<K, V>);

describe("neighbour highlighting (objects spec §5.3)", () => {
  it("marks the direct relations of the hovered node: what leads into it up, what it leads to down", () => {
    const shown = neighbourhood(links, "b", false);
    expect(entries(shown.nodes)).toEqual({ b: "self", a: "up", e: "up", c: "down" });
    expect(entries(shown.links)).toEqual({ ab: "up", eb: "up", bc: "down" });
  });

  it("follows the whole chain both ways with Shift", () => {
    const shown = neighbourhood(links, "c", true);
    expect(entries(shown.nodes)).toEqual({ c: "self", b: "up", a: "up", e: "up", d: "down" });
    expect(entries(shown.links)).toEqual({ bc: "up", ab: "up", eb: "up", cd: "down" });
  });

  it("ends in a cycle and marks a node that is both up and down as up", () => {
    const cycle: GraphLink[] = [{ id: "pq", from: "p", to: "q" }, { id: "qp", from: "q", to: "p" }, { id: "qr", from: "q", to: "r" }];
    const shown = neighbourhood(cycle, "p", true);
    expect(entries(shown.nodes)).toEqual({ p: "self", q: "up", r: "down" });
  });
});

describe("path focus", () => {
  it("highlights the shortest chain between two selected nodes, whatever the direction of its links", () => {
    const shown = pathBetween(links, "a", "e")!;
    expect(entries(shown.nodes)).toEqual({ a: "path", b: "path", e: "path" });
    expect(entries(shown.links)).toEqual({ ab: "path", eb: "path" });
  });

  it("finds nothing between unconnected nodes", () => {
    expect(pathBetween(links, "a", "x")).toBeNull();
    expect(pathBetween(links, "a", "a")).toBeNull();
  });
});

describe("a connector as a directed link", () => {
  it("points from its start to its end unless only its start has the arrow", () => {
    expect(linkOf({ id: "l", from: "a", to: "b", props: { arrowStart: "none", arrowEnd: "arrow" } })).toEqual({ id: "l", from: "a", to: "b" });
    expect(linkOf({ id: "l", from: "a", to: "b", props: { arrowStart: "arrow", arrowEnd: "none" } })).toEqual({ id: "l", from: "b", to: "a" });
    expect(linkOf({ id: "l", from: "a", to: "b", props: { arrowStart: "arrow", arrowEnd: "arrow" } })).toEqual({ id: "l", from: "a", to: "b" });
  });
});
