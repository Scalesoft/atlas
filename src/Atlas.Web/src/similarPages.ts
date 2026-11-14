import { api } from "./api";
import { createDebouncedLookup } from "./lib/debouncedLookup";

/** One existing page that resembles the title of a page being created (P1; the server's SimilarPage). */
export interface SimilarPage {
  id: string;
  spaceId: string;
  spaceName: string;
  title: string;
  isDraft: boolean;
  /** What matched: the title, a previous published title or import path (alias), a section heading, or (with the createSuggestions capability, after the lexical ones) the meaning of the title. */
  matchedBy: "title" | "alias" | "heading" | "meaning";
  headingTitle: string | null;
  headingId: string | null;
  /** 0-1 similarity (at least 0.35; 1 is an identical title). Without it the match meter is not drawn. */
  score: number;
}

/** Up to three pages, which is all the panel shows. */
export const SIMILAR_PAGES_LIMIT = 3;
/** Wait this long after the last keystroke, and look only from this many characters of the trimmed title. */
export const SIMILAR_PAGES_DELAY_MS = 400;
export const SIMILAR_PAGES_MIN_LENGTH = 8;

export interface SimilarPagesApi {
  /** `spaceId` only ranks the creation's own space first; every readable space is searched. */
  find(title: string, spaceId: string, signal?: AbortSignal): Promise<SimilarPage[]>;
}

/** GET /api/search/similar-pages. The server answers an empty list for a title too short to compare and for a lookup that timed out. */
export const similarPagesApi: SimilarPagesApi = {
  async find(title, spaceId, signal) {
    const query = new URLSearchParams({ title, spaceId, limit: String(SIMILAR_PAGES_LIMIT) });
    const response = await api<{ items?: SimilarPage[] }>(`/search/similar-pages?${query}`, { signal });
    return Array.isArray(response?.items) ? response.items.slice(0, SIMILAR_PAGES_LIMIT) : [];
  },
};

export interface SimilarPagesWatcherOptions {
  find: (title: string, signal: AbortSignal) => Promise<SimilarPage[]>;
  /** Called with the pages of the latest title, or with `[]` when there is nothing to show (short title, failed lookup). */
  onResult: (items: SimilarPage[]) => void;
  delayMs?: number;
  minLength?: number;
}

/**
 * The debounced lookup (lib/debouncedLookup.ts) of similar pages for the title being typed: a late answer of an older
 * title is never shown, and a failed lookup is an empty result, never an error. Creating the page does not wait for it.
 */
export function createSimilarPagesWatcher({ find, onResult, delayMs = SIMILAR_PAGES_DELAY_MS, minLength = SIMILAR_PAGES_MIN_LENGTH }: SimilarPagesWatcherOptions) {
  return createDebouncedLookup<SimilarPage[]>({ find, onResult, empty: () => [], delayMs, minLength });
}

/** Number of single-character edits between two titles, ignoring case and outer spaces. */
export function titleDrift(a: string, b: string): number {
  const left = a.trim().toLowerCase();
  const right = b.trim().toLowerCase();
  let row = Array.from({ length: right.length + 1 }, (_, index) => index);
  for (let i = 1; i <= left.length; i++) {
    const next = [i];
    for (let j = 1; j <= right.length; j++)
      next[j] = Math.min(row[j]! + 1, next[j - 1]! + 1, row[j - 1]! + (left[i - 1] === right[j - 1] ? 0 : 1));
    row = next;
  }
  return row[right.length]!;
}

/** The match meter's percentage, or null when the server sent no usable score (then no meter is drawn). */
export function matchPercent(score: number | null | undefined): number | null {
  return typeof score === "number" && Number.isFinite(score) ? Math.min(100, Math.max(0, Math.round(score * 100))) : null;
}
