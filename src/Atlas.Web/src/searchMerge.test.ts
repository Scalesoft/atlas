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
  it("falls back to the new ranking when the selected row is gone", () => {
    expect(ids(mergeSearchResults([row("a"), row("b")], [row("c"), row("a")], "b", key))).toEqual(["c", "a"]);
  });
});
