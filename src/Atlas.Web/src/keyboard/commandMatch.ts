import { rankSuggestion } from "@atlas/markdown/match";
import { foldText } from "../lib/foldText";

export interface MatchableCommand { label: string; detail: string; keywords?: readonly string[] }

/**
 * The palette's commands for a query, best first: the slash menu's tiers (the label starts with it, a word of it does, a
 * keyword or the detail does, it is inside), then commands that hold every typed word somewhere, in any order. Case and
 * diacritics aside; the first result is the one Enter runs.
 */
export function matchCommands<T extends MatchableCommand>(query: string, items: readonly T[]): T[] {
  const typed = query.trim();
  if (!typed) return [...items];
  const words = foldText(typed).split(/\s+/);
  const ranked = items.map((item, index) => {
    const tier = rankSuggestion(typed, { title: item.label, keywords: [item.detail, ...(item.keywords ?? [])] });
    const text = foldText([item.label, item.detail, ...(item.keywords ?? [])].join(" "));
    return { item, index, tier: tier ?? (words.every(word => text.includes(word)) ? 4 : undefined) };
  });
  return ranked.filter(entry => entry.tier !== undefined)
    .sort((a, b) => a.tier! - b.tier! || a.index - b.index)
    .map(entry => entry.item);
}
