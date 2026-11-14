import { afterEach, describe, expect, it, vi } from "vitest";
import { foldAll, isFolded, onFoldChange, resetFoldStateForTests, setFolded } from "./foldState";

afterEach(() => {
  localStorage.clear();
  resetFoldStateForTests();
  vi.restoreAllMocks();
});

describe("fold state", () => {
  it("keeps folds by block id under one key and tells its listeners once per change", () => {
    const changed = vi.fn(), stop = onFoldChange(changed);
    setFolded("h1", true);
    foldAll(["r1", "r2", "h1", null, ""], true);
    expect(changed).toHaveBeenCalledTimes(2);
    expect(JSON.parse(localStorage.getItem("atlas.folds2")!)).toEqual(["h1", "r1", "r2"]);
    foldAll(["h1", "r1"], false);
    expect([isFolded("h1"), isFolded("r1"), isFolded("r2"), isFolded(null)]).toEqual([false, false, true, false]);
    stop();
  });

  it("moves the 3.5 headings in once as `page#id`; text keys, bare requirement ids and 3.6's bare folds had no page and go", () => {
    localStorage.setItem("atlas.headings.collapsed", JSON.stringify(["page-1#abc", "page-2#t:Úvod", "broken"]));
    localStorage.setItem("atlas.requirements.collapsed", JSON.stringify(["req-1"]));
    localStorage.setItem("atlas.folds", JSON.stringify(["abc"]));
    expect([isFolded("page-1#abc"), isFolded("abc"), isFolded("req-1"), isFolded("page-2#t:Úvod")]).toEqual([true, false, false, false]);
    expect(JSON.parse(localStorage.getItem("atlas.folds2")!)).toEqual(["page-1#abc"]);
    for (const old of ["atlas.headings.collapsed", "atlas.requirements.collapsed", "atlas.folds"]) expect(localStorage.getItem(old)).toBeNull();
    // Once only: what the reader opens later does not come back from the old keys.
    setFolded("page-1#abc", false);
    resetFoldStateForTests();
    expect(isFolded("page-1#abc")).toBe(false);
  });

  it("lasts as long as the page without storage", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    setFolded("x", true);
    expect(isFolded("x")).toBe(true);
  });
});
