// @vitest-environment node
import { describe, expect, it } from "vitest";
import {
  readTreeExpansion,
  revealTreeAncestors,
  treeExpansionKey,
  writeTreeExpansion,
} from "./treeExpansion";

class MemoryStorage {
  readonly values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
}
const empty = () => ({ openedSpaces: new Set(), collapsedPages: new Set() });

describe("tree expansion preference", () => {
  it("round-trips bounded identifiers under an account-specific versioned key", () => {
    const storage = new MemoryStorage();
    writeTreeExpansion(storage, "user/one", {
      openedSpaces: new Set(["space-1", "space-2", "bad\nspace"]),
      collapsedPages: new Set(["page-1", "bad\npage", ...Array.from({ length: 600 }, (_, index) => `page-${index + 2}`)]),
    });

    expect(treeExpansionKey("user/one")).not.toBe(treeExpansionKey("user/two"));
    expect(readTreeExpansion(storage, "user/two")).toEqual(empty());
    const restored = readTreeExpansion(storage, "user/one");
    expect(restored.openedSpaces).toEqual(new Set(["space-1", "space-2"]));
    expect(restored.collapsedPages.has("page-1")).toBe(true);
    expect(restored.collapsedPages.has("bad\npage")).toBe(false);
    expect(restored.collapsedPages.size).toBeLessThanOrEqual(500);
  });

  it("opens the space the single-space sidebar remembered and keeps its collapsed pages", () => {
    const storage = new MemoryStorage();
    storage.values.set(treeExpansionKey("user-1", 1), JSON.stringify({ version: 1, lastSpaceId: "space-1", collapsedPages: ["page-1"] }));
    expect(readTreeExpansion(storage, "user-1")).toEqual({ openedSpaces: new Set(["space-1"]), collapsedPages: new Set(["page-1"]) });
    storage.values.set(treeExpansionKey("user-2", 1), JSON.stringify({ version: 1, lastSpaceId: "bad\nspace", collapsedPages: ["page-1"] }));
    expect(readTreeExpansion(storage, "user-2")).toEqual({ openedSpaces: new Set(), collapsedPages: new Set(["page-1"]) });
    // Once the account saved the new shape, the old entry no longer counts.
    writeTreeExpansion(storage, "user-1", { openedSpaces: new Set(), collapsedPages: new Set() });
    expect(readTreeExpansion(storage, "user-1")).toEqual(empty());
  });

  it("falls back to empty state for corrupt or unavailable storage", () => {
    const corrupt = new MemoryStorage();
    corrupt.values.set(treeExpansionKey("user-1"), "{not json");
    expect(readTreeExpansion(corrupt, "user-1")).toEqual(empty());
    const denied = { getItem: () => { throw new DOMException("denied"); }, setItem: () => { throw new DOMException("denied"); } };
    expect(readTreeExpansion(denied, "user-1")).toEqual(empty());
    expect(() => writeTreeExpansion(denied, "user-1", { openedSpaces: new Set(["space-1"]), collapsedPages: new Set() })).not.toThrow();
  });

  it("reveals only the known ancestor chain and stops at a cycle", () => {
    const pages = [
      { id: "root", parentId: null },
      { id: "parent", parentId: "root" },
      { id: "child", parentId: "parent" },
      { id: "cycle-a", parentId: "cycle-b" },
      { id: "cycle-b", parentId: "cycle-a" },
    ];
    expect(revealTreeAncestors(pages, "child", new Set(["root", "parent", "elsewhere"]))).toEqual(new Set(["elsewhere"]));
    expect(revealTreeAncestors(pages, "cycle-a", new Set(["cycle-a", "cycle-b", "elsewhere"]))).toEqual(new Set(["cycle-a", "elsewhere"]));
  });
});
