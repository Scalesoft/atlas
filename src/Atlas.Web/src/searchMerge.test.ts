// @vitest-environment node
import { describe, expect, it } from "vitest";
import { mergeSearchResults } from "./searchMerge";

const row = (id: string, extra = "") => ({ id, extra });
const ids = (rows: { id: string }[]) => rows.map(r => r.id);
const key = (r: { id: string }) => r.id;

describe("mergeSearchResults", () => {
  it("keeps the selected row in place and inserts new results below it", () => {
    const shown = [row("a"), row("b"), row("c")];
    const incoming = [row("n1"), row("c", "full"), row("a"), row("b"), row("n2")];
    const merged = mergeSearchResults(shown, incoming, "b", key);
    expect(ids(merged)).toEqual(["a", "b", "n1", "c", "n2"]);
    expect(merged[1]).toBe(incoming[3]);
  });
  it("reorders freely before anything is selected", () => {
    const incoming = [row("n1"), row("c"), row("a")];
    expect(ids(mergeSearchResults([row("a"), row("b")], incoming, undefined, key))).toEqual(["n1", "c", "a"]);
  });
  it("keeps the row under the cursor when the full pass left it out of its first page", () => {
    // „luboš krbec“: the preview's contact page must not vanish when pages found only by meaning fill the full pass.
    expect(ids(mergeSearchResults([row("a"), row("b")], [row("c"), row("a")], "b", key))).toEqual(["a", "b", "c"]);
  });
  it("takes the new ranking when nothing shown is under the cursor", () => {
    expect(ids(mergeSearchResults([row("a"), row("b")], [row("c"), row("a")], "x", key))).toEqual(["c", "a"]);
  });
});
