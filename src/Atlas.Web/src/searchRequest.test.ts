import { describe, expect, it } from "vitest";
import { searchRequestPath, type SearchRequestFilters } from "./searchRequest";

const filters: SearchRequestFilters = { spaceId: "", content: "all", changedWithin: "any", sort: "relevance" };
const params = (path: string) => new URL(path, "http://atlas.test").searchParams;

describe("searchRequestPath", () => {
  it("flags only the full first-page pass for missed queries and passes the context space", () => {
    const context = { ...filters, contextSpaceId: "provoz" };
    const full = params(searchRequestPath("heslo", undefined, false, context));
    expect(full.get("recordMiss")).toBe("true");
    expect(full.get("contextSpaceId")).toBe("provoz");
    // The prefix preview, the pages after the first and callers without a space never record.
    for (const path of [searchRequestPath("heslo", undefined, true, context), searchRequestPath("heslo", 50, false, context), searchRequestPath("heslo", undefined, false, filters)]) {
      expect(params(path).has("recordMiss")).toBe(false);
      expect(params(path).has("contextSpaceId")).toBe(false);
    }
  });

  // T1: a next page of a result the server relaxed (not every word matched) says so, or the server probes the strict query again.
  it("passes a relaxed result on to its next pages only", () => {
    expect(params(searchRequestPath("heslo serveru", 50, false, { ...filters, relaxed: true })).get("relaxed")).toBe("true");
    expect(params(searchRequestPath("heslo serveru", undefined, false, { ...filters, relaxed: true })).has("relaxed")).toBe(false);
    expect(params(searchRequestPath("heslo serveru", 50, false, filters)).has("relaxed")).toBe(false);
  });

  it("does not flag tag listings or searches of concepts and of a time window", () => {
    const context = { ...filters, contextSpaceId: "provoz" };
    expect(searchRequestPath("heslo", undefined, false, { ...context, tagId: "t 1" })).toBe("/tags/t%201/pages?q=heslo&limit=50");
    expect(params(searchRequestPath("heslo", undefined, false, { ...context, content: "drafts" })).has("recordMiss")).toBe(false);
    expect(params(searchRequestPath("heslo", undefined, false, { ...context, changedWithin: "7d" })).has("recordMiss")).toBe(false);
    expect(params(searchRequestPath("heslo", undefined, false, { ...context, content: "published" })).get("recordMiss")).toBe("true");
  });

  it("keeps the filters of the dialog in the query", () => {
    const path = searchRequestPath("heslo", 50, true, { spaceId: "s1", content: "published", changedWithin: "30d", sort: "latest" });
    expect(path.startsWith("/search?")).toBe(true);
    expect(Object.fromEntries(params(path))).toEqual({ q: "heslo", limit: "50", spaceId: "s1", content: "published", changedWithin: "30d", sort: "latest", offset: "50", fuzzy: "false" });
  });
});
