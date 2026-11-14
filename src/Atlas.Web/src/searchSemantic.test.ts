// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { SearchResult } from "./api";
import { isHeadingRow } from "./searchSemantic";

const hit = (title: string, extra: Partial<SearchResult> = {}) =>
  ({ id: title, spaceId: "s", spaceName: "Portál", title, snippet: "", headingId: "h", headingTitle: "Popis nasazení v KBN", ...extra }) as SearchResult;

describe("isHeadingRow", () => {
  it("keeps a page whose title or earlier title matches among the articles, even when a heading matches too", () => {
    expect(isHeadingRow(hit("Deployment in KBN"), false, "kbn")).toBe(false);
    expect(isHeadingRow(hit("Nemocnice", { matchedAlias: "Deployment in KBN" }), false, "KBN")).toBe(false);
    expect(isHeadingRow(hit("External access"), false, "KBN")).toBe(true);
    expect(isHeadingRow(hit("External access", { headingId: null }), false, "KBN")).toBe(false);
  });
});
