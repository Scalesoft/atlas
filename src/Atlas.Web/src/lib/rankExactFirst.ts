import { foldSearchText, rankSuggestions, type SuggestionRankInput } from "@atlas/markdown/match";

const words = (text: string) => foldSearchText(text).split(/[^\p{L}\p{N}]+/u).filter(Boolean);

/**
 * The slash menu's ranking (`rankSuggestions`), with the items whose whole title is the query, case and diacritics aside, first:
 * „Graf“ before „Grafy“ for „graf“, whichever came first in the list. After them the items every typed word begins a word of, in
 * any order („otestov objedn“ finds „Objednávky otestovány“, a key's parts count as words), which the editor's ranking, matching the
 * query as one piece, does not find.
 */
export function rankExactFirst<T>(query: string, items: readonly T[], read: (item: T) => SuggestionRankInput): T[] {
  const ranked = rankSuggestions(query.trim(), items, read);
  const typed = foldSearchText(query.trim());
  if (!typed) return ranked;
  const exact = (item: T) => foldSearchText(read(item).title) === typed;
  const tokens = words(typed), found = new Set(ranked);
  const byWords = tokens.length < 2 ? [] : items.filter(item => {
    if (found.has(item)) return false;
    const { title, keywords = [] } = read(item), all = [title, ...keywords].flatMap(words);
    return tokens.every(token => all.some(word => word.startsWith(token)));
  });
  return [...ranked.filter(exact), ...ranked.filter(item => !exact(item)), ...byWords];
}
