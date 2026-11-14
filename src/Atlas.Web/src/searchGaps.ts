import { api, post } from "./api";

/** One query that ended without a result for at least `minimumUsers` different people in the period (G2). */
export interface SearchGap {
  /** The most frequent typed form: lower case, diacritics kept. */
  query: string;
  /** Identifies the gap for a resolution. */
  normalizedQuery: string;
  users: number;
  /** Phase 3 (Q5): distinct people whose strongest reason was an assistant answer without sources / a report of missing content. Omitted when zero, never above `users`. */
  unanswered?: number;
  reported?: number;
  /** A day, `yyyy-mm-dd`. */
  lastSearchedOn: string;
}
export interface SearchGapList {
  items: SearchGap[];
  minimumUsers: number;
  retentionDays: number;
}
export type SearchGapDays = 7 | 30 | 90;

export interface SearchGapsApi {
  list(spaceId: string, days: SearchGapDays, signal?: AbortSignal): Promise<SearchGapList>;
  resolve(spaceId: string, normalizedQuery: string): Promise<void>;
}

/** The statistic of a space for its administrator; the server answers 404 to everyone else. */
export const searchGapsApi: SearchGapsApi = {
  list: (spaceId, days, signal) => api<SearchGapList>(`/spaces/${encodeURIComponent(spaceId)}/search-gaps?days=${days}`, { signal }),
  resolve: (spaceId, normalizedQuery) => post<void>(`/spaces/${encodeURIComponent(spaceId)}/search-gaps/resolutions`, { normalizedQuery }),
};
