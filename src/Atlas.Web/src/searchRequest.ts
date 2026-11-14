/** What the omnisearch dialog currently filters by; `tagId` turns the search into a tag listing. */
export interface SearchRequestFilters {
  spaceId: string;
  content: string;
  changedWithin: string;
  sort: string;
  tagId?: string;
  /** The space the omnisearch was opened in, so a missed query can be counted for it (G2). */
  contextSpaceId?: string;
  /** The first page was relaxed (not every word of the query matched): its next pages continue that result. */
  relaxed?: boolean;
}

/** Results per request; the paging helpers derive their offsets from it. */
export const SEARCH_PAGE_SIZE = 50;

/**
 * The request of one omnisearch pass. Only the full first-page pass of a plain search is flagged
 * `recordMiss` (the server keeps a zero-result query for "Nenalezené dotazy"): not the prefix
 * preview, not later pages, not a tag listing, concepts or a time window, and not without a space.
 */
export function searchRequestPath(text: string, offset: number | undefined, prefixOnly: boolean, filters: SearchRequestFilters) {
  const params = new URLSearchParams({ q: text, limit: String(SEARCH_PAGE_SIZE) });
  if (filters.spaceId) params.set("spaceId", filters.spaceId);
  if (filters.content !== "all") params.set("content", filters.content);
  if (filters.changedWithin !== "any") params.set("changedWithin", filters.changedWithin);
  if (filters.sort !== "relevance") params.set("sort", filters.sort);
  if (offset !== undefined) params.set("offset", String(offset));
  if (offset && filters.relaxed) params.set("relaxed", "true");
  if (prefixOnly) params.set("fuzzy", "false");
  if (filters.tagId) return `/tags/${encodeURIComponent(filters.tagId)}/pages?${params}`;
  const space = filters.spaceId || filters.contextSpaceId;
  if (!prefixOnly && !offset && space && filters.content !== "drafts" && filters.changedWithin === "any") {
    params.set("recordMiss", "true");
    if (filters.contextSpaceId) params.set("contextSpaceId", filters.contextSpaceId);
  }
  return `/search?${params}`;
}
