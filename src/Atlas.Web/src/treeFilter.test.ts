// @vitest-environment node
import { describe, expect, it } from "vitest";
import { filterTree } from "./treeFilter";
import type { TreePage } from "./treeMove";

const node = (id: string, title: string, parentId: string | null = null, extra: Partial<TreePage> = {}): TreePage =>
  ({ id, parentId, title, spaceId: "s", version: 1, isPublished: true, canEdit: true, ...extra });
const pages = [
  node("a", "Architektura"), node("a1", "Databáze", "a"), node("a2", "Síť", "a"), node("a21", "Firewall", "a2"),
  node("b", "Provoz"), node("r", "Kontakty", "b", { type: "reference" }), node("x", "", "b", { type: "reference", readable: false }),
];

describe("filterTree", () => {
  it("matches titles without accents and case, in the tree's order, with their ancestors", () => {
    const filter = filterTree(pages, "sit", new Set())!;
    expect(filter.matches.map(page => page.id)).toEqual(["a2"]);
    expect([...filter.hidden].sort()).toEqual(["a1", "a21", "b", "r", "x"]);
    expect(filterTree(pages, "FIRE", new Set())!.matches.map(page => page.id)).toEqual(["a21"]);
  });

  it("unfolds only the way to a match and keeps every other folded branch for when it closes", () => {
    const filter = filterTree(pages, "firewall", new Set(["a", "a2", "b"]))!;
    expect([...filter.collapsed]).toEqual(["b"]);
    expect(filter.hidden.has("a")).toBe(false);
  });

  it("finds reference nodes by their target's title but never an unreadable one, and is off for an empty query", () => {
    expect(filterTree(pages, "kontakt", new Set())!.matches.map(page => page.id)).toEqual(["r"]);
    expect(filterTree(pages, "zzz", new Set())!.matches).toEqual([]);
    expect(filterTree(pages, "  ", new Set())).toBeNull();
  });

  it("matches the words of a title, not its Markdown marks", () => {
    const marked = [node("m", "**Rozpočet** a [web](https://example.cz)")];
    expect(filterTree(marked, "rozpočet a web", new Set())!.matches.map(page => page.id)).toEqual(["m"]);
    expect(filterTree(marked, "example", new Set())!.matches).toEqual([]);
  });
});
