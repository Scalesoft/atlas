// @vitest-environment node
import { describe, expect, it } from "vitest";
import { isBoardNode, pageAncestors, resolveTreeInsert, resolveTreeMove, treePlaceOf, visibleTreeOrder, type TreePage } from "./treeMove";
const pages: TreePage[] = [
  { id:"a", parentId:null }, { id:"b", parentId:null }, { id:"c", parentId:null },
  { id:"a1", parentId:"a" }, { id:"a2", parentId:"a1" },
].map(page => ({ ...page, spaceId:"s", title:page.id, version:1, isPublished:true, canEdit:true }));
describe("tree move destination", () => {
  it("resolves sibling placement, nesting and moving back to the space root", () => {
    expect(resolveTreeMove(pages,"c",{targetId:"a",position:"before"})).toEqual({parentId:null,beforePageId:"a"});
    expect(resolveTreeMove(pages,"a",{targetId:"b",position:"after"})).toEqual({parentId:null,beforePageId:"c"});
    expect(resolveTreeMove(pages,"b",{targetId:"a",position:"inside"})).toEqual({parentId:"a",beforePageId:null});
    expect(resolveTreeMove(pages,"a1",{targetId:null,position:"inside"})).toEqual({parentId:null,beforePageId:null});
  });
  it("rejects cycles, self, unchanged positions and unavailable edit rights", () => {
    expect(resolveTreeMove(pages,"a",{targetId:"a2",position:"inside"})).toBeUndefined();
    expect(resolveTreeMove(pages,"a",{targetId:"a",position:"after"})).toBeUndefined();
    expect(resolveTreeMove(pages,"a",{targetId:"b",position:"before"})).toBeUndefined();
    expect(resolveTreeMove(pages,"c",{targetId:null,position:"inside"})).toBeUndefined();
    expect(resolveTreeMove(pages.map(p=>({...p,canEdit:false})),"b",{targetId:"a",position:"inside"})).toBeUndefined();
    expect(resolveTreeMove(pages,"a",{targetId:"unknown",position:"inside"})).toBeUndefined();
  });
  it("builds the accessible ancestor chain without looping through corrupt data", () => {
    expect(pageAncestors(pages,"a2").map(page=>page.id)).toEqual(["a","a1"]);
    expect(pageAncestors(pages.filter(page=>page.id!=="a"),"a2").map(page=>page.id)).toEqual(["a1"]);
    expect(pageAncestors([{...pages[0]!,parentId:"a2"},...pages.slice(1)],"a2").map(page=>page.id)).toEqual(["a","a1"]);
  });
});

describe("visibleTreeOrder", () => {
  const node = (id: string, parentId: string | null = null): TreePage =>
    ({ id, parentId, spaceId: "s", title: id, version: 1, isPublished: true, canEdit: true });

  it("walks the tree depth first, parents before their children", () => {
    const pages = [node("a"), node("a1", "a"), node("a2", "a"), node("b")];
    expect(visibleTreeOrder(pages, new Set()).map(p => p.id)).toEqual(["a", "a1", "a2", "b"]);
  });

  it("skips the subtree under a collapsed node", () => {
    const pages = [node("a"), node("a1", "a"), node("a1x", "a1"), node("b")];
    expect(visibleTreeOrder(pages, new Set(["a"])).map(p => p.id)).toEqual(["a", "b"]);
  });

  it("hides deeper descendants when an inner node is collapsed", () => {
    const pages = [node("a"), node("a1", "a"), node("a1x", "a1")];
    expect(visibleTreeOrder(pages, new Set(["a1"])).map(p => p.id)).toEqual(["a", "a1"]);
  });

  it("ignores a page whose parent is not in the list", () => {
    // Tree slices arrive per space; an orphan must not silently become a root.
    const pages = [node("a"), node("orphan", "missing")];
    expect(visibleTreeOrder(pages, new Set()).map(p => p.id)).toEqual(["a"]);
  });
});

describe("boards in the tree", () => {
  const node = (id: string, parentId: string | null = null, type: "page" | "board" = "page"): TreePage =>
    ({ id, parentId, spaceId: "s", title: id, version: type === "board" ? 0 : 1, isPublished: true, canEdit: true, type });
  const mixed = [node("a"), node("b1", "a", "board"), node("a1", "a"), node("b2", null, "board"), node("c")];

  it("moves a board between pages and boards, under a page and back to the root", () => {
    expect(resolveTreeMove(mixed, "b2", { targetId: "a", position: "before" })).toEqual({ parentId: null, beforePageId: "a" });
    expect(resolveTreeMove(mixed, "b2", { targetId: "a", position: "inside" })).toEqual({ parentId: "a", beforePageId: null });
    expect(resolveTreeMove(mixed, "b1", { targetId: null, position: "inside" })).toEqual({ parentId: null, beforePageId: null });
    // A page placed before a board: the board's id is the sibling the server orders by.
    expect(resolveTreeMove(mixed, "c", { targetId: "b2", position: "before" })).toEqual({ parentId: null, beforePageId: "b2" });
    expect(resolveTreeMove(mixed, "a1", { targetId: "b1", position: "before" })).toEqual({ parentId: "a", beforePageId: "b1" });
    expect(resolveTreeMove(mixed, "b1", { targetId: "a1", position: "before" })).toBeUndefined();
  });

  it("never puts anything inside a board: boards are leaves", () => {
    expect(resolveTreeMove(mixed, "c", { targetId: "b2", position: "inside" })).toBeUndefined();
    expect(resolveTreeMove(mixed, "b1", { targetId: "b2", position: "inside" })).toBeUndefined();
  });

  it("treats a reference node as a leaf and places what comes from outside by the same gate", () => {
    const withReference = [...mixed, node("r", "a", "page")].map(item => item.id === "r" ? { ...item, type: "reference" as const } : item);
    expect(resolveTreeMove(withReference, "c", { targetId: "r", position: "inside" })).toBeUndefined();
    expect(resolveTreeInsert(withReference, { targetId: "r", position: "inside" })).toBeUndefined();
    expect(resolveTreeInsert(withReference, { targetId: "a", position: "inside" })).toEqual({ parentId: "a", beforePageId: null });
    expect(resolveTreeInsert(withReference, { targetId: "a1", position: "before" })).toEqual({ parentId: "a", beforePageId: "a1" });
    expect(resolveTreeInsert(withReference, { targetId: "c", position: "after" })).toEqual({ parentId: null, beforePageId: null });
    expect(resolveTreeInsert(withReference, { targetId: null, position: "inside" })).toEqual({ parentId: null, beforePageId: null });
    expect(resolveTreeInsert(withReference.map(item => ({ ...item, canEdit: false })), { targetId: "a", position: "inside" })).toBeUndefined();
  });

  it("keeps a board's ancestors walkable and orders it among its page siblings", () => {
    expect(pageAncestors(mixed, "b1").map(page => page.id)).toEqual(["a"]);
    expect(visibleTreeOrder(mixed, new Set()).map(page => page.id)).toEqual(["a", "b1", "a1", "b2", "c"]);
    expect(isBoardNode(mixed[1]!)).toBe(true);
    expect(isBoardNode(mixed[0]!)).toBe(false);
  });
});

describe("treePlaceOf", () => {
  it("names the parent and the next sibling, which is what puts a node back", () => {
    expect(treePlaceOf(pages, "a")).toEqual({ parentId: null, beforePageId: "b" });
    expect(treePlaceOf(pages, "c")).toEqual({ parentId: null, beforePageId: null });
    expect(treePlaceOf(pages, "a1")).toEqual({ parentId: "a", beforePageId: null });
  });
});
