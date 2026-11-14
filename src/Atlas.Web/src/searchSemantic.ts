import type { SearchResult } from "./api";
import { highlightSearch } from "./searchHighlight";

/** The passage a row shows instead of its snippet: sent with a hit, or (meaning-only) the snippet itself with its heading. */
export interface RowPassage { text: string; headingTitle: string | null; headingId: string | null }

/** Nothing semantic exists without the capability, so a row is then exactly the row of today. */
export function rowPassage(result: SearchResult, enabled: boolean): RowPassage | undefined {
  if (!enabled) return undefined;
  if (result.passage) return { text: result.passage.text, headingTitle: result.passage.headingTitle ?? null, headingId: result.passage.headingId ?? null };
  if (result.semantic) return { text: result.snippet, headingTitle: result.headingTitle ?? null, headingId: result.headingId ?? null };
  return undefined;
}

/** A heading row belongs to "Nadpisy"; a meaning-only hit or a passage stays an article row that links to its heading. A page
 * whose title or earlier title (alias) matches stays an article row too: the server ranks it above heading matches. */
export const isHeadingRow = (result: SearchResult, enabled: boolean, query: string) => !!result.headingId && !rowPassage(result, enabled)
  && !result.matchedAlias && !highlightSearch(result.title, query).some(part => part.matched);

export const isSemanticRow = (result: SearchResult, enabled: boolean) => enabled && result.semantic === true;
