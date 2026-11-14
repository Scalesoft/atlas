// @vitest-environment node
import { describe, expect, it } from "vitest";
import { loadBatchesTogether } from "./searchPaging";
import { SEARCH_PAGE_SIZE } from "./searchRequest";

describe("loadBatchesTogether", () => {
  it("fetches every missing batch at consecutive offsets", async () => {
    const offsets: number[] = [];
    const found = await loadBatchesTogether(50, 50, 3 * SEARCH_PAGE_SIZE, async offset => { offsets.push(offset); return offset; });
    expect(offsets).toEqual([50, 100, 150]);
    expect(found).toEqual([50, 100, 150]);
  });
  it("leaves one batch or none to the one-by-one loading", async () => {
    const fetchBatch = async () => { throw new Error("must not fetch"); };
    expect(await loadBatchesTogether(50, 50, 60, fetchBatch)).toEqual([]);
    expect(await loadBatchesTogether(null, 50, 400, fetchBatch)).toEqual([]);
  });
});
